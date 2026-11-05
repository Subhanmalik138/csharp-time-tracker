namespace TimeLedger;

/// <summary>A paying customer. Every project belongs to exactly one client,
/// and the client's hourly rate is the default billing rate for its projects.</summary>
public sealed record Client
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Default hourly rate in <see cref="Currency"/>; a project may override it.</summary>
    public required decimal HourlyRate { get; init; }
    public string Currency { get; init; } = "USD";
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A billable (or internal) piece of work for one client.</summary>
public sealed record Project
{
    public required Guid Id { get; init; }
    public required Guid ClientId { get; init; }
    public required string Name { get; init; }
    /// <summary>When set, overrides the client's hourly rate for this project.</summary>
    public decimal? HourlyRate { get; init; }
    public bool Archived { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One block of tracked time. <see cref="End"/> is null while the timer runs.</summary>
public sealed record TimeEntry
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public string Description { get; init; } = string.Empty;
    public required DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public bool Billable { get; init; } = true;
    public List<string> Tags { get; init; } = [];

    public bool IsRunning => End is null;

    /// <summary>Elapsed time so far; for a finished entry this is the final duration.</summary>
    public TimeSpan Duration(DateTimeOffset now) => (End ?? now) - Start;

    public TimeEntry Stopped(DateTimeOffset end) => this with { End = end };
}

/// <summary>The whole data file: one JSON document, written atomically.</summary>
public sealed class DataFile
{
    public List<Client> Clients { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<TimeEntry> Entries { get; set; } = [];
}
