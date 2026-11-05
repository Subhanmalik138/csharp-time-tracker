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
            """);
    }
}
