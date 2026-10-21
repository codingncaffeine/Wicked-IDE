using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wicked.App.Chat;
using Wicked.Core.Files;
using Wicked.Core.Lsp;
using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.App.Language;

/// <summary>A suggestion as the list shows it: the server's item, its icon, and where it sorts for what was typed.</summary>
public sealed record SuggestionRow(CompletionItem Item, string Icon, string Detail, int Score)
{
    public string Label => Item.Label;
}

public sealed partial class EditorLanguageUi
{
    private const int MaxRows = 12;

    private EditorPopup _suggest = null!;
    private ListBox _list = null!;
    private EditorPopup _details = null!;
    private CancellationTokenSource? _suggestCts;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _resolveCts;
    private IReadOnlyList<CompletionItem> _all = [];
    private bool _incomplete;
    private int _wordStart = -1;
    private long _requestVersion;
    private Rope? _requestText;

    /// <summary>Whether the suggestion list is open.</summary>
    public bool SuggestionsOpen => _suggest.IsOpen;

    /// <summary>The rows shown now, best first (tests read them).</summary>
    public IReadOnlyList<SuggestionRow> Suggestions => _list.ItemsSource as IReadOnlyList<SuggestionRow> ?? [];

    private void InitSuggestions()
    {
        _suggest = new EditorPopup(_editor, "SuggestList", 520, 320);
        _details = new EditorPopup(_editor, "SuggestDetails", 420, 320);
        _list = new ListBox
        {
            Name = "SuggestItems",
            Classes = { "suggest" },
            Focusable = false,
            ItemTemplate = new FuncDataTemplate<SuggestionRow>((row, _) => row is null ? new TextBlock() : RowView(row)),
        };
        _list.SelectionChanged += (_, _) => _ = ShowDetailsAsync();
        _list.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            if (_list.SelectedItem is SuggestionRow row) _ = AcceptAsync(row);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _suggest.Content = _list;
    }

