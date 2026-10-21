using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Wicked.App.Editor;
using Wicked.App.Language;
using Wicked.App.Workbench;
using Wicked.Core.Debug;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.App.Debug;

public sealed partial class DebugController
{
    /// <summary>The layer of the editor's marks the debugger owns: breakpoint glyphs and the stopped line.</summary>
    public const string MarksLayer = "debug";

    /// <summary>The layer of the values drawn after lines.</summary>
    public const string InlineLayer = "debug-inline";

    private readonly Dictionary<Document, int> _documents = [];
    private readonly List<Binder> _binders = [];

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SamePath(string? a, string? b) => a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);

    private void WireEditors()
    {
        var bench = _shell.Workbench;
        bench.TabOpened += OnTabOpened;
        bench.TabClosed += OnTabClosed;
        bench.Saved += tab =>
        {
            if (_root is { } root && SamePath(tab.Path, LaunchFile.PathIn(root))) _ = ReloadLaunchFileAsync();
        };
        bench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workbench.Workbench.Root)) FolderChanged(bench.Root);
        };
    }

    private void OnTabOpened(EditorTab tab)
    {
        if (tab.Path is null || tab.ViewFactory is not null) return;
        var document = tab.Document;
        if (_documents.TryGetValue(document, out var count)) _documents[document] = count + 1;
        else
        {
            _documents[document] = 1;
            document.Changed += change => OnDocumentChanged(tab.Path!, change);
        }

        tab.EditorCreated += editor => Attach(tab, editor);
        if (tab.Editor is { } existing) Attach(tab, existing);
    }

    private void OnTabClosed(EditorTab tab)
    {
        if (_documents.TryGetValue(tab.Document, out var count)) _documents[tab.Document] = count - 1;
        foreach (var binder in _binders.Where(b => b.Tab == tab).ToList())
        {
            binder.Dispose();
            _binders.Remove(binder);
        }
    }

    private void Attach(EditorTab tab, CodeEditor editor)
    {
        if (_binders.Any(b => b.Editor == editor)) return;
        var binder = new Binder(this, tab, editor);
        _binders.Add(binder);
        // An editor that goes while its tab stays (Change Language Mode makes a new one) takes its binder with it.
        editor.Closed += () =>
        {
            binder.Dispose();
            _binders.Remove(binder);
        };
        binder.Redecorate();
    }

    /// <summary>
    /// The text of a file with breakpoints was edited: each breakpoint's line start is carried through the edit, so the breakpoint
    /// stays on its code (Enter above it moves it down, a deleted line takes its breakpoint onto the line that joined it).
    /// </summary>
    private void OnDocumentChanged(string path, DocumentChange change)
    {
        var bps = Breakpoints.ForFile(path);
        if (bps.Count == 0) return;
        Breakpoints.Relocate(path, MoveBreakpoints(bps, change));
    }

    /// <summary>Where each breakpoint's line (1-based) is after an edit: its line start carried through the change.</summary>
    internal static Dictionary<int, int> MoveBreakpoints(IReadOnlyList<SourceBreakpoint> breakpoints, DocumentChange change)
    {
        var moved = new Dictionary<int, int>();
        foreach (var bp in breakpoints)
        {
            var line = Math.Clamp(bp.Line - 1, 0, change.Before.LineCount - 1);
            var start = ChangeSet.Map(change.Before.LineStart(line), change.Changes, stickAfter: true);
            moved[bp.Id] = change.After.LineOf(Math.Clamp(start, 0, change.After.Length)) + 1;
        }

        return moved;
    }

    /// <summary>The editors' debugger marks drawn again (for the files named, or all).</summary>
    public void RefreshEditorMarks(IReadOnlySet<string>? files = null)
    {
        foreach (var binder in _binders)
        {
            if (files is null || files.Count == 0 || files.Any(f => SamePath(f, binder.Tab.Path))) binder.Redecorate();
        }
    }

    // ── Breakpoint commands ──────────────────────────────────────────────────────────────────────

    /// <summary>The gutter click and F9: a breakpoint on a line (1-based) comes or goes.</summary>
    public void ToggleBreakpoint(string path, int line) => Breakpoints.Toggle(path, line);

    public void ToggleBreakpointAtCaret()
    {
        if (_shell.Workbench.ActiveTab is not { Path: { } path } tab) return;
        ToggleBreakpoint(path, tab.Document.Text.LineOf(tab.Document.Primary.Active) + 1);
    }

    /// <summary>Right-click in the glyph margin of a line (1-based): the breakpoint menu.</summary>
    private void ShowBreakpointMenu(Binder binder, int line, Point at)
    {
        if (binder.Tab.Path is not { } path) return;
        var existing = Breakpoints.At(path, line);
        var menu = new ContextMenu { PlacementTarget = binder.Editor };
        MenuItem Item(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            return item;
        }

        if (existing is null)
        {
            menu.Items.Add(Item("Add Breakpoint", () => Breakpoints.Add(path, line)));
            menu.Items.Add(Item("Add Conditional Breakpoint…", () => EditBreakpoint(Breakpoints.Add(path, line), "condition")));
            menu.Items.Add(Item("Add Logpoint…", () => EditBreakpoint(Breakpoints.Add(path, line), "log")));
        }
        else
        {
            menu.Items.Add(Item("Remove Breakpoint", () => Breakpoints.Remove(existing.Id)));
            menu.Items.Add(Item(existing.Kind == BreakpointKind.Logpoint ? "Edit Logpoint…" : "Edit Breakpoint…", () => EditBreakpoint(existing, existing.Kind == BreakpointKind.Logpoint ? "log" : null)));
            menu.Items.Add(Item(existing.Enabled ? "Disable Breakpoint" : "Enable Breakpoint", () => Breakpoints.Update(existing.Id, b => b with { Enabled = !b.Enabled })));
        }

        menu.Open(binder.Editor);
    }

    // ── Values after lines ───────────────────────────────────────────────────────────────────────

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>How many lines above the stopped one get values.</summary>
    private const int InlineReach = 60;

    private void ClearInline()
    {
        foreach (var binder in _binders) binder.Editor.SetDecorations(InlineLayer, DecorationSet.Empty, binder.Tab.Document.Version);
    }

    /// <summary>
    /// "name = value" after each line, above the stopped one, that uses a variable of the frame — worked out on a worker from a
    /// snapshot of the text and handed to the editor as ghost text.
    /// </summary>
    private void ShowInlineValues(DebugSession session, DapStackFrame frame, DebugNode? scope)
    {
        ClearInline();
        if (!_shell.Settings.DebugInlineValues || scope is null || frame.Source?.Path is not { } path) return;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in scope.Children.Where(c => c.Kind == DebugNodeKind.Variable && c.Value.Length > 0)) values[variable.Name] = Short(variable.Value);
        if (values.Count == 0) return;
        var tabs = _binders.Where(b => SamePath(b.Tab.Path, path)).ToList();
        if (tabs.Count == 0) return;
        var line = frame.Line - 1;
        var text = tabs[0].Tab.Document.Text;
        var version = tabs[0].Tab.Document.Version;
        var stamp = _stopVersion;
        _ = Task.Run(() =>
        {
            var inlays = new List<InlineDecoration>();
            if (line >= text.LineCount) return;
            for (var i = Math.Max(0, line - InlineReach); i <= line; i++)
            {
                var content = text.Line(i);
                var trimmed = content.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#') || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal)) continue; // not a path: comment markers
                var shown = new List<string>();
                foreach (Match m in Identifier.Matches(content))
                {
                    if (values.TryGetValue(m.Value, out var value) && !shown.Any(s => s.StartsWith(m.Value + " =", StringComparison.Ordinal))) shown.Add($"{m.Value} = {value}");
                }

                if (shown.Count > 0) inlays.Add(new InlineDecoration(text.LineStart(i) + content.Length, "  " + string.Join(", ", shown), InlineStyle.Ghost));
            }

            var set = new DecorationSet([], [], [], inlays);
            _post(() =>
            {
                if (stamp != _stopVersion || _current is null) return;
                foreach (var binder in tabs) binder.Editor.SetDecorations(InlineLayer, set, version);
            });
        });
    }

    private static string Short(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= 48 ? value : value[..47] + "…";
    }

    // ── One editor ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The debugger's hold on one editor: its gutter, the right-click menu, and the value card over a name.</summary>
    private sealed class Binder : IDisposable
    {
        private readonly DebugController _c;
        private readonly EditorPopup _popup;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly HashSet<string> _failed = [];
        private (int Start, int End) _range = (-1, -1);
        private (int Start, int End) _shown = (-1, -1);
        private int _generation;

        public Binder(DebugController controller, EditorTab tab, CodeEditor editor)
        {
            _c = controller;
            Tab = tab;
            Editor = editor;
            _popup = new EditorPopup(editor, "DebugHoverCard", 520, 320);
            editor.ShowGlyphMargin = true;
            var previous = editor.GlyphMarginClicked;
            editor.GlyphMarginClicked = line =>
            {
                var rope = editor.Document.Text;
                var key = rope.LineStart(Math.Clamp(line, 0, rope.LineCount - 1));
                // A lightbulb on the line takes the click for its quick fixes; any other click is a breakpoint.
                if (previous is not null && editor.Decorations[EditorLanguageUi.LightbulbLayer].Gutter.Any(g => g.LineStart == key)) previous(line);
                else if (Tab.Path is { } path) _c.ToggleBreakpoint(path, line + 1);
            };
            editor.GlyphMarginContext = (line, at) => _c.ShowBreakpointMenu(this, line + 1, at);
            editor.PointerOverText += OnPointer;
            editor.HoverTaken = Taken;
            editor.Scrolled += HideCard;
            editor.Document.Changed += OnChanged;
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                _ = ShowCardAsync();
            };
        }

        public EditorTab Tab { get; }

        public CodeEditor Editor { get; }

        public void Dispose()
        {
            _timer.Stop();
            _popup.Hide();
            Editor.PointerOverText -= OnPointer;
            Editor.Scrolled -= HideCard;
            Editor.Document.Changed -= OnChanged;
            Editor.HoverTaken = null;
        }

        private void OnChanged(DocumentChange change)
        {
            HideCard();
            _failed.Clear();
        }

        private void HideCard()
        {
            _generation++;
            _shown = (-1, -1);
            _popup.Hide();
        }

        /// <summary>Draws the breakpoints and the stopped line of this file.</summary>
        public void Redecorate()
        {
            if (Tab.Path is not { } path) return;
            var document = Editor.Document;
            var text = document.Text;
            var gutter = new List<GutterDecoration>();
            var lines = new List<LineDecoration>();
            var anySession = _c.Manager.Sessions.Count > 0;
            foreach (var bp in _c.Breakpoints.ForFile(path))
            {
                if (bp.Line < 1 || bp.Line > text.LineCount) continue;
                var glyph = !bp.Enabled ? GutterGlyph.BreakpointDisabled
                    : anySession && _c.ResultOf(bp.Id) is { Verified: false } ? GutterGlyph.BreakpointUnverified
                    : bp.Kind switch { BreakpointKind.Logpoint => GutterGlyph.Logpoint, BreakpointKind.Conditional => GutterGlyph.BreakpointConditional, _ => GutterGlyph.Breakpoint };
                gutter.Add(new GutterDecoration(DecorationSet.LineKey(text, bp.Line - 1), glyph));
            }

            if (_c.CurrentFrame is { Frame: { } frame } && SamePath(frame.Source?.Path, path) && frame.Line >= 1 && frame.Line <= text.LineCount)
            {
                var key = DecorationSet.LineKey(text, frame.Line - 1);
                lines.Add(new LineDecoration(key, LineStyle.Stopped));
                gutter.Add(new GutterDecoration(key, GutterGlyph.Stopped));
            }

            Editor.SetDecorations(MarksLayer, new DecorationSet([], lines, gutter, []), document.Version);
        }

        // ── The value card ───────────────────────────────────────────────────────────────────────

        /// <summary>The expression under an offset: the selection when the offset is inside it, otherwise a name with the member accesses before it.</summary>
        private (int Start, int End) ExpressionAt(int offset)
        {
            var document = Editor.Document;
            var text = document.Text;
            var selection = document.Primary;
            if (!selection.IsEmpty && offset >= selection.Start && offset <= selection.End && selection.Length <= 200 && !text.Substring(selection.Start, selection.Length).Contains('\n')) return (selection.Start, selection.End);
            var (start, end) = TextNavigation.WordAt(text, offset);
            if (end <= start || char.IsDigit(text[start])) return (-1, -1);
            while (start >= 2 && text[start - 1] == '.')
            {
                var (before, after) = TextNavigation.WordAt(text, start - 1);
                if (after != start - 1 || before >= after) break;
                start = before;
            }

            return (start, end);
        }

        private bool Taken(int offset)
        {
            if (!_c.IsStopped || _c.CurrentFrame is null) return false;
            var range = ExpressionAt(offset);
            return range.Start >= 0 && !_failed.Contains(Editor.Document.Text.Substring(range.Start, range.End - range.Start));
        }

        private void OnPointer(int offset, Point at, Avalonia.Input.KeyModifiers mods)
        {
            if (!_c.IsStopped || offset < 0 || mods.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                _timer.Stop();
                if (_popup.IsOpen && !_popup.IsPointerOver) DispatcherTimer.RunOnce(() =>
                {
                    if (!_popup.IsPointerOver && !Editor.IsPointerOver) HideCard();
                }, TimeSpan.FromMilliseconds(300));
                return;
            }

            var range = ExpressionAt(offset);
            if (range.Start < 0)
            {
                _timer.Stop();
                if (_popup.IsOpen && !_popup.IsPointerOver) HideCard();
                return;
            }

            if (range == _shown && _popup.IsOpen) return;
            if (_popup.IsOpen && !_popup.IsPointerOver) HideCard();
            _range = range;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, _c._shell.Settings.HoverDelay));
            _timer.Stop();
            _timer.Start();
        }

        private async Task ShowCardAsync()
        {
            if (_c.CurrentFrame is not { } current || current.Session.State != DebugSessionState.Stopped || _range.Start < 0) return;
            var range = _range;
            var text = Editor.Document.Text;
            if (range.End > text.Length) return;
            var expression = text.Substring(range.Start, range.End - range.Start);
            var generation = ++_generation;
            DapEvaluation result;
            List<DapVariable> children = [];
            try
            {
                (result, children) = await Task.Run(async () =>
                {
                    var r = await current.Session.EvaluateAsync(expression, current.Frame.Id, "hover");
                    var kids = r.VariablesReference > 0 ? (await current.Session.VariablesAsync(r.VariablesReference, count: 13)).ToList() : [];
                    return (r, kids);
                });
            }
            catch (Exception ex) when (ex is DapException or IOException)
            {
                _failed.Add(expression);
                return;
            }

            if (generation != _generation || Editor.Document.Version != Tab.Document.Version) return;
            var muted = EditorLanguageUi.Resource<IBrush>("Brush.Text.Muted");
            var stack = new StackPanel { Spacing = 3 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new TextBlock { Text = expression, FontWeight = FontWeight.SemiBold });
            head.Children.Add(new TextBlock { Text = result.Result, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            stack.Children.Add(head);
            if (result.Type is { Length: > 0 } type) stack.Children.Add(new TextBlock { Text = type, Foreground = muted, FontSize = 11 });
            foreach (var child in children.Take(12))
            {
                stack.Children.Add(new TextBlock { Text = $"{child.Name}: {child.Value}", Foreground = EditorLanguageUi.Resource<IBrush>("Brush.Text.Secondary"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 480 });
            }

            if (children.Count > 12) stack.Children.Add(new TextBlock { Text = "…", Foreground = muted });
            _popup.Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            _shown = range;
            _popup.Show(_popup.AnchorFor(range.Start, Math.Max(range.End, range.Start + 1)), above: true);
        }
    }
}
