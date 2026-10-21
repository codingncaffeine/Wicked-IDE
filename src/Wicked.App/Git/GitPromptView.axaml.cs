using Avalonia.Controls;
using Avalonia.Threading;

namespace Wicked.App.Git;

/// <summary>The credential question: the cursor goes to the answer box as it opens.</summary>
public partial class GitPromptView : UserControl
{
    public GitPromptView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not null) Dispatcher.UIThread.Post(() => GitPromptAnswer.Focus(), DispatcherPriority.Loaded);
        };
    }
}
