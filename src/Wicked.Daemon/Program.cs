using Wicked.Core;
using Wicked.Daemon;

if (args.Any(a => a is "--version" or "-V"))
{
    Console.WriteLine($"wickedd {BuildInfo.Stamp}");
    Console.WriteLine(BuildInfo.Licence);
    return 0;
}

if (args.Any(a => a is "--help" or "-h"))
{
    Console.WriteLine("""
        Usage: wickedd [options]

        The Wicked daemon: keeps the model store, downloads, engines and updates running, and serves the OpenAI and
        Anthropic APIs on http://127.0.0.1:1337 (settings.json and Settings > Server change that). One runs per
        profile; WICKED_HOME chooses another profile.

          --port N    Serve the API on port N for this run.
          --serve     Serve the API even if settings.json has it switched off.
          --version   Print the version and licence, then exit.
        """);
    return 0;
}

int? port = null;
var serve = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed) && parsed is >= 0 and <= 65535)
    {
        port = parsed;
        i++;
    }
    else if (args[i] == "--serve")
    {
        serve = true;
    }
    else
    {
        Console.Error.WriteLine($"Unknown option: {args[i]}. Try wickedd --help.");
        return 2;
    }
}

var paths = WickedPaths.ForCurrentUser();
paths.EnsureCreated();
Log.Initialize("wickedd", paths.Logs);
try
{
    await using var host = await DaemonHost.StartAsync(paths, CancellationToken.None, new DaemonOptions { Port = port, ForceGateway = serve });
    await host.WaitForShutdownAsync(CancellationToken.None);
    Log.Info("wickedd stopping");
    return 0;
}
catch (DaemonAlreadyRunningException ex)
{
    Log.Info(ex.Message);
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(Log.Crash("wickedd", ex));
    return 1;
}
finally
{
    Log.Flush(TimeSpan.FromSeconds(2));
}
