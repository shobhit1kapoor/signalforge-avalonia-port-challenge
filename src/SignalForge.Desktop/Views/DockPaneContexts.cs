using System.Text.Json.Serialization;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Desktop.Views;

public abstract class DockPaneContext
{
    [JsonIgnore]
    public MainViewModel? ViewModel { get; set; }

    [JsonIgnore]
    public DockWorkspaceHost? Host { get; set; }
}

public sealed class ExplorerDockPaneContext : DockPaneContext { }

public sealed class FeatureDockPaneContext : DockPaneContext
{
    public int SelectedFeatureTabIndex { get; set; }
}

public sealed class InspectorDockPaneContext : DockPaneContext { }

public sealed class TimelineDockPaneContext : DockPaneContext { }
