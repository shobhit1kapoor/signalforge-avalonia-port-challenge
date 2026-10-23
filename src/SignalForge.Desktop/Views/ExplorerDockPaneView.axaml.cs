using Avalonia.Controls;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public partial class ExplorerDockPaneView : UserControl
{
    public ExplorerDockPaneView()
    {
        InitializeComponent();
    }

    public void Bind(MainViewModel viewModel) => DataContext = viewModel;
}
