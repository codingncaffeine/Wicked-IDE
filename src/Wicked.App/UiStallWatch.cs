using System.Diagnostics;
using Avalonia.Threading;
using Wicked.Core;

namespace Wicked.App;

/// <summary>
/// Measures how long the window's thread takes to answer. A thread of its own posts a ping to the UI thread and times the
/// answer; one that comes back later than the threshold means the UI thread was busy with something for that long, and
/// is logged. Normally it pings ten times a second and reports stalls over 50 ms; with <c>WICKED_UI_TRACE=1</c> it pings
/// every 2 ms and reports anything over 4 ms, the budget the house rule allows the UI thread for one piece of work.
/// </summary>
internal sealed class UiStallWatch
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _threshold;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private long _stalls;
    private long _worstTicks;

    private UiStallWatch(bool trace)
    {
        _interval = trace ? TimeSpan.FromMilliseconds(2) : TimeSpan.FromMilliseconds(100);
        _threshold = trace ? TimeSpan.FromMilliseconds(4) : TimeSpan.FromMilliseconds(50);
        _thread = new Thread(Run) { IsBackground = true, Name = "Wicked UI stall watch" };
    }

    public static UiStallWatch? Current { get; private set; }

    /// <summary>How many times the UI thread answered later than the threshold.</summary>
    public long Stalls => Interlocked.Read(ref _stalls);

    /// <summary>The longest wait for an answer so far.</summary>
    public TimeSpan Worst => TimeSpan.FromTicks(Interlocked.Read(ref _worstTicks));

    public static UiStallWatch Start(bool trace)
    {
        var watch = new UiStallWatch(trace);
        Current = watch;
        watch._thread.Start();
        Log.Info($"UI stall watch: reporting waits over {watch._threshold.TotalMilliseconds:0} ms");
        return watch;
    }

    public void Stop() => _stopping = true;

    private void Run()
    {
        using var answered = new ManualResetEventSlim();
        while (!_stopping)
        {
            answered.Reset();
            var sent = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(answered.Set, DispatcherPriority.Normal);
            if (!answered.Wait(_threshold))
            {
                // Late: wait for the answer (or the end of the app) and report how long the thread was away.
                while (!answered.Wait(TimeSpan.FromMilliseconds(250)) && !_stopping)
                {
                }

                if (_stopping) return;
                var took = Stopwatch.GetElapsedTime(sent);
                Interlocked.Increment(ref _stalls);
                if (took.Ticks > Interlocked.Read(ref _worstTicks)) Interlocked.Exchange(ref _worstTicks, took.Ticks);
                Log.Warn($"UI thread busy for {took.TotalMilliseconds:0.0} ms");
            }

            Thread.Sleep(_interval);
        }
    }
}
