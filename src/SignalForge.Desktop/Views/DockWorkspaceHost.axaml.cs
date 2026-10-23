using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;
using SignalForge.Desktop.Services;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public partial class DockWorkspaceHost : UserControl
{
    private const int StateVersion = 1;
    private readonly Factory _factory = new SignalForgeDockFactory();
    private readonly DockSerializer _serializer = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private MainViewModel? _viewModel;
    private ILocalTerminalSessionHost? _terminalHost;
    private RootDock? _layout;
    private FeatureDockPaneContext? _featureContext;
    private FeatureDockPaneView? _featurePane;
    private string? _statePath;
    private int _selectedFeatureTabIndex;
    private bool _initialized;

    public DockWorkspaceHost()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveState();
        };
    }

    public string? Status { get; private set; }

    public void InitializeWorkspace(MainViewModel viewModel, ILocalTerminalSessionHost terminalHost)
    {
        if (_initialized)
            return;

        _initialized = true;
        _viewModel = viewModel;
        _terminalHost = terminalHost;
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SignalForge", "Settings");
        _statePath = Path.Combine(settingsDirectory, "dock-workspace.json");

        _layout = TryLoadState() ?? CreateDefaultLayout();
        RebindContexts(_layout);

        var surface = this.FindControl<DockControl>("DockSurface")
            ?? throw new InvalidOperationException("Dock surface was not created.");
        surface.Factory = _factory;
        // DockControl registers its host-window locator when Layout is assigned;
        // InitializeLayout is disabled so the factory is initialized once afterward.
        surface.Layout = _layout;
        _factory.InitLayout(_layout);
        surface.LayoutUpdated += OnLayoutUpdated;
        SaveState();
    }

    public void ResetLayout()
    {
        if (_viewModel is null)
            return;

        CloseFloatingWindows(_layout);
        _selectedFeatureTabIndex = 0;
        _layout = CreateDefaultLayout();
        RebindContexts(_layout);
        var surface = this.FindControl<DockControl>("DockSurface");
        if (surface is not null)
        {
            surface.Layout = _layout;
            _factory.InitLayout(_layout);
        }
        SaveState();
    }

    private void CloseFloatingWindows(RootDock? layout)
    {
        if (layout?.Windows is not { } windows)
            return;

        foreach (var window in windows.ToArray())
            _factory.CloseWindow(window);
    }

    public void SelectFeatureTab(int index)
    {
        if (index < 0)
            return;

        _selectedFeatureTabIndex = index;
        if (_featureContext is not null)
            _featureContext.SelectedFeatureTabIndex = index;

        _featurePane?.SelectFeatureTab(index);
        QueueSave();
    }

    public void SelectAdjacentFeatureTab(int direction)
    {
        if (_featurePane is not { } featurePane)
            return;

        const int featureTabCount = 14;
        var current = Math.Clamp(featurePane.SelectedFeatureTabIndex, 0, featureTabCount - 1);
        SelectFeatureTab((current + direction + featureTabCount) % featureTabCount);
    }

    public int SelectedFeatureTabIndex => _featurePane?.SelectedFeatureTabIndex ?? _selectedFeatureTabIndex;

    public TerminalControl? GetLocalTerminal() => _featurePane?.GetLocalTerminal();

    internal void AttachLocalTerminal(TerminalControl terminal)
    {
        _terminalHost?.Attach(terminal);
        if (_viewModel is not null)
            _ = _viewModel.StartLocalSessionAsync();
    }

    public RemoteTerminalView? GetRemoteTerminal() => _featurePane?.GetRemoteTerminal();

    internal void RegisterFeaturePane(FeatureDockPaneView featurePane)
    {
        _featurePane = featurePane;
        _featurePane.SelectFeatureTab(_selectedFeatureTabIndex);
    }

    internal void OnFeatureTabSelectionChanged(int index)
    {
        _selectedFeatureTabIndex = Math.Max(0, index);
        if (_featureContext is not null)
            _featureContext.SelectedFeatureTabIndex = _selectedFeatureTabIndex;
        QueueSave();
    }

    private RootDock? TryLoadState()
    {
        if (_statePath is null || !File.Exists(_statePath))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<DockWorkspaceState>(File.ReadAllText(_statePath));
            if (state is null || state.Version != StateVersion || string.IsNullOrWhiteSpace(state.LayoutJson))
                return null;

            var layout = _serializer.Deserialize<RootDock>(state.LayoutJson);
            if (layout is null)
                return null;

            // Use native host windows for floated panes. Managed floating panes are
            // clipped to this control's layer and can disappear when the host is
            // nested inside the main window's workspace grid.
            layout.FloatingWindowHostMode = DockFloatingWindowHostMode.Native;
            if (layout.Windows is not null)
            {
                foreach (var window in layout.Windows)
                    if (string.IsNullOrWhiteSpace(window.Title))
                        window.Title = window.Layout?.ActiveDockable?.Title ?? "SignalForge floating pane";
            }
            _selectedFeatureTabIndex = Math.Max(0, state.SelectedFeatureTabIndex);
            return layout;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Status = "Saved dock layout could not be loaded; using defaults.";
            return null;
        }
    }

    private RootDock CreateDefaultLayout()
    {
        var explorer = CreateTool("pane.explorer", "Device Explorer", new ExplorerDockPaneContext());
        var inspector = CreateTool("pane.inspector", "Stream Inspector", new InspectorDockPaneContext());
        var timeline = CreateTool("pane.timeline", "Event Timeline", new TimelineDockPaneContext());
        var featureContext = CreateFeatureContext();
        var feature = new Document
        {
            Id = "document.workspace",
            Title = "Protocol Studio",
            CanClose = false,
            CanFloat = true,
            Context = featureContext,
        };
        var documents = new DocumentDock
        {
            Id = "dock.documents",
            CanClose = false,
            VisibleDockables = new List<IDockable> { feature },
            ActiveDockable = feature,
            DefaultDockable = feature,
        };

        var explorerDock = new ToolDock
        {
            Id = "dock.explorer",
            Alignment = Alignment.Left,
            CanClose = false,
            AutoHide = false,
            IsExpanded = true,
            Proportion = 0.19,
            VisibleDockables = new List<IDockable> { explorer },
            ActiveDockable = explorer,
            DefaultDockable = explorer,
        };
        var inspectorDock = new ToolDock
        {
            Id = "dock.inspector",
            Alignment = Alignment.Right,
            CanClose = false,
            AutoHide = false,
            IsExpanded = true,
            Proportion = 0.22,
            VisibleDockables = new List<IDockable> { inspector },
            ActiveDockable = inspector,
            DefaultDockable = inspector,
        };
        var horizontal = new ProportionalDock
        {
            Id = "dock.work-area",
            Orientation = Orientation.Horizontal,
            ActiveDockable = documents,
            DefaultDockable = documents,
            VisibleDockables = new List<IDockable>
            {
                explorerDock,
                new ProportionalDockSplitter { Id = "split.explorer", Proportion = 0.01 },
                documents,
                new ProportionalDockSplitter { Id = "split.inspector", Proportion = 0.01 },
                inspectorDock,
            },
        };
        var timelineDock = new ToolDock
        {
            Id = "dock.timeline",
            Alignment = Alignment.Bottom,
            CanClose = false,
            AutoHide = false,
            IsExpanded = true,
            Proportion = 0.25,
            VisibleDockables = new List<IDockable> { timeline },
            ActiveDockable = timeline,
            DefaultDockable = timeline,
        };
        var rootSplit = new ProportionalDock
        {
            Id = "dock.root-split",
            Orientation = Orientation.Vertical,
            ActiveDockable = horizontal,
            DefaultDockable = horizontal,
            VisibleDockables = new List<IDockable>
            {
                horizontal,
                new ProportionalDockSplitter { Id = "split.timeline", Proportion = 0.01 },
                timelineDock,
            },
        };
        var root = new RootDock
        {
            Id = "root.workspace",
            Title = "SignalForge",
            FloatingWindowHostMode = DockFloatingWindowHostMode.Native,
            VisibleDockables = new List<IDockable> { rootSplit },
            ActiveDockable = rootSplit,
            DefaultDockable = rootSplit,
        };
        return root;
    }

    private Tool CreateTool(string id, string title, DockPaneContext context) => new()
    {
        Id = id,
        Title = title,
        CanClose = false,
        CanFloat = true,
        CanDrag = true,
        CanDrop = true,
        Context = context,
    };

    private FeatureDockPaneContext CreateFeatureContext() => new()
    {
        ViewModel = _viewModel,
        Host = this,
        SelectedFeatureTabIndex = _selectedFeatureTabIndex,
    };

    private void RebindContexts(IDockable node)
    {
        switch (node.Id)
        {
            case "pane.explorer":
                var explorer = new ExplorerDockPaneContext { ViewModel = _viewModel, Host = this };
                node.Context = explorer;
                break;
            case "pane.inspector":
                var inspector = new InspectorDockPaneContext { ViewModel = _viewModel, Host = this };
                node.Context = inspector;
                break;
            case "pane.timeline":
                var timeline = new TimelineDockPaneContext { ViewModel = _viewModel, Host = this };
                node.Context = timeline;
                break;
            case "document.workspace":
                _featureContext = CreateFeatureContext();
                node.Context = _featureContext;
                break;
        }

        if (node is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
                RebindContexts(child);
        }

        if (node is IRootDock root && root.Windows is not null)
        {
            foreach (var window in root.Windows)
                if (window.Layout is IDockable windowLayout)
                    RebindContexts(windowLayout);
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => QueueSave();

    private void QueueSave()
    {
        if (!_initialized)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState()
    {
        if (_statePath is null || _layout is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var layoutJson = _serializer.Serialize(_layout);
            var selectedFeatureTabIndex = _featurePane?.SelectedFeatureTabIndex ?? _selectedFeatureTabIndex;
            var json = JsonSerializer.Serialize(new DockWorkspaceState(StateVersion, selectedFeatureTabIndex, layoutJson));
            var temporaryPath = _statePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _statePath, overwrite: true);
            Status = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Status = $"Dock layout could not be saved: {exception.Message}";
        }
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        SaveState();
    }

    private sealed record DockWorkspaceState(int Version, int SelectedFeatureTabIndex, string LayoutJson);

    private sealed class SignalForgeDockFactory : Factory
    {
        public override IDockWindow? CreateWindowFrom(IDockable dockable)
        {
            var window = base.CreateWindowFrom(dockable);
            if (window is not null)
                window.Title = dockable.Title;
            return window;
        }
    }
}