    private static Control RowView(SuggestionRow row) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        Children =
        {
            new Wicked.App.Shell.Icon { Data = Resource<Geometry>(row.Icon), Size = 14, Foreground = Resource<IBrush>(IconBrush(row.Item.Kind)), Margin = new Thickness(0, 0, 8, 0) },
            new TextBlock { [Grid.ColumnProperty] = 1, Text = row.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { [Grid.ColumnProperty] = 2, Text = row.Detail, Classes = { "muted" }, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    /// <summary>The protocol's completion kinds as icons, close to the codicons VS Code uses.</summary>
    internal static string IconFor(int? kind) => kind switch
    {
        2 or 3 or 4 => "Icon.SquareFunction",          // method, function, constructor
        5 or 10 => "Icon.Wrench",                        // field, property
        6 => "Icon.Variable",                            // variable
        7 or 22 => "Icon.Box",                           // class, struct
        8 => "Icon.Braces",                              // interface
        9 => "Icon.Boxes",                               // module
        13 or 20 => "Icon.ListTree",                     // enum, enum member
        14 => "Icon.KeyRound",                           // keyword
        15 => "Icon.FileCode",                           // snippet
        17 or 19 => "Icon.FileText",                     // file, folder
        21 => "Icon.CircleDot",                          // constant
        25 => "Icon.Braces",                             // type parameter
        _ => "Icon.TextCursorInput",
    };

    private static string IconBrush(int? kind) => kind switch
    {
        2 or 3 or 4 => "Brush.Accent",
        7 or 8 or 13 or 22 or 25 => "Brush.Warning",
        6 or 5 or 10 or 21 or 20 => "Brush.Success",
        _ => "Brush.Text.Secondary",
    };

    // ── Opening ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+Space: suggestions for the caret, asked for whatever is typed.</summary>
    public Task TriggerSuggestAsync() => RequestSuggestionsAsync(null, invoked: true);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private void OnTyped(string text)
    {
        if (text.Length == 0) return;
        var last = text[^1];
        var settings = _features.Settings;
        if (Doc?.Client is not { } client)
        {
            // No server: the open documents' words, while a word is typed.
            if (settings.WordBasedSuggestions == "off") return;
            if (_suggest.IsOpen)
            {
                if (IsWordChar(last)) _ = FilterAsync();
                else CloseSuggestions();
            }
            else if (IsWordChar(last) && settings.QuickSuggestions && !char.IsDigit(PrefixAtCaret().FirstOrDefault('a'))) _ = RequestSuggestionsAsync(null, invoked: false);

            return;
        }

        var isTrigger = client.CompletionTriggers.Contains(last.ToString());
        if (_suggest.IsOpen)
        {
            if (isTrigger) _ = RequestSuggestionsAsync(last.ToString(), invoked: false);
            else if (IsWordChar(last)) _ = _incomplete ? RequestSuggestionsAsync(null, invoked: false) : FilterAsync();
            else CloseSuggestions();
        }
        else if (isTrigger && settings.SuggestOnTriggerCharacters) _ = RequestSuggestionsAsync(last.ToString(), invoked: false);
        else if (IsWordChar(last) && settings.QuickSuggestions && !char.IsDigit(PrefixAtCaret().FirstOrDefault('a'))) _ = RequestSuggestionsAsync(null, invoked: false);

        OnTypedForSignature(last, client);
        if (settings.FormatOnType && client.OnTypeFormattingTriggers.Contains(last.ToString())) _ = FormatOnTypeAsync(last.ToString());
    }

    /// <summary>The word typed so far before the caret (what the list is filtered by).</summary>
    private string PrefixAtCaret()
    {
        var rope = _editor.Document.Text;
        var caret = _editor.Document.Primary.Active;
        var start = caret;
        while (start > 0 && IsWordChar(rope[start - 1])) start--;
        return rope.Substring(start, caret - start);
    }

    private async Task RequestSuggestionsAsync(string? trigger, bool invoked)
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        var caret = _editor.Document.Primary.Active;
        var prefix = PrefixAtCaret();
        _wordStart = caret - prefix.Length;
        _requestText = _editor.Document.Text;
        _requestVersion = _editor.Document.Version;
        CompletionList? list = null;
        if (Doc is { } doc)
        {
            try
            {
                var answer = await doc.RequestAsync(async (c, text, version) =>
                {
                    if (!c.Supports("completionProvider")) return (List: (CompletionList?)null, Text: text, Version: version);
                    return (List: await c.CompletionAsync(doc.Uri, LspText.PositionOf(text, caret), trigger, cts.Token).ConfigureAwait(false), Text: text, Version: version);
                }, cts.Token);
                list = answer.List;
                if (answer.Text is not null)
                {
                    _requestText = answer.Text;
                    _requestVersion = answer.Version;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                return;
            }
        }

        if (cts.IsCancellationRequested) return;

        // No server, or nothing from it: the words of the open documents, as VS Code's word-based suggestions.
        if ((list is null || list.Items.Count == 0) && trigger is null && _features.Settings.WordBasedSuggestions != "off")
        {
            var sources = _features.WordSources(_editor.Document, _features.Settings.WordBasedSuggestions);
            var typed = (Start: _wordStart, End: caret);
            var current = _editor.Document.Text;
            list = await Task.Run(() => WordSuggestions.From(sources, current, typed), cts.Token);
            if (cts.IsCancellationRequested) return;
        }
        if (list is null || list.Items.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        _all = list.Items;
        _incomplete = list.IsIncomplete;
        await FilterAsync();
    }

    /// <summary>The items scored against what is typed, on a worker; the best first, the server's order for ties.</summary>
    private async Task FilterAsync()
    {
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        var prefix = PrefixAtCaret();
        var items = _all;
        var rows = await Task.Run(() => Rank(items, prefix), cts.Token);
        if (cts.IsCancellationRequested) return;
        if (rows.Count == 0 || (rows.Count == 1 && rows[0].Item.Label == prefix && prefix.Length > 0))
        {
            CloseSuggestions();
            return;
        }

        _list.ItemsSource = rows;
        _list.SelectedIndex = Math.Max(0, rows.ToList().FindIndex(r => r.Item.Preselect == true && prefix.Length == 0));
        _list.MaxHeight = Math.Min(rows.Count, MaxRows) * 24 + 8;
        var at = Math.Clamp(_wordStart, 0, _editor.Document.Text.Length);
        _suggest.Show(_suggest.AnchorFor(at, at + Math.Max(1, prefix.Length)));
    }

    internal static List<SuggestionRow> Rank(IReadOnlyList<CompletionItem> items, string prefix)
    {
        var rows = new List<SuggestionRow>(items.Count);
        foreach (var item in items)
        {
            var key = item.FilterText ?? item.Label;
            var score = prefix.Length == 0 ? 0 : FuzzyMatch.Score(prefix, key);
            if (score < 0) continue;
            // An exact start of the word ranks first, case-sensitively above case-insensitively, as VS Code ranks them.
            if (prefix.Length > 0 && key.StartsWith(prefix, StringComparison.Ordinal)) score += 1000;
            else if (prefix.Length > 0 && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) score += 500;
            rows.Add(new SuggestionRow(item, IconFor(item.Kind), item.Detail?.Split('\n')[0] ?? "", score));
        }

        return [.. rows.OrderByDescending(r => r.Score).ThenBy(r => r.Item.SortText ?? r.Item.Label, StringComparer.Ordinal).ThenBy(r => r.Item.Label, StringComparer.Ordinal).Take(400)];
    }

    public void CloseSuggestions()
    {
        _suggestCts?.Cancel();
        _filterCts?.Cancel();
        _resolveCts?.Cancel();
        _suggest.Hide();
        _details.Hide();
        _all = [];
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────

    private bool SuggestionKey(KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            _ = TriggerSuggestAsync();
            return true;
        }

        if (!_suggest.IsOpen || _list.ItemCount == 0) return false;
        var count = _list.ItemCount;
        switch (e.Key)
        {
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                _list.SelectedIndex = (_list.SelectedIndex + 1) % count;
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None:
                _list.SelectedIndex = (_list.SelectedIndex - 1 + count) % count;
                break;
            case Key.PageDown:
                _list.SelectedIndex = Math.Min(count - 1, _list.SelectedIndex + MaxRows - 1);
                break;
            case Key.PageUp:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - MaxRows + 1);
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None && _features.Settings.AcceptSuggestionOnEnter != "off":
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                if (_list.SelectedItem is SuggestionRow row) _ = AcceptAsync(row);
                break;
            case Key.Escape:
                CloseSuggestions();
                break;
            default:
                return false;
        }

        if (_list.SelectedItem is { } selected) _list.ScrollIntoView(selected);
        return true;
    }

    /// <summary>A commit character of the selected item (a '.' after a field, say) accepts it, then is typed.</summary>
    private bool OnTypePreview(string text)
    {
        if (!_suggest.IsOpen || text.Length != 1 || _list.SelectedItem is not SuggestionRow row) return false;
        if (row.Item.CommitCharacters?.Contains(text) == true) _ = AcceptAsync(row, wait: false);
        return false;
    }

    // ── Accepting ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts a suggestion in: its text edit (or its text over the typed word), as a snippet when it is one, its extra
    /// edits (an import added at the top) in the same undo step, then its command.
    /// </summary>
    public async Task AcceptAsync(SuggestionRow row, bool wait = true)
    {
        var doc = Doc;
        var item = row.Item;
        _suggest.Hide();
        _details.Hide();
        if (doc?.Client is { } client && item.AdditionalTextEdits is null && wait)
        {
            // Servers fill in imports lazily; a short wait keeps Enter feeling instant.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try
            {
                item = await client.ResolveAsync(item, cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        }

        var document = _editor.Document;
        var caret = document.Primary.Active;
        var (start, end, newText) = EditOf(item, document.Text, caret);
        if (item.AdditionalTextEdits is { Count: > 0 } extra)
        {
            // In the coordinates of the text as it is (the extra edits sit away from the caret, at the imports).
            var changes = LspText.ChangesFrom(document.Text, extra).Where(c => c.End <= start || c.Offset >= end).ToList();
            if (changes.Count > 0)
            {
                var set = ChangeSet.Normalise(changes);
                document.Apply(changes, LspText.MapSelections(document.Selections, changes), EditKind.Other, document.PrimaryIndex);
                start = ChangeSet.Map(start, set, stickAfter: false);
                end = ChangeSet.Map(end, set);
                caret = document.Primary.Active;
            }
        }

        if (item.InsertTextFormat == InsertTextFormat.Snippet)
        {
            if (end > caret) document.Apply([new TextChange(caret, end - caret, "")], [Selection.Caret(caret)]);
            document.SetSelections([Selection.Caret(caret)]);
            _editor.InsertSnippet(new SnippetDefinition(item.Label, ImmutableArray<string>.Empty, newText, "", ImmutableArray<string>.Empty), caret - start);
        }
        else
        {
            document.Apply([new TextChange(start, end - start, newText)], [Selection.Caret(start + newText.Length)]);
        }

        _editor.RevealCaret();
        await RunCommandAsync(item.Command);
    }

    /// <summary>
    /// Where a suggestion goes and what it says: its text edit's range (the insert range of an insert/replace pair, VS
    /// Code's default) stretched to the caret for what was typed since it was asked for, else the typed word.
    /// </summary>
    private (int Start, int End, string Text) EditOf(CompletionItem item, Rope text, int caret)
    {
        var wordStart = Math.Clamp(_wordStart, 0, caret);
        if (item.TextEdit is { ValueKind: JsonValueKind.Object } edit && edit.TryGetProperty("newText", out var nt))
        {
            var range = edit.TryGetProperty("range", out var r) ? r : edit.TryGetProperty("insert", out var ins) ? ins : default;
            if (range.ValueKind == JsonValueKind.Object)
            {
                var asked = _requestText ?? text;
                var (s, _) = LspText.OffsetsOf(asked, LspShapes.Read<Wicked.Core.Lsp.Range>(range));
                // The range was for the text when asked; typing since only added at the caret.
                return (Math.Min(s, caret), caret, Normalise(nt.GetString() ?? ""));
            }
        }

        return (wordStart, caret, Normalise(item.InsertText ?? item.Label));
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private async Task RunCommandAsync(JsonElement? command)
    {
        if (command is not { ValueKind: JsonValueKind.Object } c || !c.TryGetProperty("command", out var name)) return;
        switch (name.GetString())
        {
            case "editor.action.triggerParameterHints":
                await TriggerSignatureAsync();
                return;
            case "editor.action.triggerSuggest":
                await TriggerSuggestAsync();
                return;
        }

        if (Doc?.Client is { } client)
        {
            try
            {
                await client.ExecuteCommandAsync(c);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                Wicked.Core.Log.Warn($"A suggestion's command failed: {ex.Message}");
            }
        }
    }

    /// <summary>The selected suggestion's documentation beside the list, resolved from the server when it sends it lazily.</summary>
    private async Task ShowDetailsAsync()
    {
        _resolveCts?.Cancel();
        if (_list.SelectedItem is not SuggestionRow row || Doc?.Client is not { } client)
        {
            _details.Hide();
            return;
        }

        var cts = _resolveCts = new CancellationTokenSource();
        var item = row.Item;
        try
        {
            if (item.Documentation is null) item = await client.ResolveAsync(item, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return;
        }

        var docs = LspShapes.Text(item.Documentation);
        var markdown = (item.Detail is { Length: > 0 } detail ? $"```\n{detail}\n```\n\n" : "") + docs;
        if (cts.IsCancellationRequested || markdown.Trim().Length == 0 || !_suggest.IsOpen)
        {
            _details.Hide();
            return;
        }

        var blocks = await Task.Run(() => MarkdownParser.Parse(markdown), cts.Token);
        if (cts.IsCancellationRequested) return;
        _details.Content = new ScrollViewer { Content = new MarkdownView { Blocks = blocks }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var anchor = _suggest.AnchorFor(Math.Max(0, _wordStart), Math.Max(0, _wordStart) + 1);
        _details.Show(new Rect(anchor.X + 524, anchor.Y, 1, anchor.Height));
    }

    // ── Format on type ───────────────────────────────────────────────────────────────────────────

    private async Task FormatOnTypeAsync(string typed)
    {
        if (Doc is not { } doc || _features.TabOf(_editor) is not { } tab) return;
        var caret = _editor.Document.Primary.Active;
        var options = _features.FormattingOf(tab);
        var answer = await doc.RequestAsync(async (c, text, version) =>
            (Edits: await c.FormatOnTypeAsync(doc.Uri, LspText.PositionOf(text, caret), typed, options).ConfigureAwait(false), Version: version));
        if (answer.Edits is { Count: > 0 } edits && answer.Version == _editor.Document.Version) LanguageFeatures.ApplyEdits(_editor.Document, edits);
    }
}
