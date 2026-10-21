using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.Editor;
using Wicked.App.Extensions;
using Wicked.App.Shell;
using Wicked.Core.Settings;

namespace Wicked.App.ViewModels;

/// <summary>The extension host in the window: the Extensions panel, the commands extensions add, their keys and their side panels.</summary>
public sealed partial class ShellViewModel
{
    private readonly Lock _commandGate = new();
    private readonly List<WorkbenchCommand> _extensionCommands = [];
    private readonly List<Keybinding> _extensionBindings = [];
    private readonly IReadOnlyList<WorkbenchCommand> _builtInCommands;
    private IReadOnlyList<Keybinding> _userBindings = [];

    /// <summary>Installed extensions and init.csx.</summary>
    public ExtensionsViewModel Extensions { get; private set; } = null!;

    /// <summary>The side panels extensions added, each with a button on the rail.</summary>
    public ObservableCollection<CustomPanel> CustomPanels { get; } = [];

    [ObservableProperty]
    private CustomPanel? _activeCustomPanel;

    public bool IsExtensions => SidebarVisible && Panel == SidePanel.Extensions;

    public bool IsCustomPanel => SidebarVisible && Panel == SidePanel.Custom;

    // OnPanelChanged (in ShellViewModel.Tasks.cs, one implementation per partial method) calls RaiseExtensionPanels too.

    partial void OnSidebarVisibleChanged(bool value) => RaiseExtensionPanels();

    private void RaiseExtensionPanels()
    {
        OnPropertyChanged(nameof(IsExtensions));
        OnPropertyChanged(nameof(IsCustomPanel));
        foreach (var panel in CustomPanels) panel.IsOpen = IsCustomPanel && ReferenceEquals(panel, ActiveCustomPanel);
    }

    private void WireExtensions()
    {
        Extensions = new ExtensionsViewModel(this);
        // A contributed theme joins the theme list (and leaves it) as its extension comes and goes.
        Extensions.Bridge.ThemeFiles.Changed += () => _post(() => _ = ReloadThemesAsync());
    }

    /// <summary>Starts the extension host: installed extensions load and init.csx runs, off the interface thread.</summary>
    private void StartExtensions() => _ = Extensions.StartAsync();

    private IEnumerable<WorkbenchCommand> ExtensionCommands()
    {
        static Task Sync(Action a)
        {
            a();
            return Task.CompletedTask;
        }

        return
        [
            new("workbench.view.extensions", "View", "Show Extensions", "Ctrl+Shift+X", () => Sync(() => Navigate(ShellRoutes.Extensions))),
            new("wicked.extensions.installFromFolder", "Extensions", "Install from Folder...", null, () =>
            {
                Navigate(ShellRoutes.Extensions);
                return Extensions.InstallFromFolderCommand.ExecuteAsync(null);
            }),
            new("wicked.extensions.installFromZip", "Extensions", "Install from a .zip...", null, () =>
            {
                Navigate(ShellRoutes.Extensions);
                return Extensions.InstallFromZipCommand.ExecuteAsync(null);
            }),
            new("wicked.extensions.installFromGitHub", "Extensions", "Install from a GitHub Release...", null, () => Sync(() =>
            {
                Navigate(ShellRoutes.Extensions);
                Extensions.GitHubOpen = true;
            })),
            new("wicked.initScript.reload", "Preferences", "Reload init.csx", null, () => Extensions.Init.RunAsync()),
            new("wicked.initScript.open", "Preferences", "Open init.csx", null, async () =>
            {
                var path = await Extensions.Init.EnsureStarterAsync();
                await Workbench.OpenAsync(path);
            }),
        ];
    }

    // ── Commands and keys that extensions add ──

    /// <summary>Adds a command to the palette and the key map; any thread. Throws when the id is taken.</summary>
    internal IDisposable AddExtensionCommand(WorkbenchCommand command)
    {
        lock (_commandGate)
        {
            if (_builtInCommands.Concat(_extensionCommands).Any(c => c.Id == command.Id)) throw new InvalidOperationException($"The command {command.Id} already exists");
            _extensionCommands.Add(command);
            RebuildCommands();
        }

        return new Undo(() =>
        {
            lock (_commandGate)
            {
                _extensionCommands.Remove(command);
                RebuildCommands();
            }
        });
    }

    /// <summary>Adds a key binding for a command (Avalonia's gesture syntax); any thread. The user's keybindings.json still wins.</summary>
    internal IDisposable AddExtensionBinding(string commandId, string keys)
    {
        var binding = new Keybinding(keys, commandId, false);
        lock (_commandGate)
        {
            _extensionBindings.Add(binding);
            RebuildCommands();
        }

        return new Undo(() =>
        {
            lock (_commandGate)
            {
                _extensionBindings.Remove(binding);
                RebuildCommands();
            }
        });
    }

    /// <summary>keybindings.json was read: it is applied over the defaults and the keys extensions added.</summary>
    private void SetUserBindings(IReadOnlyList<Keybinding> bindings)
    {
        lock (_commandGate)
        {
            _userBindings = bindings;
            RebuildCommands();
        }
    }

    /// <summary>The command list and both key maps, made again. Called with the gate held.</summary>
    private void RebuildCommands()
    {
        Commands = [.. _builtInCommands, .. _extensionCommands];
        // The workbench key map takes the last binding first, the editor's the first: each gets the order that lets the user's win.
        Keys = new KeyDispatcher(Commands, [.. _extensionBindings, .. _userBindings]);
        EditorKeymap.Apply([.. _userBindings, .. _extensionBindings]);
    }

    // ── Side panels that extensions add ──

    internal void ShowCustomPanel(CustomPanel panel)
    {
        ActiveCustomPanel = panel;
        Panel = SidePanel.Custom;
        SidebarVisible = true;
        RaiseExtensionPanels();
    }

    internal void RemoveCustomPanel(string id)
    {
        if (CustomPanels.FirstOrDefault(p => p.Id == id) is not { } panel) return;
        CustomPanels.Remove(panel);
        if (!ReferenceEquals(ActiveCustomPanel, panel)) return;
        ActiveCustomPanel = null;
        if (Panel == SidePanel.Custom) Panel = SidePanel.Explorer;
    }

    /// <summary>A rail button for an extension's panel: opens it, or closes the side bar when it is the open one.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleCustomPanel(CustomPanel panel)
    {
        if (IsCustomPanel && ReferenceEquals(ActiveCustomPanel, panel)) SidebarVisible = false;
        else ShowCustomPanel(panel);
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) undo();
        }
    }
}
