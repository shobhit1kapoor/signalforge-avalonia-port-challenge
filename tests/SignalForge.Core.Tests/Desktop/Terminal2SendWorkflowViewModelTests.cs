using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Protocols.Terminal2;
using SignalForge.Core.Traces;
using SignalForge.Desktop.Services;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Core.Tests.Desktop;

[Collection("Avalonia dispatcher")]
public sealed class Terminal2SendWorkflowViewModelTests
{
    [Fact]
    public void Terminal2_send_workflows_keep_exact_bytes_and_never_send_recalled_text_implicitly() => RunWithDispatcher(async () =>
    {
        VerifyRecallAndDefaultSettings();
        await VerifyStxEtxSendAsync();
        await VerifySendAsYouTypeAsync();
        await VerifyMacroPauseAndResumeAsync();
        await VerifyMacroTrafficLoggingAsync();
        await VerifyToolbarTrafficLoggingAsync();
        await VerifyFilterTriggeredMacroAsync();
        await VerifyFilterMacroQueueBoundedAsync();
        await VerifyMacroHotkeyExecutionAsync();
    });

    private static void VerifyRecallAndDefaultSettings()
    {
        var viewModel = CreateViewModel();
        viewModel.Terminal2ReceiveHistory.Append("LIVE #1", new TraceEvent(
            1, 1, DateTimeOffset.UnixEpoch, TraceEventKind.Receive,
            [0x52, 0x58, 0x0A], SignalForge.Core.Transport.ByteDirection.Rx, "loopback:test"));
        var row = Assert.Single(viewModel.Terminal2ReceiveHistory.Lines);

        Assert.Equal(string.Empty, viewModel.Terminal2SendText);
        viewModel.Terminal2ReceiveHistory.SelectedLine = row;
        Assert.Equal("RX", viewModel.Terminal2SendText);
        Assert.Contains("No bytes were sent", viewModel.Terminal2TextStatus, StringComparison.Ordinal);
        Assert.False(viewModel.CanSend);

        viewModel.Terminal2SendText = "edited after selection";
        viewModel.Terminal2ReceiveHistory.RecallSelectedLineCommand.Execute(null);
        Assert.Equal(row.Text, viewModel.Terminal2SendText);
        Assert.False(viewModel.CanSend);

        var defaults = CreateViewModel();
        Assert.False(defaults.Terminal2SendAsYouType);
        Assert.Contains("OFF", defaults.Terminal2SendAsYouTypeStatus, StringComparison.Ordinal);
    }

    private static async Task VerifyStxEtxSendAsync()
    {
        var setup = await StartConnectedViewModelAsync(CreateTcpProfile("stx-send", sendCr: true, sendLf: true));
        try
        {
            Assert.False(setup.ViewModel.Terminal2SendAsYouType);
            setup.ViewModel.Terminal2SendText = "A$02$$";
            var payload = Assert.IsType<byte[]>(Terminal2SendTextParser.Parse("A$02$$").Value);
            var expected = Terminal2StxEtxCodec.Encode(payload).Concat(new byte[] { 0x0D, 0x0A }).ToArray();

            await setup.ViewModel.SendTerminal2StxEtxCommand.ExecuteAsync(null);
            var received = await ReadExactlyAsync(setup.Peer.GetStream(), expected.Length);

            Assert.Equal(expected, received);
            Assert.Contains("STX/ETX frame", setup.ViewModel.Terminal2TextStatus, StringComparison.Ordinal);

            var tracePath = setup.ViewModel.RecordingTracePath;
            await setup.ViewModel.ShutdownAsync();
            var trace = await TraceReader.LoadAsync(tracePath);
            var tx = Assert.Single(trace.Events, item => item.Kind == TraceEventKind.Transmit);
            Assert.Equal(expected, tx.Payload);
        }
        finally
        {
            await StopAndCleanAsync(setup);
        }
    }

    private static async Task VerifySendAsYouTypeAsync()
    {
        var profile = CreateTcpProfile("typing-send", sendAsYouType: true, sendCr: false, sendLf: false);
        var setup = await StartConnectedViewModelAsync(profile);
        try
        {
            Assert.True(setup.ViewModel.Terminal2SendAsYouType);
            setup.ViewModel.Terminal2SendText = "A$";
            Assert.Equal(new byte[] { (byte)'A' }, await ReadExactlyAsync(setup.Peer.GetStream(), 1));

            setup.ViewModel.Terminal2SendText = "A$4";
            await Task.Delay(100);
            Assert.False(setup.Peer.GetStream().DataAvailable);

            setup.ViewModel.Terminal2SendText = "A$41$$";
            Assert.Equal(new byte[] { 0x41, (byte)'$' }, await ReadExactlyAsync(setup.Peer.GetStream(), 2));
            Assert.Contains("cannot be retracted", setup.ViewModel.Terminal2SendAsYouTypeStatus, StringComparison.Ordinal);
        }
        finally
        {
            await StopAndCleanAsync(setup);
        }
    }

