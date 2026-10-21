using Avalonia;
using Avalonia.Controls;
using TextMateSharp.Grammars;
using Wicked.Editor.Syntax;

namespace Wicked.App.Chat;

/// <summary>The one code highlighter every code block in the chat shares (one thread, started when the first block needs it).</summary>
public static class ChatHighlighter
{
    private static readonly Lazy<CodeHighlighter> Instance = new(() => new CodeHighlighter(_theme), LazyThreadSafetyMode.ExecutionAndPublication);
    private static ThemeName _theme = ThemeName.DarkPlus;

    public static CodeHighlighter Shared => Instance.Value;

    /// <summary>The code colours (Settings, Code colours); blocks already on screen are highlighted again.</summary>
    public static ThemeName Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            if (Instance.IsValueCreated) Instance.Value.Theme = value;
        }
    }
}

/// <summary>What a code block's buttons do; the chat's view model provides it.</summary>
public interface ICodeActions
{
    /// <summary>Whether Insert has an editor to put the code in.</summary>
    bool CanInsert { get; }

    event EventHandler? CanInsertChanged;

    void Copy(string text);

    void Insert(string text);

    /// <summary>Proposes the code to the active editor as a review (over the selection, the whole file, or at the caret).</summary>
    void Apply(string text);

    void OpenLink(string url);
}

/// <summary>The chat's actions, set once on the chat view and inherited by every block and code block beneath it.</summary>
public static class ChatContext
{
    public static readonly AttachedProperty<ICodeActions?> ActionsProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, ICodeActions?>("Actions", inherits: true);

    public static ICodeActions? GetActions(Control control) => control.GetValue(ActionsProperty);

    public static void SetActions(Control control, ICodeActions? value) => control.SetValue(ActionsProperty, value);
}
