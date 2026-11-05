namespace TimeLedger;

public static class Program
{
    public static int Main(string[] args)
    {
        var store = new JsonStore(JsonStore.DefaultPath());
        return Cli.Run(args, store, Console.Out, Console.Error);
    }
}
