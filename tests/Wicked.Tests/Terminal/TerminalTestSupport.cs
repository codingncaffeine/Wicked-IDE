using System.Buffers;
using System.Text;
using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

/// <summary>A work folder under the repository's gitignored scratch folder, removed afterwards.</summary>
internal sealed class ScratchDir : IDisposable
{
    public ScratchDir()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Wicked.slnx"))) root = System.IO.Path.GetDirectoryName(root);
        Root = Path.Combine(root ?? Path.GetTempPath(), "scratch", "terminal-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A program run on a pty with its output collected, for tests that watch what a real terminal would show.</summary>
internal sealed class PtyRun : IDisposable
{
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    public PtyRun(string file, string[] args, string cwd, ScratchDir home, Dictionary<string, string?>? extra = null, int columns = 80, int rows = 24)
    {
        // A profile of its own: the program never reads the real home or shell start-up files.
        var env = Pty.BuildEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = home.Root,
            ["ENV"] = Path.Combine(home.Root, "no-such-rc"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home.Root, "config"),
            ["PS1"] = "$ ",
            ["DBUS_SESSION_BUS_ADDRESS"] = null,
            ["WAYLAND_DISPLAY"] = null,
            ["DISPLAY"] = null,
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(home.Root, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        }.Concat(extra ?? []));
        Session = Pty.Start(new PtyOptions(file, args, cwd, env, columns, rows), (buffer, length) =>
        {
            lock (_gate) _text.Append(Encoding.UTF8.GetString(buffer, 0, length));
            ArrayPool<byte>.Shared.Return(buffer);
        });
    }

    public IPty Session { get; }

    public string Output
    {
        get
        {
            lock (_gate) return _text.ToString();
        }
    }

    /// <summary>The exit code, waiting at most <paramref name="seconds"/>.</summary>
    public async Task<int> ExitAsync(double seconds = 20)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        return await Session.Exited.WaitAsync(timeout.Token);
    }

    public async Task<bool> WaitForAsync(string text, double seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (Output.Contains(text, StringComparison.Ordinal)) return true;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return Output.Contains(text, StringComparison.Ordinal);
    }

    public void Dispose() => Session.Dispose();
}
