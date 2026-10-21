using System.Collections.Concurrent;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using Wicked.Core;

namespace Wicked.Editor.Syntax;

/// <summary>A run of one line's text drawn in one colour (0xAARRGGBB) and style.</summary>
public readonly record struct ColoredSpan(int Start, int Length, uint Color, TokenStyle Style);

/// <summary>One line of highlighted code. Immutable.</summary>
public sealed record HighlightedLine(string Text, ColoredSpan[] Spans);

/// <summary>
/// Highlights code that is not a document: the code blocks of a chat reply. One thread of its own owns the grammars (as
/// <see cref="SyntaxService"/> does for the open files), so the UI thread only hands lines over and receives finished
/// results. A session keeps the grammar state at the end of each line it has done, so a block that grows by appending
/// is highlighted from the first line that changed, not from the top.
/// </summary>
public sealed class CodeHighlighter : IDisposable
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<CodeSession> _dirty = new();
    private ThemeName _theme;
    private volatile bool _stopping;
    private volatile bool _themeChanged;

    // The highlighter thread's own.
    private Registry? _registry;
    private ColorTable? _colors;
    private readonly List<CodeSession> _sessions = [];

    public CodeHighlighter(ThemeName theme = ThemeName.DarkPlus)
    {
        _theme = theme;
        _thread = new Thread(Run) { IsBackground = true, Name = "Wicked code blocks", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The code colour theme; open sessions are highlighted again in the new colours.</summary>
    public ThemeName Theme
    {
        get => _theme;
        set
        {
            if (value == _theme) return;
            _theme = value;
            _themeChanged = true;
            _wake.Set();
        }
    }

    /// <summary>
    /// A session for one code block. <paramref name="done"/> is called on the highlighter's thread with the lines it has
    /// highlighted, for the newest text asked for; the caller marshals it where it is needed.
    /// </summary>
    public CodeSession Open(string? language, Action<IReadOnlyList<HighlightedLine>> done) => new(this, language, done);

    internal void Enqueue(CodeSession session)
    {
        _dirty.Enqueue(session);
        _wake.Set();
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            _registry = new Registry(Languages.Options);
            _registry.SetTheme(Languages.Registry.LoadTheme(_theme));
            _colors = ColorTable.From(_registry.GetTheme());
        }
        catch (Exception ex)
        {
            Log.Error("Code block highlighting could not start", ex);
            return;
        }

        while (!_stopping)
        {
            if (_themeChanged)
            {
                _themeChanged = false;
                _registry.SetTheme(Languages.Registry.LoadTheme(_theme));
                _colors = ColorTable.From(_registry.GetTheme());
                foreach (var session in _sessions.ToArray()) session.Restart();
            }

            if (!_dirty.TryDequeue(out var next))
            {
                _wake.WaitOne(TimeSpan.FromSeconds(5));
                continue;
            }

            try
            {
                if (next.Step(_registry, _colors) && !_sessions.Contains(next)) _sessions.Add(next);
                _sessions.RemoveAll(s => s.Closed);
            }
            catch (Exception ex)
            {
                Log.Error("Highlighting a code block failed; it stays plain", ex);
                next.Close();
            }
        }
    }
}

/// <summary>One code block's highlighting. <see cref="Update"/> and <see cref="Close"/> may be called from any thread.</summary>
public sealed class CodeSession
{
    private readonly CodeHighlighter _owner;
    private readonly string? _language;
    private readonly Action<IReadOnlyList<HighlightedLine>> _done;
    private string[]? _latest;
    private volatile bool _closed;
    private int _queued;

    // The highlighter thread's own.
    private IGrammar? _grammar;
    private bool _grammarLoaded;
    private string[] _lines = [];
    private readonly List<HighlightedLine> _result = [];
    private readonly List<IStateStack?> _ends = [];

    internal CodeSession(CodeHighlighter owner, string? language, Action<IReadOnlyList<HighlightedLine>> done)
    {
        _owner = owner;
        _language = language;
        _done = done;
    }

    internal bool Closed => _closed;

    /// <summary>The block's lines so far; the highlighter takes the newest and skips the ones in between.</summary>
    public void Update(string[] lines)
    {
        Volatile.Write(ref _latest, lines);
        if (Interlocked.Exchange(ref _queued, 1) == 0) _owner.Enqueue(this);
    }

    public void Close() => _closed = true;

    internal void Restart()
    {
        _lines = [];
        _result.Clear();
        _ends.Clear();
        if (Volatile.Read(ref _latest) is not null && Interlocked.Exchange(ref _queued, 1) == 0) _owner.Enqueue(this);
    }

    /// <summary>Highlights from the first line that differs from the last pass; true while the session should be kept.</summary>
    internal bool Step(Registry registry, ColorTable colors)
    {
        Interlocked.Exchange(ref _queued, 0);
        if (_closed) return false;
        var lines = Volatile.Read(ref _latest);
        if (lines is null) return true;
        if (!_grammarLoaded)
        {
            _grammarLoaded = true;
            _grammar = Languages.ForName(_language) is { } language ? registry.LoadGrammar(language.Scope) : null;
        }

        var keep = 0;
        while (keep < _lines.Length && keep < lines.Length && string.Equals(_lines[keep], lines[keep], StringComparison.Ordinal)) keep++;
        // The block's last line may still be growing, so it is always done again.
        keep = Math.Min(keep, Math.Max(0, lines.Length - 1));
        if (keep < _result.Count)
        {
            _result.RemoveRange(keep, _result.Count - keep);
            _ends.RemoveRange(keep, _ends.Count - keep);
        }

        var state = keep == 0 ? null : _ends[keep - 1];
        for (var i = keep; i < lines.Length; i++)
        {
            if (_closed) return false;
            var text = lines[i];
            if (_grammar is null || text.Length > SyntaxService.MaxLineLength)
            {
                _result.Add(new HighlightedLine(text, []));
                _ends.Add(state);
                continue;
            }

            var tokens = _grammar.TokenizeLine2(new LineText(text), state, TimeSpan.FromMilliseconds(50));
            _result.Add(new HighlightedLine(text, Spans(new LineTokens(tokens.Tokens), text.Length, colors)));
            state = tokens.RuleStack;
            _ends.Add(state);
        }

        _lines = lines;
        _done([.. _result]);
        return true;
    }

    private static ColoredSpan[] Spans(LineTokens tokens, int length, ColorTable colors)
    {
        var spans = new List<ColoredSpan>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var start = tokens.Start(i);
            var end = i + 1 < tokens.Count ? tokens.Start(i + 1) : length;
            if (end > length) end = length;
            if (end <= start) continue;
            spans.Add(new ColoredSpan(start, end - start, colors[tokens.Foreground(i)], tokens.Style(i)));
        }

        return [.. spans];
    }
}
