using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Wicked.App.Git;
using Wicked.App.Workbench;
using Wicked.Core.Git;
using Wicked.Editor.Model;

namespace Wicked.App.Assistant;

/// <summary>
/// A change the assistant proposes to a stretch of a document (D18: edits as reviewable diffs). The stretch follows the
/// document's edits while the review is open; each change can be accepted (it goes into the document as one undo step)
/// or rejected (it leaves the proposal), one at a time or all at once. Nothing reaches the document unasked.
/// </summary>
public sealed class EditProposal
{
    private readonly Document _document;
    private int _start;
    private int _end;
    private string[] _proposed;

    public EditProposal(EditorTab tab, int start, int end, string proposed)
    {
        Tab = tab;
        _document = tab.Document;
        _start = start;
        _end = end;
        _proposed = TextLines.Split(proposed.Replace("\r\n", "\n", StringComparison.Ordinal));
        _document.Changed += Follow;
    }

    public EditorTab Tab { get; }

    /// <summary>The stretch's text in the document now.</summary>
    public string Original => _document.Text.Substring(_start, _end - _start);

    public string Proposed => string.Join('\n', _proposed);

    private string[] OriginalLines => TextLines.Split(Original);

    public IReadOnlyList<LineBlock> Changes => DiffEngine.Diff(OriginalLines, _proposed).Blocks;

    public bool IsSettled => Changes.Count == 0;

    /// <summary>The proposal changed (a change taken or dropped): the view shows the comparison again.</summary>
    public event Action? Changed;

    private void Follow(DocumentChange change)
    {
        _start = ChangeSet.Map(_start, change.Changes, stickAfter: false);
        _end = Math.Max(_start, ChangeSet.Map(_end, change.Changes, stickAfter: true));
    }

    /// <summary>Every change into the document, as one step.</summary>
    public void AcceptAll()
    {
        var text = Proposed;
        if (text != Original) _document.Apply([new TextChange(_start, _end - _start, text)], [Selection.Caret(_start + text.Length)]);
        Changed?.Invoke();
    }

    /// <summary>The proposal dropped: the document stays as it is.</summary>
    public void RejectAll()
    {
        _proposed = OriginalLines;
        Changed?.Invoke();
    }

    /// <summary>One change into the document (its lines replaced by the proposal's), the others still to review.</summary>
    public void Accept(int index)
    {
        var changes = Changes;
        if (index < 0 || index >= changes.Count) return;
        var block = changes[index];
        var original = OriginalLines;
        var lines = original[..block.OrigStart].Concat(_proposed[block.ModStart..block.ModEnd]).Concat(original[block.OrigEnd..]);
        var text = string.Join('\n', lines);
        _document.Apply([new TextChange(_start, _end - _start, text)], [.. _document.Selections]);
        Changed?.Invoke();
    }

    /// <summary>One change dropped from the proposal: those lines stay as they are.</summary>
    public void Reject(int index)
    {
        var changes = Changes;
        if (index < 0 || index >= changes.Count) return;
        var block = changes[index];
        var original = OriginalLines;
        _proposed = [.. _proposed[..block.ModStart].Concat(original[block.OrigStart..block.OrigEnd]).Concat(_proposed[block.ModEnd..])];
        Changed?.Invoke();
    }

    public void Close() => _document.Changed -= Follow;
}

/// <summary>
/// The review of a proposal, as a tab: the stretch as it is on the left, as proposed on the right, and the buttons to
/// take or leave the current change or all of them. It closes itself when nothing is left to decide.
/// </summary>
public sealed class EditReviewView : DockPanel
{
    private readonly EditProposal _proposal;
    private readonly Wicked.Core.Settings.WickedSettings _settings;
    private readonly ContentControl _diffHost = new();
    private readonly TextBlock _count = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private int _current;

    public EditReviewView(EditProposal proposal, string title, Action close, Wicked.Core.Settings.WickedSettings settings)
    {
        Name = "EditReview";
        _proposal = proposal;
        _settings = settings;
        Closed = close;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 6, 8, 6) };
        bar.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_count);
        bar.Children.Add(Button("ReviewPrevious", "Previous", () => Move(-1), "secondary small"));
        bar.Children.Add(Button("ReviewNext", "Next", () => Move(1), "secondary small"));
        bar.Children.Add(Button("ReviewAccept", "Accept change", () => _proposal.Accept(_current), "primary small"));
        bar.Children.Add(Button("ReviewReject", "Reject change", () => _proposal.Reject(_current), "secondary small"));
        bar.Children.Add(Button("ReviewAcceptAll", "Accept all", _proposal.AcceptAll, "secondary small"));
        bar.Children.Add(Button("ReviewRejectAll", "Reject all", _proposal.RejectAll, "secondary small"));
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_diffHost);
        proposal.Changed += Show;
        Show();
    }

    /// <summary>Closes the review's tab (set by the one who opened it).</summary>
    public Action Closed { get; }

    public EditProposal Proposal => _proposal;

    /// <summary>The change the buttons act on, 0-based.</summary>
    public int Current => _current;

    private static Button Button(string name, string text, Action click, string classes)
    {
        var button = new Button { Name = name, Content = text };
        foreach (var c in classes.Split(' ')) button.Classes.Add(c);
        button.Click += (_, _) => click();
        return button;
    }

    private void Move(int by)
    {
        var count = _proposal.Changes.Count;
        if (count == 0) return;
        _current = (_current + by + count) % count;
        Show();
    }

    private void Show()
    {
        var changes = _proposal.Changes;
        if (changes.Count == 0)
        {
            _proposal.Close();
            Closed();
            return;
        }

        _current = Math.Clamp(_current, 0, changes.Count - 1);
        _count.Text = $"Change {_current + 1} of {changes.Count}";
        var original = _proposal.Original;
        var proposed = _proposal.Proposed;
        var focus = _current;
        var diff = new DiffEditorView("", _proposal.Tab.Path ?? _proposal.Tab.Title, () => Task.FromResult(original), () => Task.FromResult(proposed), null, _settings);
        diff.Compared += () => diff.ShowChange(focus);
        _diffHost.Content = diff;
    }
}
