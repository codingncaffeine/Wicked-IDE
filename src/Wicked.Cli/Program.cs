using Wicked.Core;

if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
{
    Console.WriteLine("""
        Usage: wicked-cli <command>

          status      Say whether the daemon is running, and which build.
          paths       Print the folders Wicked uses for this profile.
          --version   Print the version and licence, then exit.
        """);
    return args.Length == 0 ? 2 : 0;
}

if (args.Any(a => a is "--version" or "-V"))
{
    Console.WriteLine($"wicked-cli {BuildInfo.Stamp}");
    Console.WriteLine(BuildInfo.Licence);
    return 0;
}

var paths = WickedPaths.ForCurrentUser();
switch (args[0])
{
    case "status":
    {
        using var client = new DaemonClient(paths.DaemonSocket, TimeSpan.FromSeconds(3));
        var health = await client.GetHealthAsync(CancellationToken.None);
        if (health is null)
        {
            Console.WriteLine($"The daemon is not running (no answer on {paths.DaemonSocket}).");
            return 1;
        }

        Console.WriteLine($"The daemon is running: wickedd {health.Version}, process {health.ProcessId}, since {health.StartedAt:yyyy-MM-dd HH:mm:ss}.");
        return 0;
    }

    case "paths":
        Console.WriteLine($"config   {paths.Config}");
        Console.WriteLine($"data     {paths.Data}");
        Console.WriteLine($"state    {paths.State}");
        Console.WriteLine($"cache    {paths.Cache}");
        Console.WriteLine($"runtime  {paths.Runtime}");
        if (paths.Isolated) Console.WriteLine($"(isolated by {WickedPaths.HomeVariable})");
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command: {args[0]}. Try wicked-cli --help.");
        return 2;
}
