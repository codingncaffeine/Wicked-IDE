using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Wicked.Core;
using Wicked.Core.Files;
using Wicked.Editor.Syntax;

namespace Wicked.App.Workbench;

/// <summary>An unsaved file's text as kept on disk until it is saved, closed or brought back.</summary>
public sealed record UnsavedBackup(string? Path, string? Folder, string Text, string LineBreak, string Encoding, bool Bom, string? Language, int ProcessId);

/// <summary>
/// files.hotExit, as VS Code has it on by default: the text of every unsaved file is kept in the profile a moment after each
/// edit (so a crash loses at most that moment), closing the window asks nothing, and the next window on the same folder
/// opens those files again, still unsaved. A file that is saved, or closed with an answer, loses its backup. Backups are
/// written and read on workers; a backup made by another window that is still running is left to it.
/// </summary>
public sealed class HotExit
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private readonly Workbench _bench;
    private readonly string _folder;
    private readonly Func<bool> _enabled;
    private readonly Dictionary<EditorTab, (string Id, DispatcherTimer Timer)> _tabs = [];
    private bool _exiting;

    public HotExit(Workbench bench, string folder, Func<bool> enabled)
    {
        _bench = bench;
        _folder = folder;
        _enabled = enabled;
        bench.TabOpened += OnOpened;
        bench.TabClosed += OnClosed;
    }

    private string FileOf(string id) => Path.Combine(_folder, id + ".json");

    private void OnOpened(EditorTab tab)
    {
        if (tab.ViewFactory is not null || _tabs.ContainsKey(tab)) return;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Delay };
        var id = Guid.NewGuid().ToString("N");
        _tabs[tab] = (id, timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = WriteAsync(tab);
        };
        void Later()
        {
            timer.Stop();
            timer.Start();
        }

        tab.Document.Changed += _ => Later();
        tab.Document.DirtyChanged += Later;
    }

    private void OnClosed(EditorTab tab)
    {
        if (!_tabs.Remove(tab, out var entry)) return;
        entry.Timer.Stop();
        // Closed with an answer (saved, or not saved on purpose): its backup goes. Closing the window keeps them.
        if (!_exiting) Delete(entry.Id);
    }

    /// <summary>Keeps the tab's text now, or drops its backup when it has nothing unsaved.</summary>
    private Task WriteAsync(EditorTab tab)
    {
        if (!_tabs.TryGetValue(tab, out var entry)) return Task.CompletedTask;
        if (!tab.IsDirty || !_enabled())
        {
            Delete(entry.Id);
            return Task.CompletedTask;
        }

        var text = tab.Document.Text;
        var backup = new UnsavedBackup(tab.Path, _bench.Root, "", tab.Document.LineBreak, tab.Encoding.Name, tab.Encoding.Bom, tab.Language?.Id, Environment.ProcessId);
        var file = FileOf(entry.Id);
        return Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_folder);
                var temp = file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(backup with { Text = text.ToString() }));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Keeping an unsaved file failed: {ex.Message}");
            }
        });
    }

    private void Delete(string id) => _ = Task.Run(() =>
    {
        try
        {
            File.Delete(FileOf(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    });

    /// <summary>The window is closing: every unsaved file is kept now, and no backup is dropped from here on.</summary>
    public async Task FlushAsync()
    {
        _exiting = true;
        foreach (var (tab, entry) in _tabs.ToList())
        {
            entry.Timer.Stop();
            await WriteAsync(tab);
        }
    }

    /// <summary>
    /// Brings back the unsaved files kept for this window's folder (none, for a window with no folder): each opens again with
    /// its unsaved text. Returns how many came back.
    /// </summary>
    public async Task<int> RestoreAsync()
    {
        var folder = _bench.Root;
        var found = await Task.Run(() =>
        {
            var list = new List<(string File, UnsavedBackup Backup)>();
            if (!Directory.Exists(_folder)) return list;
            foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<UnsavedBackup>(File.ReadAllText(file)) is { } backup && backup.Folder == folder && !Running(backup.ProcessId)) list.Add((file, backup));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    Log.Warn($"An unsaved file's backup {Path.GetFileName(file)} could not be read: {ex.Message}");
                }
            }

            return list;
        });

        foreach (var (file, backup) in found)
        {
            EditorTab? tab = backup.Path is null ? _bench.NewFile()
                : File.Exists(backup.Path) ? await _bench.OpenAsync(backup.Path) : _bench.OpenNew(backup.Path);
            if (tab is null) continue;
            if (backup.Path is null && backup.Language is { } language) _bench.ChangeLanguage(tab, Languages.ForName(language));
            tab.Encoding = new FileEncoding(backup.Encoding, backup.Bom);
            tab.Document.LineBreak = backup.LineBreak;
            var (rope, _) = await Task.Run(() => Wicked.Editor.Text.TextLoading.Normalise(backup.Text));
            tab.Document.ReplaceAll(rope);
            _ = Task.Run(() => File.Delete(file));
        }

        return found.Count;
    }

    /// <summary>Another Wicked that is still running made it (its own backups are its business).</summary>
    private static bool Running(int processId)
    {
        if (processId == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.ProcessName.Contains("wicked", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
