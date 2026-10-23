using Avalonia.Controls;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public partial class InspectorDockPaneView : UserControl
{
    public InspectorDockPaneView()
    {
        InitializeComponent();
    }

    public void Bind(MainViewModel viewModel) => DataContext = viewModel;
}
