using TimeLedger;
using Xunit;

namespace TimeLedger.Tests;

/// <summary>Timer rules, client/project validation and manual-entry guards.</summary>
public sealed class TrackerServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(5));

    private static TrackerService Service(DataFile? data = null, DateTimeOffset? now = null)
    {
        var clock = now ?? T0;
        return new TrackerService(data ?? new DataFile(), () => clock);
    }

    private static TrackerService Seeded(out DataFile data)
    {
        data = new DataFile();
        var svc = Service(data);
        svc.AddClient("Acme", 50m);
        svc.AddClient("Globex", 80m);
        svc.AddProject("Acme", "Website", 60m);
        svc.AddProject("Acme", "Support", null);
        svc.AddProject("Globex", "Website", null);
        return svc;
    }

    [Fact]
    public void AddClient_RejectsDuplicateName_CaseInsensitive()
    {
        var svc = Service();
        svc.AddClient("Acme", 50m);
        Assert.Throws<TrackerException>(() => svc.AddClient("acme", 60m));
    }

    [Fact]
    public void AddClient_RejectsNegativeRate()
    {
        Assert.Throws<TrackerException>(() => Service().AddClient("Acme", -1m));
    }

    [Fact]
    public void AddProject_RequiresExistingClient()
    {
        Assert.Throws<TrackerException>(() => Service().AddProject("Nope", "Website", null));
    }

    [Fact]
    public void FindProject_BareName_ThrowsWhenAmbiguous()
    {
        var svc = Seeded(out _);
        var ex = Assert.Throws<TrackerException>(() => svc.FindProject("Website"));
        Assert.Contains("Client/Project", ex.Message);
    }

    [Fact]
    public void FindProject_ClientSlashProject_Disambiguates()
    {
        var svc = Seeded(out _);
        Assert.Equal("Globex", svc.ClientName(svc.FindProject("Globex/Website")));
        Assert.Equal(60m, svc.EffectiveRate(svc.FindProject("Acme/Website")));
        Assert.Equal(80m, svc.EffectiveRate(svc.FindProject("Globex/Website")));
    }

    [Fact]
    public void EffectiveRate_FallsBackToClientRate_WhenNoOverride()
    {
        var svc = Seeded(out _);
        Assert.Equal(50m, svc.EffectiveRate(svc.FindProject("Acme/Support")));
    }

    [Fact]
    public void Start_RejectsSecondTimerWhileOneRuns()
    {
        var svc = Seeded(out _);
        svc.Start("Acme/Website", "", [], true);
        var ex = Assert.Throws<TrackerException>(() => svc.Start("Acme/Support", "", [], true));
        Assert.Contains("already running", ex.Message);
    }

    [Fact]
    public void Stop_WithNoTimerRunning_Throws()
    {
        Assert.Throws<TrackerException>(() => Service().Stop(null));
    }

    [Fact]
    public void Stop_ReturnsElapsedDuration_AndClearsRunningEntry()
    {
        var data = new DataFile();
        var now = T0;
        var svc = new TrackerService(data, () => now);
        svc.AddClient("Acme", 50m);
        svc.AddProject("Acme", "Website", null);
        svc.Start("Website", "work", [], true);
        now = T0.AddMinutes(90);
        var (entry, duration) = svc.Stop(null);
        Assert.Equal(TimeSpan.FromMinutes(90), duration);
        Assert.Equal(T0.AddMinutes(90), entry.End);
        Assert.Null(svc.RunningEntry());
    }

    [Fact]
    public void AddManualEntry_RejectsOverlap_OnSameProject()
    {
        var svc = Seeded(out _);
        svc.AddManualEntry("Acme/Website", T0, T0.AddHours(2), "", [], true);
        Assert.Throws<TrackerException>(() =>
            svc.AddManualEntry("Acme/Website", T0.AddHours(1), T0.AddHours(3), "", [], true));
    }

    [Fact]
    public void AddManualEntry_AllowsAdjacentBlocks_AndOtherProjects()
    {
        var svc = Seeded(out _);
        svc.AddManualEntry("Acme/Website", T0, T0.AddHours(2), "", [], true);
        svc.AddManualEntry("Acme/Website", T0.AddHours(2), T0.AddHours(3), "", [], true);
        svc.AddManualEntry("Acme/Support", T0.AddHours(1), T0.AddHours(3), "", [], true);
        Assert.Equal(3, svc.Data.Entries.Count);
    }

    [Fact]
    public void AddManualEntry_RejectsEndBeforeStart()
    {
        var svc = Seeded(out _);
        Assert.Throws<TrackerException>(() =>
            svc.AddManualEntry("Acme/Website", T0.AddHours(1), T0, "", [], true));
    }
}
