using Avalonia.Input;
using Avalonia.Threading;
using Wicked.App.Editor;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.App.Assistant;

/// <summary>
/// Ghost text in one editor (D18): when typing pauses, the text around the caret goes to a local coding model's
/// fill-in-the-middle endpoint on a worker, and its answer is drawn faded after the caret. Tab takes all of it,
/// Ctrl+Right the next word, Escape drops it; typing what it says walks into it, anything else asks again. A newer
/// keystroke cancels the request in flight, so a slow model never shows a suggestion for text that is gone.
/// </summary>
public sealed class InlineCompletions
{
    public const string Layer = "ghost";
    private const int PrefixChars = 6000;
    private const int SuffixChars = 2000;
    private const int ExtraChars = 1500;

    private readonly CodeEditor _editor;
    private readonly IInfillEngine _engine;
    private readonly Func<string?> _model;
    private readonly Func<IReadOnlyList<InfillContext>> _neighbours;
    private readonly Func<Wicked.Core.Settings.WickedSettings> _settings;
    private readonly DispatcherTimer _pause;
    private CancellationTokenSource? _cts;

    public InlineCompletions(CodeEditor editor, IInfillEngine engine, Func<string?> model, Func<IReadOnlyList<InfillContext>> neighbours, Func<Wicked.Core.Settings.WickedSettings> settings)
    {
        _editor = editor;
        _engine = engine;
        _model = model;
        _neighbours = neighbours;
        _settings = settings;
        _pause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(50, settings().InlineSuggestDelay)) };
        _pause.Tick += (_, _) =>
        {
            _pause.Stop();
            _ = RequestAsync();
        };
        editor.Typed += OnTyped;
        editor.TypePreview = Chain(editor.TypePreview, OnTypePreview);
        editor.KeyPreviews.Add(OnKey);
        editor.Document.SelectionsChanged += () =>
        {
            if (Suggestion is not null && _editor.Document.Primary.Active != _at) Dismiss();
        };
        editor.LostFocus += (_, _) => Dismiss();
    }

    /// <summary>The suggestion shown now (all of it, every line), or null.</summary>
    public string? Suggestion { get; private set; }

    /// <summary>Where it would go in.</summary>
    private int _at = -1;

    /// <summary>A request finished (tests wait on it): whether it produced a suggestion.</summary>
    public event Action<bool>? Answered;

    private static Func<string, bool> Chain(Func<string, bool>? first, Func<string, bool> then) =>
        first is null ? then : text => first(text) || then(text);

    private void OnTyped(string text)
    {
        if (!_settings().InlineSuggest) return;
        _pause.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _settings().InlineSuggestDelay));
        _pause.Stop();
        _pause.Start();
    }

    /// <summary>Alt+\ (VS Code's Trigger Inline Suggestion): ask now.</summary>
    public Task TriggerAsync() => RequestAsync(explicitly: true);

    private async Task RequestAsync(bool explicitly = false)
    {
        _cts?.Cancel();
        var doc = _editor.Document;
        if (doc.Selections.Length != 1 || !doc.Primary.IsEmpty || _editor.InSnippet || doc.IsReadOnly) return;
        if (_model() is not { Length: > 0 } model)
        {
            if (explicitly) Log.Info("No coding model for suggestions: install one from the Models panel or set wicked.completionModel");
            return;
        }

        var caret = doc.Primary.Active;
        var text = doc.Text;
        var line = text.LineOf(caret);
        var lineEnd = text.LineStart(line) + text.LineLength(line);
        // Only where a suggestion reads naturally: at the end of a line, or before closing punctuation.
        var rest = text.Substring(caret, lineEnd - caret);
        if (!explicitly && rest.Trim().Length > 0 && !rest.TrimStart().All(c => ")]}\"';,".Contains(c))) return;

        var cts = _cts = new CancellationTokenSource();
        var version = doc.Version;
        var extra = _neighbours();
        InfillResult? result = null;
        var suffixText = "";
        try
        {
            var start = Math.Max(0, caret - PrefixChars);
            var end = Math.Min(text.Length, caret + SuffixChars);
            var prefix = await Task.Run(() => text.Substring(start, caret - start), cts.Token);
            var suffix = await Task.Run(() => text.Substring(caret, end - caret), cts.Token);
            var trimmed = extra.Select(e => e with { Text = e.Text.Length > ExtraChars ? e.Text[..ExtraChars] : e.Text }).ToList();
            result = await _engine.CompleteAsync(new InfillRequest(model, prefix, suffix, trimmed, Stop: ["```", "\n\n\n"]), cts.Token);
            suffixText = suffix;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ChatException ex)
        {
            Log.Warn($"A suggestion failed: {ex.Message}");
            Answered?.Invoke(false);
            return;
        }

        if (cts.IsCancellationRequested || doc.Version != version || doc.Primary.Active != caret) return;
        var suggestion = Clean(result.Text, rest, suffixText);
        if (suggestion.Length == 0)
        {
            Answered?.Invoke(false);
            return;
        }

        Show(suggestion, caret);
        Answered?.Invoke(true);
    }

    /// <summary>
    /// The model's text made fit to show: cut where it starts writing prose (a code fence) or repeats the code after the
    /// caret (its next line), without trailing blank lines, and without what already follows the caret on the line.
    /// </summary>
    internal static string Clean(string text, string restOfLine, string suffix = "")
    {
        var s = text;
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0) s = s[..fence];
        var next = suffix.Split('\n').Skip(1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (next is not null)
        {
            var lines = s.Split('\n');
            var repeat = Array.FindIndex(lines, 1, l => l.Trim() == next);
            if (repeat > 0) s = string.Join('\n', lines[..repeat]);
        }

        s = s.TrimEnd();
        var rest = restOfLine.TrimEnd();
        if (rest.Length > 0 && s.EndsWith(rest, StringComparison.Ordinal)) s = s[..^rest.Length];
        return s;
    }

    private void Show(string suggestion, int at)
    {
        Suggestion = suggestion;
        _at = at;
        var lines = suggestion.Split('\n');
        // The first line in the text; more lines are counted after it until the editor can draw rows of its own.
        var shown = lines.Length == 1 ? lines[0] : $"{lines[0]}  ⏎ +{lines.Length - 1} lines (Tab)";
        _editor.SetDecorations(Layer, new DecorationSet([], [], [], [new InlineDecoration(at, shown, InlineStyle.Ghost)]), _editor.Document.Version);
    }

    public void Dismiss()
    {
        _cts?.Cancel();
        _pause.Stop();
        if (Suggestion is null) return;
        Suggestion = null;
        _at = -1;
        _editor.SetDecorations(Layer, DecorationSet.Empty, _editor.Document.Version);
    }

    /// <summary>Typing the suggestion's next character walks into it instead of asking again.</summary>
    private bool OnTypePreview(string typed)
    {
        if (Suggestion is not { } s || !s.StartsWith(typed, StringComparison.Ordinal)) return false;
        var rest = s[typed.Length..];
        var at = _at + typed.Length;
        _editor.Document.Apply([new TextChange(_at, 0, typed)], [Selection.Caret(at)], EditKind.Typing);
        if (rest.Length == 0) Dismiss();
        else Show(rest, at);
        return true;
    }

    private bool OnKey(KeyEventArgs e)
    {
        if (Suggestion is not { } s) return false;
        switch (e.Key)
        {
            case Key.Tab when e.KeyModifiers == KeyModifiers.None && !_editor.InSnippet:
                Accept(s);
                return true;
            case Key.Right when e.KeyModifiers == KeyModifiers.Control:
                var word = NextWord(s);
                Accept(word);
                if (word.Length < s.Length) Show(s[word.Length..], _at);
                return true;
            case Key.Escape:
                Dismiss();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Puts <paramref name="text"/> (all or the front of the suggestion) in at the caret, as one undo step.</summary>
    private void Accept(string text)
    {
        var at = _at;
        _editor.Document.Apply([new TextChange(at, 0, text)], [Selection.Caret(at + text.Length)]);
        if (text == Suggestion) Dismiss();
        else _at = at + text.Length;
        _editor.RevealCaret();
    }

    /// <summary>The suggestion up to the end of its next word (with the spaces before it), as Ctrl+Right takes it.</summary>
    internal static string NextWord(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]) && s[i] != '\n') i++;
        if (i < s.Length && !char.IsLetterOrDigit(s[i]) && s[i] != '_') return s[..(i + 1)];
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return s[..Math.Max(1, i)];
    }
}
