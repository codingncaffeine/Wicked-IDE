using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Core.Projects;
using Wicked.Core.Testing;

namespace Wicked.App.Projects;

/// <summary>A template in the New Project list, greyed out with the reason when its tool is not installed.</summary>
public sealed partial class TemplateRow : ObservableObject
{
    public TemplateRow(ProjectTemplate template, bool available)
    {
        Template = template;
        Available = available;
    }

    public ProjectTemplate Template { get; }

    public bool Available { get; }

    public string Language => Template.Language;

    public string Name => Template.Name;

    public string Description => Template.Description;

    /// <summary>"needs cargo" for a template whose tool is missing.</summary>
    public string Note => Available ? "" : $"needs {Template.Tool}";
}

/// <summary>
/// File > New Project (Ctrl+Shift+N, the welcome page, the empty explorer), as Visual Studio and Rider offer it: a template,
/// a name and a location; the language's own tool makes it on a worker, its output in the Output panel's "New Project"
/// channel, then the folder opens with its main file. Wicked recognises the language of every file by itself afterwards.
/// </summary>
public sealed partial class NewProjectViewModel : ObservableObject
{
    private const string Channel = "New Project";
    private readonly ShellViewModel _shell;
    private readonly ToolEnv _env;
    private CancellationTokenSource? _cts;

    public NewProjectViewModel(ShellViewModel shell, ToolEnv? env = null)
    {
        _shell = shell;
        _env = env ?? new ToolEnv();
    }

    public ObservableCollection<TemplateRow> Templates { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(CanCreate))]
    private TemplateRow? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(CanCreate), nameof(NameProblem))]
    private string _projectName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(CanCreate))]
    private string _location = "";

    [ObservableProperty]
    private bool _createGit = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _busy;

    [ObservableProperty]
    private string _error = "";

    /// <summary>Where it will be made, shown under the fields.</summary>
    public string TargetText => ProjectName.Length == 0 || Location.Length == 0 ? "" : "Creates " + Path.Combine(Location, ProjectName);

    public string NameProblem => ProjectName.Length == 0 ? "" : ProjectTemplates.CheckName(ProjectName) ?? "";

    public bool CanCreate => !Busy && Selected is { Available: true } && ProjectTemplates.CheckName(ProjectName) is null && Location.Length > 0;

    /// <summary>Opens the dialog: the templates checked against the tools on the PATH (on a worker), the last location offered.</summary>
    public async Task OpenAsync()
    {
        Error = "";
        if (Location.Length == 0) Location = _shell.LastProjectLocation ?? DefaultLocation();
        if (ProjectName.Length == 0) ProjectName = "MyProject";
        IsOpen = true;
        var env = _env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rows = await Task.Run(() => ProjectTemplates.All.Select(t => new TemplateRow(t, ProjectTemplates.IsAvailable(t, env, home))).ToList());
        var keep = Selected?.Template.Id;
        Templates.Clear();
        foreach (var row in rows) Templates.Add(row);
        Selected = rows.FirstOrDefault(r => r.Template.Id == keep && r.Available) ?? rows.FirstOrDefault(r => r.Available);
    }

    private static string DefaultLocation()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(home, "Projects");
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_shell.Workbench.Dialogs is { } dialogs && await dialogs.PickFolderAsync() is { } folder) Location = folder;
    }

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate || Selected is not { } row) return;
        Busy = true;
        Error = "";
        _cts = new CancellationTokenSource();
        var request = new ProjectRequest(ProjectName.Trim(), Location.Trim(), CreateGit);
        var output = _shell.Output;
        output.Write(Channel, $"──── {row.Language} {row.Name}: {request.Root} ────");
        ProjectResult result;
        try
        {
            var env = _env;
            var token = _cts.Token;
            result = await Task.Run(() =>
            {
                Directory.CreateDirectory(request.Location); // ~/Projects the first time
                return ProjectCreator.CreateAsync(row.Template, request, env, line => output.Write(Channel, line), token);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            result = new ProjectResult(request.Root, null, ex is OperationCanceledException ? "Cancelled" : ex.Message);
        }
        finally
        {
            Busy = false;
        }

        if (!result.Ok)
        {
            Error = result.Error!;
            output.Write(Channel, result.Error!);
            return;
        }

        IsOpen = false;
        ProjectName = "";
        _shell.RememberProjectLocation(request.Location);
        await _shell.OpenFolderAndRememberAsync(result.Root);
        if (result.OpenFile is { } file) await _shell.Workbench.OpenAsync(file);
        _shell.Notices.Post(new Notice("new-project", $"Created {request.Name}", NoticeKind.Success));
    }
}
