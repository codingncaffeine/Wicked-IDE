using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core.Terminal;

namespace Wicked.App.Terminal;

/// <summary>One terminal: its shell, its session, and the pane that shows it.</summary>
public sealed partial class TerminalInstanceViewModel : ObservableObject, IDisposable
{
    private string _oscTitle = "";
    private string? _customName;

    internal TerminalInstanceViewModel(ShellProfile profile, string? workingDirectory)
    {
        Profile = profile;
        WorkingDirectory = workingDirectory;
        ProfileName = profile.Name;
    }

    public ShellProfile Profile { get; }

    public string ProfileName { get; }

    /// <summary>Where the shell is working: the folder it started in, then whatever it reports.</summary>
    [ObservableProperty]
    private string? _workingDirectory;

    /// <summary>The session; null until the shell has started.</summary>
    public TerminalSession? Session { get; internal set; }

    /// <summary>What shows it; null until fonts and session are ready.</summary>
    [ObservableProperty]
    private TerminalPane? _pane;

    /// <summary>Null while the shell runs.</summary>
    [ObservableProperty]
    private int? _exitCode;

    /// <summary>Set if the shell could not be started.</summary>
    [ObservableProperty]
    private string? _startError;

    /// <summary>The marks of the last prompt or command a shell with integration reported.</summary>
    [ObservableProperty]
    private CommandRecord? _lastCommand;

    /// <summary>Progress a program reported (ConEmu OSC 9;4): state 0-4 and percent.</summary>
    [ObservableProperty]
    private int _progressState;

    [ObservableProperty]
    private int _progressValue;

    public string Title => !string.IsNullOrEmpty(_customName) ? _customName : !string.IsNullOrEmpty(_oscTitle) ? _oscTitle : ProfileName;

    /// <summary>The name the user gave it, or null.</summary>
    public string? CustomName
    {
        get => _customName;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (SetProperty(ref _customName, name)) OnPropertyChanged(nameof(Title));
        }
    }

    public bool HasExited => ExitCode is not null;

    internal void SetTitle(string title)
    {
        _oscTitle = title;
        OnPropertyChanged(nameof(Title));
    }

    partial void OnExitCodeChanged(int? value) => OnPropertyChanged(nameof(HasExited));

    /// <summary>The session's events, marshalled to the UI thread.</summary>
    internal void Wire(TerminalSession session, Action<TerminalInstanceViewModel, int> exited)
    {
        session.TitleChanged += title => Dispatcher.UIThread.Post(() => SetTitle(title));
        session.WorkingDirectoryChanged += dir => Dispatcher.UIThread.Post(() => WorkingDirectory = dir);
        session.CommandFinished += record => Dispatcher.UIThread.Post(() => LastCommand = record);
        session.PromptStarted += record => Dispatcher.UIThread.Post(() => LastCommand = record);
        session.ProgressChanged += (state, value) => Dispatcher.UIThread.Post(() =>
        {
            ProgressState = state;
            ProgressValue = value;
        });
        session.Exited += code => Dispatcher.UIThread.Post(() =>
        {
            ExitCode = code;
            exited(this, code);
        });
    }

    public void Dispose()
    {
        Session?.Dispose();
        Pane?.View.Detach();
    }

}
