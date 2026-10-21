using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wicked.App.Editor;
using Wicked.App.Language;
using Wicked.Core.Chat;

namespace Wicked.App.Assistant;

/// <summary>
/// Ctrl+I in an editor (VS Code's inline chat): a box over the selection takes an instruction ("make this async"), the
/// assistant's model rewrites the selection (or writes code at the caret) on a worker, and the answer opens as a review
/// to accept change by change. Escape or a click elsewhere drops it.
/// </summary>
public sealed class InlineEdit
{
    private const string Instructions = """
        You edit code. You get a file's name, the code around a region, the region itself, and an instruction. Reply with
        the new code for the region only: no explanation, no code fences, the same indentation as the region. If the region
        is empty, reply with the code to insert there.
        """;

    private readonly CodeEditor _editor;
    private readonly IChatEngine _engine;
    private readonly Func<string?> _model;
    private readonly Action<int, int, string, string> _propose;
    private readonly EditorPopup _popup;
    private readonly TextBox _box;
    private readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 11 };
    private CancellationTokenSource? _cts;
    private (int Start, int End) _region;

    public InlineEdit(CodeEditor editor, IChatEngine engine, Func<string?> model, Action<int, int, string, string> propose)
    {
        _editor = editor;
        _engine = engine;
        _model = model;
        _propose = propose;
        _popup = new EditorPopup(editor, "InlineEdit", 520, 120);
        _box = new TextBox { Name = "InlineEditInput", Classes = { "find" }, MinWidth = 380, PlaceholderText = "Edit with the assistant (Enter to ask, Escape to close)" };
        _box.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        _popup.Content = new StackPanel { Spacing = 4, Children = { _box, _status } };
        editor.KeyPreviews.Add(e =>
        {
            if (e.Key != Key.I || e.KeyModifiers != KeyModifiers.Control) return false;
            Open();
            return true;
        });
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>A request finished (tests wait on it): whether a proposal came of it.</summary>
    public event Action<bool>? Answered;

    public void Open()
    {
        var p = _editor.Document.Primary;
        _region = (p.Start, p.End);
        _status.Text = _model() is null ? "No model to ask: install one from the Models panel." : p.IsEmpty ? "Writes code at the caret." : "Rewrites the selection.";
        _popup.Show(_popup.AnchorFor(p.Start, p.Start + 1), above: true);
        _box.Text = "";
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _box.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    public void Close()
    {
        _cts?.Cancel();
        _popup.Hide();
        _editor.Focus();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && (_box.Text ?? "").Trim().Length > 0)
        {
            e.Handled = true;
            _ = AskAsync(_box.Text!.Trim());
        }
    }

    /// <summary>Asks about a given stretch (a problem's lines to fix), without the box.</summary>
    public Task AskAboutAsync(int start, int end, string instruction)
    {
        _region = (start, end);
        return AskAsync(instruction);
    }

    /// <summary>Asks for the rewrite; the answer becomes a proposal over the region.</summary>
    public async Task AskAsync(string instruction)
    {
        if (_model() is not { } model) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var text = _editor.Document.Text;
        var (start, end) = _region;
        var firstLine = text.LineOf(start);
        var lastLine = text.LineOf(end);
        var before = text.Substring(text.LineStart(Math.Max(0, firstLine - 40)), start - text.LineStart(Math.Max(0, firstLine - 40)));
        var afterEnd = lastLine + 40 < text.LineCount ? text.LineStart(lastLine + 40) : text.Length;
        var after = text.Substring(end, afterEnd - end);
        var region = text.Substring(start, end - start);
        var file = _editor.FilePath is { } path ? Path.GetFileName(path) : "untitled";
        _status.Text = "Writing…";
        var prompt = $"File: {file}\n\nCode before the region:\n{before}\n<<<REGION>>>\n{region}\n<<<END REGION>>>\nCode after the region:\n{after}\n\nInstruction: {instruction}";
        var answer = new StringBuilder();
        try
        {
            await Task.Run(async () =>
            {
                await foreach (var chunk in _engine.StreamAsync(new ChatRequest(model, [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, prompt)], new ChatSampling(Temperature: 0.2)), cts.Token).ConfigureAwait(false))
                {
                    if (chunk is ContentDelta d) answer.Append(d.Text);
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ChatException ex)
        {
            _status.Text = ex.Message;
            Answered?.Invoke(false);
            return;
        }

        var code = StripFences(answer.ToString());
        if (code.Length == 0 && region.Length == 0)
        {
            _status.Text = "The model gave nothing back.";
            Answered?.Invoke(false);
            return;
        }

        // Code that ended a selection of whole lines keeps its last line break.
        if (region.EndsWith('\n') && !code.EndsWith('\n')) code += "\n";
        _popup.Hide();
        _propose(start, end, code, instruction);
        Answered?.Invoke(true);
    }

    /// <summary>The code inside a reply that came in a fence anyway (models add them).</summary>
    internal static string StripFences(string reply)
    {
        var s = reply.Trim('\n', '\r');
        var open = s.IndexOf("```", StringComparison.Ordinal);
        if (open < 0) return s;
        var lineEnd = s.IndexOf('\n', open);
        if (lineEnd < 0) return s;
        var close = s.IndexOf("```", lineEnd, StringComparison.Ordinal);
        return (close < 0 ? s[(lineEnd + 1)..] : s[(lineEnd + 1)..close]).TrimEnd('\n', '\r');
    }
}
