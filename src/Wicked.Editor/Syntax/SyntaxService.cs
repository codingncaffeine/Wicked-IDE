using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using Wicked.Core;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Editor.Syntax;

/// <summary>
/// Highlights every open document on one thread of its own. TextMate grammars are not safe to share between threads,
/// so they all live here; the UI thread only hands over edits (a queue) and receives immutable
/// <see cref="SyntaxSnapshot"/>s. Work goes in slices of a few milliseconds, the documents in sight first, so a 100k-line
/// file highlighting in the background never holds back the one being typed in.
/// </summary>
public sealed class SyntaxService : IDisposable
{
    /// <summary>A slice of work before the thread looks again at what matters most.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(6);

    /// <summary>Longer lines are drawn unhighlighted, as VS Code does (its maxTokenizationLineLength).</summary>
    public const int MaxLineLength = 20_000;

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private readonly List<SyntaxSession> _sessions = [];
    private ThemeName _theme;
    private volatile bool _stopping;
    private volatile bool _themeChanged;

    // Owned by the syntax thread.
    private Registry? _registry;
    private ColorTable? _colors;

    public SyntaxService(ThemeName theme = ThemeName.DarkPlus)
    {
        _theme = theme;
        _thread = new Thread(Run) { IsBackground = true, Name = "Wicked syntax", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>
    /// Starts highlighting a document. <paramref name="published"/> is called on the syntax thread with each new
    /// snapshot; the caller marshals it to wherever it is needed.
    /// </summary>
    public SyntaxSession Open(Rope text, long version, LanguageInfo? language, Action<SyntaxSnapshot> published)
    {
        var session = new SyntaxSession(this, text, version, language?.Scope, published);
        lock (_gate) _sessions.Add(session);
        Wake();
        return session;
    }

    internal void Wake() => _wake.Set();

    /// <summary>The colour theme for code. Every document is highlighted afresh in the new colours, the ones in sight first.</summary>
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

    private string? _custom;

    /// <summary>
    /// A VS Code theme's JSON (with its tokenColors) to colour code by instead of a bundled one — an imported window
    /// theme's own code colours. Null goes back to <see cref="Theme"/>.
    /// </summary>
    public string? CustomTheme
    {
        get => _custom;
        set
        {
            if (value == _custom) return;
            _custom = value;
            _themeChanged = true;
            _wake.Set();
        }
    }

    /// <summary>The raw theme to load: the custom JSON when it reads, else the bundled one.</summary>
    internal static TextMateSharp.Themes.IRawTheme Load(ThemeName name, string? custom)
    {
        if (custom is not null)
        {
            try
            {
                using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(custom)));
                return TextMateSharp.Internal.Themes.Reader.ThemeReader.ReadThemeSync(reader);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                Log.Warn($"The theme's code colours could not be read: {ex.Message}");
            }
        }

        return Languages.Registry.LoadTheme(name);
    }

    /// <summary>The bundled code colour themes, for the theme picker.</summary>
    public static IReadOnlyList<ThemeName> Themes { get; } = Enum.GetValues<ThemeName>();

    internal void Remove(SyntaxSession session)
    {
        lock (_gate) _sessions.Remove(session);
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
            _registry = new Registry(Languages.Registry);
            _registry.SetTheme(Load(_theme, _custom));
            _colors = ColorTable.From(_registry.GetTheme());
        }
        catch (Exception ex)
        {
            Log.Error("Syntax highlighting could not start", ex);
            return;
        }

        while (!_stopping)
        {
            if (_themeChanged)
            {
                _themeChanged = false;
                _registry.SetTheme(Load(_theme, _custom));
                _colors = ColorTable.From(_registry.GetTheme());
                List<SyntaxSession> all;
                lock (_gate) all = [.. _sessions];
                foreach (var session in all) session.Restart();
            }

            SyntaxSession[] sessions;
            lock (_gate) sessions = [.. _sessions.OrderByDescending(s => s.Visible)];

            var worked = false;
            foreach (var session in sessions)
            {
                try
                {
                    if (session.Step(_registry, _colors, Slice))
                    {
                        // Its turn is over: it goes behind the others of its priority, so two big files share the thread.
                        lock (_gate)
                        {
                            if (_sessions.Remove(session)) _sessions.Add(session);
                        }

                        worked = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Syntax highlighting failed for a document; it stays as it is", ex);
                    Remove(session);
                }
            }

            if (!worked) _wake.WaitOne(TimeSpan.FromSeconds(5));
        }
    }
}

