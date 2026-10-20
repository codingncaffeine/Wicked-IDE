using System.Collections.Concurrent;
using Wicked.Core;
using Wicked.Editor.Syntax;

namespace Wicked.Editor.Layout;

/// <summary>
/// What a shaped line depends on: its text, its highlighting and colours (compared by reference: a new snapshot's
/// tokens are new objects), the default colour, the tab size and the fonts. Two lines that agree on all of it look the
/// same, wherever they are.
/// </summary>
public readonly struct LineKey(string text, LineTokens? tokens, ColorTable? colors, uint defaultColor, int tabSize, int fontVersion) : IEquatable<LineKey>
{
    public string Text { get; } = text;

    public LineTokens? Tokens { get; } = tokens;

    public ColorTable? Colors { get; } = colors;

    public uint DefaultColor { get; } = defaultColor;

    public int TabSize { get; } = tabSize;

    public int FontVersion { get; } = fontVersion;

    public bool Equals(LineKey other) =>
        ReferenceEquals(Tokens, other.Tokens) && ReferenceEquals(Colors, other.Colors) && DefaultColor == other.DefaultColor
        && TabSize == other.TabSize && FontVersion == other.FontVersion && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is LineKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(string.GetHashCode(Text, StringComparison.Ordinal), Tokens is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Tokens), DefaultColor, TabSize, FontVersion);
}

/// <summary>
/// Shapes lines on a thread of its own and keeps them in a cache the UI thread reads without locking. Requests are
/// served newest first (what is in sight now beats what was in sight a moment ago). Shaped lines hold native Skia
/// objects, so an evicted line is freed only once the render thread has drawn a frame built after the eviction.
/// </summary>
public sealed class LayoutService : IDisposable
{
    private const int Capacity = 6000;

    private static long _frameGeneration;
    private static long _renderedGeneration;

    private readonly ConcurrentDictionary<LineKey, ShapedLine> _cache = new();
    private readonly ConcurrentStack<LineKey[]> _requests = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly List<(ShapedLine Line, long Generation)> _graveyard = [];
    private volatile bool _stopping;
    private long _clock;

    public LayoutService(EditorFonts fonts)
    {
        _fonts = fonts;
        _thread = new Thread(Run) { IsBackground = true, Name = "Wicked layout" };
        _thread.Start();
    }

    private volatile EditorFonts _fonts;

    /// <summary>The fonts lines are shaped with. Changing them (a zoom, a new size) shapes lines afresh: the cache key holds the fonts' version, so old lines simply age out.</summary>
    public EditorFonts Fonts
    {
        get => _fonts;
        set
        {
            _fonts = value;
            _wake.Set();
        }
    }

    /// <summary>Raised on the layout thread after a batch of lines is ready; listeners post a redraw to their own thread.</summary>
    public event Action? Ready;

    /// <summary>Lines shaped so far, for tests and the performance log.</summary>
    public long LinesShaped => Interlocked.Read(ref _linesShaped);

    private long _linesShaped;

    /// <summary>A cached line, without waiting; any thread.</summary>
    public bool TryGet(in LineKey key, out ShapedLine line)
    {
        if (!_cache.TryGetValue(key, out line!)) return false;
        line.LastUsed = Interlocked.Increment(ref _clock);
        return true;
    }

    /// <summary>Asks for lines to be shaped; the newest request is served first. Any thread.</summary>
    public void Request(LineKey[] keys)
    {
        if (keys.Length == 0) return;
        _requests.Push(keys);
        _wake.Set();
    }

    /// <summary>The number every frame built from now on carries (the UI thread calls it per frame).</summary>
    public static long NextFrameGeneration() => Interlocked.Increment(ref _frameGeneration);

    /// <summary>The render thread reports each frame it has drawn, so the lines older frames used can be freed.</summary>
    public static void FrameRendered(long generation)
    {
        long seen;
        while ((seen = Interlocked.Read(ref _renderedGeneration)) < generation && Interlocked.CompareExchange(ref _renderedGeneration, generation, seen) != seen)
        {
        }
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
        var shaperFonts = Fonts;
        var shaper = new LineShaper(shaperFonts);
        while (!_stopping)
        {
            if (!ReferenceEquals(shaperFonts, Fonts))
            {
                shaper.Dispose();
                shaperFonts = Fonts;
                shaper = new LineShaper(shaperFonts);
            }

            if (!_requests.TryPop(out var batch))
            {
                Sweep();
                _wake.WaitOne(TimeSpan.FromSeconds(2));
                continue;
            }

            var shaped = 0;
            foreach (var key in batch)
            {
                if (_cache.ContainsKey(key) || key.FontVersion != shaperFonts.Version) continue;
                try
                {
                    var line = shaper.Shape(key.Text, key.Tokens, key.Colors, key.DefaultColor, key.TabSize);
                    line.LastUsed = Interlocked.Increment(ref _clock);
                    if (_cache.TryAdd(key, line)) shaped++;
                    else line.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("A line could not be shaped; it is drawn unshaped", ex);
                }

                // A newer request (the user scrolled on) goes first: put the rest of this one back behind it.
                if (!_requests.IsEmpty)
                {
                    var rest = batch.SkipWhile(k => !k.Equals(key)).Skip(1).ToArray();
                    if (rest.Length > 0)
                    {
                        _requests.TryPop(out var newest);
                        _requests.Push(rest);
                        if (newest is not null) _requests.Push(newest);
                    }

                    break;
                }
            }

            Interlocked.Add(ref _linesShaped, shaped);
            if (shaped > 0) Ready?.Invoke();
            if (_cache.Count > Capacity) Evict();
        }

        shaper.Dispose();
        foreach (var line in _cache.Values) line.Dispose();
    }

    /// <summary>Drops the least recently used quarter of the cache into the graveyard.</summary>
    private void Evict()
    {
        var generation = Interlocked.Read(ref _frameGeneration);
        foreach (var (key, line) in _cache.OrderBy(p => p.Value.LastUsed).Take(_cache.Count / 4).ToList())
        {
            if (_cache.TryRemove(key, out _)) _graveyard.Add((line, generation));
        }

        Sweep();
    }

    /// <summary>
    /// Frees evicted lines that no frame can still be drawing. A frame built while the eviction ran may have looked a
    /// line up just before it went, so a line waits for a frame two generations past its eviction.
    /// </summary>
    private void Sweep()
    {
        var rendered = Interlocked.Read(ref _renderedGeneration);
        for (var i = _graveyard.Count - 1; i >= 0; i--)
        {
            if (_graveyard[i].Generation + 1 < rendered)
            {
                _graveyard[i].Line.Dispose();
                _graveyard.RemoveAt(i);
            }
        }
    }
}
