using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Wicked.App.Shell;

/// <summary>
/// Screen readers read a control's automation name: a button that shows only an icon has none of its own, so the one its
/// tooltip gives ("Hide the panel (Ctrl+J)") is put there as it loads, in every window, once for the application.
/// </summary>
public static class AccessibleNames
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        Control.LoadedEvent.AddClassHandler<Button>((button, _) => Name(button));
        Control.LoadedEvent.AddClassHandler<ToggleButton>((button, _) => Name(button));
    }

    private static void Name(ContentControl control)
    {
        if (!string.IsNullOrEmpty(AutomationProperties.GetName(control))) return;
        if (control.Content is string) return;
        if (ToolTip.GetTip(control) is string tip && tip.Length > 0) AutomationProperties.SetName(control, tip);
    }

    /// <summary>The buttons in a tree that a screen reader could not name (tests, and a check in the window's own suite).</summary>
    public static IEnumerable<ContentControl> Unnamed(Avalonia.Visual root) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Button>().Cast<ContentControl>()
            .Concat(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<ToggleButton>())
            .Where(b => b.IsEffectivelyVisible && string.IsNullOrEmpty(AutomationProperties.GetName(b)) && !HasText(b));

    private static bool HasText(ContentControl control) =>
        control.Content is string { Length: > 0 }
        || Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(control).OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
}
