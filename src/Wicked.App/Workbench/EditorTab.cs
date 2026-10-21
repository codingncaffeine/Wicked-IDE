using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.Editor;
using Wicked.Core.Files;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;

namespace Wicked.App.Workbench;

/// <summary>
/// One open document in a group's tab strip. A preview tab (single-click in the explorer) is replaced by the next preview
/// until it is edited or double-clicked, as in VS Code. Its editor control is made on first show and kept while the tab lives.
/// </summary>
public sealed partial class EditorTab : ObservableObject
{
    private static int _untitled;

    public EditorTab(string? path, Document document, LanguageInfo? language, FileEncoding encoding, DateTime lastWriteUtc)
    {
        Path = path;
        Document = document;
        Language = language;
        Encoding = encoding;
        LastWriteUtc = lastWriteUtc;
        UntitledName = path is null ? $"Untitled-{Interlocked.Increment(ref _untitled)}" : null;
        document.DirtyChanged += () =>
        {
            OnPropertyChanged(nameof(IsDirty));
            if (document.IsDirty) IsPreview = false;
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Tooltip))]
    private string? _path;

    [ObservableProperty]
    private bool _isPreview;

    [ObservableProperty]
    private bool _isActive;

    public Document Document { get; }

    public LanguageInfo? Language { get; private set; }

    public FileEncoding Encoding { get; set; }

    /// <summary>What the project's .editorconfig says for this file (resolved on a worker when it opened).</summary>
    public Wicked.Editor.Features.EditorConfigProperties EditorConfig { get; set; } = Wicked.Editor.Features.EditorConfigProperties.Empty;

    public DateTime LastWriteUtc { get; set; }

    private string? UntitledName { get; }

    /// <summary>A name for a tab that shows generated text and has no file (a diff): shown in place of "Untitled-N".</summary>
    public string? Label { get; init; }

    public string Title => Path is null ? Label ?? UntitledName! : System.IO.Path.GetFileName(Path);

    public string Tooltip => Path ?? UntitledName!;

    public bool IsDirty => Document.IsDirty;

    /// <summary>The editor control, made by the group view the first time the tab is shown.</summary>
    public CodeEditor? Editor { get; private set; }

    public CodeEditor EditorFor(EditorServices services, Wicked.Core.Settings.WickedSettings settings, SnippetLibrary? snippets = null)
    {
        if (Editor is not null) return Editor;
        Editor = new CodeEditor(services, Document, Language) { EditorConfig = EditorConfig, Snippets = snippets, FilePath = Path };
        Editor.ApplySettings(settings);
        return Editor;
    }

    /// <summary>After Save As: a new name may mean a new language.</summary>
    public void Renamed(string path)
    {
        Path = path;
        Language = Languages.ForFile(path);
        if (Editor is not null) Editor.FilePath = path;
    }

    public void Close() => Editor?.Close();
}
