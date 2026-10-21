using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Git;

namespace Wicked.App.Git;

/// <summary>
/// A question git asked for a credential or a confirmation, shown as a small dialog. The answer is held only while the dialog
/// is open and goes straight back to git through the relay; nothing is saved by Wicked (the user's credential helper does
/// that, if they have one).
/// </summary>
public sealed partial class GitPromptViewModel : ObservableObject
{
    private readonly Action<string?> _done;
    private bool _answered;

    public GitPromptViewModel(AskpassRequest request, Action<string?> done)
    {
        _done = done;
        Prompt = request.Prompt.TrimEnd().TrimEnd(':').Trim();
        IsSecret = request.IsSecret;
        Title = request.IsSecret ? "Git needs a secret" : "Git asks";
    }

    public string Title { get; }

    public string Prompt { get; }

    public bool IsSecret { get; }

    /// <summary>"•" while typing a secret, nothing for plain text.</summary>
    public char PasswordChar => IsSecret ? '•' : '\0';

    [ObservableProperty]
    private string _answer = "";

    [RelayCommand]
    private void Accept() => Finish(Answer);

    [RelayCommand]
    private void Cancel() => Finish(null);

    private void Finish(string? answer)
    {
        if (_answered) return;
        _answered = true;
        _done(answer);
    }
}
