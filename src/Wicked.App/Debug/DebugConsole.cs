using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core.Debug;

namespace Wicked.App.Debug;

/// <summary>A line of the Debug Console and the kind of text it is (stdout, stderr, console, important, input, result, error).</summary>
public sealed partial class ConsoleLine(string category, string text, string? path = null, int line = 0) : ObservableObject
{
    public string Category { get; } = category;

    [ObservableProperty]
    private string _text = text;

    /// <summary>Where the line says it came from, when the adapter told (a click opens it).</summary>
    public string? Path { get; } = path;

    public int Line { get; } = line;

    public bool IsError => Category is "stderr" or "error";

    public bool IsInput => Category == "input";

    public bool IsResult => Category == "result";

    public bool IsMuted => Category is "console" or "telemetry";

    public bool IsImportant => Category == "important";

    public bool HasSource => Path is not null;

    public string SourceText => Path is null ? "" : $"{System.IO.Path.GetFileName(Path)}:{Line}";
}

/// <summary>
/// The Debug Console: what the program and the adapters write, and the box underneath that evaluates an expression in the
/// frame being looked at. Text arrives from any thread and reaches the list in batches at most every 50 ms; a line the program
/// has not finished (no newline yet) is completed by the next piece of the same kind.
/// </summary>
public sealed partial class DebugConsoleViewModel : ObservableObject
{
    public const int LineLimit = 10_000;

    private readonly ConcurrentQueue<(string Category, string Text, string? Path, int Line)> _pending = new();
    private readonly Action<Action> _post;
    private readonly List<string> _history = [];
    private int _historyAt;
    private int _flushPosted;
    private ConsoleLine? _open;

    public DebugConsoleViewModel(Action<Action>? post = null, Func<string, Task>? evaluate = null)
    {
        _post = post ?? (a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background));
        Evaluate = evaluate;
    }

    public ObservableCollection<ConsoleLine> Lines { get; } = [];

    /// <summary>Opens a file at a line (1-based) when a line that names its source is double-clicked.</summary>
    public Func<string, int, Task>? OpenSource { get; set; }

    /// <summary>Runs an expression the user typed; the shell sets it (the controller evaluates in the selected frame).</summary>
    public Func<string, Task>? Evaluate { get; set; }

    [ObservableProperty]
    private string _input = "";

    /// <summary>Writes text from any thread.</summary>
    public void Write(string category, string text, string? path = null, int line = 0)
    {
        if (text.Length == 0) return;
        _pending.Enqueue((category, text, path, line));
        if (Interlocked.Exchange(ref _flushPosted, 1) == 1) return;
        _ = Task.Delay(50).ContinueWith(_ => _post(Flush), TaskScheduler.Default);
    }

    /// <summary>Everything written so far goes into the list. UI thread.</summary>
    public void Flush()
    {
        Volatile.Write(ref _flushPosted, 0);
        while (_pending.TryDequeue(out var item))
        {
            var (category, text, path, line) = item;
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var parts = text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                if (last && parts[i].Length == 0) break; // the text ended with a newline
                if (_open is { } open && open.Category == category && i == 0)
                {
                    open.Text += parts[i];
                    _open = last ? open : null;
                    continue;
                }

                var added = new ConsoleLine(category, parts[i], path, line);
                Lines.Add(added);
                _open = last ? added : null;
            }
        }

        if (Lines.Count > LineLimit) for (var i = 0; i < 1000; i++) Lines.RemoveAt(0);
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        _open = null;
        Lines.Clear();
    }

    public string AllText => string.Join('\n', Lines.Select(l => l.Text));

    // ── The box ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Enter in the box: the expression is shown as typed, evaluated, remembered.</summary>
    public async Task SubmitAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0) return;
        Input = "";
        if (_history.Count == 0 || _history[^1] != text) _history.Add(text);
        _historyAt = _history.Count;
        Write("input", text + "\n");
        Flush();
        if (Evaluate is { } evaluate) await evaluate(text);
    }

    /// <summary>Up (-1) and Down (+1) in the box walk the expressions typed before.</summary>
    public void Recall(int direction)
    {
        if (_history.Count == 0) return;
        _historyAt = Math.Clamp(_historyAt + direction, 0, _history.Count);
        Input = _historyAt >= _history.Count ? "" : _history[_historyAt];
    }

    public static string CategoryOf(DapOutput output) => output.Category switch
    {
        "stdout" or "stderr" or "console" or "important" or "telemetry" => output.Category,
        _ => "console",
    };
}
