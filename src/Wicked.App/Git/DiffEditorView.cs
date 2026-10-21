using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Wicked.App.Editor;
using Wicked.App.Shell;
using Wicked.Core.Git;
using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;

namespace Wicked.App.Git;

/// <summary>
/// Two texts side by side (VS Code's diff editor): the original on the left, the changed one on the right, a row for
/// each line of either with a blank filler where the other side has none, so the two scroll as one. Removed lines are
/// red on the left, added green on the right, the words that changed inside a changed line stronger. Both sides are
/// read-only with their own line numbers; Open File edits the real file. The texts are read and compared on workers.
/// </summary>
public sealed class DiffEditorView : DockPanel
{
    public const string Layer = "diff";

    private readonly ContentControl _left = new();
    private readonly ContentControl _right = new();
    private readonly TextBlock _status = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private CodeEditor? _leftEditor;
    private CodeEditor? _rightEditor;
    private SideBySideDiff? _diff;
    private bool _syncing;

    public DiffEditorView(string title, string path, Func<Task<string>> original, Func<Task<string>> modified, Func<Task>? openFile, Wicked.Core.Settings.WickedSettings settings)
    {
        Name = "DiffEditor";
        Path = path;
        var header = new DockPanel { Margin = new Thickness(12, 6, 8, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(Quiet("DiffPrevious", "Icon.ArrowUp", "Previous Change (Shift+Alt+F5)", () => Step(-1)));
        buttons.Children.Add(Quiet("DiffNext", "Icon.ArrowDown", "Next Change (Alt+F5)", () => Step(1)));
        if (openFile is not null) buttons.Children.Add(Quiet("DiffOpenFile", "Icon.FileText", "Open File", () => _ = openFile()));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _status },
        });
        SetDock(header, Dock.Top);
        Children.Add(header);
        var sides = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
        sides.Children.Add(_left);
        var divider = new Border { [Grid.ColumnProperty] = 1, Background = Language.EditorLanguageUi.Resource<IBrush>("Brush.Border") };
        sides.Children.Add(divider);
        _right[Grid.ColumnProperty] = 2;
        sides.Children.Add(_right);
        Children.Add(sides);
        _ = LoadAsync(original, modified, settings);
    }

    /// <summary>The file compared (for its language's colours, and for a test to find the tab).</summary>
    public string Path { get; }

    /// <summary>The diff shown, once worked out.</summary>
    public SideBySideDiff? Diff => _diff;

    public CodeEditor? Left => _leftEditor;

    public CodeEditor? Right => _rightEditor;

    private static Button Quiet(string name, string icon, string tip, Action click)
    {
        var button = new Button { Name = name, Classes = { "quiet" }, Content = new Icon { Data = Language.EditorLanguageUi.Resource<Geometry>(icon), Size = 15 } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The two sides as texts with a line per row (fillers blank), each side's marks, its line numbers by row.</summary>
    internal sealed record Sides(string Left, string Right, DecorationSet LeftMarks, DecorationSet RightMarks, int?[] LeftNumbers, int?[] RightNumbers, int Changes);

    private async Task LoadAsync(Func<Task<string>> original, Func<Task<string>> modified, Wicked.Core.Settings.WickedSettings settings)
    {
        _status.Text = "Comparing…";
        string before, after;
        try
        {
            before = await Task.Run(original);
            after = await Task.Run(modified);
        }
        catch (Exception ex) when (ex is IOException or GitException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not read the file: {ex.Message}";
            return;
        }

        var (diff, sides) = await Task.Run(() =>
        {
            var d = SideBySideDiff.Build(TextLines.Split(before), TextLines.Split(after));
            return (d, Lay(d, TextLines.Split(before), TextLines.Split(after)));
        });
        _diff = diff;
        var services = await EditorServices.Ready;
        var language = Languages.ForFile(Path);
        _leftEditor = Side(services, sides.Left, language, sides.LeftMarks, sides.LeftNumbers, settings, "DiffLeft");
        _rightEditor = Side(services, sides.Right, language, sides.RightMarks, sides.RightNumbers, settings, "DiffRight");
        _leftEditor.ViewScrolled += () => Follow(_leftEditor, _rightEditor);
        _rightEditor.ViewScrolled += () => Follow(_rightEditor, _leftEditor);
        _left.Content = _leftEditor;
        _right.Content = _rightEditor;
        _status.Text = sides.Changes == 0 ? "No changes" : $"{sides.Changes} change{(sides.Changes == 1 ? "" : "s")}" + (diff.TimedOut ? " (the comparison took too long and is approximate)" : "");
        Compared?.Invoke();
        _changeRows = [.. diff.ChangeRows];
        if (_changeRows.Length > 0) Later(() => Reveal(_changeRows[0]));
    }

    /// <summary>The comparison is on screen (tests wait on it).</summary>
    public event Action? Compared;

    private static void Later(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Loaded);

    private static CodeEditor Side(EditorServices services, string text, LanguageInfo? language, DecorationSet marks, int?[] numbers, Wicked.Core.Settings.WickedSettings settings, string name)
    {
        var doc = new Document(text) { IsReadOnly = true, KeepsHistory = false };
        var editor = new CodeEditor(services, doc, language) { Name = name };
        editor.ApplySettings(settings with { Minimap = false, WordWrap = "off" });
        editor.LineNumber = line => line >= 0 && line < numbers.Length ? numbers[line] : null;
        editor.SetDecorations(Layer, marks, doc.Version);
        return editor;
    }

    internal static Sides Lay(SideBySideDiff diff, string[] before, string[] after)
    {
        var left = new StringBuilder();
        var right = new StringBuilder();
        var leftNumbers = new int?[diff.Rows.Length];
        var rightNumbers = new int?[diff.Rows.Length];
        var leftLines = new List<LineDecoration>();
        var rightLines = new List<LineDecoration>();
        var leftRanges = new List<RangeDecoration>();
        var rightRanges = new List<RangeDecoration>();
        var leftStart = new int[diff.Rows.Length];
        var rightStart = new int[diff.Rows.Length];
        for (var row = 0; row < diff.Rows.Length; row++)
        {
            var r = diff.Rows[row];
            if (row > 0)
            {
                left.Append('\n');
                right.Append('\n');
            }

            leftStart[row] = left.Length;
            rightStart[row] = right.Length;
            if (r.Left is { } l)
            {
                left.Append(before[l]);
                leftNumbers[row] = l + 1;
            }

            if (r.Right is { } rr)
            {
                right.Append(after[rr]);
                rightNumbers[row] = rr + 1;
            }

            switch (r.Kind)
            {
                case AlignedRowKind.Removed:
                    leftLines.Add(new LineDecoration(leftStart[row], LineStyle.Removed));
                    break;
                case AlignedRowKind.Added:
                    rightLines.Add(new LineDecoration(rightStart[row], LineStyle.Inserted));
                    break;
                case AlignedRowKind.Modified:
                    leftLines.Add(new LineDecoration(leftStart[row], LineStyle.Removed));
                    rightLines.Add(new LineDecoration(rightStart[row], LineStyle.Inserted));
                    break;
            }
        }

        foreach (var change in diff.Inline)
        {
            var lr = diff.LeftRowOfLine[change.LeftLine];
            var rr = diff.RightRowOfLine[change.RightLine];
            foreach (var c in change.Left) leftRanges.Add(new RangeDecoration(leftStart[lr] + c.Start, leftStart[lr] + c.End, RangeStyle.Removed));
            foreach (var c in change.Right) rightRanges.Add(new RangeDecoration(rightStart[rr] + c.Start, rightStart[rr] + c.End, RangeStyle.Inserted));
        }

        return new Sides(left.ToString(), right.ToString(),
            new DecorationSet(leftRanges, leftLines, [], []), new DecorationSet(rightRanges, rightLines, [], []),
            leftNumbers, rightNumbers, diff.Blocks.Length);
    }

    private void Follow(CodeEditor from, CodeEditor to)
    {
        if (_syncing) return;
        _syncing = true;
        to.ScrollToY(from.ScrollY);
        _syncing = false;
    }

    private int _current = -1;
    private int[] _changeRows = [];

    /// <summary>Alt+F5 / Shift+Alt+F5: the next or previous change, both sides scrolled to it.</summary>
    public void Step(int direction)
    {
        if (_changeRows.Length == 0) return;
        _current = (_current + direction + _changeRows.Length) % _changeRows.Length;
        Reveal(_changeRows[_current]);
    }

    /// <summary>The number of changes shown, once compared.</summary>
    public int ChangeCount => _changeRows.Length;

    /// <summary>Both sides scrolled to a change (0-based), as the review's buttons move between them.</summary>
    public void ShowChange(int index)
    {
        if (_changeRows.Length == 0) return;
        _current = Math.Clamp(index, 0, _changeRows.Length - 1);
        Reveal(_changeRows[_current]);
    }

    private void Reveal(int row)
    {
        if (_rightEditor is null || _leftEditor is null) return;
        var text = _rightEditor.Document.Text;
        var at = text.LineStart(Math.Clamp(row, 0, text.LineCount - 1));
        _rightEditor.Document.SetSelections([Selection.Caret(at)]);
        _rightEditor.Reveal(at);
        _leftEditor.ScrollToY(_rightEditor.ScrollY);
    }
}
