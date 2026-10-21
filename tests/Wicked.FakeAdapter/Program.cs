using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Wicked.FakeAdapter;

// Runs the scripted debug adapter: over standard input and output, or (with --port N) as a TCP server for one client.
// Flags: --no-terminate (the adapter does not offer a terminate request), --restart (it offers restart).

var arguments = args.ToList();
var options = new ScriptedAdapterOptions
{
    SupportsTerminate = !arguments.Contains("--no-terminate"),
    SupportsRestart = arguments.Contains("--restart"),
};
var portIndex = arguments.IndexOf("--port");
if (portIndex >= 0)
{
    var listener = new TcpListener(IPAddress.Loopback, int.Parse(arguments[portIndex + 1], CultureInfo.InvariantCulture));
    listener.Start();
    Console.Error.WriteLine("listening");
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            using (client)
            {
                var stream = client.GetStream();
                await new ScriptedAdapter(stream, stream, options).RunAsync(CancellationToken.None);
            }
        });
    }
}

await new ScriptedAdapter(Console.OpenStandardInput(), Console.OpenStandardOutput(), options).RunAsync(CancellationToken.None);