/// <summary>
/// One document's highlighting. <see cref="Edit"/>, <see cref="Visible"/> and <see cref="Close"/> may be called from any
/// thread (they queue); everything else belongs to the syntax thread.
/// </summary>
public sealed class SyntaxSession
{
    private readonly SyntaxService _service;
    private readonly ConcurrentQueue<DocumentChange> _inbox = new();
    private readonly Action<SyntaxSnapshot> _published;
    private readonly string? _scope;
    private volatile bool _closed;
    private volatile bool _visible = true;

    // The syntax thread's own.
    private IGrammar? _grammar;
    private bool _grammarLoaded;
    private Rope _text;
    private long _version;
    private readonly ImmutableList<LineTokens?>.Builder _lines;
    private readonly List<IStateStack?> _endStates;
    private int _valid;
    private int _dirtyUntil;
    private long _publishedVersion = -1;
    private int _publishedValid = -1;

    internal SyntaxSession(SyntaxService service, Rope text, long version, string? scope, Action<SyntaxSnapshot> published)
    {
        _service = service;
        _text = text;
        _version = version;
        _scope = scope;
        _published = published;
        _lines = ImmutableList.CreateBuilder<LineTokens?>();
        _lines.AddRange(Enumerable.Repeat<LineTokens?>(null, text.LineCount));
        _endStates = [.. Enumerable.Repeat<IStateStack?>(null, text.LineCount)];
    }

    /// <summary>How many lines have been highlighted so far, for tests and the performance log.</summary>
    public long LinesHighlighted => Interlocked.Read(ref _linesHighlighted);

    private long _linesHighlighted;

