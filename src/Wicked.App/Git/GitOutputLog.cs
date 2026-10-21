using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core.Git;

namespace Wicked.App.Git;

/// <summary>One line of the Git output: when, what was run, and how it ended.</summary>
public sealed record GitOutputLine(string Text, bool IsError);

/// <summary>
/// Everything git was asked to do and said back, as lines for the bottom panel's Output tab: each command with its exit
/// code and time, then what it wrote on stderr. Commands finish on worker threads; lines reach <see cref="Lines"/> on the UI
/// thread in batches, so a burst of fifty status reads costs one pass. Mount <see cref="Lines"/> in the Output tab (a list of
/// monospace rows; <c>GitOutputView</c> is one) and call <see cref="Clear"/> from its Clear button.
/// </summary>
public sealed partial class GitOutputLog : ObservableObject
{
    private const int Capacity = 3000;
    private readonly Lock _gate = new();
    private readonly List<GitOutputLine> _pending = [];
    private readonly Action<Action> _post;
    private bool _scheduled;

    public GitOutputLog(Action<Action>? post = null) => _post = post ?? (a => Dispatcher.UIThread.Post(a));

    public ObservableCollection<GitOutputLine> Lines { get; } = [];

    /// <summary>Raised on the UI thread when a command failed, so the shell can light the Output tab.</summary>
    public event Action<GitOutputLine>? ErrorAppended;

    /// <summary>Attaches to a GitEnvironment's command log (call once per environment).</summary>
    public void Follow(GitCommandLog log)
    {
        foreach (var existing in log.Snapshot()) Add(existing);
        log.Appended += Add;
    }

    public void Stop(GitCommandLog log) => log.Appended -= Add;

    private void Add(GitLogEntry entry)
    {
        var repo = Path.GetFileName(entry.Repository.TrimEnd(Path.DirectorySeparatorChar));
        var status = entry.ExitCode == 0 ? "" : $" (exit {entry.ExitCode.ToString(CultureInfo.InvariantCulture)})";
        var head = $"{entry.Time:HH:mm:ss}  [{repo}] {entry.Command}{status}  {entry.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
        List<GitOutputLine> lines = [new GitOutputLine(head, entry.ExitCode != 0)];
        if (entry.Error.Length > 0)
        {
            foreach (var line in entry.Error.Split('\n')) lines.Add(new GitOutputLine("    " + line.TrimEnd('\r'), entry.ExitCode != 0));
        }

        bool schedule;
        lock (_gate)
        {
            _pending.AddRange(lines);
            schedule = !_scheduled;
            _scheduled = true;
        }

        if (schedule) _post(Flush);
    }

    private void Flush()
    {
        List<GitOutputLine> batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            _scheduled = false;
        }

        foreach (var line in batch) Lines.Add(line);
        while (Lines.Count > Capacity) Lines.RemoveAt(0);
        if (batch.Any(l => l.IsError && !l.Text.StartsWith(' '))) ErrorAppended?.Invoke(batch.Last(l => l.IsError));
    }

    public void Clear()
    {
        Lines.Clear();
    }
}
