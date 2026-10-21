using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wicked.App.Chat;
using Wicked.Core.Lsp;
using Wicked.Editor.Model;

namespace Wicked.App.Language;

public sealed partial class EditorLanguageUi
{
    // ── Signature help ───────────────────────────────────────────────────────────────────────────

    private EditorPopup _signature = null!;
    private CancellationTokenSource? _signatureCts;
    private SignatureHelp? _help;
    private int _activeSignature;

    /// <summary>The signature help shown now (tests read it).</summary>
    public SignatureHelp? Signature => _signature.IsOpen ? _help : null;

    private void InitSignature() => _signature = new EditorPopup(_editor, "SignatureHelp", 640, 260);

    private static IReadOnlyList<string> SignatureTriggers(LanguageClient client, string member)
    {
        if (!client.Initialized.Capabilities.TryGetProperty("signatureHelpProvider", out var p) || p.ValueKind != JsonValueKind.Object) return [];
        return p.TryGetProperty(member, out var t) && t.ValueKind == JsonValueKind.Array ? [.. t.EnumerateArray().Select(x => x.GetString()!)] : [];
    }

    private void OnTypedForSignature(char last, LanguageClient client)
    {
        if (!_features.Settings.ParameterHints) return;
        var c = last.ToString();
        if (SignatureTriggers(client, "triggerCharacters").Contains(c) || (_signature.IsOpen && SignatureTriggers(client, "retriggerCharacters").Contains(c)))
        {
            _ = TriggerSignatureAsync();
        }
        else if (_signature.IsOpen)
        {
            // Typing an argument moves the active parameter; the server says which.
            _ = TriggerSignatureAsync();
        }
    }

    private void OnCaretMovedForSignature()
    {
        if (_signature.IsOpen) _ = TriggerSignatureAsync();
    }

