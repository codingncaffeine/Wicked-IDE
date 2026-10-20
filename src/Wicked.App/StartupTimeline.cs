using System.Diagnostics;
using System.Globalization;
using System.Text;
using Wicked.Core;

namespace Wicked.App;

/// <summary>
/// Where the time to the first frame goes, logged once when that frame is drawn: each step since the one before it, from
/// <see cref="Program.Main"/>. The UI-thread steps are the ones the stall watch reports as one long wait at startup.
/// </summary>
internal static class StartupTimeline
{
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static readonly List<(string Step, long At)> Steps = [];
    private static bool _done;

    /// <summary>Touches the clock so it starts in <c>Main</c>, not at the first mark.</summary>
    public static void Begin() => _ = Started;

    public static void Mark(string step)
    {
        if (_done) return;
        lock (Steps) Steps.Add((step, Stopwatch.GetTimestamp()));
    }

    /// <summary>The last mark: logs the timeline and stops recording.</summary>
    public static void FirstFrame()
    {
        if (_done) return;
        Mark("first frame");
        _done = true;
        var line = new StringBuilder("Startup:");
        var previous = Started;
        lock (Steps)
        {
            foreach (var (step, at) in Steps)
            {
                line.Append(CultureInfo.InvariantCulture, $" {step} +{Stopwatch.GetElapsedTime(previous, at).TotalMilliseconds:0.0} ms,");
                previous = at;
            }
        }

        line.Append(CultureInfo.InvariantCulture, $" total {Stopwatch.GetElapsedTime(Started, previous).TotalMilliseconds:0} ms");
        Log.Info(line.ToString());
    }
}
