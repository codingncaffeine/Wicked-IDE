using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wicked.App.Shell;

/// <summary>Bottom panel tabs can be taken back as well as added (an extension was disabled).</summary>
public sealed partial class BottomPanel
{
    /// <summary>Removes a tab; when it was the active one the first remaining tab takes its place and the panel hides if none is left.</summary>
    public void Remove(string id)
    {
        if (this[id] is not { } tab) return;
        var wasActive = ReferenceEquals(Active, tab);
        Tabs.Remove(tab);
        if (!wasActive) return;
        Active = null;
        if (Tabs.Count > 0) Select(Tabs[0]);
        else IsVisible = false;
    }
}

/// <summary>A side panel an extension added: a rail button and a view made when it is first opened.</summary>
public sealed class CustomPanel(string id, string owner, string title, string iconKey, Func<Control> make) : ObservableObject
{
    private Control? _view;

    public string Id { get; } = id;

    /// <summary>The extension that added it.</summary>
    public string Owner { get; } = owner;

    public string Title { get; } = title;

    /// <summary>The resource key of the rail icon (<c>Icon.Puzzle</c>).</summary>
    public string IconKey { get; } = iconKey;

    public Control View => _view ??= make();

    public bool HasView => _view is not null;

    /// <summary>The rail button looks lit while this panel is open.</summary>
    public bool IsOpen
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>The theme files that extensions contribute; the theme service reads them (and listens to <see cref="Changed"/>).</summary>
public sealed class ExtensionThemeFiles
{
    private readonly Lock _gate = new();
    private readonly List<(string Path, string? Label)> _files = [];

    public event Action? Changed;

    public IReadOnlyList<(string Path, string? Label)> Files
    {
        get
        {
            lock (_gate) return [.. _files];
        }
    }

    public IDisposable Add(string path, string? label)
    {
        var entry = (path, label);
        lock (_gate) _files.Add(entry);
        Changed?.Invoke();
        return new Removal(this, entry);
    }

    private sealed class Removal(ExtensionThemeFiles owner, (string Path, string? Label) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._files.Remove(entry);
            owner.Changed?.Invoke();
        }
    }
}
