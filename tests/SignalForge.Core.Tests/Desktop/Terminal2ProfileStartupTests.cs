using RoyalTerminal.Avalonia.Controls;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Profiles;
using SignalForge.Desktop.Services;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Core.Tests.Desktop;

public sealed class Terminal2ProfileStartupTests
{
    [Fact]
    public async Task Main_view_model_creates_and_loads_default_profile_when_library_is_empty()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(new NoopTerminalHost(), temporary.Path);
        try
        {
            Assert.Equal(Terminal2ProfileLifecycle.DefaultProfileName, viewModel.Terminal2ProfileName);
            Assert.Contains("loaded Default", viewModel.Terminal2ProfileStatus, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(temporary.Path, "Default.profile")));
            Assert.Equal("Default", File.ReadAllText(Path.Combine(temporary.Path, "Active")));
        }
        finally
        {
            await viewModel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Main_view_model_restores_saved_active_profile_into_the_migration_workspace()
    {
        using var temporary = new TemporaryDirectory();
        var collection = new Terminal2ProfileCollection(temporary.Path);
        Assert.True(collection.Create(new Terminal2Profile { Name = "Default" }).IsValid);
        Assert.True(collection.Create(new Terminal2Profile
        {
            Name = "Pump Station",
            ConnectionType = Terminal2ConnectionType.TCPClient,
            TcpConnectAddress = "192.0.2.9",
            TcpConnectPort = "1502",
            SendCR = false,
            ShowHex = true,
        }).IsValid);
        File.WriteAllText(Path.Combine(temporary.Path, "Active"), "Pump Station");

        var viewModel = new MainViewModel(new NoopTerminalHost(), temporary.Path);
        try
        {
            Assert.Equal("Pump Station", viewModel.Terminal2ProfileName);
            Assert.Equal("TCP client", viewModel.TransportType);
            Assert.Equal("192.0.2.9", viewModel.Host);
            Assert.Equal("1502", viewModel.Port);
            Assert.Contains("CR OFF", viewModel.Terminal2SendSettings, StringComparison.Ordinal);
            Assert.Equal("Pump Station", File.ReadAllText(Path.Combine(temporary.Path, "Active")));
        }
        finally
        {
            await viewModel.ShutdownAsync();
        }
    }

    private sealed class NoopTerminalHost : ILocalTerminalSessionHost
    {
        public void Attach(TerminalControl control) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "signalforge-profile-startup-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
