using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SignalForge.Desktop.ViewModels;
using System.ComponentModel;

namespace SignalForge.Desktop.Views;

/// <summary>
/// Search-and-run overlay for host-provided <see cref="CommandPaletteItem"/> actions.
/// Add this view to a host overlay and assign a <see cref="CommandPaletteViewModel"/>.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    private CommandPaletteViewModel? _viewModel;

    public CommandPaletteView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AttachViewModel(DataContext as CommandPaletteViewModel);
    }

    private void OnDataContextChanged(object? sender, EventArgs args) =>
        AttachViewModel(DataContext as CommandPaletteViewModel);

    private void AttachViewModel(CommandPaletteViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (_viewModel.IsOpen)
                FocusSearchBox();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _viewModel?.IsOpen == true)
            FocusSearchBox();
    }

    private void FocusSearchBox() => Dispatcher.UIThread.Post(() => SearchBox.Focus());

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (_viewModel?.IsOpen != true)
            return;

        switch (args.Key)
        {
            case Key.Up:
                _viewModel.MoveSelection(-1);
                args.Handled = true;
                break;
            case Key.Down:
                _viewModel.MoveSelection(1);
                args.Handled = true;
                break;
            case Key.Enter:
                _ = _viewModel.ExecuteSelectedAsync();
                args.Handled = true;
                break;
            case Key.Escape:
                _viewModel.Close();
                args.Handled = true;
                break;
        }
    }
}