    /// <summary>Ctrl+Shift+Space: the signature of the call the caret is in.</summary>
    public async Task TriggerSignatureAsync()
    {
        if (Doc is not { } doc) return;
        _signatureCts?.Cancel();
        var cts = _signatureCts = new CancellationTokenSource();
        var caret = _editor.Document.Primary.Active;
        SignatureHelp? help;
        try
        {
            help = await doc.RequestAsync((c, text, _) => c.Supports("signatureHelpProvider") ? c.SignatureHelpAsync(doc.Uri, LspText.PositionOf(text, caret), cts.Token) : Task.FromResult<SignatureHelp?>(null), cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return;
        }

        if (cts.IsCancellationRequested) return;
        if (help is not { Signatures.Count: > 0 })
        {
            CloseSignature();
            return;
        }

        if (_help is null || _help.Signatures.Count != help.Signatures.Count) _activeSignature = help.ActiveSignature ?? 0;
        _help = help;
        await ShowSignatureAsync(cts.Token);
    }

    private async Task ShowSignatureAsync(CancellationToken ct)
    {
        if (_help is not { } help) return;
        var index = Math.Clamp(_activeSignature, 0, help.Signatures.Count - 1);
        var sig = help.Signatures[index];
        var active = sig.ActiveParameter ?? help.ActiveParameter ?? 0;
        var (from, to) = ParameterSpan(sig, active);
        var label = new SelectableTextBlock { FontFamily = Resource<FontFamily>("Font.Code") ?? FontFamily.Default, TextWrapping = TextWrapping.Wrap };
        label.Inlines = [];
        if (from >= 0 && to > from && to <= sig.Label.Length)
        {
            label.Inlines.Add(new Run(sig.Label[..from]));
            label.Inlines.Add(new Run(sig.Label[from..to]) { FontWeight = FontWeight.Bold, Foreground = Resource<IBrush>("Brush.Accent") });
            label.Inlines.Add(new Run(sig.Label[to..]));
        }
        else label.Inlines.Add(new Run(sig.Label));

        var stack = new StackPanel { Spacing = 6 };
        var head = new DockPanel();
        if (help.Signatures.Count > 1)
        {
            var count = new TextBlock { Text = $"{index + 1}/{help.Signatures.Count}", Classes = { "muted" }, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(count, Dock.Left);
            head.Children.Add(count);
        }

        head.Children.Add(label);
        stack.Children.Add(head);
        var parameterDoc = sig.Parameters is { } ps && active >= 0 && active < ps.Count ? LspShapes.Text(ps[active].Documentation) : "";
        var docs = string.Join("\n\n", new[] { parameterDoc, LspShapes.Text(sig.Documentation) }.Where(t => t.Length > 0));
        if (docs.Length > 0)
        {
            var blocks = await Task.Run(() => MarkdownParser.Parse(docs), ct);
            if (ct.IsCancellationRequested) return;
            stack.Children.Add(new MarkdownView { Blocks = blocks });
        }

        _signature.Content = stack;
        var caret = _editor.Document.Primary.Active;
        _signature.Show(_signature.AnchorFor(caret, caret + 1), above: true);
    }

    /// <summary>Where a parameter sits in the signature's label: its label is a substring or an [start, end) pair.</summary>
    private static (int From, int To) ParameterSpan(SignatureInformation sig, int active)
    {
        if (sig.Parameters is not { } ps || active < 0 || active >= ps.Count) return (-1, -1);
        var label = ps[active].Label;
        if (label.ValueKind == JsonValueKind.Array && label.GetArrayLength() == 2) return (label[0].GetInt32(), label[1].GetInt32());
        if (label.ValueKind == JsonValueKind.String && label.GetString() is { Length: > 0 } name)
        {
            var at = sig.Label.IndexOf(name, sig.Label.IndexOf('(') + 1, StringComparison.Ordinal);
            if (at < 0) at = sig.Label.IndexOf(name, StringComparison.Ordinal);
            return at < 0 ? (-1, -1) : (at, at + name.Length);
        }

        return (-1, -1);
    }

    private bool SignatureKey(KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            _ = TriggerSignatureAsync();
            return true;
        }

        if (!_signature.IsOpen || _help is null) return false;
        switch (e.Key)
        {
            case Key.Escape:
                CloseSignature();
                return true;
            case Key.Up or Key.Down when _help.Signatures.Count > 1 && !_suggest.IsOpen && e.KeyModifiers == KeyModifiers.Alt:
                _activeSignature = (_activeSignature + (e.Key == Key.Down ? 1 : -1) + _help.Signatures.Count) % _help.Signatures.Count;
                _ = ShowSignatureAsync(CancellationToken.None);
                return true;
            default:
                return false;
        }
    }

    public void CloseSignature()
    {
        _signatureCts?.Cancel();
        _signature.Hide();
        _help = null;
    }

    // ── Rename (F2) ──────────────────────────────────────────────────────────────────────────────

    private EditorPopup? _rename;
    private TextBox? _renameBox;
    private (int Start, int End) _renameRange;

    /// <summary>Whether the rename box is open (tests).</summary>
    public bool RenameOpen => _rename?.IsOpen == true;

    /// <summary>F2: a box over the symbol with its name; Enter renames it everywhere the server knows of, Escape leaves it.</summary>
    public async Task StartRenameAsync()
    {
        if (Doc is not { } doc) return;
        var caret = _editor.Document.Primary.Active;
        (int, int, string?)? prepared;
        try
        {
            prepared = await doc.RequestAsync(async (c, text, _) =>
            {
                if (!c.Supports("renameProvider")) return ((int, int, string?)?)null;
                var canPrepare = c.Initialized.Capabilities.TryGetProperty("renameProvider", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("prepareProvider", out var pp) && pp.ValueKind == JsonValueKind.True;
                if (canPrepare && await c.PrepareRenameAsync(doc.Uri, LspText.PositionOf(text, caret)).ConfigureAwait(false) is { } r)
                {
                    var (s, e) = LspText.OffsetsOf(text, r.Range);
                    return (s, e, r.Placeholder);
                }

                if (canPrepare) return null;
                var (ws, we) = TextNavigation.WordAt(text, caret);
                return (ws, we, (string?)null);
            });
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return;
        }

        if (prepared is not { } at || at.Item2 <= at.Item1)
        {
            _features.Tell("This element cannot be renamed.");
            return;
        }

        var rope = _editor.Document.Text;
        var current = at.Item3 ?? rope.Substring(at.Item1, at.Item2 - at.Item1);
        _renameRange = (at.Item1, at.Item2);
        _rename ??= new EditorPopup(_editor, "RenameBox", 360, 80);
        _renameBox = new TextBox { Name = "RenameInput", Text = current, MinWidth = 220, Classes = { "find" } };
        _renameBox.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = FinishRenameAsync(_renameBox.Text ?? "");
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseRename();
                _editor.Focus();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _rename.Content = new StackPanel { Spacing = 4, Children = { _renameBox, new TextBlock { Text = "Enter to rename, Escape to cancel", Classes = { "muted" }, FontSize = 11 } } };
        _rename.Show(_rename.AnchorFor(at.Item1, at.Item2));
        _renameBox.Focus();
        _renameBox.SelectAll();
    }

    private bool RenameKey(KeyEventArgs e) => false;

    /// <summary>Enter in the rename box: the server's edit applied across the files it names.</summary>
    public async Task FinishRenameAsync(string newName)
    {
        CloseRename();
        _editor.Focus();
        if (Doc is not { } doc || newName.Length == 0) return;
        var (start, _) = _renameRange;
        var current = _editor.Document.Text.Substring(_renameRange.Start, _renameRange.End - _renameRange.Start);
        if (newName == current) return;
        WorkspaceEdit? edit;
        try
        {
            edit = await doc.RequestAsync((c, text, _) => c.RenameAsync(doc.Uri, LspText.PositionOf(text, start), newName));
        }
        catch (JsonRpcException ex)
        {
            _features.Tell($"Rename failed: {ex.Message}");
            return;
        }

        if (edit is null || edit.Changes.Count == 0)
        {
            _features.Tell("Rename produced no changes.");
            return;
        }

        await _features.ApplyWorkspaceEditAsync(edit);
    }

    public void CloseRename() => _rename?.Hide();

    // ── Quick fixes and refactorings (Ctrl+.) ────────────────────────────────────────────────────

    private EditorPopup? _actionsPopup;
    private ListBox? _actionsList;

    /// <summary>The actions offered now (tests).</summary>
    public IReadOnlyList<CodeAction> ActionsShown => _actionsList?.ItemsSource as IReadOnlyList<CodeAction> ?? [];

    public bool ActionsOpen => _actionsPopup?.IsOpen == true;

    /// <summary>Ctrl+. or a lightbulb click: the fixes and refactorings for the selection or the caret's problems.</summary>
    public async Task ShowActionsAsync()
    {
        if (Doc is not { } doc) return;
        var primary = _editor.Document.Primary;
        var line = _editor.Document.Text.LineOf(primary.Active);
        var problems = _features.DiagnosticsAt(doc, primary.Active);
        if (problems.Count == 0) problems = [.. _features.DiagnosticsOf(doc).Where(d => d.Range.Start.Line <= line && d.Range.End.Line >= line)];
        IReadOnlyList<CodeAction>? actions;
        try
        {
            actions = await doc.RequestAsync((c, text, _) => c.Supports("codeActionProvider")
                ? c.CodeActionsAsync(doc.Uri, LspText.RangeOf(text, primary.Start, primary.End), problems)
                : Task.FromResult<IReadOnlyList<CodeAction>>([]));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return;
        }

        if (actions is not { Count: > 0 })
        {
            _features.Tell("No code actions available");
            return;
        }

        var ordered = actions.OrderByDescending(a => a.IsPreferred).ThenBy(a => a.Kind?.StartsWith("quickfix", StringComparison.Ordinal) == true ? 0 : 1).ToList();
        _actionsPopup ??= new EditorPopup(_editor, "CodeActions", 520, 320);
        _actionsList = new ListBox
        {
            Name = "CodeActionItems",
            Classes = { "suggest" },
            ItemsSource = ordered,
            SelectedIndex = 0,
            Focusable = false,
            ItemTemplate = new FuncDataTemplate<CodeAction>((a, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Wicked.App.Shell.Icon { Data = Resource<Geometry>(a?.Kind?.StartsWith("quickfix", StringComparison.Ordinal) == true ? "Icon.Lightbulb" : "Icon.Wrench"), Size = 14, Foreground = Resource<IBrush>("Brush.Warning") },
                    new TextBlock { Text = a?.Title, FontWeight = a?.IsPreferred == true ? FontWeight.SemiBold : FontWeight.Normal },
                },
            }),
        };
        _actionsList.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            if (_actionsList.SelectedItem is CodeAction a) _ = RunActionAsync(a);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _actionsPopup.Content = _actionsList;
        var caret = _editor.Document.Primary.Active;
        _actionsPopup.Show(_actionsPopup.AnchorFor(caret, caret + 1));
    }

    private bool ActionsKey(KeyEventArgs e)
    {
        if (_actionsPopup?.IsOpen != true || _actionsList is null || _actionsList.ItemCount == 0) return false;
        var count = _actionsList.ItemCount;
        switch (e.Key)
        {
            case Key.Down:
                _actionsList.SelectedIndex = (_actionsList.SelectedIndex + 1) % count;
                return true;
            case Key.Up:
                _actionsList.SelectedIndex = (_actionsList.SelectedIndex - 1 + count) % count;
                return true;
            case Key.Enter or Key.Tab:
                if (_actionsList.SelectedItem is CodeAction a) _ = RunActionAsync(a);
                return true;
            case Key.Escape:
                CloseActions();
                return true;
            default:
                CloseActions();
                return false;
        }
    }

    public void CloseActions() => _actionsPopup?.Hide();

    /// <summary>A chosen action: its edit (resolved first when the server sends it lazily), then its command.</summary>
    public async Task RunActionAsync(CodeAction action)
    {
        CloseActions();
        if (Doc?.Client is not { } client) return;
        try
        {
            action = await client.ResolveAsync(action);
            if (action.Edit is { } edit) await _features.ApplyWorkspaceEditAsync(edit);
            if (action.Command is { ValueKind: JsonValueKind.Object } command)
            {
                // A code action's command is the Command object itself, or a bare command whose arguments ride beside it.
                await client.ExecuteCommandAsync(command.TryGetProperty("command", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : command);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            _features.Tell($"{action.Title} failed: {ex.Message}");
        }
    }
}
