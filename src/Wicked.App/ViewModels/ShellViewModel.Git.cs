using System.Collections.Specialized;
using Wicked.App.Git;
using Wicked.App.Workbench;
using Wicked.Core.Git;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.App.ViewModels;

/// <summary>Source control in the rest of the window: its log in Output, diffs as tabs, change marks in every editor's gutter.</summary>
public sealed partial class ShellViewModel
{
    public const string GitChannel = "Git";
    public const string GitLayer = "git";

    private readonly Dictionary<Document, (LineChangeTracker Tracker, int Tabs)> _trackers = [];

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowSourceControl() => Navigate(Wicked.App.Shell.ShellRoutes.SourceControl);

    private void WireSourceControl()
    {
        SourceControl.Output.Lines.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
            foreach (GitOutputLine line in e.NewItems) Output.Write(GitChannel, line.Text);
        };
        SourceControl.OutputRequested += () => Navigate($"output/{GitChannel}");
        SourceControl.DiffRequested += OpenDiff;
        SourceControl.RepositoryChanged += repository =>
        {
            foreach (var (tracker, _) in _trackers.Values)
            {
                if (tracker.Repository == repository) _ = tracker.RefreshBaseAsync();
            }
        };
        Workbench.TabOpened += TrackChanges;
        Workbench.TabClosed += tab =>
        {
            if (!_trackers.TryGetValue(tab.Document, out var entry)) return;
            if (entry.Tabs > 1)
            {
                _trackers[tab.Document] = (entry.Tracker, entry.Tabs - 1);
                return;
            }

            _trackers.Remove(tab.Document);
            entry.Tracker.Dispose();
        };
    }

    /// <summary>A diff the Source Control panel asked for, opened as a side-by-side tab.</summary>
    private void OpenDiff(ScmDiffRequest request)
    {
        request.Handled = true;
        var full = request.FullPath;
        Workbench.OpenView(request.Title, () => new DiffEditorView(request.Title, full, () => request.ReadOriginalAsync(), () => request.ReadModifiedAsync(),
            File.Exists(full) ? () => Workbench.OpenAsync(full) : null, Settings));
    }

    /// <summary>The gutter's change marks for a file in a repository: worked out on a worker as its text changes.</summary>
    private void TrackChanges(EditorTab tab)
    {
        if (tab.Path is not { } path || tab.ViewFactory is not null) return;
        var document = tab.Document;
        if (_trackers.TryGetValue(document, out var known))
        {
            _trackers[document] = (known.Tracker, known.Tabs + 1);
            return;
        }

        if (SourceControl.CreateTracker(path) is not { } tracker) return;
        _trackers[document] = (tracker, 1);
        long submitted = document.Version;
        tracker.MarksChanged += marks => _post(() =>
        {
            if (!_trackers.TryGetValue(document, out var entry) || entry.Tracker != tracker) return;
            var set = MarksOf(document.Text, marks, submitted, document.Version);
            foreach (var editor in Workbench.AllTabs.Where(t => ReferenceEquals(t.Document, document)).Select(t => t.Editor).OfType<Wicked.App.Editor.CodeEditor>())
            {
                editor.SetDecorations(GitLayer, set.Set, set.Version);
            }
        });
        tab.EditorCreated += editor =>
        {
            if (_trackers.TryGetValue(document, out var entry)) editor.SetDecorations(GitLayer, MarksOf(document.Text, entry.Tracker.Marks, submitted, document.Version).Set, document.Version);
        };
        document.Changed += change =>
        {
            if (!_trackers.TryGetValue(document, out var entry) || entry.Tracker != tracker) return;
            var text = change.After;
            submitted = change.Version;
            tracker.TextChanged(text.ToString);
        };
        _ = tracker.RefreshBaseAsync().ContinueWith(_ =>
        {
            var text = document.Text;
            tracker.TextChanged(text.ToString);
        }, TaskScheduler.Default);
    }

    /// <summary>Marks as gutter glyphs, for the text they were worked out on (the last one given to the tracker).</summary>
    private static (DecorationSet Set, long Version) MarksOf(Wicked.Editor.Text.Rope text, IEnumerable<LineChange> marks, long submitted, long current)
    {
        var glyphs = new List<GutterDecoration>();
        foreach (var m in marks)
        {
            var glyph = m.Kind switch
            {
                LineChangeKind.Added => GutterGlyph.Added,
                LineChangeKind.Modified => GutterGlyph.Modified,
                _ => GutterGlyph.Deleted,
            };
            // Lines removed after the last line: the wedge goes under it, at the line after when there is one.
            var line = m.Kind == LineChangeKind.DeletedBelow ? Math.Min(m.Line + 1, text.LineCount - 1) : m.Line;
            if (line < text.LineCount) glyphs.Add(new GutterDecoration(DecorationSet.LineKey(text, line), glyph));
        }

        return (DecorationSet.OfGutter(glyphs), Math.Min(submitted, current));
    }
}
