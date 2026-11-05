namespace TimeLedger;

/// <summary>User-facing error: bad input, a missing client/project, or a timer
/// rule violation. The CLI prints the message and exits non-zero.</summary>
public sealed class TrackerException(string message) : Exception(message);

/// <summary>Business logic over a <see cref="DataFile"/>: clients, projects and
/// the single running timer. Pure except for the clock, which is injected so
/// tests can pin "now".</summary>
public sealed class TrackerService
{
    private readonly DataFile _data;
    private readonly Func<DateTimeOffset> _now;

    public TrackerService(DataFile data, Func<DateTimeOffset>? now = null)
    {
        _data = data;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public DateTimeOffset Now => _now();

    // ----- Clients -----

    public Client AddClient(string name, decimal hourlyRate, string currency = "USD")
    {
        name = name.Trim();
        if (name.Length == 0) throw new TrackerException("Client name must not be empty.");
        if (hourlyRate < 0) throw new TrackerException("Hourly rate must not be negative.");
        if (_data.Clients.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new TrackerException($"Client '{name}' already exists.");
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = name,
            HourlyRate = hourlyRate,
            Currency = currency.ToUpperInvariant(),
            CreatedAt = Now,
        };
        _data.Clients.Add(client);
        return client;
    }

    public IReadOnlyList<Client> Clients() => _data.Clients.OrderBy(c => c.Name).ToList();

    public Client FindClient(string name) =>
        _data.Clients.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new TrackerException($"No client named '{name}'. Add it first: client add {name} --rate <hourly-rate>");

    // ----- Projects -----

    public Project AddProject(string clientName, string name, decimal? hourlyRate)
    {
        var client = FindClient(clientName);
        name = name.Trim();
        if (name.Length == 0) throw new TrackerException("Project name must not be empty.");
        if (hourlyRate is < 0) throw new TrackerException("Hourly rate must not be negative.");
        if (_data.Projects.Any(p => p.ClientId == client.Id
                && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new TrackerException($"Project '{name}' already exists for client '{client.Name}'.");
        var project = new Project
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = name,
            HourlyRate = hourlyRate,
            CreatedAt = Now,
        };
        _data.Projects.Add(project);
        return project;
    }

    public IReadOnlyList<Project> Projects(string? clientName = null, bool includeArchived = false)
    {
        var query = _data.Projects.AsEnumerable();
        if (clientName is not null) query = query.Where(p => p.ClientId == FindClient(clientName).Id);
        if (!includeArchived) query = query.Where(p => !p.Archived);
        return query.OrderBy(p => p.Name).ToList();
    }

    /// <summary>Resolves a project by name. Accepts "Client/Project" to disambiguate
    /// projects that share a name across clients; a bare name must be unique.</summary>
    public Project FindProject(string reference)
    {
        if (reference.Contains('/'))
        {
            var parts = reference.Split('/', 2);
            var client = FindClient(parts[0].Trim());
            return _data.Projects.FirstOrDefault(p => p.ClientId == client.Id
                    && string.Equals(p.Name, parts[1].Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new TrackerException($"No project '{parts[1].Trim()}' for client '{client.Name}'.");
        }

        var matches = _data.Projects
            .Where(p => string.Equals(p.Name, reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches switch
        {
            [] => throw new TrackerException($"No project named '{reference}'. Add it first: project add <client> {reference}"),
            [var only] => only,
            _ => throw new TrackerException($"Project name '{reference}' is ambiguous — use Client/Project, e.g. '{ClientName(matches[0])}/{reference}'."),
        };
    }

    public Project FindProjectById(Guid id) =>
        _data.Projects.FirstOrDefault(p => p.Id == id)
        ?? throw new TrackerException("Project for this entry no longer exists.");

    public string ClientName(Project project) =>
        _data.Clients.FirstOrDefault(c => c.Id == project.ClientId)?.Name ?? "(unknown client)";

    /// <summary>Effective billing rate: the project's override, else its client's rate.</summary>
    public decimal EffectiveRate(Project project) =>
        project.HourlyRate ?? _data.Clients.First(c => c.Id == project.ClientId).HourlyRate;

    // ----- Timer -----

    public TimeEntry? RunningEntry() => _data.Entries.FirstOrDefault(e => e.IsRunning);

    public TimeEntry Start(string projectRef, string description, IEnumerable<string> tags, bool billable)
    {
        if (RunningEntry() is { } running)
        {
            var project = _data.Projects.First(p => p.Id == running.ProjectId);
            throw new TrackerException(
                $"Timer already running on '{project.Name}' since {running.Start:yyyy-MM-dd HH:mm}. Stop it first: timeledger stop");
        }
        var target = FindProject(projectRef);
        if (target.Archived) throw new TrackerException($"Project '{target.Name}' is archived.");
        var entry = new TimeEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = target.Id,
            Description = description,
            Start = Now,
            Billable = billable,
            Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
        };
        _data.Entries.Add(entry);
        return entry;
    }

    public (TimeEntry Entry, TimeSpan Duration) Stop(string? description)
    {
        var running = RunningEntry()
            ?? throw new TrackerException("No timer is running.");
        var now = Now;
        var stopped = running.Stopped(now);
        if (description is not null) stopped = stopped with { Description = description };
        _data.Entries[_data.Entries.IndexOf(running)] = stopped;
        return (stopped, now - running.Start);
    }
}
