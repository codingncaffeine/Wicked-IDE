using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Wicked.App.Terminal;
using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

/// <summary>
/// The acceptance run: a hundred megabytes of coloured log text through a real pty (<c>cat</c>) into the emulator, with the
/// view attached in a headless window. The UI thread's work per frame is timed from the view's own instrument; its budget
/// is 4 ms. WICKED_TERMINAL_PERF_MB changes the size (default 100).
/// </summary>
public sealed class TerminalPerfTests
{
    private static int Megabytes => int.TryParse(Environment.GetEnvironmentVariable("WICKED_TERMINAL_PERF_MB"), CultureInfo.InvariantCulture, out var mb) && mb > 0 ? mb : 100;

    /// <summary>Log-like output: timestamps, levels, a colour or two per line, some long lines, some blank ones.</summary>
    private static void WriteLog(string path, long bytes)
    {
        var random = new Random(42);
        string[] levels = ["\u001b[32mINFO\u001b[0m ", "\u001b[33mWARN\u001b[0m ", "\u001b[31;1mERROR\u001b[0m", "\u001b[90mDEBUG\u001b[0m"];
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var text = new StringBuilder();
        long written = 0;
        var n = 0L;
        while (written < bytes)
        {
            text.Clear();
            for (var i = 0; i < 4000; i++, n++)
            {
                text.Append("2026-10-20 12:").Append((n / 60 % 60).ToString("00", CultureInfo.InvariantCulture)).Append(':').Append((n % 60).ToString("00", CultureInfo.InvariantCulture)).Append(' ');
                text.Append(levels[random.Next(levels.Length)]).Append(" [worker-").Append(random.Next(16)).Append("] request ").Append(n).Append(" handled in ").Append(random.Next(900)).Append(" ms");
                if (n % 17 == 0) text.Append(" \u001b[4mhttps://example.com/item/").Append(n).Append("\u001b[0m");
                if (n % 53 == 0) text.Append(" café 漢字 ").Append('x', random.Next(200));
                text.Append('\n');
                if (n % 211 == 0) text.Append('\n');
            }

            var chunk = Encoding.UTF8.GetBytes(text.ToString());
            stream.Write(chunk);
            written += chunk.Length;
        }
    }

    private static string? ResultsFile()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Wicked.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir is null ? null : Path.Combine(dir, "scratch", "terminal-perf-results.txt");
    }

    private static double Percentile(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    [Fact]
    public void TheEmulatorAloneParsesAHundredMegabytesQuickly()
    {
        using var dir = new ScratchDir();
        var path = Path.Combine(dir.Root, "big.log");
        WriteLog(path, Megabytes * 1024L * 1024L);
        var data = File.ReadAllBytes(path);
        var emulator = new TerminalEmulator(120, 40);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < data.Length; i += 65536) emulator.Write(data.AsSpan(i, Math.Min(65536, data.Length - i)));
        watch.Stop();
        var mbps = data.Length / 1048576.0 / watch.Elapsed.TotalSeconds;
        var line = $"emulator alone: {data.Length / 1048576.0:0} MB in {watch.Elapsed.TotalSeconds:0.00} s = {mbps:0} MB/s, scrollback {emulator.ScrollbackCount} lines";
        TestContext.Current.SendDiagnosticMessage(line);
        if (ResultsFile() is { } file) File.AppendAllText(file, line + "\n");
        Assert.Equal(10_000, emulator.ScrollbackCount);
        Assert.True(mbps > 40, line);
    }

    [Fact]
    public async Task CatOfAHundredMegabytesNeverStallsTheUiThread()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/cat");
        using var dir = new ScratchDir();
        var path = Path.Combine(dir.Root, "big.log");
        WriteLog(path, Megabytes * 1024L * 1024L);
        var size = new FileInfo(path).Length;

        var fonts = await TerminalFonts.GetAsync(14);
        var env = Pty.BuildEnvironment(new Dictionary<string, string?> { ["HOME"] = dir.Root, ["DBUS_SESSION_BUS_ADDRESS"] = null, ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = null });
        var session = new TerminalSession(new TerminalSessionOptions { Launch = new PtyOptions("/bin/cat", [path], dir.Root, env, 120, 40) });
        var slices = new List<double>();
        var paints = new List<double>();
        var frames = 0;
        double seconds = 0;
        long processed = 0;
        int scrollback = 0;
        string lastLine = "";

        await Ui.Run(() =>
        {
            var pane = new TerminalPane(fonts);
            var window = new Window { Width = 1100, Height = 700, Content = pane };
            window.Show();
            pane.View.Attach(session);
            pane.View.Focus();
            Ui.Pump(0.3);
            TerminalView.UiSlice = ms => slices.Add(ms);
            TerminalDrawOperation.PaintSlice = ms => paints.Add(ms);
            session.FrameReady += _ => Interlocked.Increment(ref frames);
            try
            {
                // Everything a frame costs the UI thread is the view's own work; the pump's sleeps are the harness, not the app.
                var watch = Stopwatch.StartNew();
                session.Start();
                while (session.ExitCode is null && watch.Elapsed < TimeSpan.FromSeconds(300)) Ui.Pump(0.05);
                Ui.Pump(0.5);
                seconds = watch.Elapsed.TotalSeconds - 0.5;
                processed = session.BytesProcessed;
                scrollback = pane.View.Frame?.ScrollbackLines ?? 0;
                var frame = pane.View.Frame!;
                lastLine = string.Join('\n', frame.Lines.Select(r => string.Concat(r.Cells.Select(c => frame.TextOf(c))).TrimEnd()).Where(t => t.Length > 0).TakeLast(3));
            }
            finally
            {
                TerminalView.UiSlice = null;
                TerminalDrawOperation.PaintSlice = null;
                session.Dispose();
                window.Close();
            }

            return 0;
        });

        slices.Sort();
        paints.Sort();
        var mbps = processed / 1048576.0 / seconds;
        var report = $"cat through a pty with the view attached: {size / 1048576.0:0} MB file, {processed / 1048576.0:0} MB through the terminal in {seconds:0.0} s = {mbps:0} MB/s; " +
                     $"{frames} frames built, {slices.Count} UI-thread slices: max {slices.LastOrDefault():0.00} ms, p99 {Percentile(slices, 0.99):0.00} ms, p50 {Percentile(slices, 0.5):0.00} ms; " +
                     $"Skia paint (render thread in the app, inline in this harness): max {paints.LastOrDefault():0.00} ms, p99 {Percentile(paints, 0.99):0.00} ms";
        TestContext.Current.SendDiagnosticMessage(report);
        if (ResultsFile() is { } file) File.AppendAllText(file, report + "\n");

        Assert.True(processed >= size, $"{processed} of {size} bytes reached the emulator");
        Assert.Equal(0, session.ExitCode);
        Assert.Equal(10_000, scrollback);
        Assert.Contains("request", lastLine, StringComparison.Ordinal);
        Assert.True(slices.Count > 10, report);
        Assert.True(Percentile(slices, 0.99) < 4, report);
        Assert.True(slices.Last() < 12, report);
    }
}
