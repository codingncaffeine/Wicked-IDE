using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.Editor.View;

/// <summary>The editor's derived facts for one version of the text: brackets with their partners and depths, indent guides, folding ranges.</summary>
public sealed record FeatureSnapshot(long Version, int LineCount, BracketIndex Brackets, IndentGuides Guides, FoldingRanges Folding, MinimapModel Minimap);

/// <summary>
/// Works out a document's brackets and indent guides on a worker after it changes (tens of milliseconds for 100k lines,
/// nothing for an ordinary file). Requests that pile up while it works collapse into the newest; results arrive through
/// the callback on the worker's thread.
/// </summary>
public sealed class FeatureWorker
{
    private readonly Action<FeatureSnapshot> _published;
    private readonly Lock _gate = new();
    private (Rope Text, long Version, SyntaxSnapshot? Syntax, EditOptions Options)? _pending;

    /// <summary>The language's region markers (#region and its kin), for folding.</summary>
    public FoldingMarkers? Markers { get; init; }
    private bool _running;

    public FeatureWorker(Action<FeatureSnapshot> published) => _published = published;

    /// <summary>Asks for the facts of this version; returns at once.</summary>
    public void Request(Rope text, long version, SyntaxSnapshot? syntax, EditOptions options)
    {
        lock (_gate)
        {
            _pending = (text, version, syntax, options);
            if (_running) return;
            _running = true;
        }

        _ = Task.Run(Run);
    }

    private void Run()
    {
        while (true)
        {
            (Rope Text, long Version, SyntaxSnapshot? Syntax, EditOptions Options) work;
            lock (_gate)
            {
                if (_pending is not { } next)
                {
                    _running = false;
                    return;
                }

                work = next;
                _pending = null;
            }

            try
            {
                // Highlighting of an older version still tells strings and comments apart on the lines it has reached.
                var brackets = BracketIndex.Build(work.Text, work.Version, work.Syntax, work.Options.Pairs);
                var guides = IndentGuides.Compute(work.Text, work.Version, work.Options.TabSize, work.Options.InsertSpaces);
                var folding = FoldingRanges.Compute(work.Text, work.Version, work.Options.TabSize, Markers);
                var minimap = MinimapModel.Build(work.Text, work.Version, work.Syntax, work.Options.TabSize);
                _published(new FeatureSnapshot(work.Version, work.Text.LineCount, brackets, guides, folding, minimap));
            }
            catch (Exception ex)
            {
                Wicked.Core.Log.Error("Brackets and guides could not be worked out", ex);
            }
        }
    }
}
