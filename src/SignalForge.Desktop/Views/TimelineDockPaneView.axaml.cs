using Avalonia.Controls;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public partial class TimelineDockPaneView : UserControl
{
    public TimelineDockPaneView()
    {
        InitializeComponent();
    }

    public void Bind(MainViewModel viewModel) => DataContext = viewModel;
}