    /// <summary>Documents in sight are highlighted first.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            _service.Wake();
        }
    }

    /// <summary>
    /// The lines in sight. When they lie beyond what the in-order pass has reached, they are highlighted at once from an
    /// approximate starting state (VS Code's viewport tokenization), and corrected when the in-order pass arrives.
    /// </summary>
    public void SetViewport(int firstLine, int lastLine)
    {
        Interlocked.Exchange(ref _viewport, ((long)firstLine << 32) | (uint)lastLine);
        _service.Wake();
    }

    private long _viewport = -1;
    private long _approximated = -1;
    private long _approximatedVersion = -1;

    /// <summary>Queues an edit; the highlighting follows it from the first changed line.</summary>
    public void Edit(DocumentChange change)
    {
        _inbox.Enqueue(change);
        _service.Wake();
    }

    public void Close()
    {
        _closed = true;
        _service.Remove(this);
        _service.Wake();
    }

    /// <summary>
    /// Highlights from the top again (new colours): the old tokens stay until their lines are redone, and the lines in
    /// sight are done first through the viewport pass.
    /// </summary>
    internal void Restart()
    {
        _valid = 0;
        _dirtyUntil = int.MaxValue;
        for (var i = 0; i < _endStates.Count; i++) _endStates[i] = null;
        _approximated = -1;
        _publishedValid = -1;
    }

    /// <summary>Does one slice of work; true when there was work to do.</summary>
    internal bool Step(TextMateSharp.Registry.Registry registry, ColorTable colors, TimeSpan slice)
    {
        if (_closed) return false;
        var edited = false;
        while (_inbox.TryDequeue(out var change))
        {
            ApplyEdit(change);
            edited = true;
        }

        if (!_grammarLoaded)
        {
            _grammarLoaded = true;
            _grammar = _scope is null ? null : registry.LoadGrammar(_scope);
            if (_grammar is null) _valid = _text.LineCount;
        }

        if (_grammar is null)
        {
            _valid = _text.LineCount;
            return Publish(colors) || edited;
        }

        if (_valid >= _text.LineCount) return Publish(colors) || edited;
        if (Approximate()) return Publish(colors) || true;

        var clock = Stopwatch.StartNew();
        var line = _valid;
        var state = line == 0 ? null : _endStates[line - 1];
        var offset = _text.LineStart(line);
        while (line < _text.LineCount && clock.Elapsed < slice)
        {
            var length = line + 1 < _text.LineCount ? _text.LineStart(line + 1) - 1 - offset : _text.Length - offset;
            IStateStack? next;
            if (length > SyntaxService.MaxLineLength)
            {
                _lines[line] = null;
                next = state;
            }
            else
            {
                var result = _grammar.TokenizeLine2(new LineText(_text.Substring(offset, length)), state, TimeSpan.FromMilliseconds(50));
                _lines[line] = new LineTokens(result.Tokens);
                next = result.RuleStack;
            }

            // Past the edited lines, a line that ends in the state it ended in before means every line after it is
            // highlighted as it was: the work is done.
            var converged = line >= _dirtyUntil && _endStates[line] is { } before && SameState(before, next);
            _endStates[line] = next;
            Interlocked.Increment(ref _linesHighlighted);
            state = next;
            offset += length + 1;
            line++;
            if (converged)
            {
                line = _text.LineCount;
                break;
            }
        }

        _valid = line;
        Publish(colors);
        return true;
    }

    /// <summary>
    /// Highlights the lines in sight from an approximate start when the in-order pass is far from them: starting a little
    /// above them from the grammar's initial state gets keywords, strings and comments right in all but odd cases. The
    /// tokens go into the line list (beyond <see cref="SyntaxSnapshot.ValidLines"/>, so they count as provisional); the
    /// end states are left alone, so the in-order pass corrects them when it gets there.
    /// </summary>
    private bool Approximate()
    {
        var viewport = Interlocked.Read(ref _viewport);
        if (viewport < 0 || (viewport == _approximated && _approximatedVersion == _version)) return false;
        var first = (int)(viewport >> 32);
        var last = Math.Min((int)(uint)viewport, _text.LineCount - 1);
        _approximated = viewport;
        _approximatedVersion = _version;
        if (first <= _valid + 50 || first > last) return false;

        const int Lead = 30;
        var start = Math.Max(_valid, first - Lead);
        var end = Math.Min(_text.LineCount - 1, last + Lead);
        IStateStack? state = start == _valid && _valid > 0 ? _endStates[_valid - 1] : null;
        for (var line = start; line <= end; line++)
        {
            var text = _text.Line(line);
            if (text.Length > SyntaxService.MaxLineLength) continue;
            var result = _grammar!.TokenizeLine2(new LineText(text), state, TimeSpan.FromMilliseconds(50));
            _lines[line] = new LineTokens(result.Tokens);
            state = result.RuleStack;
        }

        _publishedValid = -1; // publish even though the in-order pass has not moved
        return true;
    }

    /// <summary>
    /// Whether two end-of-line states continue the same way: the same rule, end pattern and depth at every level, as
    /// VS Code's TextMate engine compares them. TextMateSharp's own Equals compares scope lists by reference and never
    /// matches two separately computed states, so highlighting would never stop early.
    /// </summary>
    internal static bool SameState(IStateStack? a, IStateStack? b)
    {
        var x = a as StateStack;
        var y = b as StateStack;
        while (x is not null && y is not null)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x.Depth != y.Depth || !Equals(x.RuleId, y.RuleId) || x.EndRule != y.EndRule || x.BeginRuleCapturedEOL != y.BeginRuleCapturedEOL) return false;
            x = x.Parent;
            y = y.Parent;
        }

        return x is null && y is null && (a is null) == (b is null);
    }

    private bool Publish(ColorTable colors)
    {
        if (_publishedVersion == _version && _publishedValid == _valid) return false;
        _publishedVersion = _version;
        _publishedValid = _valid;
        _published(new SyntaxSnapshot(_version, _lines.ToImmutable(), _valid, colors));
        return true;
    }

    /// <summary>Splices the per-line arrays to the edit's new line structure and marks where highlighting must resume.</summary>
    private void ApplyEdit(DocumentChange change)
    {
        var before = change.Before;
        var firstTouched = int.MaxValue;
        var lastTouchedNew = 0;
        var lineShift = 0;
        var spans = new List<(int First, int OldCount, int NewCount)>();
        foreach (var c in change.Changes)
        {
            var first = before.LineOf(c.Offset);
            var last = before.LineOf(c.End);
            spans.Add((first, last - first + 1, Rope.CountBreaks(c.Inserted) + 1));
        }

        // Splice from the bottom so the line numbers above stay valid; then work out where the touched lines ended up.
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var (first, oldCount, newCount) = spans[i];
            // The first line keeps its old tokens until it is highlighted again: stale colours for a moment beat no colours.
            var keep = _lines[first];
            _lines.RemoveRange(first, oldCount);
            _endStates.RemoveRange(first, oldCount);
            _lines.InsertRange(first, Enumerable.Repeat<LineTokens?>(null, newCount));
            _endStates.InsertRange(first, Enumerable.Repeat<IStateStack?>(null, newCount));
            _lines[first] = keep;
        }

        foreach (var (first, oldCount, newCount) in spans)
        {
            firstTouched = Math.Min(firstTouched, first + lineShift);
            lastTouchedNew = Math.Max(lastTouchedNew, first + lineShift + newCount - 1);
            lineShift += newCount - oldCount;
        }

        // An edit that lands while earlier work is unfinished: highlight on to the end rather than risk stopping early.
        _dirtyUntil = _valid < _dirtyUntil || _valid < _text.LineCount ? int.MaxValue : lastTouchedNew;
        _valid = Math.Min(_valid, firstTouched);
        _text = change.After;
        _version = change.Version;
        Debug.Assert(_lines.Count == _text.LineCount);
        if (_grammar is null && _grammarLoaded) _valid = _text.LineCount;
    }
}
