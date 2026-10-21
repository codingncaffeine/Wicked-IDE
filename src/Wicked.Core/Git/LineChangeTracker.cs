using System.Collections.Immutable;

namespace Wicked.Core.Git;

/// <summary>
/// The gutter marks of one open file. It keeps the file as the index holds it (read once, again when the index changes) and,
/// each time the editor reports a change, diffs the working text against it on a worker after a short pause, so typing is never
/// held up and a burst of keystrokes costs one diff. The newest request wins; an older diff still running is cancelled.
///
/// The editor side: when a document opens, make a tracker for its path, call <see cref="TextChanged"/> from the document's
/// change event with a function returning the text (a captured immutable snapshot, converted on the worker), call
/// <see cref="RefreshBaseAsync"/> when the repository reports a new index, and draw <see cref="Marks"/> from
/// <see cref="MarksChanged"/> (raised on a pool thread: post it to the UI thread).
/// </summary>
public sealed class LineChangeTracker : IDisposable
{
    private readonly GitRepository _repository;
    private readonly TimeSpan _pause;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private string[]? _baseLines;
    private Func<string>? _latest;
    private CancellationTokenSource? _running;
    private int _generation;

    public LineChangeTracker(GitRepository repository, string path, TimeSpan? pause = null)
    {
        _repository = repository;
        Path = path;
        _pause = pause ?? TimeSpan.FromMilliseconds(120);
    }

    /// <summary>The file's path relative to the repository root, with '/'.</summary>
    public string Path { get; }

    public ImmutableArray<LineChange> Marks { get; private set; } = [];

    /// <summary>The changed blocks behind <see cref="Marks"/>, for the peek widget and the hunk actions.</summary>
    public ImmutableArray<LineBlock> Blocks { get; private set; } = [];

    /// <summary>True when git has no index version of the file (untracked or ignored): nothing is marked then.</summary>
    public bool IsUntracked => _baseLines is null;

    /// <summary>Raised on a pool thread after each recomputation.</summary>
    public event Action<ImmutableArray<LineChange>>? MarksChanged;

    /// <summary>Reads the index's version of the file again, then marks the latest text against it.</summary>
    public async Task RefreshBaseAsync(CancellationToken ct = default)
    {
        var text = await _repository.ShowIndexAsync(Path, ct).ConfigureAwait(false);
        lock (_gate) _baseLines = text is null ? null : TextLines.Split(text);
        Schedule(immediately: true);
    }

    /// <summary>The text changed: <paramref name="snapshot"/> returns the current text and is called on a worker.</summary>
    public void TextChanged(Func<string> snapshot)
    {
        lock (_gate) _latest = snapshot;
        Schedule(immediately: false);
    }

    private void Schedule(bool immediately)
    {
        CancellationTokenSource cts;
        int generation;
        lock (_gate)
        {
            _running?.Cancel();
            _running = cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            generation = ++_generation;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediately) await Task.Delay(_pause, cts.Token).ConfigureAwait(false);
                Func<string>? snapshot;
                string[]? baseLines;
                lock (_gate)
                {
                    snapshot = _latest;
                    baseLines = _baseLines;
                }

                if (snapshot is null) return;
                var working = TextLines.Split(snapshot());
                var diff = baseLines is null ? null : DiffEngine.Diff(baseLines, working, ct: cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (generation != _generation) return;
                    Blocks = diff?.Blocks ?? [];
                    Marks = diff is null ? [] : GutterMarks.FromBlocks(diff.Blocks, working.Length);
                }

                MarksChanged?.Invoke(Marks);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
