using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Wicked.App.Projects;

public partial class NewProjectView : UserControl
{
    /// <summary>A template whose tool is missing is drawn faded.</summary>
    public static readonly IValueConverter AvailableOpacity = new FuncValueConverter<bool, double>(available => available ? 1 : 0.5);

    public NewProjectView()
    {
        InitializeComponent();
    }
}
