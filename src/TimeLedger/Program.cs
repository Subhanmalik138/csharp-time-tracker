namespace TimeLedger;

/// <summary>Entry point. Full command handling lands in the next commit;
/// this stub keeps the scaffold buildable and documents the command surface.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("TimeLedger — CLI time tracker for freelancers.");
        Console.WriteLine("Commands: client, project, start, stop, status, log, entries, report, invoice.");
        Console.WriteLine("Run `timeledger <command> --help` for details.");
        return 0;
    }
}
