using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Editor.Model;

namespace Wicked.App.Shell;

/// <summary>The bottom panel's tab ids, for commands and routes that open one.</summary>
public static class PanelIds
{
    public const string Problems = "problems";
    public const string Output = "output";
    public const string Terminal = "terminal";
    public const string DebugConsole = "debug-console";
    public const string ApiLog = "api-log";
    public const string References = "references";
}

/// <summary>
/// One channel of the Output panel ("Language Servers", "Git", "Tasks", …): lines written from any thread, shown in a
/// read-only document that keeps the newest <see cref="LineLimit"/> lines.
/// </summary>
public sealed class OutputChannel
{
    public const int LineLimit = 20_000;

    private readonly ConcurrentQueue<string> _pending = new();

    internal OutputChannel(string name)
    {
        Name = name;
        Document = new Document("") { IsReadOnly = true, KeepsHistory = false };
    }

    public string Name { get; }

    /// <summary>The text shown (touched on the UI thread only).</summary>
    public Document Document { get; }

    internal bool HasPending => !_pending.IsEmpty;

    /// <summary>Adds a line; any thread. It appears at the next flush (within a frame or two).</summary>
    public void Append(string line) => _pending.Enqueue(line);

    /// <summary>The waiting lines written into the document as one edit, the oldest lines dropped past the limit. UI thread.</summary>
    internal void Flush()
    {
        if (_pending.IsEmpty) return;
        var text = new System.Text.StringBuilder();
        while (_pending.TryDequeue(out var line))
        {
            text.Append(line.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n')).Append('\n');
        }

        var doc = Document;
        var rope = doc.Text;
        var followsEnd = doc.Primary.IsEmpty && doc.Primary.Active == rope.Length;
        var changes = new List<TextChange> { new(rope.Length, 0, text.ToString()) };
        var lines = rope.LineCount - 1 + text.ToString().Count(c => c == '\n');
        var drop = lines - LineLimit;
        var dropped = 0;
        if (drop > 0)
        {
            dropped = drop >= rope.LineCount ? rope.Length : rope.LineStart(Math.Min(drop, rope.LineCount - 1));
            if (dropped > 0) changes.Insert(0, new TextChange(0, dropped, ""));
        }

        var end = rope.Length - dropped + text.Length;
        var caret = followsEnd ? end : Math.Max(0, doc.Primary.Active - dropped);
        doc.Write(changes, [Selection.Caret(Math.Min(caret, end))]);
    }

    /// <summary>Clears the channel (its Clear button).</summary>
    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        Document.Write([new TextChange(0, Document.Text.Length, "")], [Selection.Caret(0)]);
    }
}

/// <summary>
/// The Output panel's channels. Writers on any thread queue lines; one flush on the UI thread at most every 50 ms writes
/// them, so a chatty server never costs more than a small edit per frame.
/// </summary>
public sealed partial class OutputLog : ObservableObject
{
    private readonly ConcurrentDictionary<string, OutputChannel> _byName = new();
    private readonly Action<Action> _post;
    private int _flushPosted;

    public OutputLog(Action<Action>? post = null) => _post = post ?? (a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background));

    public ObservableCollection<OutputChannel> Channels { get; } = [];

    [ObservableProperty]
    private OutputChannel? _shown;

    /// <summary>The channel called <paramref name="name"/>, made on first use; any thread.</summary>
    public OutputChannel Channel(string name)
    {
        var channel = _byName.GetOrAdd(name, n => new OutputChannel(n));
        if (!Channels.Contains(channel)) _post(() =>
        {
            if (Channels.Contains(channel)) return;
            Channels.Add(channel);
            Shown ??= channel;
        });
        return channel;
    }

    /// <summary>Writes a line to a channel; any thread.</summary>
    public void Write(string channel, string line)
    {
        Channel(channel).Append(line);
        if (Interlocked.Exchange(ref _flushPosted, 1) == 1) return;
        _ = Task.Delay(50).ContinueWith(_ => _post(Flush), TaskScheduler.Default);
    }

    /// <summary>Every channel's waiting lines written. UI thread.</summary>
    public void Flush()
    {
        Volatile.Write(ref _flushPosted, 0);
        foreach (var channel in _byName.Values) channel.Flush();
    }
}
