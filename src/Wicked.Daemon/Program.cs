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

        The Wicked daemon: keeps the model store, downloads, engines and the local API running for the
        window and the command line. One runs per profile; WICKED_HOME chooses another profile.

          --version   Print the version and licence, then exit.
        """);
    return 0;
}

var paths = WickedPaths.ForCurrentUser();
paths.EnsureCreated();
Log.Initialize("wickedd", paths.Logs);
try
{
    await using var host = await DaemonHost.StartAsync(paths, CancellationToken.None);
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
