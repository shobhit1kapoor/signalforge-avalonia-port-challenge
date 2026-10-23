using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Protocols.Modbus;
using SignalForge.Core.Protocols.Terminal2;
using SignalForge.Core.Simulation;
using SignalForge.Core.Testing;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Desktop.ViewModels;

public partial class ProtocolTestingViewModel : ViewModelBase
{
    public const string SimulatorSource = "Local Modbus simulator · loopback only";
    public const string Terminal2SimulatorSource = "Local Terminal2 STX/ETX/LRC simulator · loopback only";
    public const string ReplaySource = "Loaded trace replay · offline";

    private readonly ProtocolTestRunner _runner = new();
    private TraceDocument? _replayDocument;
    private string? _replayPath;
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource? _runCompletion;
    private ProtocolTestScenario? _lastScenario;
    private ProtocolTestResult? _lastResult;
    private string _lastRunSource = SimulatorSource;
    private string? _lastRunSourceReference;
    private string _lastRunEndpoint = "";
    private ProtocolTestSourceConfiguration? _lastRunSourceConfiguration;
    private readonly Action<Action> _dispatchToUi;
    private long _deviceEventOriginTicks;

    [ObservableProperty]
    private string _selectedSource = SimulatorSource;

    [ObservableProperty]
    private string _selectedSimulatorFaultPlan = "None";

    [ObservableProperty]
    private string _replaySpeed = "1.0";

    [ObservableProperty]
    private string _runState = "READY";

    [ObservableProperty]
    private string _runMessage = "Choose a local simulator or loaded trace replay. This test runner cannot connect to hardware.";

    [ObservableProperty]
    private string _traceLinkStatus = "Run a test to add its byte evidence and assertions to the trace timeline.";

