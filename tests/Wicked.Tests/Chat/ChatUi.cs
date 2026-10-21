using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Chat;
using Wicked.Tests.Models;

namespace Wicked.Tests.Chat;

/// <summary>Finding things in the assistant the way a person looks, and doing what a person does.</summary>
internal static class ChatUi
{
    public static T Control<T>(Window window, string name) where T : Control
    {
        T? found = null;
        Ui.PumpUntil(() => (found = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)) is not null, 5);
        return found ?? throw new InvalidOperationException($"No visible {typeof(T).Name} called {name}");
    }

    public static bool Absent<T>(Window window, string name) where T : Control =>
        !window.GetVisualDescendants().OfType<T>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    public static IEnumerable<T> All<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Where(c => c.Name == name && c.IsEffectivelyVisible);

    public static Button? Named(Control root, string automationName) => ModelsUi.Named(root, automationName);

    /// <summary>Clicks the composer and types.</summary>
    public static void Type(Window window, string text)
    {
        Ui.Pump(0.15); // a person looks before they click: the layout has caught up with what just changed
        Input.Click(window, Control<TextBox>(window, "Composer"));
        window.KeyTextInput(text);
        Ui.Pump(0.1);
    }

    /// <summary>Types and presses Enter, as a person sends a message.</summary>
    public static void Send(Window window, string text)
    {
        Type(window, text);
        Input.Chord(window, Key.Enter, RawInputModifiers.None);
    }

    /// <summary>The text of every message's answer, in order.</summary>
    public static IReadOnlyList<string> Answers(Window window) =>
        [.. All<MarkdownView>(window, "AnswerView").Select(TextOf)];

    public static string TextOf(Control root) =>
        string.Join("\n", root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text)
            .Concat(root.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible && t.Inlines is { Count: > 0 }).Select(t => t.Inlines!.Text)));

    public static string AnswerText(Window window, int index) => TextOf(All<MarkdownView>(window, "AnswerView").ElementAt(index));

    /// <summary>Opens the model chip and picks a model, then waits as a person does for the menu to close.</summary>
    public static void ChooseModel(Window window, string name)
    {
        Input.Click(window, Control<Button>(window, "ModelChip"));
        Ui.PumpUntil(() => Named(window, name) is not null, 5);
        Input.Click(window, Named(window, name) ?? throw new InvalidOperationException($"No model called {name} in the menu"));
        Ui.Pump(0.4);
    }

    public static IReadOnlyList<CodeBlockView> CodeBlocks(Window window) =>
        [.. window.GetVisualDescendants().OfType<CodeBlockView>().Where(c => c.IsEffectivelyVisible)];
}
