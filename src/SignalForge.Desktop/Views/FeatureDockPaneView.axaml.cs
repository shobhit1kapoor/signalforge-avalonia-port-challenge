using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia;
using Avalonia.VisualTree;
using RoyalTerminal.Avalonia.Controls;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public partial class FeatureDockPaneView : UserControl
{
    public static readonly StyledProperty<FeatureDockPaneContext?> PaneContextProperty =
        AvaloniaProperty.Register<FeatureDockPaneView, FeatureDockPaneContext?>(nameof(PaneContext));

    private FeatureDockPaneContext? _context;

    public FeatureDockPaneView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ConfigureTabs();
        if (this.FindControl<TerminalControl>("LocalTerminal") is { } terminal)
            terminal.AttachedToVisualTree += OnLocalTerminalAttached;
    }

    public FeatureDockPaneContext? PaneContext
    {
        get => GetValue(PaneContextProperty);
        set => SetValue(PaneContextProperty, value);
    }

    public int SelectedFeatureTabIndex =>
        this.FindControl<TabControl>("WorkspaceTabs")?.SelectedIndex ?? PaneContext?.SelectedFeatureTabIndex ?? 0;

    public void SelectFeatureTab(int index)
    {
        if (this.FindControl<TabControl>("WorkspaceTabs") is { Items.Count: > 0 } tabs)
            tabs.SelectedIndex = Math.Clamp(index, 0, tabs.Items.Count - 1);
    }

    public TerminalControl? GetLocalTerminal() => this.FindControl<TerminalControl>("LocalTerminal");

    public RemoteTerminalView? GetRemoteTerminal() => this.FindControl<RemoteTerminalView>("RemoteTerminal");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PaneContextProperty)
            return;

        _context = change.GetNewValue<FeatureDockPaneContext?>();
        ConfigureTabs();
    }

    private void ConfigureTabs()
    {
        if (_context is null || this.FindControl<TabControl>("WorkspaceTabs") is not { } tabs)
            return;

        tabs.SelectedIndex = Math.Clamp(_context.SelectedFeatureTabIndex, 0, Math.Max(0, tabs.Items.Count - 1));
        tabs.SelectionChanged -= OnSelectionChanged;
        tabs.SelectionChanged += OnSelectionChanged;
        _context.Host?.RegisterFeaturePane(this);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl { SelectedIndex: >= 0 } tabs || _context is null)
            return;

        _context.SelectedFeatureTabIndex = tabs.SelectedIndex;
        _context.Host?.OnFeatureTabSelectionChanged(tabs.SelectedIndex);
    }

    private void OnLocalTerminalAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TerminalControl terminal)
            _context?.Host?.AttachLocalTerminal(terminal);
    }
}