    private static async Task VerifyMacroPauseAndResumeAsync()
    {
        var profile = CreateTcpProfile("typing-macro", sendAsYouType: true, sendCr: false, sendLf: false);
        profile.Macros[0] = new Terminal2Macro
        {
            Title = "Wait briefly",
            Text = "#DELAY 250",
            DelayBetweenLinesMs = 0
        };
        var setup = await StartConnectedViewModelAsync(profile);
        try
        {
            var macroTask = setup.ViewModel.RunTerminal2MacroCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => setup.ViewModel.Terminal2MacroStatus.StartsWith("Running", StringComparison.Ordinal));
            Assert.Contains("PAUSED during macro execution", setup.ViewModel.Terminal2SendAsYouTypeStatus, StringComparison.Ordinal);

            setup.ViewModel.Terminal2SendText += "ignored";
            await Task.Delay(80);
            Assert.False(setup.Peer.GetStream().DataAvailable);

            await macroTask;
            Assert.Contains("RESUMED after macro execution", setup.ViewModel.Terminal2SendAsYouTypeStatus, StringComparison.Ordinal);
            setup.ViewModel.Terminal2SendText += "R";
            Assert.Equal(new byte[] { (byte)'R' }, await ReadExactlyAsync(setup.Peer.GetStream(), 1));
        }
        finally
        {
            await StopAndCleanAsync(setup);
        }
    }

    private static async Task VerifyMacroTrafficLoggingAsync()
    {
        var logDirectory = Directory.CreateTempSubdirectory("signalforge-terminal2-macro-log-");
        var profile = CreateTcpProfile("macro-log", sendCr: false, sendLf: false);
        profile.LogDirectory = logDirectory.FullName;
        profile.LogPrefix = "macro_";
        profile.Macros[0] = new Terminal2Macro
        {
            Title = "Start, send, and stop traffic log",
            Text = "#STARTLOG{0D}HELLO{0D}#STOPLOG",
            DelayBetweenLinesMs = 0,
            AddCR = false,
            AddLF = false
        };
        var setup = await StartConnectedViewModelAsync(profile);
        try
        {
            await setup.ViewModel.RunTerminal2MacroCommand.ExecuteAsync(null);
            Assert.Equal("HELLO"u8.ToArray(), await ReadExactlyAsync(setup.Peer.GetStream(), 5));
            await WaitUntilAsync(() => setup.ViewModel.Terminal2TrafficLogStatus.StartsWith("STOPPED", StringComparison.Ordinal));

            var logPath = Assert.Single(Directory.GetFiles(logDirectory.FullName, "*.log"));
            var log = await File.ReadAllTextAsync(logPath);
            var loggedTxHex = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Where(columns => columns.Length == 3 && columns[1] == "TX")
                .Select(columns => columns[2]);
            Assert.Equal(["48", "45", "4C", "4C", "4F"], loggedTxHex);
            Assert.Contains("traffic log saved", setup.ViewModel.OperationMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await StopAndCleanAsync(setup);
            logDirectory.Delete(recursive: true);
        }
    }

    private static async Task VerifyToolbarTrafficLoggingAsync()
    {
        var logDirectory = Directory.CreateTempSubdirectory("signalforge-terminal2-toolbar-log-");
        var profile = CreateTcpProfile("toolbar-log", sendCr: false, sendLf: false);
        profile.LogDirectory = logDirectory.FullName;
        profile.LogPrefix = "toolbar_";
        var setup = await StartConnectedViewModelAsync(profile);
        try
        {
            Assert.True(setup.ViewModel.CanToggleTerminal2TrafficLog);
            await setup.ViewModel.ToggleTerminal2TrafficLogCommand.ExecuteAsync(null);
            Assert.True(setup.ViewModel.IsTerminal2TrafficLogging);

            setup.ViewModel.SendHex = "01 02 FE";
            await setup.ViewModel.SendHexCommand.ExecuteAsync(null);
            Assert.Equal(new byte[] { 0x01, 0x02, 0xFE }, await ReadExactlyAsync(setup.Peer.GetStream(), 3));

            await setup.ViewModel.ToggleTerminal2TrafficLogCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => setup.ViewModel.Terminal2TrafficLogStatus.StartsWith("STOPPED", StringComparison.Ordinal));
            var logPath = Assert.Single(Directory.GetFiles(logDirectory.FullName, "*.log"));
            var log = await File.ReadAllTextAsync(logPath);
            Assert.Contains("TX\t0102FE", log, StringComparison.Ordinal);
            Assert.Contains("traffic log saved", setup.ViewModel.OperationMessage, StringComparison.OrdinalIgnoreCase);

            var canToggleChanged = false;
            setup.ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.CanToggleTerminal2TrafficLog))
                    canToggleChanged = true;
            };
            await setup.ViewModel.DisconnectCommand.ExecuteAsync(null);
            Assert.False(setup.ViewModel.CanToggleTerminal2TrafficLog);
            Assert.True(canToggleChanged);
        }
        finally
        {
            await StopAndCleanAsync(setup);
            logDirectory.Delete(recursive: true);
        }
    }

    private static async Task VerifyFilterTriggeredMacroAsync()
    {
        var profile = CreateTcpProfile("filter-triggered-macro", sendCr: false, sendLf: false);
        profile.Filters[0] = new Terminal2Filter
        {
            Mode = 1,
            Text = "READY",
            Macro = "Respond",
        };
        profile.Filters[1] = new Terminal2Filter
        {
            Mode = 1,
            Text = "READY",
            Macro = "Wrong second match",
        };
        profile.Macros[0] = new Terminal2Macro
        {
            Title = "Respond",
            Text = "ACK",
            DelayBetweenLinesMs = 0,
            AddCR = false,
            AddLF = false,
        };

        var setup = await StartConnectedViewModelAsync(profile);
        try
        {
            var stream = setup.Peer.GetStream();
            await stream.WriteAsync("REA"u8.ToArray());
            await Task.Delay(20);
            await stream.WriteAsync("DY\r"u8.ToArray());
            await Task.Delay(20);
            await stream.WriteAsync("\n"u8.ToArray());

            Assert.Equal("ACK"u8.ToArray(), await ReadExactlyAsync(stream, 3));
            await WaitUntilAsync(() => setup.ViewModel.Terminal2MacroStatus.StartsWith("Macro finished", StringComparison.Ordinal));
            Assert.Equal("F01", Assert.Single(setup.ViewModel.Terminal2ReceiveHistory.Lines).FilterLabel);
            Assert.Equal("CRLF", setup.ViewModel.Terminal2ReceiveHistory.Lines[0].EndingLabel);

            await stream.WriteAsync("OTHER\n"u8.ToArray());
            await WaitUntilAsync(() => setup.ViewModel.Terminal2ReceiveHistory.Lines.Count == 2);
            await Task.Delay(100);
            Assert.False(stream.DataAvailable);
        }
        finally
        {
            await StopAndCleanAsync(setup);
        }
    }

    private static async Task VerifyFilterMacroQueueBoundedAsync()
    {
        var profile = CreateTcpProfile("filter-macro-queue", sendCr: false, sendLf: false);
        profile.Filters[0] = new Terminal2Filter
        {
            Mode = 1,
            Text = "READY",
            Macro = "Slow response",
        };
        profile.Macros[0] = new Terminal2Macro
        {
            Title = "Slow response",
            Text = "#DELAY 5000{0D}ACK",
            DelayBetweenLinesMs = 0,
            AddCR = false,
            AddLF = false,
        };

        var setup = await StartConnectedViewModelAsync(profile);
        try
        {
            var stream = setup.Peer.GetStream();
            var incoming = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("READY\n", 70)));
            await stream.WriteAsync(incoming);

            await WaitUntilAsync(() => setup.ViewModel.Terminal2EffectMessages.Any(message =>
                message.Contains("RX filter macro queue is full (64)", StringComparison.Ordinal)));
            Assert.Contains("queue is full (64)", setup.ViewModel.Terminal2MacroStatus, StringComparison.Ordinal);

            await setup.ViewModel.DisconnectCommand.ExecuteAsync(null);
            Assert.False(stream.DataAvailable);
        }
        finally
        {
            await StopAndCleanAsync(setup);
        }
    }

    private static async Task VerifyMacroHotkeyExecutionAsync()
    {
        const int bankTwoShiftF12Index = 48 + 12 + 11;
        var profile = CreateTcpProfile("macro-hotkey-bank", sendCr: false, sendLf: false);
        profile.Macros[bankTwoShiftF12Index] = new Terminal2Macro
        {
            Title = "Bank two Shift F12",
            Text = "BANK2",
            DelayBetweenLinesMs = 0,
            AddCR = false,
            AddLF = false,
        };

        var setup = await StartConnectedViewModelAsync(profile);
        try
        {
            setup.ViewModel.Terminal2MacroHotkeyBank = 2;
            await setup.ViewModel.RunTerminal2MacroHotkeyAsync(bankTwoShiftF12Index);

            Assert.Equal("BANK2"u8.ToArray(), await ReadExactlyAsync(setup.Peer.GetStream(), 5));
            Assert.Contains("Macro finished", setup.ViewModel.Terminal2MacroStatus, StringComparison.Ordinal);
            Assert.Equal(bankTwoShiftF12Index, setup.ViewModel.SelectedTerminal2MacroEditorSlot?.Index);
            Assert.Equal(2, setup.ViewModel.Terminal2MacroHotkeyBank);

            setup.ViewModel.SelectedTerminal2MacroEditorSlot = setup.ViewModel.Terminal2MacroEditorSlots[70];
            Assert.Null(setup.ViewModel.SelectedTerminal2Macro);
            Assert.False(setup.ViewModel.CanRunTerminal2Macro);
        }
        finally
        {
            await StopAndCleanAsync(setup);
        }
    }

    private static MainViewModel CreateViewModel() => new(new NoopTerminalHost());

    private static Terminal2Profile CreateTcpProfile(
        string name,
        bool sendAsYouType = false,
        bool sendCr = true,
        bool sendLf = false) => new()
    {
        Name = name,
        ConnectionType = Terminal2ConnectionType.TCPServer,
        TcpListenPort = ReserveTcpPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        SendAsYouType = sendAsYouType,
        SendCR = sendCr,
        SendLF = sendLf,
    };

    private static async Task<ConnectedSetup> StartConnectedViewModelAsync(Terminal2Profile profile)
    {
        var serialized = Terminal2ProfileSerializer.Serialize(profile);
        Assert.True(serialized.IsValid, string.Join(Environment.NewLine, serialized.Issues));
        var profilePath = Path.Combine(Path.GetTempPath(), $"signalforge-{Guid.NewGuid():N}.profile");
        await File.WriteAllTextAsync(profilePath, serialized.Value!);
        var viewModel = CreateViewModel();
        viewModel.Terminal2ImportPath = profilePath;
        await viewModel.ImportTerminal2ProfileCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasTerminal2Profile, viewModel.Terminal2ProfileStatus);

        await viewModel.ConnectCommand.ExecuteAsync(null);
        var port = int.Parse(viewModel.Port, System.Globalization.CultureInfo.InvariantCulture);
        var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => viewModel.CanSend);
        return new ConnectedSetup(viewModel, peer, profilePath);
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var bytes = new byte[count];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(bytes, cancellation.Token);
        return bytes;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out while waiting for the active TCP byte session.");
            await Task.Delay(10);
        }
    }

    private static async Task StopAndCleanAsync(ConnectedSetup setup)
    {
        var tracePath = setup.ViewModel.RecordingTracePath;
        await setup.ViewModel.ShutdownAsync();
        setup.Peer.Dispose();
        if (File.Exists(tracePath))
            File.Delete(tracePath);
        if (File.Exists(setup.ProfilePath))
            File.Delete(setup.ProfilePath);
    }

    private static void RunWithDispatcher(Func<Task> action)
    {
        var previousContext = SynchronizationContext.Current;
        var dispatcher = Dispatcher.UIThread;
        AvaloniaSynchronizationContext.InstallIfNeeded();
        try
        {
            var task = action();
            while (!task.IsCompleted)
            {
                dispatcher.RunJobs();
                Thread.Sleep(1);
            }
            dispatcher.RunJobs();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed record ConnectedSetup(MainViewModel ViewModel, TcpClient Peer, string ProfilePath);

    private sealed class NoopTerminalHost : ILocalTerminalSessionHost
    {
        public void Attach(TerminalControl control) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
    }
}

[CollectionDefinition("Avalonia dispatcher", DisableParallelization = true)]
public sealed class AvaloniaDispatcherCollection { }
