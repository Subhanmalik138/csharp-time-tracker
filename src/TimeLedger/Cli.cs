namespace TimeLedger;

/// <summary>Command-line front end. Hand-rolled parsing on purpose: the whole
/// tool is one file of options, and a dependency would buy nothing here.</summary>
public static class Cli
{
    public static int Run(string[] args, JsonStore store, TextWriter stdout, TextWriter stderr)
    {
        var (dataPath, rest) = SplitGlobalOptions(args, store);
        var data = new JsonStore(dataPath).Load();
        var svc = new TrackerService(data);        try
        {
            var code = Dispatch(svc, rest, stdout);
            if (code == 0) new JsonStore(dataPath).Save(data);
            return code;
        }
        catch (TrackerException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Only global option today: --data &lt;path&gt;. Everything else is a command.</summary>
    private static (string DataPath, string[] Remaining) SplitGlobalOptions(string[] args, JsonStore store)
    {
        var path = JsonStore.DefaultPath();
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Length) { path = args[++i]; }
            else if (args[i].StartsWith("--data=", StringComparison.Ordinal)) { path = args[i]["--data=".Length..]; }
            else remaining.Add(args[i]);
        }
        return (path, remaining.ToArray());
    }

    private static int Dispatch(TrackerService svc, string[] args, TextWriter stdout)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h") { PrintHelp(stdout); return 0; }
        return args[0] switch
        {
            "client" => ClientCommand(svc, args[1..], stdout),
            "project" => ProjectCommand(svc, args[1..], stdout),
            "start" => StartCommand(svc, args[1..], stdout),
            "stop" => StopCommand(svc, args[1..], stdout),
            "status" => StatusCommand(svc, stdout),
            "log" => LogCommand(svc, args[1..], stdout),
            "entries" => EntriesCommand(svc, args[1..], stdout),
            "report" => ReportCommand(svc, args[1..], stdout),
            "invoice" => InvoiceCommand(svc, args[1..], stdout),
            _ => throw new TrackerException($"Unknown command '{args[0]}'. Run without arguments to see usage."),
        };
    }

    // ----- client -----

    private static int ClientCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        if (args is ["add", var name, .. var rest])
        {
            var opts = ParseOptions(rest);
            var rate = RequireDecimal(opts, "rate");
            var currency = opts.GetValueOrDefault("currency", "USD");
            var client = svc.AddClient(name, rate, currency);
            out_.WriteLine($"Added client '{client.Name}' at {client.HourlyRate:0.##} {client.Currency}/h.");
            return 0;
        }
        if (args is ["list"])
        {
            var clients = svc.Clients();
            if (clients.Count == 0) { out_.WriteLine("No clients yet. Add one: client add <name> --rate <hourly-rate>"); return 0; }
            out_.WriteLine($"{"Client",-24} {"Rate",-12} Created");
            foreach (var c in clients)
                out_.WriteLine($"{c.Name,-24} {c.HourlyRate:0.##} {c.Currency,-8} {c.CreatedAt:yyyy-MM-dd}");
            return 0;
        }
        throw new TrackerException("Usage: client add <name> --rate <n> [--currency CODE] | client list");
    }

    // ----- project -----

    private static int ProjectCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        if (args is ["add", var clientName, var name, .. var rest])
        {
            var opts = ParseOptions(rest);
            decimal? rate = opts.TryGetValue("rate", out var r) ? decimal.Parse(r) : null;
            var project = svc.AddProject(clientName, name, rate);
            var effective = svc.EffectiveRate(project);
            out_.WriteLine($"Added project '{project.Name}' for client '{svc.ClientName(project)}' at {effective:0.##}/h.");
            return 0;
        }
        if (args is ["list", .. var listRest])
        {
            var opts = ParseOptions(listRest);
            var projects = svc.Projects(opts.GetValueOrDefault("client"), opts.ContainsKey("all"));
            if (projects.Count == 0) { out_.WriteLine("No projects yet. Add one: project add <client> <name> [--rate n]"); return 0; }
            out_.WriteLine($"{"Project",-24} {"Client",-20} {"Rate",-10}");
            foreach (var p in projects)
                out_.WriteLine($"{p.Name,-24} {svc.ClientName(p),-20} {svc.EffectiveRate(p):0.##}/h{(p.Archived ? " (archived)" : "")}");
            return 0;
        }
        throw new TrackerException("Usage: project add <client> <name> [--rate n] | project list [--client <name>] [--all]");
    }

    // ----- timer -----

    private static int StartCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        if (args.Length == 0) throw new TrackerException("Usage: start <project> [--desc \"...\"] [--tags a,b] [--no-bill]");
        var opts = ParseOptions(args[1..]);
        var tags = opts.TryGetValue("tags", out var t) ? t.Split(',') : [];
        var entry = svc.Start(args[0], opts.GetValueOrDefault("desc", ""), tags, !opts.ContainsKey("no-bill"));
        var project = svc.FindProject(args[0]);
        out_.WriteLine($"Started '{project.Name}' ({svc.ClientName(project)}) at {entry.Start:HH:mm}.");
        return 0;
    }

    private static int StopCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        var opts = ParseOptions(args);
        var (entry, duration) = svc.Stop(opts.GetValueOrDefault("desc"));
        var project = svc.FindProjectById(entry.ProjectId);
        out_.WriteLine($"Stopped '{project.Name}': {FormatDuration(duration)}" +
                       (entry.Description.Length > 0 ? $" — {entry.Description}" : ""));
        return 0;
    }

    private static int StatusCommand(TrackerService svc, TextWriter out_)
    {
        var running = svc.RunningEntry();
        if (running is null) { out_.WriteLine("No timer running."); return 0; }
        var project = svc.FindProjectById(running.ProjectId);
        out_.WriteLine($"Running: '{project.Name}' ({svc.ClientName(project)}) — {FormatDuration(running.Duration(svc.Now))} since {running.Start:HH:mm}" +
                       (running.Description.Length > 0 ? $" — {running.Description}" : ""));
        return 0;
    }

    // ----- entries & reports -----

    private static int LogCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        if (args.Length < 3) throw new TrackerException("Usage: log <project> <start> <end> [--desc \"...\"] [--tags a,b] [--no-bill]\nTimes: \"2026-11-05 09:00\", \"2026-11-05T09:00\", or \"09:00\" for today.");
        var opts = ParseOptions(args[3..]);
        var start = ParseDateTime(args[1]);
        var end = ParseDateTime(args[2]);
        if (end <= start) throw new TrackerException("End time must be after start time.");
        if (svc.RunningEntry() is { } running && running.ProjectId == svc.FindProject(args[0]).Id)
            throw new TrackerException("A timer is already running on this project — stop it before logging a manual entry.");
        var tags = opts.TryGetValue("tags", out var t) ? t.Split(',') : [];
        var entry = svc.AddManualEntry(args[0], start, end, opts.GetValueOrDefault("desc", ""), tags, !opts.ContainsKey("no-bill"));
        out_.WriteLine($"Logged {FormatDuration(entry.Duration(svc.Now))} on '{svc.FindProjectById(entry.ProjectId).Name}' ({start:yyyy-MM-dd HH:mm} – {end:HH:mm}).");
        return 0;
    }

    private static int EntriesCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        var opts = ParseOptions(args);
        var (from, to) = ParseRange(opts);
        Guid? projectId = opts.TryGetValue("project", out var p) ? svc.FindProject(p).Id : null;
        Guid? clientId = opts.TryGetValue("client", out var c) ? svc.FindClient(c).Id : null;
        var entries = new ReportService(svc.Data, svc).Entries(from, to, projectId, clientId);
        if (entries.Count == 0) { out_.WriteLine("No entries in this range."); return 0; }
        out_.WriteLine($"{"Started",-17} {"Project",-20} {"Duration",-10} Description");
        foreach (var e in entries)
        {
            var project = svc.FindProjectById(e.ProjectId);
            out_.WriteLine($"{e.Start:yyyy-MM-dd HH:mm}  {project.Name,-20} {(e.IsRunning ? "running" : FormatDuration(e.Duration(svc.Now))),-10} {(e.Billable ? "" : "[non-billable] ")}{e.Description}");
        }
        return 0;
    }

    private static int ReportCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        var opts = ParseOptions(args);
        var (from, to) = ParseRange(opts);
        Guid? clientId = opts.TryGetValue("client", out var c) ? svc.FindClient(c).Id : null;
        var summaries = new ReportService(svc.Data, svc).Summaries(from, to, clientId);
        if (summaries.Count == 0) { out_.WriteLine("No tracked time in this range."); return 0; }
        var label = from is null ? "All time" : $"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}";
        out_.WriteLine($"Billable report — {label}");
        out_.WriteLine($"{"Project",-20} {"Client",-16} {"Hours",-8} {"Billable",-9} Amount");
        decimal total = 0;
        foreach (var s in summaries)
        {
            total += s.Amount;
            out_.WriteLine($"{s.Project.Name,-20} {s.ClientName,-16} {s.Total.TotalHours,-8:0.##} {s.Billable.TotalHours,-9:0.##} {s.Amount:0.00}");
        }
        out_.WriteLine($"Total billable: {total:0.00}");
        return 0;
    }

    private static int InvoiceCommand(TrackerService svc, string[] args, TextWriter out_)
    {
        var opts = ParseOptions(args);
        if (!opts.TryGetValue("client", out var clientName)) throw new TrackerException("Usage: invoice --client <name> [--from <date>] [--to <date>] [--week|--month] [--csv <path>]");
        var client = svc.FindClient(clientName);
        var (from, to) = ParseRange(opts);
        var lines = new ReportService(svc.Data, svc).InvoiceLines(client, from, to);
        if (lines.Count == 0) { out_.WriteLine($"No finished entries for '{client.Name}' in this range."); return 0; }
        var total = lines.Sum(l => l.Amount);
        if (opts.TryGetValue("csv", out var csvPath))
        {
            CsvExporter.WriteInvoice(csvPath, client, lines, from, to);
            out_.WriteLine($"Invoice for '{client.Name}': {lines.Count} entries, {total:0.00} {client.Currency} — wrote {csvPath}");
            return 0;
        }
        out_.WriteLine($"Invoice — {client.Name} ({(from is null ? "all time" : $"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}")})");
        foreach (var line in lines)
            out_.WriteLine($"{line.Entry.Start:yyyy-MM-dd HH:mm}  {line.Project.Name,-20} {FormatDuration(line.Entry.Duration(line.Entry.End!.Value)),-8} {line.Amount:0.00} {client.Currency}  {line.Entry.Description}");
        out_.WriteLine($"Total: {total:0.00} {client.Currency}");
        return 0;
    }

    // ----- date handling -----

    /// <summary>Parses "2026-11-05 09:00", "2026-11-05T09:00", "2026-11-05" or a bare
    /// "09:00" (today). Times are taken in local time.</summary>
    public static DateTimeOffset ParseDateTime(string value)
    {
        var formats = new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd", "HH:mm" };
        if (DateTimeOffset.TryParseExact(value, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
        {
            if (value.Length == 5) // bare "HH:mm" parsed against today's date by TryParseExact
                parsed = new DateTimeOffset(DateTime.Today.Add(parsed.TimeOfDay), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
            return parsed;
        }
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out parsed)) return parsed;
        throw new TrackerException($"Cannot parse time '{value}'. Use \"yyyy-MM-dd HH:mm\" or \"HH:mm\" for today.");
    }

    /// <summary>--from/--to dates, or --week (Mon–Sun) / --month shortcuts.
    /// Date-only bounds cover the whole day; the range end is exclusive.</summary>
    private static (DateTimeOffset? From, DateTimeOffset? To) ParseRange(Dictionary<string, string> opts)
    {
        if (opts.ContainsKey("week"))
        {
            var today = DateTime.Today;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            return (StartOfDay(monday), StartOfDay(monday.AddDays(7)));
        }
        if (opts.ContainsKey("month"))
        {
            var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            return (StartOfDay(first), StartOfDay(first.AddMonths(1)));
        }
        DateTimeOffset? from = opts.TryGetValue("from", out var f) ? StartOfDay(ParseDateTime(f).Date) : null;
        DateTimeOffset? to = opts.TryGetValue("to", out var t) ? StartOfDay(ParseDateTime(t).Date.AddDays(1)) : null;
        return (from, to);
    }

    private static DateTimeOffset StartOfDay(DateTime date) =>
        new(date, TimeZoneInfo.Local.GetUtcOffset(date));

    // ----- helpers -----

    /// <summary>Parses --key value / --key=value pairs and bare --flags.</summary>
    public static Dictionary<string, string> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new TrackerException($"Unexpected argument '{arg}'.");
            var key = arg[2..];
            if (key.Contains('='))
            {
                var parts = key.Split('=', 2);
                opts[parts[0]] = parts[1];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                opts[key] = args[++i];
            }
            else
            {
                opts[key] = "true";
            }
        }
        return opts;
    }

    private static decimal RequireDecimal(Dictionary<string, string> opts, string key)
    {
        if (!opts.TryGetValue(key, out var raw) || !decimal.TryParse(raw, out var value))
            throw new TrackerException($"Missing or invalid --{key} <number>.");
        return value;
    }

    public static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.FromMinutes(1)) return $"{(int)d.TotalSeconds}s";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m";
        return $"{(int)d.TotalHours}h {d.Minutes:00}m";
    }

    private static void PrintHelp(TextWriter out_)
    {
        out_.WriteLine("""
            TimeLedger — CLI time tracker for freelancers.

            Usage: timeledger [--data <path>] <command> [options]

            Commands:
              client add <name> --rate <n> [--currency CODE]   Add a client
              client list                                       List clients
              project add <client> <name> [--rate n]            Add a project
              project list [--client <name>] [--all]            List projects
              start <project> [--desc "..."] [--tags a,b] [--no-bill]
                                                                Start the timer
              stop [--desc "..."]                               Stop the timer
              status                                            Show the running timer
              log <project> <start> <end> [--desc "..."]       Log a finished block
              entries [--project <name>] [--client <name>] [--from <date>] [--to <date>]
                                                                List time entries
              report [--client <name>] [--from <date>] [--to <date>] [--week|--month]
                                                                Billable summary by project
              invoice --client <name> [--from <date>] [--to <date>] [--week|--month] [--csv <path>]
                                                                Per-entry invoice, optionally as CSV
            """);
    }
}
