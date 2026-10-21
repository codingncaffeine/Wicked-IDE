using Wicked.Cli;
using Wicked.Core;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
var app = new CliApp(WickedPaths.ForCurrentUser(), Console.Out, Console.Error, Console.In, CliApp.DefaultHooks());
try
{
    return await app.RunAsync(args, stop.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
