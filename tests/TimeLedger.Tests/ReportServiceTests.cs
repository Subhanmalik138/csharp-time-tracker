using TimeLedger;
using Xunit;

namespace TimeLedger.Tests;

/// <summary>Billing math, summaries and CSV output.</summary>
public sealed class ReportServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(5));

    private static (DataFile Data, TrackerService Svc, ReportService Reports) Seeded()
    {
        var data = new DataFile();
        var svc = new TrackerService(data, () => T0);
        svc.AddClient("Acme", 50m);
        svc.AddProject("Acme", "Website", 60m); // override: 60/h
        svc.AddProject("Acme", "Support", null); // falls back: 50/h
        svc.AddManualEntry("Acme/Website", T0, T0.AddHours(2).AddMinutes(30), "Landing page", [], true);
        svc.AddManualEntry("Acme/Website", T0.AddHours(3), T0.AddHours(4), "Bug fixes", [], false);
        svc.AddManualEntry("Acme/Support", T0, T0.AddHours(1), "Tickets", [], true);
        return (data, svc, new ReportService(data, svc));
    }

    [Fact]
    public void Summaries_ChargeBillableHoursAtEffectiveRate_AndIgnoreNonBillable()
    {
        var (_, _, reports) = Seeded();
        var summaries = reports.Summaries(null, null);
        var website = summaries.Single(s => s.Project.Name == "Website");
        Assert.Equal(TimeSpan.FromHours(3.5), website.Total);
        Assert.Equal(TimeSpan.FromHours(2.5), website.Billable);
        Assert.Equal(150m, website.Amount); // 2.5h × 60
        var support = summaries.Single(s => s.Project.Name == "Support");
        Assert.Equal(50m, support.Amount); // 1h × 50 (client fallback rate)
    }

    [Fact]
    public void Summaries_RespectDateRange()
    {
        var (_, _, reports) = Seeded();
        var summaries = reports.Summaries(T0.Date.AddDays(1), null);
        Assert.Empty(summaries);
    }

    [Fact]
    public void InvoiceLines_ChargeZeroForNonBillable()
    {
        var (_, svc, reports) = Seeded();
        var client = svc.FindClient("Acme");
        var lines = reports.InvoiceLines(client, null, null);
        Assert.Equal(3, lines.Count);
        Assert.Equal(0m, lines.Single(l => l.Entry.Description == "Bug fixes").Amount);
        Assert.Equal(200m, lines.Sum(l => l.Amount)); // 150 + 0 + 50
    }

    [Fact]
    public void CsvExporter_QuotesFieldsWithCommasAndQuotes()
    {
        var (data, svc, reports) = Seeded();
        svc.AddManualEntry("Acme/Website", T0.AddDays(1), T0.AddDays(1).AddHours(1), "Fix \"login\", then deploy", [], true);
        var client = svc.FindClient("Acme");
        var lines = reports.InvoiceLines(client, null, null);
        var path = Path.Combine(Path.GetTempPath(), $"tl-invoice-{Guid.NewGuid():N}.csv");
        try
        {
            CsvExporter.WriteInvoice(path, client, lines, null, null);
            var csv = File.ReadAllText(path);
            Assert.Contains("\"Fix \"\"login\"\", then deploy\"", csv);
            Assert.Contains("TOTAL,,,,260.00,USD,", csv); // 200 + 1h × 60
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Entries_ReturnNewestFirst_AndFilterByProject()
    {
        var (_, svc, reports) = Seeded();
        var project = svc.FindProject("Acme/Website");
        var entries = reports.Entries(null, null, project.Id);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].Start > entries[1].Start);
    }
}

/// <summary>JSON store round-trips and atomic saves.</summary>
public sealed class JsonStoreTests
{
    [Fact]
    public void SaveThenLoad_PreservesClientsProjectsAndEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tl-data-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonStore(path);
            var data = new DataFile();
            var svc = new TrackerService(data, () => new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(5)));
            svc.AddClient("Acme", 50m);
            svc.AddProject("Acme", "Website", 60m);
            svc.AddManualEntry("Acme/Website",
                new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(5)),
                new DateTimeOffset(2026, 11, 5, 10, 30, 0, TimeSpan.FromHours(5)),
                "work", ["a"], true);
            store.Save(data);

            var loaded = store.Load();
            Assert.Single(loaded.Clients);
            Assert.Equal("Acme", loaded.Clients[0].Name);
            Assert.Single(loaded.Projects);
            Assert.Equal(60m, loaded.Projects[0].HourlyRate);
            Assert.Single(loaded.Entries);
            Assert.Equal("work", loaded.Entries[0].Description);
            Assert.Equal(["a"], loaded.Entries[0].Tags);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDataFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tl-missing-{Guid.NewGuid():N}.json");
        var loaded = new JsonStore(path).Load();
        Assert.Empty(loaded.Clients);
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public void ParseDateTime_AcceptsDateTimeAndBareTime()
    {
        var dt = Cli.ParseDateTime("2026-11-05 09:30");
        Assert.Equal(2026, dt.Year);
        Assert.Equal(30, dt.Minute);
        Assert.Throws<TrackerException>(() => Cli.ParseDateTime("not a date"));
    }
}
