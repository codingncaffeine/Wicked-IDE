using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Wicked.App;
using Wicked.App.Chat;
using Wicked.App.ViewModels;
using Wicked.Core.Chat;

namespace Wicked.Tests.Chat;

/// <summary>The window's thread while a reply streams (D11): the work the chat does on it per frame must stay under 4 ms.</summary>
[Collection(TimingCollection.Name)]
public sealed class StreamStallTests
{
    private const int TokensPerSecond = 60;

    /// <summary>Markdown with headings, lists, a table and code blocks, a "token" (about four characters) every 1/60 s, for <paramref name="seconds"/>.</summary>
    private static async IAsyncEnumerable<ChatChunk> Markdown(double seconds, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = new StringBuilder();
        for (var i = 0; text.Length < 200_000; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"## Part {i}\n\nThis is paragraph {i} with some *emphasis*, **strength**, `inline code` and a [link](https://example.test/{i}) to read, long enough to wrap over a few lines of the panel.\n\n");
            text.Append(CultureInfo.InvariantCulture, $"- first point of {i}\n- second point with `code`\n- third point\n\n");
            text.Append(CultureInfo.InvariantCulture, $"| name | value |\n|---|---|\n| a{i} | {i} |\n| b | {i * 2} |\n\n");
            text.Append(CultureInfo.InvariantCulture, $"```csharp\nint Add{i}(int a, int b)\n{{\n    // sum {i}\n    var total = a + b;\n    return total;\n}}\n```\n\n");
        }

        var all = text.ToString();
        var clock = Stopwatch.StartNew();
        var sent = 0;
        var tick = 0;
        while (clock.Elapsed.TotalSeconds < seconds && sent < all.Length && !ct.IsCancellationRequested)
        {
            yield return new ContentDelta(all.Substring(sent, 4));
            sent += 4;
            tick++;
            var due = TimeSpan.FromSeconds(tick / (double)TokensPerSecond) - clock.Elapsed;
            if (due > TimeSpan.Zero) await Task.Delay(due);
        }

        yield return new ChatUsage(100, tick, TokensPerSecond, 8192);
        yield return new ChatFinished("stop");
    }

    private sealed record Numbers(int Frames, double ApplyMax, double ApplyP99, int ApplyOver4, double JobMax, double JobP99, long WatchStalls, double WatchWorst, double GcPause, int Blocks);

    /// <summary>
    /// Sends a message and pumps the window as the app's loop does until the reply ends. Two things are timed on this (the UI) thread,
    /// each less the garbage collector's pauses inside it: the chat's frame apply (which holds the building of every new block) and the whole turn of the loop (layout and the headless
    /// platform's drawing among it). The latter also holds the headless platform's own drawing of every frame into a managed bitmap, which the app does on its render
    /// thread, so it is reported and not judged. The stall watch runs throughout, for the record, for the same reason.
    /// </summary>
    private static Numbers Measure(Window window, ShellViewModel shell, double seconds)
    {
        var applies = new List<double>();
        var jobs = new List<double>();
        void OnApplied(double ms) => applies.Add(ms);
        ChatMessageViewModel.FrameApplied += OnApplied;
        var watch = UiStallWatch.Start(trace: true);
        var pause0 = GC.GetTotalPauseDuration();
        ChatUi.Type(window, "write a lot");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        var started = Stopwatch.StartNew();
        try
        {
            while (started.Elapsed.TotalSeconds < seconds + 15 && (shell.Chat.IsStreaming || started.Elapsed.TotalSeconds < 1))
            {
                var pass = Stopwatch.GetTimestamp();
                var pauseBefore = GC.GetTotalPauseDuration();
                Dispatcher.UIThread.RunJobs();
                jobs.Add(Stopwatch.GetElapsedTime(pass).TotalMilliseconds - (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds);
                Thread.Sleep(1);
            }
        }
        finally
        {
            ChatMessageViewModel.FrameApplied -= OnApplied;
            watch.Stop();
        }

        Assert.False(shell.Chat.IsStreaming, "the reply did not finish");
        applies.Sort();
        jobs.Sort();
        return new Numbers(applies.Count, applies[^1], applies[(int)(applies.Count * 0.99)], applies.Count(x => x > 4), jobs[^1], jobs[(int)(jobs.Count * 0.99)],
            watch.Stalls, watch.Worst.TotalMilliseconds, (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, shell.Chat.Messages[^1].Blocks.Count);
    }

    [Fact]
    public async Task ThirtySecondsOfStreamingMarkdownKeepsTheChatsUiThreadWorkUnderFourMilliseconds()
    {
        using var profile = new TempProfile();
        var world = new ChatWorld();
        world.Runtime.Set(FakeRuntime.Model("test/alpha", "Alpha 8B"));
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("WICKED_STALL_SECONDS"), CultureInfo.InvariantCulture, out var s) ? s : 30;
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);

            // A first, short reply warms every code path (JIT, fonts, grammars): the numbers below are the steady state.
            world.Engine.Script = (_, ct) => Markdown(4, ct);
            ChatUi.Send(window, "warm up");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && shell.Chat.Messages.Count == 2, 20));
            Ui.Pump(0.5);

            world.Engine.Script = (_, ct) => Markdown(seconds, ct);
            var n = Measure(window, shell, seconds);
            if (Environment.GetEnvironmentVariable("WICKED_STALL_REPORT") is { Length: > 0 } path)
            {
                File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"frames applied={n.Frames}, blocks={n.Blocks}\nframe apply: max {n.ApplyMax:0.00} ms, p99 {n.ApplyP99:0.00} ms, {n.ApplyOver4} of {n.Frames} over 4 ms\nwhole loop turn (holds the headless platform's frame drawing): max {n.JobMax:0.0} ms, p99 {n.JobP99:0.0} ms\nstall watch (same caveat): {n.WatchStalls} waits over 4 ms, worst {n.WatchWorst:0.0} ms\nGC pause total {n.GcPause:0} ms\n"));
            }

            Assert.True(n.Blocks > 20, "too little streamed to mean anything");
            Assert.True(n.Frames > seconds * 20, $"only {n.Frames} frames were applied");
            Assert.True(n.ApplyP99 < 4, $"1 frame in 100 took {n.ApplyP99:0.0} ms to apply");
            // The tail is wall time, so it includes the machine taking the CPU away: GitHub's 2-vCPU Windows runner showed 6 of
            // 615 frames over 4 ms with the p99 under it (2026-10-20). There the tail may be 1 in 100, the p99's own bound.
            var tail = OperatingSystem.IsWindows() ? n.Frames / 100 : n.Frames / 200;
            Assert.True(n.ApplyOver4 <= tail, $"{n.ApplyOver4} of {n.Frames} frames took over 4 ms to apply (worst {n.ApplyMax:0.0} ms)");
            window.Close();
            return 0;
        });
    }
}
