using System.Globalization;
using System.Text;

namespace TimeLedger;

/// <summary>One line of a billable summary: a project, its hours and what they are worth.</summary>
public sealed record ProjectSummary(Project Project, string ClientName, decimal Rate, TimeSpan Total, TimeSpan Billable, decimal Amount);

/// <summary>One invoice line: a finished entry with its computed charge.</summary>
public sealed record InvoiceLine(TimeEntry Entry, Project Project, decimal Amount);

/// <summary>Read-side queries over a <see cref="DataFile"/>: entry listing,
/// per-project billable summaries and per-client invoice lines. Billing is
/// hours × effective rate (project override, else client rate), rounded to
/// cents; non-billable entries never contribute to an amount.</summary>
public sealed class ReportService
{
    private readonly DataFile _data;
    private readonly TrackerService _svc;

    public ReportService(DataFile data, TrackerService svc)
    {
        _data = data;
        _svc = svc;
    }

    public IReadOnlyList<TimeEntry> Entries(DateTimeOffset? from, DateTimeOffset? to, Guid? projectId = null, Guid? clientId = null)
    {
        var query = _data.Entries.AsEnumerable();
        if (from is not null) query = query.Where(e => e.Start >= from);
        if (to is not null) query = query.Where(e => e.Start < to);
        if (projectId is not null) query = query.Where(e => e.ProjectId == projectId);
        if (clientId is not null)
        {
            var projectIds = _data.Projects.Where(p => p.ClientId == clientId).Select(p => p.Id).ToHashSet();
            query = query.Where(e => projectIds.Contains(e.ProjectId));
        }
        return query.OrderByDescending(e => e.Start).ToList();
    }

    public IReadOnlyList<ProjectSummary> Summaries(DateTimeOffset? from, DateTimeOffset? to, Guid? clientId = null)
    {
        var entries = Entries(from, to, clientId: clientId);
        return entries
            .GroupBy(e => e.ProjectId)
            .Select(g =>
            {
                var project = _svc.FindProjectById(g.Key);
                var now = _svc.Now;
                var total = g.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Duration(now));
                var billable = g.Where(e => e.Billable).Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Duration(now));
                var rate = _svc.EffectiveRate(project);
                var amount = Math.Round((decimal)billable.TotalHours * rate, 2, MidpointRounding.AwayFromZero);
                return new ProjectSummary(project, _svc.ClientName(project), rate, total, billable, amount);
            })
            .OrderByDescending(s => s.Amount)
            .ToList();
    }

    public IReadOnlyList<InvoiceLine> InvoiceLines(Client client, DateTimeOffset? from, DateTimeOffset? to)
    {
        var entries = Entries(from, to, clientId: client.Id).Where(e => !e.IsRunning);
        return entries.Select(e =>
        {
            var project = _svc.FindProjectById(e.ProjectId);
            var amount = e.Billable
                ? Math.Round((decimal)e.Duration(_svc.Now).TotalHours * _svc.EffectiveRate(project), 2, MidpointRounding.AwayFromZero)
                : 0m;
            return new InvoiceLine(e, project, amount);
        }).ToList();
    }
}

/// <summary>Minimal CSV writer: quotes fields containing commas, quotes or newlines.</summary>
public static class CsvExporter
{
    public static void WriteInvoice(string path, Client client, IReadOnlyList<InvoiceLine> lines, DateTimeOffset? from, DateTimeOffset? to)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,project,description,duration_hours,amount,currency,billable");
        foreach (var line in lines)
        {
            var e = line.Entry;
            sb.AppendLine(string.Join(",",
                Field(e.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Field(line.Project.Name),
                Field(e.Description),
                Field((e.Duration(e.End ?? e.Start)).TotalHours.ToString("0.##", CultureInfo.InvariantCulture)),
                Field(line.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
                Field(client.Currency),
                Field(e.Billable ? "yes" : "no")));
        }
        sb.AppendLine(string.Join(",", Field("TOTAL"), "", "", "",
            Field(lines.Sum(l => l.Amount).ToString("0.00", CultureInfo.InvariantCulture)),
            Field(client.Currency), ""));
        File.WriteAllText(path, sb.ToString());
    }

    private static string Field(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }
}
