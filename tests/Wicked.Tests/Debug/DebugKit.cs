using System.IO.Pipelines;
using System.Text.Json;
using Wicked.Core.Debug;
using Wicked.FakeAdapter;

namespace Wicked.Tests.Debug;

/// <summary>An adapter provider whose adapters are the scripted one, in this process, joined by pipes.</summary>
internal sealed class FakeAdapterProvider(ScriptedAdapterOptions? options = null) : IDebugAdapterProvider
{
    public List<ScriptedAdapter> Adapters { get; } = [];

    public int Resolved { get; private set; }

    public Task<AdapterEndpoint> ResolveAsync(string type, JsonElement configuration, CancellationToken ct)
    {
        Resolved++;
        return Task.FromResult<AdapterEndpoint>(new Endpoint(this));
    }

    private sealed class Endpoint(FakeAdapterProvider owner) : AdapterEndpoint
    {
        public override Task<AdapterChannel> OpenAsync(CancellationToken ct)
        {
            var toAdapter = new Pipe();
            var toClient = new Pipe();
            var adapter = new ScriptedAdapter(toAdapter.Reader.AsStream(), toClient.Writer.AsStream(), owner._options);
            lock (owner.Adapters) owner.Adapters.Add(adapter);
            _ = Task.Run(async () =>
            {
                await adapter.RunAsync(CancellationToken.None);
                await toClient.Writer.CompleteAsync();
            });
            return Task.FromResult(new AdapterChannel(toClient.Reader.AsStream(), toAdapter.Writer.AsStream(), new Closer(toAdapter)));
        }
    }

    private readonly ScriptedAdapterOptions? _options = options;

    private sealed class Closer(Pipe toAdapter) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await toAdapter.Writer.CompleteAsync();
    }
}

/// <summary>Helpers the debugger tests share: waiting for things that happen on other threads, and a program for the scripted adapter to run.</summary>
internal static class DebugKit
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Waits (without blocking a thread for long) until <paramref name="done"/> holds; fails the test with <paramref name="what"/> otherwise.</summary>
    public static async Task WaitAsync(Func<bool> done, string what, double seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>A program file of <paramref name="count"/> lines ("line 1", …), plus any lines in <paramref name="special"/> by number.</summary>
    public static string Program(TempProfile profile, int count = 6, params (int Line, string Text)[] special)
    {
        var lines = Enumerable.Range(1, count).Select(i => special.FirstOrDefault(s => s.Line == i).Text ?? $"statement {i}").ToArray();
        var path = Path.Combine(profile.Root, $"program-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    public static DebugStartRequest Launch(string program, string? name = null, string type = "fake", Action<System.Text.Json.Nodes.JsonObject>? more = null)
    {
        var args = new System.Text.Json.Nodes.JsonObject { ["name"] = name ?? "Fake", ["type"] = type, ["request"] = "launch", ["program"] = program };
        more?.Invoke(args);
        return new DebugStartRequest(name ?? "Fake", type, "launch", JsonSerializer.SerializeToElement(args));
    }

    /// <summary>Records what a session announces, so a test can wait for the next stop.</summary>
    public sealed class Watch
    {
        private int _stops;
        private int _consumed;

        public Watch(DebugSession session)
        {
            Session = session;
            session.Stopped += stop =>
            {
                Last = stop;
                Interlocked.Increment(ref _stops);
            };
            session.Output += o =>
            {
                lock (Output) Output.Add(o);
            };
        }

        public DebugSession Session { get; }

        public DapStop? Last { get; private set; }

        public List<DapOutput> Output { get; } = [];

        public string Text
        {
            get
            {
                lock (Output) return string.Concat(Output.Select(o => o.Text));
            }
        }

        /// <summary>Waits for a stop that has not been waited for yet.</summary>
        public async Task<DapStop> NextStopAsync(double seconds = 10)
        {
            await WaitAsync(() => Volatile.Read(ref _stops) > _consumed, "the next stop", seconds);
            _consumed++;
            await WaitAsync(() => Session.State is DebugSessionState.Stopped or DebugSessionState.Ended, "the stopped state", seconds);
            return Last!;
        }
    }
}
