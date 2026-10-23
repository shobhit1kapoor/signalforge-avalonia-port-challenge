using Avalonia.Controls;
using Avalonia.Input;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public partial class Terminal2ReceiveHistoryView : UserControl
{
    public Terminal2ReceiveHistoryView() => InitializeComponent();

    private void OnLinesDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is Terminal2ReceiveHistoryViewModel viewModel)
            viewModel.RecallSelectedLineCommand.Execute(null);
    }
}
