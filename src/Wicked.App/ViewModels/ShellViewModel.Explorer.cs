using Wicked.App.Git;

namespace Wicked.App.ViewModels;

/// <summary>
/// What the Explorer's right-click menu does beyond the files themselves, as VS Code's: a terminal in a folder, the
/// search limited to a folder, and two files compared side by side (Select for Compare, then Compare with Selected).
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>Open in Integrated Terminal: a new terminal starting in that folder (a file's own folder for a file).</summary>
    public void OpenTerminalIn(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        Terminal.NewTerminal(workingDirectory: folder);
    }

    /// <summary>Find in Folder...: the Search panel, looking only in that folder.</summary>
    public void FindInFolder(string folder)
    {
        Search.Include = Workbench.Root is { } root ? Path.GetRelativePath(root, folder).Replace('\\', '/') : folder;
        ShowSearch();
    }

    /// <summary>The file chosen with Select for Compare, waiting for Compare with Selected.</summary>
    public string? CompareSource { get; private set; }

    public void SelectForCompare(string path) => CompareSource = path;

    /// <summary>Compare with Selected: the file selected before on the left, this one on the right, in a diff tab.</summary>
    public void CompareWithSelected(string path)
    {
        if (CompareSource is not { } left || left == path) return;
        var title = $"{Path.GetFileName(left)} ↔ {Path.GetFileName(path)}";
        var bench = Workbench;
        Workbench.OpenView(title, () => new DiffEditorView(title, path, () => File.ReadAllTextAsync(left), () => File.ReadAllTextAsync(path),
            () => bench.OpenAsync(path), Settings));
    }
}
