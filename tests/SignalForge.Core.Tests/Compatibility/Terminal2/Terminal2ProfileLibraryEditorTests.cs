using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2ProfileLibraryEditorTests
{
    [Fact]
    public void Editor_saves_connection_display_and_logging_changes_with_unknown_fields_preserved()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var collection = new Terminal2ProfileCollection(directory);
            var profile = new Terminal2Profile
            {
                Name = "Bench",
                ConnectionType = Terminal2ConnectionType.TCPClient,
                SerialPort = "COM7",
                TcpConnectAddress = "192.0.2.20",
                TcpConnectPort = "5001",
                TcpListenPort = "5002",
                ShowAscii = true,
                ShowHex = false,
                ShowInputTimestamp = true,
                ShowOutputTimestamp = false,
                InputFont = "Consolas, 10pt",
                InputBackgroundArgb = unchecked((int)0xFF102030),
                InputForegroundArgb = unchecked((int)0xFF405060),
                OutputBackgroundArgb = unchecked((int)0xFF708090),
                LogDirectory = @"C:\Terminal Logs",
                LogPrefix = "bench_",
                MaxLogSize = 64
            };
            profile.Filters[10].Text = "keep this filter";
            profile.Macros[100] = new Terminal2Macro { Title = "Keep this macro", Text = "$41" };
            profile.UnknownEntries.Add(new Terminal2UnknownProfileEntry("Profile", "FutureOption=preserve-me"));
            Assert.True(collection.Create(profile).IsValid);

            var loadedWorkspaceProfile = false;
            var viewModel = CreateViewModel(collection, () => loadedWorkspaceProfile = true);
            viewModel.SelectedProfile = "Bench";

            Assert.True(viewModel.CanEditProfile);
            Assert.Equal(Terminal2ConnectionType.TCPClient, viewModel.EditorConnectionType);
            Assert.Equal("192.0.2.20", viewModel.EditorTcpConnectAddress);
            Assert.Equal("#FF102030", viewModel.EditorInputBackgroundArgb);
            Assert.Equal("C:\\Terminal Logs", viewModel.EditorLogDirectory);

            viewModel.EditorConnectionType = Terminal2ConnectionType.TCPServer;
            viewModel.EditorSerialPort = "COM19";
            viewModel.EditorTcpListenPort = "7000";
            viewModel.EditorRestartServer = true;
            viewModel.EditorShowAscii = false;
            viewModel.EditorShowHex = true;
            viewModel.EditorShowOutputTimestamp = true;
            viewModel.EditorIgnoreCase = false;
            viewModel.EditorInputFont = "Cascadia Mono, 12pt";
            viewModel.EditorInputBackgroundArgb = "#FF112233";
            viewModel.EditorInputForegroundArgb = "#FF445566";
            viewModel.EditorOutputBackgroundArgb = "#FF778899";
            viewModel.EditorLogDirectory = @"D:\Capture Files";
            viewModel.EditorLogPrefix = "run_";
            viewModel.EditorMaxLogSize = "512";
            viewModel.SaveEditedProfileCommand.Execute(null);

            var saved = collection.Load("Bench");
            Assert.True(saved.IsValid, string.Join(Environment.NewLine, saved.Issues));
            Assert.Equal(Terminal2ConnectionType.TCPServer, saved.Value!.ConnectionType);
            Assert.Equal("COM19", saved.Value.SerialPort);
            Assert.Equal("7000", saved.Value.TcpListenPort);
            Assert.True(saved.Value.RestartServer);
            Assert.False(saved.Value.ShowAscii);
            Assert.True(saved.Value.ShowHex);
            Assert.True(saved.Value.ShowOutputTimestamp);
            Assert.False(saved.Value.IgnoreCase);
            Assert.Equal("Cascadia Mono, 12pt", saved.Value.InputFont);
            Assert.Equal(unchecked((int)0xFF112233), saved.Value.InputBackgroundArgb);
            Assert.Equal(unchecked((int)0xFF445566), saved.Value.InputForegroundArgb);
            Assert.Equal(unchecked((int)0xFF778899), saved.Value.OutputBackgroundArgb);
            Assert.Equal(@"D:\Capture Files", saved.Value.LogDirectory);
            Assert.Equal("run_", saved.Value.LogPrefix);
            Assert.Equal(512, saved.Value.MaxLogSize);
            Assert.Equal("keep this filter", saved.Value.Filters[10].Text);
            Assert.Equal("Keep this macro", saved.Value.Macros[100]!.Title);
            Assert.Contains(saved.Value.UnknownEntries, entry => entry.RawLine == "FutureOption=preserve-me");
            Assert.False(loadedWorkspaceProfile);

            var text = File.ReadAllText(Path.Combine(directory, "Bench.profile"));
            Assert.Contains("Type=TCPServer\n", text);
            Assert.Contains("MaxLogSize=512\n", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-a-number", "Max log size must be a whole number.")]
    [InlineData("#12AB", "Output background color must use #AARRGGBB format")]
    public void Editor_validation_does_not_overwrite_profile_when_values_are_invalid(string invalidValue, string message)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var collection = new Terminal2ProfileCollection(directory);
            Assert.True(collection.Create(new Terminal2Profile { Name = "Bench", MaxLogSize = 87 }).IsValid);
            var viewModel = CreateViewModel(collection);
            viewModel.SelectedProfile = "Bench";
            if (invalidValue.StartsWith('#'))
                viewModel.EditorOutputBackgroundArgb = invalidValue;
            else
                viewModel.EditorMaxLogSize = invalidValue;

            viewModel.SaveEditedProfileCommand.Execute(null);

            Assert.Contains(message, viewModel.EditorStatus, StringComparison.Ordinal);
            var reloaded = collection.Load("Bench");
            Assert.True(reloaded.IsValid);
            Assert.Equal(87, reloaded.Value!.MaxLogSize);
            Assert.Equal(unchecked((int)0xFFDCDCDC), reloaded.Value.OutputBackgroundArgb);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Terminal2ProfileLibraryViewModel CreateViewModel(
        Terminal2ProfileCollection collection,
        Action? workspaceLoad = null) => new(
            collection,
            getCurrentProfile: () => null,
            canSaveCurrent: () => false,
            loadIntoWorkspace: (_, _, _) => workspaceLoad?.Invoke());

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"signalforge-terminal2-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
