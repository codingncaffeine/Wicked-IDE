using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using Wicked.App.Shell;
using Wicked.Core.Tasks;

namespace Wicked.App.Tasks;

/// <summary>
/// A task's output in a channel of the Output panel, one channel per task. Lines arrive from the task's worker, colour
/// codes are removed here (a terminal would keep them), and the panel is brought forward as the task's presentation says.
/// This is the one place that decides where task output goes: when the terminal lands, an <see cref="ITaskOutput"/> that
/// opens a terminal session replaces this class where the shell makes it.
/// </summary>
public sealed class OutputPanelTaskOutput : ITaskOutput
{
    private readonly OutputLog _log;
    private readonly Action<string> _reveal;
    private readonly Action<Action> _post;
    private readonly string _prefix;

    /// <param name="log">The Output panel's channels.</param>
    /// <param name="reveal">Brings a channel forward by name (UI thread).</param>
    /// <param name="prefix">What the channel's name starts with ("Task - ").</param>
    /// <param name="post">Runs something on the UI thread.</param>
    public OutputPanelTaskOutput(OutputLog log, Action<string> reveal, string prefix = "Task - ", Action<Action>? post = null)
    {
        _log = log;
        _reveal = reveal;
        _prefix = prefix;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
    }

    /// <summary>The channel a label writes to.</summary>
    public string ChannelName(string label) => _prefix + label;

    public ITaskSink Open(string label, TaskPresentation presentation)
    {
        var name = ChannelName(label);
        var channel = _log.Channel(name);
        if (presentation.Clear) _post(channel.Clear);
        if (presentation.Reveal == RevealMode.Always) _post(() => _reveal(name));
        return new Sink(_log, name, presentation, () => _post(() => _reveal(name)));
    }

    private sealed class Sink(OutputLog log, string name, TaskPresentation presentation, Action reveal) : ITaskSink
    {
        public void Write(string line) => log.Write(name, AnsiText.Strip(line));

        public void Close(TaskOutcome outcome)
        {
            var failed = outcome.Error is not null || outcome.ExitCode is not (null or 0) || outcome.Errors > 0;
            if (presentation.Reveal == RevealMode.Silent && failed) reveal();
        }
    }
}

/// <summary>An observable list that can be replaced in one notification (thousands of rows at once).</summary>
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void Reset(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