    [ObservableProperty]
    private string _reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "signalforge-protocol-test-report.json");

    [ObservableProperty]
    private string _reportStatus = "Complete a run to enable its self-contained JSON report.";

    [ObservableProperty]
    private ProtocolTestStepEditor? _selectedStep;

    [ObservableProperty]
    private ProtocolTestEvidenceRow? _selectedEvidenceRow;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private VirtualDeviceServerState _deviceServerState = VirtualDeviceServerState.Stopped;

    [ObservableProperty]
    private string _deviceEndpoint = "No simulator run";

    [ObservableProperty]
    private string _deviceActivityStatus = "Run a local Modbus test to observe real device state transitions and events.";

    public ObservableCollection<string> Sources { get; } = [SimulatorSource, Terminal2SimulatorSource];
    public ObservableCollection<string> SimulatorFaultPlans { get; } =
    [
        "None",
        "Drop response to request 1",
        "Corrupt Modbus function code on response 1",
        "Invalid MBAP frame on request 1",
        "Disconnect on request 1",
        "Delay first response 500 ms",
        "Corrupt Terminal2 LRC on response 1"
    ];
    public ObservableCollection<ProtocolTestStepEditor> Steps { get; } = [];
    public ObservableCollection<ProtocolTestStepResultRow> Results { get; } = [];
    public ObservableCollection<ProtocolTestEvidenceRow> Evidence { get; } = [];
    public ObservableCollection<VirtualDeviceActivityRow> DeviceActivity { get; } = [];

    public bool HasReplayTrace => _replayDocument is not null;
    public string ReplayTraceStatus => _replayDocument is null
        ? "No trace loaded. Load a trace in the timeline to enable offline replay."
        : $"Loaded {_replayDocument.Events.Count} events · {_replayPath ?? _replayDocument.Metadata.SessionName}";
    public string SimulatorFaultDescription => SelectedSimulatorFaultPlan switch
    {
        "Corrupt Modbus function code on response 1" => "Modbus TCP has no checksum; this changes the function byte. Select the Modbus simulator.",
        "Drop response to request 1" => "Drops the first response so the receive assertion times out. Select the Modbus simulator.",
        "Invalid MBAP frame on request 1" => "Returns an invalid MBAP frame. Select the Modbus simulator.",
        "Disconnect on request 1" => "Closes the connection after the first request. Select the Modbus simulator.",
        "Delay first response 500 ms" => "Delays the first response by 500 ms. Select the Modbus simulator.",
        "Corrupt Terminal2 LRC on response 1" => "Flips one bit in the first echoed LRC. Select the Terminal2 simulator.",
        _ => "No simulator fault is applied."
    };
    public bool IsReplaySource => SelectedSource == ReplaySource;
    public bool IsSimulatorSource => SelectedSource is SimulatorSource or Terminal2SimulatorSource;
    public bool IsModbusSimulatorSource => SelectedSource == SimulatorSource;
    public bool IsTerminal2SimulatorSource => SelectedSource == Terminal2SimulatorSource;
    public bool HasDeviceActivity => DeviceActivity.Count > 0;
    public bool HasNoDeviceActivity => DeviceActivity.Count == 0;
    public string DeviceServerStateLabel => DeviceServerState.ToString().ToUpperInvariant();
    public IBrush DeviceStateBrush => DeviceServerState switch
    {
        VirtualDeviceServerState.Listening or VirtualDeviceServerState.ClientConnected or VirtualDeviceServerState.Processing => new SolidColorBrush(Color.Parse("#56D4C8")),
        VirtualDeviceServerState.Starting => new SolidColorBrush(Color.Parse("#E3B869")),
        VirtualDeviceServerState.Faulted => new SolidColorBrush(Color.Parse("#DE7979")),
        _ => new SolidColorBrush(Color.Parse("#748296")),
    };
    public bool CanRunTest => !IsRunning && Steps.Count > 0 && (IsSimulatorSource || IsReplaySource && _replayDocument is not null);
    public bool CanCancelTest => IsRunning && _runCancellation is not null;
    public bool CanExportReport => !IsRunning && _lastScenario is not null && _lastResult is not null && !string.IsNullOrWhiteSpace(ReportPath);
    public bool CanEdit => !IsRunning;
    public bool CanRemoveSelectedStep => !IsRunning && SelectedStep is not null;
    public bool CanOpenSelectedTraceEvent => SelectedEvidenceRow?.TraceSequence is not null;
    public string StatusLabel => RunState switch
    {
        "PASS" => "PASS · ALL ASSERTIONS MATCHED",
        "FAIL" => "FAIL · ASSERTION OR TRANSPORT ERROR",
        "CANCELLED" => "CANCELLED",
        "RUNNING" => IsSimulatorSource ? "RUNNING · LOOPBACK SIMULATOR" : "RUNNING · OFFLINE REPLAY",
        "CANCELLING" => "CANCELLING",
        _ => "READY · NO TEST RUN",
    };
    public IBrush StatusBrush => RunState switch
    {
        "PASS" => new SolidColorBrush(Color.Parse("#56D4C8")),
        "FAIL" => new SolidColorBrush(Color.Parse("#DE7979")),
        "RUNNING" or "CANCELLING" => new SolidColorBrush(Color.Parse("#E3B869")),
        _ => new SolidColorBrush(Color.Parse("#748296")),
    };

    public event EventHandler<ProtocolTestRunCompletedEventArgs>? RunCompleted;
    public event EventHandler<ProtocolTraceRequestedEventArgs>? TraceRequested;

    public ProtocolTestingViewModel(Action<Action>? dispatchToUi = null)
    {
        _dispatchToUi = dispatchToUi ?? (action => Dispatcher.UIThread.Post(action));
        LoadReadExample();
    }

    partial void OnSelectedSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsReplaySource));
        OnPropertyChanged(nameof(IsSimulatorSource));
        OnPropertyChanged(nameof(IsModbusSimulatorSource));
        OnPropertyChanged(nameof(IsTerminal2SimulatorSource));
        OnPropertyChanged(nameof(SimulatorFaultDescription));
        NotifyCommandStates();
    }

    partial void OnSelectedSimulatorFaultPlanChanged(string value) => OnPropertyChanged(nameof(SimulatorFaultDescription));

    partial void OnReplaySpeedChanged(string value) => RunTestCommand.NotifyCanExecuteChanged();

    partial void OnReportPathChanged(string value) => ExportReportCommand.NotifyCanExecuteChanged();

    partial void OnRunStateChanged(string value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrush));
    }

    partial void OnDeviceServerStateChanged(VirtualDeviceServerState value)
    {
        OnPropertyChanged(nameof(DeviceServerStateLabel));
        OnPropertyChanged(nameof(DeviceStateBrush));
    }

    partial void OnIsRunningChanged(bool value) => NotifyCommandStates();

    partial void OnSelectedStepChanged(ProtocolTestStepEditor? value)
    {
        OnPropertyChanged(nameof(CanRemoveSelectedStep));
        RemoveSelectedStepCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedEvidenceRowChanged(ProtocolTestEvidenceRow? value)
    {
        OnPropertyChanged(nameof(CanOpenSelectedTraceEvent));
        OpenSelectedTraceEventCommand.NotifyCanExecuteChanged();
    }

    public void SetReplayTrace(TraceDocument? document, string? path)
    {
        _replayDocument = document;
        _replayPath = document is null ? null : path;
        if (document is not null && !Sources.Contains(ReplaySource))
            Sources.Add(ReplaySource);
        if (document is null)
        {
            Sources.Remove(ReplaySource);
            if (SelectedSource == ReplaySource)
                SelectedSource = SimulatorSource;
        }
        OnPropertyChanged(nameof(HasReplayTrace));
        OnPropertyChanged(nameof(ReplayTraceStatus));
        NotifyCommandStates();
    }

    public void SetTraceReferences(string source, IReadOnlyList<long> eventSequences)
    {
        for (var index = 0; index < Evidence.Count && index < eventSequences.Count; index++)
            Evidence[index].SetTraceReference(source, eventSequences[index]);
        TraceLinkStatus = $"Evidence linked to trace timeline source {source}. Select a row and open its trace event.";
        OnPropertyChanged(nameof(CanOpenSelectedTraceEvent));
        OpenSelectedTraceEventCommand.NotifyCanExecuteChanged();
        if (_lastScenario is not null && _lastResult is not null)
            ReportStatus = "Run report is ready. Evidence will include its trace timeline references.";
    }

    public async Task ShutdownAsync()
    {
        var cancellation = _runCancellation;
        if (cancellation is null)
            return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        var completion = _runCompletion?.Task;
        if (completion is not null)
        {
            try { await completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        }
    }

    [RelayCommand]
    private void AddSendStep() => AddStep(new("Send bytes", "Send bytes") { HexBytes = "01 03 00 00 00 01" });

    [RelayCommand]
    private void AddExpectedBytesStep() => AddStep(new("Expect bytes", "Expect bytes") { HexBytes = "01 03 02 00 00", TimeoutMs = "1000" });

    [RelayCommand]
    private void AddModbusAssertionStep() => AddStep(new("Assert Modbus TCP", "Assert Modbus TCP")
    {
        TransactionId = "1", UnitId = "1", FunctionCode = "3", TimeoutMs = "1000"
    });

    [RelayCommand]
    private void AddDelayStep() => AddStep(new("Delay", "Delay") { DelayMs = "100" });

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedStep))]
    private void RemoveSelectedStep()
    {
        if (SelectedStep is { } step)
        {
            var index = Steps.IndexOf(step);
            Steps.Remove(step);
            SelectedStep = Steps.Count == 0 ? null : Steps[Math.Clamp(index, 0, Steps.Count - 1)];
            NotifyCommandStates();
        }
    }

    [RelayCommand]
    private void LoadReadExample()
    {
        SelectedSource = SimulatorSource;
        SelectedSimulatorFaultPlan = "None";
        Steps.Clear();
        AddStep(new("Send bytes", "Read registers 0–1")
        {
            HexBytes = "00 01 00 00 00 06 01 03 00 00 00 02"
        });
        AddStep(new("Assert Modbus TCP", "Check response")
        {
            TransactionId = "1", UnitId = "1", FunctionCode = "3", TimeoutMs = "1000"
        });
        NotifyCommandStates();
    }

    [RelayCommand]
    private void LoadStxEtxLrcExample()
    {
        SelectedSource = Terminal2SimulatorSource;
        SelectedSimulatorFaultPlan = "None";
        var payload = System.Text.Encoding.UTF8.GetBytes("PUMP=RUN;RPM=1450");
        var frame = Terminal2StxEtxCodec.Encode(payload);
        var hex = FormatHex(frame);
        Steps.Clear();
        AddStep(new("Send bytes", "Send Terminal2 STX/ETX frame") { HexBytes = hex });
        AddStep(new("Expect bytes", "Expect echoed Terminal2 frame")
        {
            HexBytes = hex,
            TimeoutMs = "1000"
        });
        NotifyCommandStates();
    }

    [RelayCommand]
    private void ClearResults()
    {
        Results.Clear();
        Evidence.Clear();
        SelectedEvidenceRow = null;
        RunState = "READY";
        RunMessage = "Results cleared. The test steps are unchanged.";
        TraceLinkStatus = "Run a test to add its byte evidence and assertions to the trace timeline.";
        _lastScenario = null;
        _lastResult = null;
        ReportStatus = "Complete a run to enable its self-contained JSON report.";
        ExportReportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenSelectedTraceEvent));
    }

    [RelayCommand(CanExecute = nameof(CanRunTest))]
    private async Task RunTestAsync()
    {
        ProtocolTestScenario scenario;
        try
        {
            scenario = BuildScenario();
        }
        catch (Exception exception)
        {
            RunState = "FAIL";
            RunMessage = $"Test setup error: {exception.Message}";
            Results.Clear();
            Evidence.Clear();
            SelectedEvidenceRow = null;
            TraceLinkStatus = "No byte evidence was captured because setup failed before the run started.";
            Results.Add(new(1, "Setup", false, exception.Message));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastScenario = null;
        _lastResult = null;
        _lastRunSourceConfiguration = null;
        ResetDeviceActivity();
        ReportStatus = "Run in progress; the report becomes available when it ends.";
        ExportReportCommand.NotifyCanExecuteChanged();
        IsRunning = true;
        RunState = "RUNNING";
        RunMessage = IsSimulatorSource
            ? $"Starting the explicitly selected loopback-only {(IsTerminal2SimulatorSource ? "Terminal2 STX/ETX/LRC" : "Modbus")} simulator…"
            : $"Replaying the explicitly selected trace offline · {ReplayTraceStatus}";
        Results.Clear();
        Evidence.Clear();
        TraceLinkStatus = "Capturing test bytes; trace links will appear when the run completes.";

        IByteTransport? transport = null;
        VirtualDeviceServer? simulator = null;
        Terminal2StxEtxEchoServer? terminal2Simulator = null;
        try
        {
            if (SelectedSource == SimulatorSource)
            {
                simulator = new VirtualDeviceServer(new VirtualDeviceServerOptions
                {
                    Address = IPAddress.Loopback,
                    Port = 0,
                    UnitId = 1,
                    FaultPlan = CreateSimulatorFaultPlan(SelectedSimulatorFaultPlan)
                });
                simulator.Device.SetHoldingRegister(0, 0x1234);
                simulator.Device.SetHoldingRegister(1, 0xBEEF);
                simulator.Device.SetInputRegister(0, 42);
                simulator.EventOccurred += OnVirtualDeviceEventOccurred;
                _deviceEventOriginTicks = Stopwatch.GetTimestamp();
                _lastRunSourceConfiguration = new ProtocolTestSourceConfiguration(
                    "modbusTcpLoopback",
                    1,
                    simulator.FaultPlan.Rules.Select(rule => new ProtocolTestFaultConfiguration(
                        rule.Kind,
                        rule.RequestNumber,
                        rule.Delay.TotalMilliseconds,
                        rule.ByteOffset,
                        rule.XorMask)).ToArray(),
                    simulator.Snapshot);
                await simulator.StartAsync(cancellation.Token);
                DeviceEndpoint = $"{simulator.Host}:{simulator.Port}";
                transport = new TcpClientByteTransport(IPAddress.Loopback.ToString(), simulator.Port);
            }
            else if (IsTerminal2SimulatorSource)
            {
                terminal2Simulator = new Terminal2StxEtxEchoServer(new Terminal2StxEtxEchoServerOptions
                {
                    Address = IPAddress.Loopback,
                    Port = 0,
                    InjectBadLrcOnNextResponse = SelectedSimulatorFaultPlan == "Corrupt Terminal2 LRC on response 1"
                });
                await terminal2Simulator.StartAsync(cancellation.Token);
                transport = new TcpClientByteTransport(terminal2Simulator.Host, terminal2Simulator.Port);
                DeviceEndpoint = $"{terminal2Simulator.Host}:{terminal2Simulator.Port}";
                var lrcFault = SelectedSimulatorFaultPlan == "Corrupt Terminal2 LRC on response 1";
                _lastRunSourceConfiguration = new ProtocolTestSourceConfiguration(
                    "terminal2StxEtxLoopback",
                    null,
                    lrcFault
                        ? [new ProtocolTestFaultConfiguration(VirtualDeviceFaultKind.CorruptResponse,
                            RequestNumber: 1, XorMask: 0x01, Description: "Flip one bit in the echoed frame LRC byte.")]
                        : []);
            }
            else
            {
                if (_replayDocument is null)
                    throw new InvalidOperationException("Load a trace before selecting trace replay.");
                if (!double.TryParse(ReplaySpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
                    || double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
                    throw new FormatException("Replay speed must be a finite number greater than zero.");
                transport = new TraceReplayTransport(_replayDocument, speed);
                DeviceEndpoint = "Offline trace replay";
            }

            var endpoint = transport.Endpoint;
            var displayName = transport.DisplayName;
            var result = await _runner.RunForReportAsync(transport, scenario, cancellation.Token);
            if (IsTerminal2SimulatorSource)
                result = Terminal2LrcTestResultValidator.AddValidationStep(result);
            _lastScenario = scenario;
            _lastResult = result;
            _lastRunSource = SelectedSource;
            _lastRunSourceReference = IsReplaySource ? _replayPath ?? _replayDocument?.Metadata.SessionName : null;
            _lastRunEndpoint = endpoint;
            ApplyResult(result);
            RunMessage = result.Cancelled
                ? $"Test run cancelled through {displayName}; captured bytes and completed steps are preserved."
                : result.Passed
                    ? $"All {result.PassedStepCount} test steps passed through {displayName}."
                    : $"{result.FailedStepCount} test step(s) failed through {displayName}.";
            ReportStatus = "Run report is ready. Export writes one versioned JSON file with embedded evidence.";
            RunCompleted?.Invoke(this, new(result, endpoint, SelectedSource));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RunState = "CANCELLED";
            RunMessage = "Test run cancelled before byte capture began. No hardware transport was used.";
            var now = Stopwatch.GetTimestamp();
            _lastScenario = scenario;
            _lastResult = new ProtocolTestResult(scenario.Name, transport?.DisplayName ?? "Transport setup", [], [], now, now, cancelled: true);
            _lastRunSource = SelectedSource;
            _lastRunSourceReference = IsReplaySource ? _replayPath ?? _replayDocument?.Metadata.SessionName : null;
            _lastRunEndpoint = transport?.Endpoint ?? "unavailable";
            ReportStatus = "Cancelled run report is ready. Its scenario and cancellation state will be exported.";
        }
        catch (Exception exception)
        {
            RunState = "FAIL";
            RunMessage = $"Test run failed: {exception.Message}";
            Results.Clear();
            Results.Add(new(1, "Transport", false, exception.Message));
            var now = Stopwatch.GetTimestamp();
            _lastScenario = scenario;
            _lastResult = new ProtocolTestResult(scenario.Name, transport?.DisplayName ?? "Transport setup", [
                new ProtocolTestStepResult(0, "Transport setup", false, exception.Message,
                    StartedAtTicks: now, CompletedAtTicks: now)
            ], [], now, now);
            _lastRunSource = SelectedSource;
            _lastRunSourceReference = IsReplaySource ? _replayPath ?? _replayDocument?.Metadata.SessionName : null;
            _lastRunEndpoint = transport?.Endpoint ?? "unavailable";
            ReportStatus = "Run ended with a transport error. Export preserves the scenario and failure detail.";
            ExportReportCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            if (transport is not null)
            {
                try { await transport.DisposeAsync(); }
                catch { }
            }
            if (simulator is not null)
            {
                try { await simulator.DisposeAsync(); }
                catch { }
                finally { simulator.EventOccurred -= OnVirtualDeviceEventOccurred; }
            }
            if (terminal2Simulator is not null)
            {
                try { await terminal2Simulator.DisposeAsync(); }
                catch { }
            }
            if (ReferenceEquals(_runCancellation, cancellation))
                _runCancellation = null;
            cancellation.Dispose();
            IsRunning = false;
            _runCompletion?.TrySetResult();
            _runCompletion = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelTest))]
    private void CancelTest()
    {
        try
        {
            _runCancellation?.Cancel();
            RunState = "CANCELLING";
            RunMessage = "Cancellation requested; closing the selected local test source.";
        }
        catch (ObjectDisposedException) { }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedTraceEvent))]
    private void OpenSelectedTraceEvent()
    {
        if (SelectedEvidenceRow is { TraceSource: { } source, TraceSequence: { } sequence })
            TraceRequested?.Invoke(this, new(source, sequence));
    }

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        if (_lastScenario is null || _lastResult is null)
            return;

        try
        {
            var links = Evidence.Select(row => row.TraceSource is { } source && row.TraceSequence is { } sequence
                ? new ProtocolTestTimelineReference(source, sequence)
                : null).ToArray();
            var report = ProtocolTestReportFactory.Create(
                _lastScenario,
                _lastResult,
                _lastRunSource,
                _lastRunEndpoint,
                links,
                sourceReference: _lastRunSourceReference,
                sourceConfiguration: _lastRunSourceConfiguration);
            var writtenPath = await ProtocolTestReportWriter.WriteAsync(ReportPath, report);
            ReportStatus = $"Exported versioned JSON report: {writtenPath}";
        }
        catch (Exception exception)
        {
            ReportStatus = $"Report export failed: {exception.Message} Choose an existing folder and a writable .json path.";
        }
    }

    private ProtocolTestScenario BuildScenario()
    {
        var steps = new List<ProtocolTestStep>();
        foreach (var (editor, index) in Steps.Select((step, index) => (step, index)))
        {
            var name = string.IsNullOrWhiteSpace(editor.Name) ? $"Step {index + 1}" : editor.Name.Trim();
            steps.Add(editor.Type switch
            {
                "Send bytes" => new SendBytesStep(name, ParseHex(editor.HexBytes)),
                "Expect bytes" => new ExpectBytesStep(name, ParseHex(editor.HexBytes),
                    string.IsNullOrWhiteSpace(editor.MaskHex) ? default : ParseHex(editor.MaskHex), ParseTimeout(editor.TimeoutMs)),
                "Assert Modbus TCP" => new ExpectModbusTcpStep(name,
                    ParseOptionalUInt16(editor.TransactionId, "transaction ID"),
                    ParseOptionalByte(editor.UnitId, "unit ID"),
                    ParseOptionalByte(editor.FunctionCode, "function code"),
                    ParseOptionalByte(editor.ExceptionCode, "exception code"),
                    ParseTimeout(editor.TimeoutMs)),
                "Delay" => new DelayTestStep(name, ParseDelay(editor.DelayMs)),
                _ => throw new InvalidOperationException($"Unknown test step type '{editor.Type}'.")
            });
        }
        return new ProtocolTestScenario(IsTerminal2SimulatorSource ? "Terminal2 STX/ETX/LRC loopback" : "Protocol test", steps);
    }

    private static VirtualDeviceFaultPlan CreateSimulatorFaultPlan(string selection)
    {
        var rule = selection switch
        {
            "Drop response to request 1" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.DropResponse),
            "Corrupt Modbus function code on response 1" => new VirtualDeviceFaultRule(
                1, VirtualDeviceFaultKind.CorruptResponse, ByteOffset: 7, XorMask: 0x01),
            "Invalid MBAP frame on request 1" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.InvalidFrame),
            "Disconnect on request 1" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.Disconnect),
            "Delay first response 500 ms" => new VirtualDeviceFaultRule(
                1, VirtualDeviceFaultKind.DelayResponse, TimeSpan.FromMilliseconds(500)),
            _ => null,
        };
        return new VirtualDeviceFaultPlan(rule is null ? [] : [rule]);
    }

    private void ResetDeviceActivity()
    {
        DeviceActivity.Clear();
        OnPropertyChanged(nameof(HasDeviceActivity));
        OnPropertyChanged(nameof(HasNoDeviceActivity));
        DeviceServerState = VirtualDeviceServerState.Stopped;
        DeviceEndpoint = IsModbusSimulatorSource || IsTerminal2SimulatorSource
            ? "Starting loopback simulator…"
            : "Offline trace replay";
        DeviceActivityStatus = IsModbusSimulatorSource
            ? "Waiting for real Modbus device lifecycle and protocol events."
            : IsTerminal2SimulatorSource
                ? "Terminal2 echo simulator selected; Modbus state transitions are not applicable."
                : "Offline trace replay selected; no live device is running.";
    }

    private void OnVirtualDeviceEventOccurred(object? sender, VirtualDeviceEventArgs args)
    {
        var elapsed = _deviceEventOriginTicks > 0
            ? Stopwatch.GetElapsedTime(_deviceEventOriginTicks, args.MonotonicTicks)
            : TimeSpan.Zero;
        var transition = args.PreviousState is { } previous && args.CurrentState is { } current
            ? $"{previous} → {current}"
            : "";
        var row = new VirtualDeviceActivityRow(
            $"+{elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms",
            args.Kind.ToString(),
            transition,
            args.Message,
            args.ConnectionId,
            args.RequestNumber,
            args.Payload.IsEmpty ? "" : FormatHex(args.Payload.Span));

        _dispatchToUi(() =>
        {
            if (args.CurrentState is { } state)
                DeviceServerState = state;
            DeviceActivity.Insert(0, row);
            while (DeviceActivity.Count > 32)
                DeviceActivity.RemoveAt(DeviceActivity.Count - 1);
            OnPropertyChanged(nameof(HasDeviceActivity));
            OnPropertyChanged(nameof(HasNoDeviceActivity));
            DeviceActivityStatus = "Live event stream from the local TCP device · payload bytes shown only when present.";
        });
    }

    private void ApplyResult(ProtocolTestResult result)
    {
        Results.Clear();
        foreach (var step in result.Steps)
            Results.Add(new(step.StepNumber, step.Name, step.Passed, step.Detail));
        Evidence.Clear();
        foreach (var (item, index) in result.Evidence.Select((item, index) => (item, index)))
        {
            var elapsed = Stopwatch.GetElapsedTime(result.StartedAtTicks, item.MonotonicTicks);
            Evidence.Add(new(index, item.StepNumber, item.StepName, item.Direction,
                $"+{elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms",
                FormatHex(item.Bytes.Span), item.Detail));
        }
        SelectedEvidenceRow = Evidence.FirstOrDefault();
        RunState = result.Cancelled ? "CANCELLED" : result.Passed ? "PASS" : "FAIL";
    }

    private void AddStep(ProtocolTestStepEditor editor)
    {
        Steps.Add(editor);
        SelectedStep = editor;
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        AddSendStepCommand.NotifyCanExecuteChanged();
        AddExpectedBytesStepCommand.NotifyCanExecuteChanged();
        AddModbusAssertionStepCommand.NotifyCanExecuteChanged();
        AddDelayStepCommand.NotifyCanExecuteChanged();
        LoadReadExampleCommand.NotifyCanExecuteChanged();
        RunTestCommand.NotifyCanExecuteChanged();
        CancelTestCommand.NotifyCanExecuteChanged();
        RemoveSelectedStepCommand.NotifyCanExecuteChanged();
        ExportReportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunTest));
        OnPropertyChanged(nameof(CanCancelTest));
        OnPropertyChanged(nameof(CanExportReport));
        OnPropertyChanged(nameof(CanRemoveSelectedStep));
        OnPropertyChanged(nameof(CanEdit));
    }

    private static TimeSpan ParseTimeout(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds is < 1 or > 120_000)
            throw new FormatException("Receive timeout must be from 1 to 120000 milliseconds.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan ParseDelay(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds is < 0 or > 300_000)
            throw new FormatException("Delay must be from 0 to 300000 milliseconds.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static ushort? ParseOptionalUInt16(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = ParseUnsigned(text, field);
        if (value > ushort.MaxValue) throw new FormatException($"{field} must be between 0 and 65535.");
        return (ushort)value;
    }

    private static byte? ParseOptionalByte(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = ParseUnsigned(text, field);
        if (value > byte.MaxValue) throw new FormatException($"{field} must be between 0 and 255.");
        return (byte)value;
    }

    private static uint ParseUnsigned(string text, string field)
    {
        var trimmed = text.Trim();
        var isHex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = isHex ? trimmed[2..] : trimmed;
        var style = isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        if (digits.Length == 0 || !uint.TryParse(digits, style, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"{field} must be a non-negative decimal value or 0x-prefixed hexadecimal value.");
        return value;
    }

    private static byte[] ParseHex(string input)
    {
        var compact = string.Concat(input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Replace(":", "", StringComparison.Ordinal)
                .Replace(",", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Replace("0x", "", StringComparison.OrdinalIgnoreCase)));
        if (compact.Length == 0)
            throw new FormatException("Enter one or more byte pairs, such as 01 A0 FF.");
        if ((compact.Length & 1) != 0)
            throw new FormatException("Hex input must contain complete byte pairs, such as 01 A0 FF.");
        try { return Convert.FromHexString(compact); }
        catch (FormatException) { throw new FormatException("Hex input contains an invalid character."); }
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes) => bytes.IsEmpty
        ? "(empty)"
        : string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
}

public sealed record VirtualDeviceActivityRow(
    string Elapsed,
    string Kind,
    string Transition,
    string Message,
    int? ConnectionId,
    int? RequestNumber,
    string PayloadHex)
{
    public bool HasPayload => !string.IsNullOrEmpty(PayloadHex);
}

public partial class ProtocolTestStepEditor(string type, string name) : ObservableObject
{
    public string Type { get; } = type;
    public bool IsSend => Type == "Send bytes";
    public bool IsExpectBytes => Type == "Expect bytes";
    public bool IsModbusAssertion => Type == "Assert Modbus TCP";
    public bool IsDelay => Type == "Delay";

    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _hexBytes = "";
    [ObservableProperty] private string _maskHex = "";
    [ObservableProperty] private string _timeoutMs = "1000";
    [ObservableProperty] private string _transactionId = "";
    [ObservableProperty] private string _unitId = "";
    [ObservableProperty] private string _functionCode = "";
    [ObservableProperty] private string _exceptionCode = "";
    [ObservableProperty] private string _delayMs = "100";
}

public sealed class ProtocolTestStepResultRow(int stepNumber, string name, bool passed, string detail)
{
    public int StepNumber { get; } = stepNumber;
    public string Name { get; } = name;
    public bool Passed { get; } = passed;
    public string State => Passed ? "PASS" : "FAIL";
    public IBrush StateBrush => Passed ? new SolidColorBrush(Color.Parse("#56D4C8")) : new SolidColorBrush(Color.Parse("#DE7979"));
    public string Detail { get; } = detail;
}

public sealed partial class ProtocolTestEvidenceRow(
    int evidenceIndex,
    int stepNumber,
    string stepName,
    ByteDirection direction,
    string elapsed,
    string hex,
    string detail) : ObservableObject
{
    [ObservableProperty] private string _traceReference = "Not linked";
    public int EvidenceIndex { get; } = evidenceIndex;
    public int StepNumber { get; } = stepNumber;
    public string StepName { get; } = stepName;
    public ByteDirection Direction { get; } = direction;
    public string DirectionLabel => Direction == ByteDirection.Tx ? "TX" : "RX";
    public string Elapsed { get; } = elapsed;
    public string Hex { get; } = hex;
    public string Detail { get; } = detail;
    public string? TraceSource { get; private set; }
    public long? TraceSequence { get; private set; }

    public void SetTraceReference(string source, long sequence)
    {
        TraceSource = source;
        TraceSequence = sequence;
        TraceReference = $"{source} · #{sequence}";
    }
}

public sealed class ProtocolTestRunCompletedEventArgs(
    ProtocolTestResult result,
    string endpoint,
    string sourceLabel) : EventArgs
{
    public ProtocolTestResult Result { get; } = result;
    public string Endpoint { get; } = endpoint;
    public string SourceLabel { get; } = sourceLabel;
}

public sealed class ProtocolTraceRequestedEventArgs(string source, long sequence) : EventArgs
{
    public string Source { get; } = source;
    public long Sequence { get; } = sequence;
}
