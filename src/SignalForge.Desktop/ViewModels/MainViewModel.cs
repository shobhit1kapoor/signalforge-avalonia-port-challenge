using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.IO.Ports;
using System.Text;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Sessions;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;
using SignalForge.Core.Protocols.Modbus;
using SignalForge.Core.Protocols.Terminal2;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Profiles;
using SignalForge.Core.Simulation;
using SignalForge.Core.Testing;
using SignalForge.Desktop.Services;
using CoreTransportState = SignalForge.Core.Transport.TransportState;

namespace SignalForge.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#56D4C8"));
    private static readonly IBrush StartingBrush = new SolidColorBrush(Color.Parse("#E3B869"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#DE7979"));
    private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#748296"));

    private readonly ILocalTerminalSessionHost _terminalHost;
    private readonly Terminal2ProfileCollection _terminal2ProfileCollection;
    private readonly Terminal2ProfileLifecycle _terminal2ProfileLifecycle;
    private readonly SemaphoreSlim _terminal2TrafficLogGate = new(1, 1);
    private ByteSession? _byteSession;
    private Terminal2TrafficLogWriter? _terminal2TrafficLogWriter;
    private TraceDocument? _loadedTrace;
    private string _traceSourceLabel = "LIVE";
    private bool _isStarting;
    private bool _isStopping;
    private bool _decoderRebuildQueued;
    private int _decoderGeneration;
    private int _pendingTraceCount;
    private int _projectionDrainScheduled;
    private int _pendingDisplayDrops;
    private int _nextTraceSourceId;
    private int _nextTimelineSourceOrder;
    private int _nextProtocolTestSourceId;
    private int _traceComparisonNavigationVersion;
    private int _macroDisconnectRequested;
    private int _terminal2TrafficLogFailureReported;
    private const int MaxPendingTraceProjection = 2048;
    private const int ProjectionDrainBatchSize = 256;
    private const int MaximumQueuedTerminal2FilterMacros = 64;
    private readonly ConcurrentQueue<PendingTraceProjection> _pendingTraceProjection = new();
    private readonly Queue<Terminal2FilterMacroTrigger> _terminal2FilterMacroQueue = new();
    private readonly Dictionary<string, int> _timelineSourceOrder = new(StringComparer.Ordinal);
    private VirtualDeviceServer? _simulator;
    private Terminal2Profile? _terminal2Profile;
    private CancellationTokenSource? _terminal2MacroCancellation;
    private Task<Terminal2MacroExecutionResult>? _terminal2MacroTask;
    private bool _terminal2MacroRunning;
    private bool _terminal2FilterMacroDrainRunning;
    private bool _terminal2ProfileHasErrors;
    private bool _terminal2TypingPaused;
    private bool _terminal2TypingResumedAfterMacro;
    private bool _suppressTerminal2Typing;
    private string _terminal2TypingObservedText = "";
    private string _terminal2TypingPendingText = "";
    private readonly object _terminal2TypingSendLock = new();
    private Task _terminal2TypingSendTail = Task.CompletedTask;
    private string _terminal2MacroCancelReason = "Stopped by user.";

    [ObservableProperty]
    private string _sessionStatus = "WAITING FOR LOCAL PTY";

    [ObservableProperty]
    private string? _sessionError;

    [ObservableProperty]
    private string _transportStatus = "DISCONNECTED";

    [ObservableProperty]
    private string _transportDetail = "Configure a serial or TCP endpoint to start a byte session.";

    [ObservableProperty]
    private CoreTransportState _transportState = CoreTransportState.Disconnected;

    [ObservableProperty]
    private string _transportType = "TCP client";

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private string _port = "9000";

    [ObservableProperty]
    private string _serialPortName = "COM3";

    [ObservableProperty]
    private string _serialPortEnumerationStatus = "Detected ports are suggestions; unknown or virtual port names can be entered manually.";

    [ObservableProperty]
    private bool _initialDtr;

    [ObservableProperty]
    private bool _initialRts;

    [ObservableProperty]
    private string _baudRate = "115200";

    [ObservableProperty]
    private string _dataBits = "8";

    [ObservableProperty]
    private string _serialParity = nameof(Parity.None);

    [ObservableProperty]
    private string _serialStopBits = nameof(StopBits.One);

    [ObservableProperty]
    private string _serialHandshake = nameof(Handshake.None);

    [ObservableProperty]
    private string _simulatorFaultPlan = "None";

    [ObservableProperty]
    private string _simulatorStatus = "Stopped · loopback-only demo device";

    [ObservableProperty]
    private string _terminal2ImportPath = "";

    [ObservableProperty]
    private string _terminal2ExportPath = "";

    [ObservableProperty]
    private string _terminal2SendText = "";

    [ObservableProperty]
    private bool _terminal2SendAsYouType;

    [ObservableProperty]
    private string _terminal2ProfileStatus = "Import a Terminal2 .profile or .tmf file to begin.";

    [ObservableProperty]
    private string _terminal2ProfileName = "No profile loaded";

    [ObservableProperty]
    private string _terminal2ConnectionStatus = "Import a profile to stage connection settings for the next connection.";

    [ObservableProperty]
    private string _terminal2DiagnosticsText = "No import diagnostics.";

    [ObservableProperty]
    private string _terminal2TextStatus = "Text send requires an imported profile for SendCR / SendLF settings.";

    [ObservableProperty]
    private string _terminal2MacroStatus = "Import a profile with populated macro slots to run a macro.";

    [ObservableProperty]
    private bool _isTerminal2TrafficLogging;

    [ObservableProperty]
    private string _terminal2TrafficLogStatus = "Traffic log off · session JSONL capture remains active.";

    [ObservableProperty]
    private Terminal2MacroSlot? _selectedTerminal2Macro;

    [ObservableProperty]
    private Terminal2MacroSlot? _selectedTerminal2MacroEditorSlot;

    private int _terminal2MacroHotkeyBank = 1;

    public ObservableCollection<int> Terminal2MacroBanks { get; } = [1, 2, 3, 4];

    public int Terminal2MacroHotkeyBank
    {
        get => _terminal2MacroHotkeyBank;
        set
        {
            var normalizedValue = value is >= 1 and <= Terminal2MacroHotkeyResolver.BankCount ? value : 1;
            if (SetProperty(ref _terminal2MacroHotkeyBank, normalizedValue))
                OnPropertyChanged(nameof(Terminal2MacroHotkeyHint));
        }
    }

    public string Terminal2MacroHotkeyHint =>
        $"F1–F12 hotkeys use bank {Terminal2MacroHotkeyBank}; Shift/Ctrl/Alt select rows 2–4.";

    [ObservableProperty]
    private string _terminal2MacroTitleEdit = "";

    [ObservableProperty]
    private string _terminal2MacroTextEdit = "";

    [ObservableProperty]
    private string _terminal2MacroDelayBetweenCharacters = "0";

    [ObservableProperty]
    private string _terminal2MacroDelayBetweenLinesMs = "200";

    [ObservableProperty]
    private string _terminal2MacroRepeatEveryMs = "0";

    [ObservableProperty]
    private string _terminal2MacroStopAfterRepeats = "0";

    [ObservableProperty]
    private bool _terminal2MacroRepeatEnabled;

    [ObservableProperty]
    private bool _terminal2MacroAddCr = true;

    [ObservableProperty]
    private bool _terminal2MacroAddLf;

    [ObservableProperty]
    private string _terminal2MacroEditorStatus = "Import a profile or load one from the profile library to edit macro slots.";

    [ObservableProperty]
    private bool _isSimulatorActive;

    [ObservableProperty]
    private string _sendHex = "";

    [ObservableProperty]
    private string _traceFilePath = "";

    [ObservableProperty]
    private string _recordingTracePath = "";

    [ObservableProperty]
    private string _replaySpeed = "1.0";

    [ObservableProperty]
    private string _replaySeekSequence = "";

    [ObservableProperty]
    private string _replayControlStatus = "Pause replay to seek to a trace sequence.";

    [ObservableProperty]
    private string _operationMessage = "Live sessions automatically record a JSONL trace.";

    [ObservableProperty]
    private TraceEventRow? _selectedTraceRow;

    [ObservableProperty]
    private int _droppedDisplayCount;

    [ObservableProperty]
    private int _displayBacklogCount;

    [ObservableProperty]
    private long _timelineTrimmedRows;

    [ObservableProperty]
    private string _selectedDecoderMode = "Raw";

    [ObservableProperty]
    private string _selectedRtuRole = "Direction-aware (TX request / RX response)";

    public ObservableCollection<string> TransportTypes { get; } = ["TCP client", "TCP server", "Serial", "Local Modbus simulator"];
    public ProtocolTestingViewModel ProtocolTesting { get; }
    public ProtocolSchemaViewModel ProtocolSchema { get; } = new();
    public TraceComparisonViewModel TraceComparison { get; } = new();
    public Terminal2TextFileTransferViewModel Terminal2FileTransfer { get; }
    public Terminal2XModemViewModel Terminal2XModem { get; }
    public BinaryInspectorToolsViewModel BinaryInspectorTools { get; }
    public Terminal2ProfileLibraryViewModel Terminal2ProfileLibrary { get; }
    public Terminal2FiltersViewModel Terminal2Filters { get; }
    public Terminal2ReceiveHistoryViewModel Terminal2ReceiveHistory { get; }
    public ObservableCollection<string> SerialDataBitOptions { get; } = ["5", "6", "7", "8"];
    public ObservableCollection<string> SerialParityOptions { get; } = new(Enum.GetNames<Parity>());
    public ObservableCollection<string> SerialStopBitOptions { get; } = new(Enum.GetNames<StopBits>());
    public ObservableCollection<string> SerialHandshakeOptions { get; } = new(Enum.GetNames<Handshake>());
    public ObservableCollection<string> AvailableSerialPorts { get; } = [];
    public ObservableCollection<string> SimulatorFaultPlans { get; } =
    [
        "None",
        "Drop response to request 1",
        "Corrupt response byte 8 on request 1",
        "Invalid MBAP ID on request 1",
        "Disconnect on request 1",
        "Delay first response 500 ms"
    ];
    public ObservableCollection<string> DecoderModes { get; } = ["Raw", "Modbus TCP", "Modbus RTU", "Terminal2 STX-ETX"];
    public ObservableCollection<string> ModbusRtuRoles { get; } =
    [
        "Direction-aware (TX request / RX response)",
        "Request",
        "Response",
        "Unknown"
    ];
    public ObservableCollection<TraceEventRow> Timeline { get; } = [];
    public ObservableCollection<TraceEventRow> RecentTimeline { get; } = [];
    public ObservableCollection<Terminal2MacroSlot> Terminal2Macros { get; } = [];
    public ObservableCollection<Terminal2MacroSlot> Terminal2MacroEditorSlots { get; } = [];
    public ObservableCollection<string> Terminal2EffectMessages { get; } = [];

    public string SelectedHex => SelectedTraceRow?.Hex ?? "";
    public string SelectedHexDump => SelectedTraceRow?.HexDump ?? "";
    public string SelectedAscii => SelectedTraceRow?.Ascii ?? "";
    public string SelectedEventSummary => SelectedTraceRow?.Summary ?? "No trace event selected.";
    public string SelectedDecoderStatus => SelectedTraceRow?.DecoderStatus ?? "Select a trace event to see decoder output.";
    public string SelectedDecoderOutput => SelectedTraceRow?.DecoderOutput ?? "";
    public string RecentTimelineStatus => RecentTimeline.Count == 0
        ? "No byte or trace activity yet. Connect a transport or run a local simulator test."
        : $"LATEST {RecentTimeline.Count} TIMELINE EVENTS · select one to inspect it";
    public bool IsModbusRtuMode => SelectedDecoderMode == "Modbus RTU";
    public bool IsSerialSelected => TransportType == "Serial";
    public bool IsTcpSelected => TransportType is "TCP client" or "TCP server";
    public bool IsSimulatorSelected => TransportType == "Local Modbus simulator";
    public bool CanSetInitialRts => IsSerialSelected &&
        (string.Equals(SerialHandshake, nameof(Handshake.None), StringComparison.OrdinalIgnoreCase) ||
         string.Equals(SerialHandshake, nameof(Handshake.XOnXOff), StringComparison.OrdinalIgnoreCase));
    public bool CanConfigureSimulator => IsSimulatorSelected && !IsSimulatorActive;
    public bool CanSendModbusExample => CanSend && _byteSession?.Transport?.Kind != TransportKind.Replay;
    public bool HasTerminal2Profile => _terminal2Profile is not null;
    public bool CanImportTerminal2Profile => !_terminal2MacroRunning && !string.IsNullOrWhiteSpace(Terminal2ImportPath);
    public bool CanExportTerminal2Profile => !_terminal2MacroRunning && _terminal2Profile is not null && !string.IsNullOrWhiteSpace(Terminal2ExportPath);
    public bool CanRunTerminal2Macro => CanSend && _terminal2Profile is not null && !_terminal2ProfileHasErrors
        && SelectedTerminal2Macro is not null && !_terminal2MacroRunning;
    public bool CanEditTerminal2Macro => _terminal2Profile is not null && !_terminal2ProfileHasErrors
        && !_terminal2MacroRunning && SelectedTerminal2MacroEditorSlot is not null;
    public bool CanCancelTerminal2Macro => _terminal2MacroRunning;
    public bool CanSendTerminal2Text => CanSend && !_terminal2MacroRunning && _terminal2Profile is not null && !_terminal2ProfileHasErrors
        && (Terminal2SendAsYouType || !string.IsNullOrWhiteSpace(Terminal2SendText));
    public bool CanSendTerminal2StxEtx => CanSend && !_terminal2MacroRunning && !_terminal2TypingPaused
        && !Terminal2SendAsYouType && _terminal2Profile is not null && !_terminal2ProfileHasErrors;
    public bool CanToggleTerminal2TrafficLog => _terminal2TrafficLogWriter is not null
        || CanSend && _terminal2Profile is not null && !_terminal2ProfileHasErrors;
    public string Terminal2TrafficLogButtonText => IsTerminal2TrafficLogging ? "STOP LOG" : "START LOG";
    public string Terminal2SendSettings => _terminal2Profile is null
        ? "SendCR / SendLF unavailable until a profile is imported."
        : $"Imported settings · CR {(_terminal2Profile.SendCR ? "ON" : "OFF")} · LF {(_terminal2Profile.SendLF ? "ON" : "OFF")} · Send as you type {(_terminal2Profile.SendAsYouType ? "ON" : "OFF")}";
    public string Terminal2SendAsYouTypeStatus => Terminal2SendAsYouType
        ? _terminal2TypingPaused || _terminal2MacroRunning
            ? "PAUSED during macro execution · typing resumes when the macro finishes. Previously sent bytes cannot be retracted."
            : _terminal2TypingResumedAfterMacro
                ? "RESUMED after macro execution · new completed text and escapes send immediately. Sent bytes cannot be retracted."
            : "ON · completed text and $HH / $$ escapes send immediately through the traced byte session. Sent bytes cannot be retracted."
        : $"OFF · imported profile preference: {(_terminal2Profile?.SendAsYouType == true ? "ON" : "OFF")}. When enabled, sent bytes cannot be retracted.";
    public string DisplayProjectionStatus =>
        $"DISPLAY QUEUE · {DisplayBacklogCount} PENDING · {DroppedDisplayCount} DISPLAY DROPPED (TRACE INTACT) · {TimelineTrimmedRows} OLD ROWS TRIMMED";
    public string SelectedEventMeta => SelectedTraceRow is { } row
        ? $"{row.Source}  ·  #{row.Event.Sequence}  ·  {row.Timestamp}  ·  {row.Event.Endpoint ?? "no endpoint"}"
        : "Select a timeline event to inspect its exact bytes.";
    public string TraceStateLabel => _byteSession is not null
        ? (_traceSourceLabel.StartsWith("REPLAY", StringComparison.Ordinal) ? "TRACE REPLAY RUNNING" : "SESSION RECORDING")
        : _loadedTrace is null ? "NO TRACE LOADED" : $"TRACE LOADED · {_loadedTrace.Events.Count} EVENTS";
    public bool HasSelectedEvent => SelectedTraceRow is not null;
    public bool HasLoadedTrace => _loadedTrace is not null;
    public string TraceEventCount => Timeline.Count == 1 ? "1 EVENT" : $"{Timeline.Count} EVENTS";

    public IBrush SessionIndicatorBrush => SessionStatus switch
    {
        "LOCAL PTY ACTIVE" => ActiveBrush,
        "LOCAL PTY FAILED" => FailedBrush,
        "LOCAL PTY STOPPED" => StoppedBrush,
        "WAITING FOR LOCAL PTY" => StoppedBrush,
        _ => StartingBrush,
    };

    public IBrush TransportIndicatorBrush => TransportState switch
    {
        CoreTransportState.Connected or CoreTransportState.Listening => ActiveBrush,
        CoreTransportState.Connecting or CoreTransportState.Closing => StartingBrush,
        CoreTransportState.Faulted => FailedBrush,
        _ => StoppedBrush,
    };

    public bool CanSend => _byteSession?.Transport?.State == CoreTransportState.Connected && !_isStopping
        && !_terminal2MacroRunning && !Terminal2FileTransfer.IsRunning && !Terminal2XModem.IsRunning;
    public bool CanDisconnect => _byteSession is not null && !_isStopping;
    public bool CanConnect => _byteSession is null && !_isStarting && !_isStopping;
    public bool CanLoadTrace => !string.IsNullOrWhiteSpace(TraceFilePath);
    public bool CanReplayTrace => _loadedTrace is not null && _byteSession is null && !_isStarting && !_isStopping;
    private TraceReplayTransport? ActiveReplay => _byteSession?.Transport as TraceReplayTransport;
    public bool IsReplayActive => ActiveReplay?.State == CoreTransportState.Connected;
    public bool IsReplayPaused => ActiveReplay?.IsPaused == true;
    public bool CanPauseReplay => IsReplayActive && !IsReplayPaused;
    public bool CanResumeReplay => IsReplayActive && IsReplayPaused;
    public bool CanSeekReplay => CanResumeReplay && long.TryParse(ReplaySeekSequence, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence >= 0;
    public string ReplayCursorText => ActiveReplay?.NextReceiveSequence is { } sequence
        ? $"NEXT RX SEQUENCE · {sequence}"
        : ActiveReplay is null ? "OFFLINE REPLAY INACTIVE" : "NEXT RX SEQUENCE · END";

    public string SessionDescription => SessionStatus switch
    {
        "LOCAL PTY ACTIVE" => "Interactive shell attached to the local operating system.",
        "LOCAL PTY FAILED" => "The local shell could not be started.",
        "LOCAL PTY STOPPED" => "The local pseudo-terminal is stopped.",
        "WAITING FOR LOCAL PTY" => "Select the local shell tab to start an interactive session.",
        _ => "Starting an interactive local shell session.",
    };

    public string SessionNotice => string.IsNullOrWhiteSpace(SessionError) ? SessionDescription : SessionError;

    public MainViewModel(ILocalTerminalSessionHost terminalHost, string? terminal2ProfileDirectory = null)
    {
        _terminalHost = terminalHost;
        Terminal2FileTransfer = new(
            () => Volatile.Read(ref _byteSession),
            () => CanSend,
            OnTerminal2FileTransferActivityChanged);
        Terminal2XModem = new(
            () => Volatile.Read(ref _byteSession),
            () => CanSend,
            OnTerminal2XModemActivityChanged);
        BinaryInspectorTools = new();
        Terminal2Filters = new();
        Terminal2Filters.FiltersChanged += ReapplyTerminal2Filters;
        Terminal2ReceiveHistory = new(Terminal2Filters);
        Terminal2ReceiveHistory.CommandTextRecallRequested += RecallTerminal2CommandText;
        Terminal2ReceiveHistory.FilterMacroTriggered += OnTerminal2FilterMacroTriggered;
        TransportState = CoreTransportState.Disconnected;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _terminal2ProfileCollection = new Terminal2ProfileCollection(terminal2ProfileDirectory ?? Path.Combine(localAppData, "SignalForge", "Terminal2Profiles"));
        _terminal2ProfileLifecycle = new Terminal2ProfileLifecycle(_terminal2ProfileCollection);
        var activeProfileResult = _terminal2ProfileLifecycle.Initialize();
        Terminal2ProfileLibrary = new Terminal2ProfileLibraryViewModel(
            _terminal2ProfileCollection,
            () => _terminal2Profile,
            () => _terminal2Profile is not null && !_terminal2ProfileHasErrors,
            ApplyTerminal2LibraryProfile);
        if (activeProfileResult.Value is { } activeProfile)
        {
            Terminal2ProfileLibrary.SelectedProfile = activeProfile.Name;
            ApplyTerminal2LibraryProfile(activeProfile, activeProfile.Name, activeProfileResult.HasErrors);
            if (activeProfileResult.Issues.Count > 0)
                Terminal2DiagnosticsText = FormatTerminal2Issues(activeProfileResult.Issues);
        }
        else
        {
            Terminal2ProfileName = "Profile startup failed";
            Terminal2ProfileStatus = "No valid active Terminal2 profile could be loaded. Review the profile diagnostics.";
            Terminal2DiagnosticsText = FormatTerminal2Issues(activeProfileResult.Issues);
        }
        ProtocolTesting = new ProtocolTestingViewModel();
        ProtocolTesting.RunCompleted += OnProtocolTestRunCompleted;
        ProtocolTesting.TraceRequested += OnProtocolTraceRequested;
        TraceComparison.TraceEventRequested += OnTraceComparisonEventRequested;
        RefreshSerialPorts();
    }

    partial void OnSessionStatusChanged(string value)
    {
        OnPropertyChanged(nameof(SessionIndicatorBrush));
        OnPropertyChanged(nameof(SessionDescription));
        OnPropertyChanged(nameof(SessionNotice));
    }

    partial void OnSessionErrorChanged(string? value) => OnPropertyChanged(nameof(SessionNotice));

    partial void OnTransportStateChanged(CoreTransportState value)
    {
        TransportStatus = value switch
        {
            CoreTransportState.Listening => "LISTENING",
            CoreTransportState.Connected => "CONNECTED",
            CoreTransportState.Connecting => "CONNECTING",
            CoreTransportState.Closing => "CLOSING",
            CoreTransportState.Faulted => "FAULTED",
            _ => "DISCONNECTED",
        };
        OnPropertyChanged(nameof(TransportIndicatorBrush));
        OnPropertyChanged(nameof(CanSendModbusExample));
        NotifyReplayControlStates();
        NotifyCommandStates();
    }

    partial void OnIsTerminal2TrafficLoggingChanged(bool value)
    {
        OnPropertyChanged(nameof(Terminal2TrafficLogButtonText));
        OnPropertyChanged(nameof(CanToggleTerminal2TrafficLog));
        ToggleTerminal2TrafficLogCommand.NotifyCanExecuteChanged();
    }

    partial void OnTransportTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSerialSelected));
        OnPropertyChanged(nameof(IsTcpSelected));
        OnPropertyChanged(nameof(IsSimulatorSelected));
        OnPropertyChanged(nameof(CanConfigureSimulator));
        OnPropertyChanged(nameof(CanSetInitialRts));
    }

    partial void OnSerialHandshakeChanged(string value) => OnPropertyChanged(nameof(CanSetInitialRts));

    partial void OnIsSimulatorActiveChanged(bool value) => OnPropertyChanged(nameof(CanConfigureSimulator));

    partial void OnSelectedTraceRowChanged(TraceEventRow? value)
    {
        BinaryInspectorTools.Load(value?.Event);
        OnPropertyChanged(nameof(SelectedHex));
        OnPropertyChanged(nameof(SelectedHexDump));
        OnPropertyChanged(nameof(SelectedAscii));
        OnPropertyChanged(nameof(SelectedEventSummary));
        OnPropertyChanged(nameof(SelectedEventMeta));
        OnPropertyChanged(nameof(HasSelectedEvent));
        OnPropertyChanged(nameof(SelectedDecoderStatus));
        OnPropertyChanged(nameof(SelectedDecoderOutput));
    }

    partial void OnSelectedDecoderModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsModbusRtuMode));
        ScheduleDecoderRebuild();
    }

    partial void OnSelectedRtuRoleChanged(string value) => ScheduleDecoderRebuild();

    partial void OnDroppedDisplayCountChanged(int value) => OnPropertyChanged(nameof(DisplayProjectionStatus));
    partial void OnDisplayBacklogCountChanged(int value) => OnPropertyChanged(nameof(DisplayProjectionStatus));
    partial void OnTimelineTrimmedRowsChanged(long value) => OnPropertyChanged(nameof(DisplayProjectionStatus));

    partial void OnTraceFilePathChanged(string value) => LoadTraceCommand.NotifyCanExecuteChanged();

    partial void OnReplaySeekSequenceChanged(string value)
    {
        OnPropertyChanged(nameof(CanSeekReplay));
        SeekReplayCommand.NotifyCanExecuteChanged();
    }

    partial void OnSendHexChanged(string value) => SendHexCommand.NotifyCanExecuteChanged();

    partial void OnTerminal2ImportPathChanged(string value) => ImportTerminal2ProfileCommand.NotifyCanExecuteChanged();
    partial void OnTerminal2ExportPathChanged(string value) => ExportTerminal2ProfileCommand.NotifyCanExecuteChanged();
    partial void OnTerminal2SendTextChanged(string value)
    {
        SendTerminal2TextCommand.NotifyCanExecuteChanged();
        if (_terminal2TypingResumedAfterMacro && !_terminal2TypingPaused)
        {
            _terminal2TypingResumedAfterMacro = false;
            OnPropertyChanged(nameof(Terminal2SendAsYouTypeStatus));
        }
        if (_suppressTerminal2Typing)
        {
            _terminal2TypingObservedText = value;
            _terminal2TypingPendingText = "";
            return;
        }

        if (!Terminal2SendAsYouType || _terminal2Profile is null || _terminal2ProfileHasErrors)
        {
            _terminal2TypingObservedText = value;
            _terminal2TypingPendingText = "";
            return;
        }

        if (_terminal2TypingPaused || _terminal2MacroRunning)
        {
            _terminal2TypingObservedText = value;
            _terminal2TypingPendingText = "";
            Terminal2TextStatus = "Typing is paused while the imported macro runs; bytes typed during this pause are not sent.";
            return;
        }

        if (!value.StartsWith(_terminal2TypingObservedText, StringComparison.Ordinal))
        {
            _terminal2TypingObservedText = value;
            _terminal2TypingPendingText = "";
            Terminal2TextStatus = "Editor text was changed or deleted. Previously sent bytes cannot be retracted; only newly appended text will be streamed.";
            return;
        }

        var appended = value[_terminal2TypingObservedText.Length..];
        _terminal2TypingObservedText = value;
        if (appended.Length == 0)
            return;

        _terminal2TypingPendingText += appended;
        DrainTerminal2TypingText();
    }

    partial void OnTerminal2SendAsYouTypeChanged(bool value)
    {
        _terminal2TypingObservedText = Terminal2SendText;
        _terminal2TypingPendingText = "";
        _terminal2TypingResumedAfterMacro = false;
        OnPropertyChanged(nameof(Terminal2SendAsYouTypeStatus));
        SendTerminal2TextCommand.NotifyCanExecuteChanged();
        SendTerminal2StxEtxCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendTerminal2Text));
        OnPropertyChanged(nameof(CanSendTerminal2StxEtx));
    }

    partial void OnSelectedTerminal2MacroChanged(Terminal2MacroSlot? value)
    {
        RunTerminal2MacroCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunTerminal2Macro));
    }

    partial void OnSelectedTerminal2MacroEditorSlotChanged(Terminal2MacroSlot? value)
    {
        LoadTerminal2MacroEditorSlot(value);
        ApplyTerminal2MacroEditsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditTerminal2Macro));
    }

    public async Task StartLocalSessionAsync()
    {
        if (_isStarting || SessionStatus == "LOCAL PTY ACTIVE")
            return;

        _isStarting = true;
        SessionError = null;
        SessionStatus = "STARTING LOCAL PTY";
        try
        {
            await _terminalHost.StartAsync();
            SessionStatus = "LOCAL PTY ACTIVE";
        }
        catch (Exception exception)
        {
            SessionStatus = "LOCAL PTY FAILED";
            SessionError = $"Could not start the local terminal session: {exception.Message}";
        }
        finally
        {
            _isStarting = false;
        }
    }

    public void StopLocalSession()
    {
        _terminalHost.Stop();
        SessionStatus = "LOCAL PTY STOPPED";
    }

    public async Task ShutdownAsync()
    {
        await ProtocolTesting.ShutdownAsync();
        await StopTerminal2MacroAsync("Application is shutting down.");
        await StopByteSessionAsync("Window closed; trace finalized.");
        await StopTerminal2TrafficLogAsync();
        await StopSimulatorAsync();
        StopLocalSession();
    }

    [RelayCommand(CanExecute = nameof(CanToggleTerminal2TrafficLog))]
    private async Task ToggleTerminal2TrafficLogAsync()
    {
        try
        {
            if (IsTerminal2TrafficLogging)
                await StopTerminal2TrafficLogAsync();
            else
                await StartTerminal2TrafficLogAsync();
        }
        catch (Exception exception)
        {
            Terminal2TrafficLogStatus = $"LOG ERROR · {exception.Message}";
            OperationMessage = $"Could not start Terminal2 traffic logging: {exception.Message}";
        }
    }

    private async Task StartTerminal2TrafficLogAsync(CancellationToken cancellationToken = default)
    {
        await _terminal2TrafficLogGate.WaitAsync(cancellationToken);
        try
        {
            if (_terminal2TrafficLogWriter is not null)
                return;
            var profile = _terminal2Profile ?? throw new InvalidOperationException("Import a Terminal2 profile before starting its traffic log.");
            if (_terminal2ProfileHasErrors)
                throw new InvalidOperationException("Fix the profile diagnostics before starting its traffic log.");

            var writer = await Terminal2TrafficLogWriter.StartAsync(
                profile.LogDirectory, profile.LogPrefix, profile.MaxLogSize, cancellationToken);
            Volatile.Write(ref _terminal2TrafficLogWriter, writer);
            Interlocked.Exchange(ref _terminal2TrafficLogFailureReported, 0);
            UpdateTrafficLogUi(true, $"LOGGING · {Path.GetFileName(writer.CurrentFilePath)}",
                $"Terminal2 traffic log started at {writer.CurrentFilePath}. Automatic session JSONL capture continues.");
            AddTerminal2EffectMessage($"Traffic logging started · {writer.CurrentFilePath}");
        }
        finally
        {
            _terminal2TrafficLogGate.Release();
        }
    }

    private async Task StopTerminal2TrafficLogAsync(CancellationToken cancellationToken = default)
    {
        await _terminal2TrafficLogGate.WaitAsync(cancellationToken);
        try
        {
            var writer = Interlocked.Exchange(ref _terminal2TrafficLogWriter, null);
            if (writer is null)
            {
                UpdateTrafficLogUi(false, "Traffic log off · session JSONL capture remains active.", null);
                return;
            }

            var path = writer.CurrentFilePath;
            try
            {
                await writer.StopAsync(cancellationToken);
                UpdateTrafficLogUi(false, $"STOPPED · {Path.GetFileName(path)}", $"Terminal2 traffic log saved to {path}.");
                AddTerminal2EffectMessage($"Traffic logging stopped · {path}");
            }
            catch (Exception exception)
            {
                UpdateTrafficLogUi(false, $"LOG ERROR · {exception.Message}", $"Terminal2 traffic log did not finish cleanly: {exception.Message}");
            }
            finally
            {
                try { await writer.DisposeAsync(); }
                catch (Exception exception)
                {
                    UpdateTrafficLogUi(false, $"LOG ERROR · {exception.Message}", $"Terminal2 traffic log cleanup failed: {exception.Message}");
                }
            }
        }
        finally
        {
            _terminal2TrafficLogGate.Release();
        }
    }

    private void UpdateTrafficLogUi(bool isActive, string status, string? operationMessage)
    {
        void Update()
        {
            IsTerminal2TrafficLogging = isActive;
            Terminal2TrafficLogStatus = status;
            if (operationMessage is not null)
                OperationMessage = operationMessage;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Update();
        else
            Dispatcher.UIThread.Post(Update);
    }

    [RelayCommand]
    private async Task RestartLocalSessionAsync()
    {
        _terminalHost.Stop();
        SessionStatus = "LOCAL PTY STOPPED";
        await StartLocalSessionAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        _isStarting = true;
        NotifyCommandStates();
        OperationMessage = "Opening transport and starting trace recording…";
        try
        {
            var transport = await CreateTransportAsync();
            await StartByteSessionAsync(transport, "LIVE");
            OperationMessage = $"Connected session is recording to {RecordingTracePath}";
        }
        catch (Exception exception)
        {
            await StopSimulatorAsync();
            TransportState = CoreTransportState.Faulted;
            TransportDetail = exception.Message;
            OperationMessage = $"Connect failed: {exception.Message}";
        }
        finally
        {
            _isStarting = false;
            NotifyCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await StopTerminal2MacroAsync("Transport is disconnecting.");
        await StopByteSessionAsync("Session disconnected; trace finalized.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendHexAsync()
    {
        try
        {
            var bytes = ParseHex(SendHex);
            if (bytes.Length == 0)
            {
                OperationMessage = "Enter one or more bytes as hexadecimal before sending.";
                return;
            }
            var session = _byteSession ?? throw new InvalidOperationException("There is no active byte session.");
            await session.SendAsync(bytes);
            OperationMessage = $"Sent {bytes.Length} bytes: {Convert.ToHexString(bytes)}";
            SendHex = "";
        }
        catch (Exception exception)
        {
            OperationMessage = $"Send failed: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendModbusExample))]
    private async Task SendModbusReadExampleAsync()
    {
        try
        {
            var payload = new byte[] { 0x00, 0x00, 0x00, 0x02 };
            var activeSession = _byteSession ?? throw new InvalidOperationException("There is no active byte session.");
            var isSerial = activeSession.Transport?.Kind == TransportKind.Serial;
            var request = isSerial
                ? ModbusRtuCodec.Encode(1, 0x03, payload)
                : ModbusTcpEncoder.Encode(1, 1, 0x03, payload);
            await activeSession.SendAsync(request);
            OperationMessage = $"Sent Modbus {(isSerial ? "RTU" : "TCP")} read request for holding registers 0–1 (unit 1).";
        }
        catch (Exception exception)
        {
            OperationMessage = $"Modbus example send failed: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadTrace))]
    private async Task LoadTraceAsync()
    {
        try
        {
            var document = await TraceReader.LoadAsync(TraceFilePath);
            _loadedTrace = document;
            ProtocolTesting.SetReplayTrace(document, TraceFilePath);
            Timeline.Clear();
            RecentTimeline.Clear();
            OnPropertyChanged(nameof(RecentTimelineStatus));
            Terminal2Filters.SetMatchCounts(0, 0);
            SelectedTraceRow = null;
            TimelineTrimmedRows = 0;
            var source = $"LOADED #{Interlocked.Increment(ref _nextTraceSourceId)}";
            ReserveTimelineSource(source);
            foreach (var traceEvent in document.Events)
                AddTimelineRow(traceEvent, source);
            Terminal2ReceiveHistory.CompleteSource(source);
            ScheduleDecoderRebuild();
            OnPropertyChanged(nameof(HasLoadedTrace));
            OnPropertyChanged(nameof(TraceStateLabel));
            OnPropertyChanged(nameof(TraceEventCount));
            OperationMessage = $"Loaded {document.Events.Count} events from {TraceFilePath}";
            ReplayTraceCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception)
        {
            OperationMessage = $"Trace load failed: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportTerminal2Profile))]
    private async Task ImportTerminal2ProfileAsync()
    {
        var path = Terminal2ImportPath.Trim();
        _terminal2FilterMacroQueue.Clear();
        _terminal2Profile = null;
        _terminal2ProfileHasErrors = true;
        Terminal2SendAsYouType = false;
        Terminal2Filters.LoadProfile(null);
        Terminal2ConnectionStatus = "Import in progress · connection form is unchanged.";
        Terminal2Macros.Clear();
        Terminal2MacroEditorSlots.Clear();
        SelectedTerminal2Macro = null;
        SelectedTerminal2MacroEditorSlot = null;
        Terminal2ProfileName = "No profile loaded";
        Terminal2ProfileStatus = "Importing Terminal2 file…";
        Terminal2DiagnosticsText = "Import is in progress.";
        NotifyCommandStates();
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".profile", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(path), ".tmf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a Terminal2 .profile or .tmf file.");

            var contents = await File.ReadAllTextAsync(path);
            var result = Terminal2ProfileImporter.Import(path, contents);
            _terminal2Profile = result.Value;
            _terminal2ProfileHasErrors = result.Value is null || result.HasErrors;
            Terminal2SendAsYouType = result.Value?.SendAsYouType ?? false;
            Terminal2Filters.LoadProfile(result.Value);
            Terminal2ConnectionStatus = result.Value is null
                ? "Profile connection settings were not staged; the connection form is unchanged."
                : StageTerminal2ConnectionSettings(result.Value);
            Terminal2Macros.Clear();
            SelectedTerminal2Macro = null;
            if (result.Value is { } profile)
            {
                Terminal2ProfileName = profile.Name;
                Terminal2ExportPath = Path.ChangeExtension(path, ".profile");
                PopulateTerminal2MacroEditorSlots(profile);
            }
            else
            {
                Terminal2ProfileName = "Import failed";
            }

            Terminal2DiagnosticsText = FormatTerminal2Issues(result.Issues);
            Terminal2ProfileStatus = result.Value is null
                ? "No profile was loaded; review the import diagnostic."
                : result.HasErrors
                    ? "Profile parsed with errors; macro execution and text send are disabled."
                    : $"Loaded {Path.GetFileName(path)} · {Terminal2Macros.Count} populated macro slot(s).";
            if (result.Value is not null)
                Terminal2ProfileStatus += $" {Terminal2ConnectionStatus}";
            OnPropertyChanged(nameof(HasTerminal2Profile));
            OnPropertyChanged(nameof(Terminal2SendSettings));
            Terminal2ProfileLibrary.NotifyCurrentProfileChanged();
            NotifyCommandStates();
        }
        catch (Exception exception)
        {
            Terminal2ProfileName = "Import failed";
            Terminal2ProfileStatus = $"Import failed: {exception.Message}";
            Terminal2ConnectionStatus = "Profile connection settings were not staged; the connection form is unchanged.";
            Terminal2DiagnosticsText = exception.ToString();
            NotifyTerminal2CommandStates();
        }
    }

    private void ApplyTerminal2LibraryProfile(Terminal2Profile profile, string sourceName, bool hasErrors)
    {
        _terminal2FilterMacroQueue.Clear();
        var activeResult = _terminal2ProfileLifecycle.SetActive(sourceName);
        _terminal2Profile = profile;
        _terminal2ProfileHasErrors = hasErrors;
        Terminal2SendAsYouType = profile.SendAsYouType;
        Terminal2Filters.LoadProfile(profile);
        Terminal2ConnectionStatus = StageTerminal2ConnectionSettings(profile);
        Terminal2Macros.Clear();
        Terminal2MacroEditorSlots.Clear();
        SelectedTerminal2Macro = null;
        SelectedTerminal2MacroEditorSlot = null;
        Terminal2ProfileName = profile.Name;
        Terminal2ImportPath = Path.Combine(_terminal2ProfileCollection.DirectoryPath, sourceName + ".profile");
        Terminal2ExportPath = Terminal2ImportPath;
        PopulateTerminal2MacroEditorSlots(profile);
        Terminal2DiagnosticsText = hasErrors
            ? "Profile was loaded with validation errors. Review the profile library status; send and macro execution are disabled."
            : "Loaded from the local Terminal2 profile library.";
        Terminal2ProfileStatus = hasErrors
            ? $"Loaded {sourceName} with validation errors · send and macro execution disabled."
            : $"Loaded {sourceName} from the local profile library · {Terminal2Macros.Count} populated macro slot(s).";
        Terminal2ProfileStatus += $" {Terminal2ConnectionStatus}";
        if (activeResult.Value is null)
            Terminal2ProfileStatus += " The active-profile marker could not be updated; this profile will not be restored automatically.";
        OnPropertyChanged(nameof(HasTerminal2Profile));
        OnPropertyChanged(nameof(Terminal2SendSettings));
        Terminal2ProfileLibrary.NotifyCurrentProfileChanged();
        NotifyCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanExportTerminal2Profile))]
    private async Task ExportTerminal2ProfileAsync()
    {
        var profile = _terminal2Profile;
        if (profile is null)
            return;
        try
        {
            var path = Terminal2ExportPath.Trim();
            if (!string.Equals(Path.GetExtension(path), ".profile", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The export path must end in .profile.");
            var serialized = Terminal2ProfileSerializer.Serialize(profile);
            Terminal2DiagnosticsText = FormatTerminal2Issues(serialized.Issues);
            if (serialized.Value is null || serialized.HasErrors)
            {
                Terminal2ProfileStatus = "Export was blocked by serializer validation; review diagnostics.";
                return;
            }
            await File.WriteAllTextAsync(path, serialized.Value);
            Terminal2ProfileStatus = $"Exported {profile.Name} via Terminal2ProfileSerializer to {path}.";
        }
        catch (Exception exception)
        {
            Terminal2ProfileStatus = $"Export failed: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunTerminal2Macro))]
    private async Task RunTerminal2MacroAsync()
    {
        var profile = _terminal2Profile;
        var macro = SelectedTerminal2Macro;
        if (profile is null || macro is null || !CanSend)
            return;

        await RunTerminal2MacroSlotAsync(profile, macro.Index, triggerFilterSlot: null);
        if (!_terminal2FilterMacroDrainRunning && _terminal2FilterMacroQueue.Count > 0)
            await DrainTerminal2FilterMacrosAsync();
    }

    public async Task RunTerminal2MacroHotkeyAsync(int macroIndex)
    {
        var profile = _terminal2Profile;
        if (profile is null || _terminal2ProfileHasErrors)
        {
            Terminal2MacroStatus = "Macro hotkey ignored · import a valid Terminal2 profile first.";
            return;
        }

        if ((uint)macroIndex >= (uint)profile.Macros.Length)
        {
            Terminal2MacroStatus = "Macro hotkey ignored · resolved slot is outside the imported profile.";
            return;
        }

        if ((uint)macroIndex < (uint)Terminal2MacroEditorSlots.Count)
            SelectedTerminal2MacroEditorSlot = Terminal2MacroEditorSlots[macroIndex];

        if (profile.Macros[macroIndex] is not { } macro || !IsPopulatedTerminal2Macro(macro))
        {
            Terminal2MacroStatus = $"Macro hotkey ignored · bank {macroIndex / Terminal2MacroHotkeyResolver.SlotsPerBank + 1}, slot {macroIndex % Terminal2MacroHotkeyResolver.SlotsPerBank + 1:00} is empty.";
            return;
        }

        if (!CanSend)
        {
            Terminal2MacroStatus = _terminal2MacroRunning
                ? "Macro hotkey ignored · another macro is already running."
                : "Macro hotkey ignored · connect a byte session before running a profile macro.";
            return;
        }

        await RunTerminal2MacroSlotAsync(profile, macroIndex, triggerFilterSlot: null);
        if (!_terminal2FilterMacroDrainRunning && _terminal2FilterMacroQueue.Count > 0)
            await DrainTerminal2FilterMacrosAsync();
    }

    private async Task RunTerminal2MacroSlotAsync(Terminal2Profile profile, int macroIndex, int? triggerFilterSlot)
    {
        if (!ReferenceEquals(profile, _terminal2Profile)
            || (uint)macroIndex >= (uint)profile.Macros.Length
            || profile.Macros[macroIndex] is not { } macro
            || _terminal2MacroRunning
            || !CanSend)
            return;

        var cancellation = new CancellationTokenSource();
        _terminal2MacroCancellation = cancellation;
        _terminal2MacroRunning = true;
        _terminal2TypingPaused = Terminal2SendAsYouType;
        _terminal2TypingResumedAfterMacro = false;
        Interlocked.Exchange(ref _macroDisconnectRequested, 0);
        _terminal2MacroCancelReason = "Stopped by user.";
        Terminal2MacroStatus = triggerFilterSlot is { } filterSlot
            ? $"RX filter F{filterSlot + 1:00} matched · running slot {macroIndex + 1}: {macro.Title}"
            : $"Running slot {macroIndex + 1}: {macro.Title}";
        OnPropertyChanged(nameof(Terminal2SendAsYouTypeStatus));
        NotifyCommandStates();
        var disconnectAfterRun = false;
        try
        {
            if (Terminal2SendAsYouType)
                await FlushTerminal2TypingSendsAsync();
            var executor = new DesktopTerminal2MacroExecutor(this);
            var execution = Terminal2MacroEngine.RunAsync(profile, macroIndex, executor, cancellation.Token);
            _terminal2MacroTask = execution;
            var result = await execution;
            Terminal2MacroStatus = $"Macro finished · {result.CompletedRuns} completed run(s).";
            foreach (var issue in result.Issues)
                AddTerminal2EffectMessage(FormatTerminal2Issue(issue));
            disconnectAfterRun = Interlocked.Exchange(ref _macroDisconnectRequested, 0) != 0;
        }
        catch (OperationCanceledException)
        {
            Terminal2MacroStatus = $"Macro cancelled · {_terminal2MacroCancelReason}";
            disconnectAfterRun = Interlocked.Exchange(ref _macroDisconnectRequested, 0) != 0;
        }
        catch (Exception exception)
        {
            Terminal2MacroStatus = $"Macro stopped: {exception.Message}";
            AddTerminal2EffectMessage(exception.ToString());
            disconnectAfterRun = Interlocked.Exchange(ref _macroDisconnectRequested, 0) != 0;
        }
        finally
        {
            _terminal2MacroTask = null;
            _terminal2MacroCancellation = null;
            _terminal2MacroRunning = false;
            _terminal2TypingPaused = false;
            _terminal2TypingResumedAfterMacro = Terminal2SendAsYouType;
            _terminal2TypingObservedText = Terminal2SendText;
            OnPropertyChanged(nameof(Terminal2SendAsYouTypeStatus));
            cancellation.Dispose();
            NotifyCommandStates();
        }

        if (disconnectAfterRun)
        {
            _terminal2FilterMacroQueue.Clear();
            if (_byteSession is not null)
                await StopByteSessionAsync("Disconnected by the imported Terminal2 macro directive.");
        }
    }

    private void OnTerminal2FilterMacroTriggered(string macroTitle, int filterSlot)
    {
        var profile = _terminal2Profile;
        if (profile is null || _terminal2ProfileHasErrors)
            return;

        var macroIndex = Terminal2MacroEngine.FindMacroIndex(profile, macroTitle);
        if (macroIndex < 0)
        {
            Terminal2MacroStatus = $"RX filter F{filterSlot + 1:00} refers to missing macro title “{macroTitle}”.";
            AddTerminal2EffectMessage(Terminal2MacroStatus);
            return;
        }

        if (!CanRunTerminal2FilterMacro)
        {
            Terminal2MacroStatus = $"RX filter F{filterSlot + 1:00} matched, but no connected byte session is available; macro “{macroTitle}” was not run.";
            return;
        }

        if (_terminal2FilterMacroQueue.Count >= MaximumQueuedTerminal2FilterMacros)
        {
            Terminal2MacroStatus = $"RX filter macro queue is full ({MaximumQueuedTerminal2FilterMacros}); trigger F{filterSlot + 1:00} was dropped.";
            AddTerminal2EffectMessage(Terminal2MacroStatus);
            return;
        }

        _terminal2FilterMacroQueue.Enqueue(new Terminal2FilterMacroTrigger(macroTitle, filterSlot));
        Terminal2MacroStatus = $"Queued RX filter F{filterSlot + 1:00} macro “{macroTitle}” · {_terminal2FilterMacroQueue.Count} pending.";
        if (!_terminal2MacroRunning && !_terminal2FilterMacroDrainRunning)
            _ = DrainTerminal2FilterMacrosAsync();
    }

    private bool CanRunTerminal2FilterMacro => !_isStopping
        && _terminal2Profile is not null
        && !_terminal2ProfileHasErrors
        && _byteSession?.Transport?.State == CoreTransportState.Connected
        && !Terminal2FileTransfer.IsRunning
        && !Terminal2XModem.IsRunning;

    private async Task DrainTerminal2FilterMacrosAsync()
    {
        if (_terminal2FilterMacroDrainRunning || _terminal2MacroRunning)
            return;

        _terminal2FilterMacroDrainRunning = true;
        try
        {
            while (_terminal2FilterMacroQueue.TryDequeue(out var trigger))
            {
                var profile = _terminal2Profile;
                if (profile is null || _terminal2ProfileHasErrors || !CanRunTerminal2FilterMacro)
                {
                    _terminal2FilterMacroQueue.Clear();
                    Terminal2MacroStatus = "Pending RX filter macros were cleared because the profile or byte session is no longer available.";
                    return;
                }

                var macroIndex = Terminal2MacroEngine.FindMacroIndex(profile, trigger.MacroTitle);
                if (macroIndex < 0)
                {
                    Terminal2MacroStatus = $"Queued RX filter macro title “{trigger.MacroTitle}” is no longer present in the profile.";
                    continue;
                }

                await RunTerminal2MacroSlotAsync(profile, macroIndex, trigger.FilterSlot);
            }
        }
        finally
        {
            _terminal2FilterMacroDrainRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditTerminal2Macro))]
    private void ApplyTerminal2MacroEdits()
    {
        var profile = _terminal2Profile;
        var slot = SelectedTerminal2MacroEditorSlot;
        if (profile is null || slot is null || (uint)slot.Index >= (uint)profile.Macros.Length || !CanEditTerminal2Macro)
            return;

        if (!TryReadMacroInteger(Terminal2MacroDelayBetweenCharacters, 0, 60_000, "Delay between characters", out var characterDelay, out var error)
            || !TryReadMacroInteger(Terminal2MacroDelayBetweenLinesMs, 0, 999_999_999, "Delay between lines", out var lineDelay, out error)
            || !TryReadMacroInteger(Terminal2MacroRepeatEveryMs, 0, 999_999_999, "Repeat interval", out var repeatEvery, out error)
            || !TryReadMacroInteger(Terminal2MacroStopAfterRepeats, 0, 60_000, "Stop after repeats", out var stopAfter, out error))
        {
            Terminal2MacroEditorStatus = error;
            return;
        }

        if (Terminal2MacroTitleEdit.Contains('\r') || Terminal2MacroTitleEdit.Contains('\n')
            || Terminal2MacroTextEdit.Contains('\r') || Terminal2MacroTextEdit.Contains('\n'))
        {
            Terminal2MacroEditorStatus = "Title and macro text must be single-line profile values. Use {0D} and {0A} inside macro text for line breaks.";
            return;
        }

        var updated = new Terminal2Macro
        {
            Title = Terminal2MacroTitleEdit,
            Text = Terminal2MacroTextEdit,
            DelayBetweenCharacters = characterDelay,
            DelayBetweenLinesMs = lineDelay,
            RepeatEveryMs = repeatEvery,
            StopAfterRepeats = stopAfter,
            RepeatEnabled = Terminal2MacroRepeatEnabled,
            AddCR = Terminal2MacroAddCr,
            AddLF = Terminal2MacroAddLf,
        };

        profile.Macros[slot.Index] = IsPopulatedTerminal2Macro(updated) ? updated : null;
        RefreshTerminal2MacroSlot(slot.Index);
        Terminal2MacroEditorStatus = profile.Macros[slot.Index] is null
            ? $"Bank {slot.Index / 48 + 1}, slot {slot.Index % 48 + 1} is empty."
            : $"Updated bank {slot.Index / 48 + 1}, slot {slot.Index % 48 + 1}. Save the profile in PROFILE LIBRARY to keep the edit.";
        NotifyTerminal2CommandStates();
    }

    private void PopulateTerminal2MacroEditorSlots(Terminal2Profile profile, int? selectIndex = null)
    {
        Terminal2MacroEditorSlots.Clear();
        Terminal2Macros.Clear();
        for (var index = 0; index < profile.Macros.Length; index++)
        {
            var macro = profile.Macros[index];
            var title = macro is not null && !string.IsNullOrWhiteSpace(macro.Title)
                ? macro.Title
                : index < profile.Titles.Length && !string.IsNullOrWhiteSpace(profile.Titles[index])
                    ? profile.Titles[index]
                    : $"Bank {index / 48 + 1} · Slot {index % 48 + 1:00}";
            var summary = macro is not null && IsPopulatedTerminal2Macro(macro) ? SummarizeMacroText(macro.Text) : "(empty)";
            var item = new Terminal2MacroSlot(index, title, summary);
            Terminal2MacroEditorSlots.Add(item);
            if (macro is not null && IsPopulatedTerminal2Macro(macro))
                Terminal2Macros.Add(item);
        }

        var initialIndex = selectIndex ?? Terminal2Macros.FirstOrDefault()?.Index ?? 0;
        SelectedTerminal2Macro = Terminal2Macros.FirstOrDefault(item => item.Index == initialIndex)
            ?? Terminal2Macros.FirstOrDefault();
        SelectedTerminal2MacroEditorSlot = Terminal2MacroEditorSlots.FirstOrDefault(item => item.Index == initialIndex)
            ?? Terminal2MacroEditorSlots.FirstOrDefault();
    }

    private void LoadTerminal2MacroEditorSlot(Terminal2MacroSlot? slot)
    {
        var macro = slot is not null && (uint)slot.Index < (uint)(_terminal2Profile?.Macros.Length ?? 0)
            ? _terminal2Profile?.Macros[slot.Index]
            : null;
        Terminal2MacroTitleEdit = macro?.Title ?? string.Empty;
        Terminal2MacroTextEdit = macro?.Text ?? string.Empty;
        Terminal2MacroDelayBetweenCharacters = (macro?.DelayBetweenCharacters ?? 0).ToString(CultureInfo.InvariantCulture);
        Terminal2MacroDelayBetweenLinesMs = (macro?.DelayBetweenLinesMs ?? 200).ToString(CultureInfo.InvariantCulture);
        Terminal2MacroRepeatEveryMs = (macro?.RepeatEveryMs ?? 0).ToString(CultureInfo.InvariantCulture);
        Terminal2MacroStopAfterRepeats = (macro?.StopAfterRepeats ?? 0).ToString(CultureInfo.InvariantCulture);
        Terminal2MacroRepeatEnabled = macro?.RepeatEnabled ?? false;
        Terminal2MacroAddCr = macro?.AddCR ?? true;
        Terminal2MacroAddLf = macro?.AddLF ?? false;
        SelectedTerminal2Macro = slot is null
            ? null
            : Terminal2Macros.FirstOrDefault(item => item.Index == slot.Index);
        if (slot is not null)
            Terminal2MacroHotkeyBank = slot.Index / Terminal2MacroHotkeyResolver.SlotsPerBank + 1;
        Terminal2MacroEditorStatus = slot is null
            ? "Select a bank and slot to edit."
            : macro is null || !IsPopulatedTerminal2Macro(macro)
                ? "Empty slot. Applying the default values leaves it empty."
                : $"Editing bank {slot.Index / 48 + 1}, slot {slot.Index % 48 + 1}.";
    }

    private void RefreshTerminal2MacroSlot(int index)
    {
        var profile = _terminal2Profile;
        if (profile is null || (uint)index >= (uint)profile.Macros.Length)
            return;

        var macro = profile.Macros[index];
        var title = macro is not null && !string.IsNullOrWhiteSpace(macro.Title)
            ? macro.Title
            : index < profile.Titles.Length && !string.IsNullOrWhiteSpace(profile.Titles[index])
                ? profile.Titles[index]
                : $"Bank {index / 48 + 1} · Slot {index % 48 + 1:00}";
        var summary = macro is not null && IsPopulatedTerminal2Macro(macro) ? SummarizeMacroText(macro.Text) : "(empty)";
        var item = new Terminal2MacroSlot(index, title, summary);
        Terminal2MacroEditorSlots[index] = item;
        var oldPopulated = Terminal2Macros.FirstOrDefault(candidate => candidate.Index == index);
        if (macro is not null && IsPopulatedTerminal2Macro(macro))
        {
            var position = oldPopulated is null ? -1 : Terminal2Macros.IndexOf(oldPopulated);
            if (position < 0)
                Terminal2Macros.Add(item);
            else
                Terminal2Macros[position] = item;
            SelectedTerminal2Macro = item;
        }
        else if (oldPopulated is not null)
        {
            Terminal2Macros.Remove(oldPopulated);
            SelectedTerminal2Macro = Terminal2Macros.FirstOrDefault();
        }
        SelectedTerminal2MacroEditorSlot = item;
    }

    private static bool TryReadMacroInteger(
        string value,
        int minimum,
        int maximum,
        string label,
        out int result,
        out string error)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= minimum && result <= maximum)
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be a whole number from {minimum} to {maximum}.";
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanCancelTerminal2Macro))]
    private async Task CancelTerminal2MacroAsync()
    {
        await StopTerminal2MacroAsync("Stopped by user.");
    }

    [RelayCommand(CanExecute = nameof(CanSendTerminal2Text))]
    private async Task SendTerminal2TextAsync()
    {
        var profile = _terminal2Profile;
        var session = _byteSession;
        if (profile is null || session is null || !CanSend)
            return;

        try
        {
            if (Terminal2SendAsYouType)
            {
                await FlushTerminal2TypingSendsAsync();
                if (_terminal2TypingPendingText.Length > 0)
                {
                    Terminal2TextStatus = "Complete the pending $HH or $$ escape before ending the typed command.";
                    return;
                }

                var terminator = new List<byte>();
                if (profile.SendCR)
                    terminator.Add((byte)'\r');
                if (profile.SendLF)
                    terminator.Add((byte)'\n');
                if (terminator.Count > 0)
                    await session.SendAsync(terminator.ToArray());

                Terminal2TextStatus = terminator.Count == 0
                    ? "Typed bytes were already sent. No CR/LF terminator is enabled; the editor was cleared."
                    : $"Typed bytes were already sent · appended {terminator.Count} CR/LF byte(s) · {Convert.ToHexString(terminator.ToArray())}";
                SetTerminal2CommandTextWithoutStreaming("");
                return;
            }

            var parsed = Terminal2SendTextParser.Parse(Terminal2SendText);
            if (!parsed.IsValid || parsed.Value is null)
            {
                Terminal2TextStatus = FormatTerminal2Issues(parsed.Issues);
                return;
            }
            if (parsed.Value.Length == 0)
            {
                Terminal2TextStatus = "Enter text or $HH byte escapes before sending.";
                return;
            }

            var bytes = parsed.Value.ToList();
            if (profile.SendCR)
                bytes.Add((byte)'\r');
            if (profile.SendLF)
                bytes.Add((byte)'\n');
            await session.SendAsync(bytes.ToArray());
            Terminal2TextStatus = $"Sent {bytes.Count} byte(s) via Terminal2SendTextParser · {Convert.ToHexString(bytes.ToArray())}";
            if (profile.ClearCommand)
                SetTerminal2CommandTextWithoutStreaming("");
        }
        catch (Exception exception)
        {
            Terminal2TextStatus = $"Text send failed: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendTerminal2StxEtx))]
    private async Task SendTerminal2StxEtxAsync()
    {
        var profile = _terminal2Profile;
        var session = _byteSession;
        if (profile is null || session is null || !CanSendTerminal2StxEtx)
            return;

        try
        {
            var parsed = Terminal2SendTextParser.Parse(Terminal2SendText);
            if (!parsed.IsValid || parsed.Value is null)
            {
                Terminal2TextStatus = FormatTerminal2Issues(parsed.Issues);
                return;
            }

            var bytes = Terminal2StxEtxCodec.Encode(parsed.Value).ToList();
            if (profile.SendCR)
                bytes.Add((byte)'\r');
            if (profile.SendLF)
                bytes.Add((byte)'\n');

            await session.SendAsync(bytes.ToArray());
            Terminal2TextStatus = $"Sent STX/ETX frame plus profile CR/LF · {bytes.Count} byte(s) · {Convert.ToHexString(bytes.ToArray())}";
            if (profile.ClearCommand)
                SetTerminal2CommandTextWithoutStreaming("");
        }
        catch (Exception exception)
        {
            Terminal2TextStatus = $"STX/ETX send failed: {exception.Message}";
        }
    }

    private void RecallTerminal2CommandText(string text)
    {
        SetTerminal2CommandTextWithoutStreaming(text);
        Terminal2TextStatus = "Recalled the displayed RX line into the command editor. No bytes were sent.";
    }

    private void SetTerminal2CommandTextWithoutStreaming(string text)
    {
        _suppressTerminal2Typing = true;
        try { Terminal2SendText = text; }
        finally { _suppressTerminal2Typing = false; }
        _terminal2TypingObservedText = text;
        _terminal2TypingPendingText = "";
    }

    private void DrainTerminal2TypingText()
    {
        var invalidEscapeFound = false;
        while (_terminal2TypingPendingText.Length > 0)
        {
            var scan = ScanTerminal2TypingText(_terminal2TypingPendingText);
            if (scan.InvalidOffset is { } invalidOffset)
            {
                if (invalidOffset > 0)
                    ParseAndQueueTerminal2TypingText(_terminal2TypingPendingText[..invalidOffset]);

                _terminal2TypingPendingText = _terminal2TypingPendingText[(invalidOffset + scan.InvalidLength)..];
                invalidEscapeFound = true;
                continue;
            }

            if (scan.CompleteLength == 0)
            {
                Terminal2TextStatus = "Waiting for the rest of a $HH byte escape or $$ literal-dollar escape. Nothing incomplete has been sent.";
                return;
            }

            var complete = _terminal2TypingPendingText[..scan.CompleteLength];
            _terminal2TypingPendingText = _terminal2TypingPendingText[scan.CompleteLength..];
            ParseAndQueueTerminal2TypingText(complete);
        }

        if (invalidEscapeFound)
            Terminal2TextStatus = "Invalid $HH escape was not sent. Use two hexadecimal digits, or $$ for a literal dollar.";
    }

    private void ParseAndQueueTerminal2TypingText(string text)
    {
        if (text.Length == 0)
            return;

        var parsed = Terminal2SendTextParser.Parse(text);
        if (!parsed.IsValid || parsed.Value is null)
        {
            Terminal2TextStatus = FormatTerminal2Issues(parsed.Issues);
            return;
        }
        if (parsed.Value.Length > 0)
            QueueTerminal2TypingSend(parsed.Value);
    }

    private static TypingScan ScanTerminal2TypingText(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            if (current != '$')
            {
                if (char.IsHighSurrogate(current) && index + 1 == text.Length)
                    return new TypingScan(index, null, 0);
                index += char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                continue;
            }

            if (index + 1 >= text.Length)
                return new TypingScan(index, null, 0);
            if (text[index + 1] == '$')
            {
                index += 2;
                continue;
            }
            if (!IsTerminal2HexDigit(text[index + 1]))
                return new TypingScan(index, index, 2);
            if (index + 2 >= text.Length)
                return new TypingScan(index, null, 0);
            if (!IsTerminal2HexDigit(text[index + 2]))
                return new TypingScan(index, index, 2);
            index += 3;
        }

        return new TypingScan(index, null, 0);
    }

    private static bool IsTerminal2HexDigit(char value) => value is >= '0' and <= '9'
        or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private void QueueTerminal2TypingSend(byte[] bytes)
    {
        var payload = bytes.ToArray();
        lock (_terminal2TypingSendLock)
            _terminal2TypingSendTail = SendTerminal2TypingAfterAsync(_terminal2TypingSendTail, payload);
        Terminal2TextStatus = $"Queued {payload.Length} typed byte(s) through the traced session · sent bytes cannot be retracted.";
    }

    private async Task SendTerminal2TypingAfterAsync(Task previous, byte[] bytes)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* The previous send reports its own error; continue with new input. */ }

        try
        {
            var session = GetConnectedByteSession();
            await session.SendAsync(bytes).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => Terminal2TextStatus =
                $"Send-as-you-type sent {bytes.Length} byte(s) · {Convert.ToHexString(bytes)} · sent bytes cannot be retracted.");
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => Terminal2TextStatus =
                $"Send-as-you-type failed: {exception.Message} · text remains in the editor; verify the trace before retrying.");
        }
    }

    private async Task FlushTerminal2TypingSendsAsync()
    {
        Task pending;
        lock (_terminal2TypingSendLock)
            pending = _terminal2TypingSendTail;
        await pending;
    }

    private readonly record struct TypingScan(int CompleteLength, int? InvalidOffset, int InvalidLength);

    private async Task StopTerminal2MacroAsync(string reason)
    {
        var cancellation = _terminal2MacroCancellation;
        var execution = _terminal2MacroTask;
        if (cancellation is null)
            return;

        _terminal2MacroCancelReason = reason;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        if (execution is not null)
        {
            try { await execution; }
            catch (OperationCanceledException) { }
            catch (Exception exception) { AddTerminal2EffectMessage($"Macro cleanup: {exception.Message}"); }
        }
    }

    private bool CanTerminal2MacroContinue =>
        !_isStopping && Volatile.Read(ref _byteSession)?.Transport?.State == CoreTransportState.Connected &&
        Volatile.Read(ref _macroDisconnectRequested) == 0;

    private void AddTerminal2EffectMessage(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Terminal2EffectMessages.Add($"{DateTimeOffset.Now:HH:mm:ss} · {message.Trim()}");
            while (Terminal2EffectMessages.Count > 80)
                Terminal2EffectMessages.RemoveAt(0);
        });
    }

    private void NotifyTerminal2CommandStates()
    {
        ImportTerminal2ProfileCommand.NotifyCanExecuteChanged();
        ExportTerminal2ProfileCommand.NotifyCanExecuteChanged();
        RunTerminal2MacroCommand.NotifyCanExecuteChanged();
        CancelTerminal2MacroCommand.NotifyCanExecuteChanged();
        SendTerminal2TextCommand.NotifyCanExecuteChanged();
        SendTerminal2StxEtxCommand.NotifyCanExecuteChanged();
        ApplyTerminal2MacroEditsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasTerminal2Profile));
        OnPropertyChanged(nameof(CanImportTerminal2Profile));
        OnPropertyChanged(nameof(CanExportTerminal2Profile));
        OnPropertyChanged(nameof(CanRunTerminal2Macro));
        OnPropertyChanged(nameof(CanCancelTerminal2Macro));
        OnPropertyChanged(nameof(CanSendTerminal2Text));
        OnPropertyChanged(nameof(CanSendTerminal2StxEtx));
        OnPropertyChanged(nameof(Terminal2SendAsYouTypeStatus));
        OnPropertyChanged(nameof(CanEditTerminal2Macro));
        OnPropertyChanged(nameof(Terminal2SendSettings));
    }

    private static bool IsPopulatedTerminal2Macro(Terminal2Macro macro) =>
        !string.IsNullOrEmpty(macro.Title)
        || !string.IsNullOrEmpty(macro.Text)
        || macro.DelayBetweenCharacters != 0
        || macro.DelayBetweenLinesMs != 200
        || macro.RepeatEnabled
        || macro.RepeatEveryMs != 0
        || macro.StopAfterRepeats != 0
        || !macro.AddCR
        || macro.AddLF;

    private static string SummarizeMacroText(string text)
    {
        var oneLine = text.Replace("{0D}", "↵", StringComparison.Ordinal)
            .Replace("{0A}", "↵", StringComparison.Ordinal)
            .Replace("\r", "↵", StringComparison.Ordinal)
            .Replace("\n", "↵", StringComparison.Ordinal);
        return oneLine.Length > 64 ? oneLine[..61] + "…" : oneLine;
    }

    private static string FormatTerminal2Issues(IReadOnlyList<Terminal2ValidationIssue> issues) => issues.Count == 0
        ? "No parse diagnostics."
        : string.Join(Environment.NewLine, issues.Select(FormatTerminal2Issue));

    private static string FormatTerminal2Issue(Terminal2ValidationIssue issue)
    {
        var location = issue.LineNumber is { } line ? $"line {line}" : issue.CharacterOffset is { } offset ? $"offset {offset}" : null;
        var field = string.IsNullOrWhiteSpace(issue.Field) ? "" : $" · {issue.Field}";
        return $"{issue.Severity} · {issue.Code}{(location is null ? "" : $" · {location}")}{field}: {issue.Message}";
    }

    private sealed class DesktopTerminal2MacroExecutor(MainViewModel owner) : ITerminal2MacroExecutor
    {
        public bool CanContinue => owner.CanTerminal2MacroContinue;

        public async ValueTask SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = owner.GetConnectedByteSession();
            await session.SendAsync(bytes, cancellationToken);
        }

        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanContinue)
                throw new OperationCanceledException("The byte transport is no longer connected.", cancellationToken);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanContinue)
                throw new OperationCanceledException("The byte transport is no longer connected.", cancellationToken);
        }

        public async ValueTask ExecuteEffectAsync(Terminal2MacroEffect effect, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (effect.Kind)
            {
                case Terminal2MacroEffectKind.Note:
                    owner.AddTerminal2EffectMessage($"Note: {effect.Text ?? "(empty)"}");
                    return;
                case Terminal2MacroEffectKind.Freeze:
                    Dispatcher.UIThread.Post(owner.Terminal2ReceiveHistory.ToggleFreezeFromMacro);
                    owner.AddTerminal2EffectMessage("Freeze directive toggled the Terminal2 receive-line display; byte capture continues.");
                    return;
                case Terminal2MacroEffectKind.StartLog:
                    await owner.StartTerminal2TrafficLogAsync(cancellationToken);
                    return;
                case Terminal2MacroEffectKind.StopLog:
                    await owner.StopTerminal2TrafficLogAsync(cancellationToken);
                    return;
                case Terminal2MacroEffectKind.Disconnect:
                    Interlocked.Exchange(ref owner._macroDisconnectRequested, 1);
                    owner.AddTerminal2EffectMessage("Disconnect requested by macro; it will run after the macro engine exits safely.");
                    return;
            }

            if (owner.GetConnectedByteSession().Transport is not ISerialLineControl serial)
            {
                owner.AddTerminal2EffectMessage($"{effect.Kind} directive unavailable: the active transport does not expose serial line control.");
                return;
            }

            switch (effect.Kind)
            {
                case Terminal2MacroEffectKind.ToggleDtr:
                    serial.DtrEnable = !serial.DtrEnable;
                    owner.AddTerminal2EffectMessage($"DTR toggled {(serial.DtrEnable ? "ON" : "OFF")}.");
                    break;
                case Terminal2MacroEffectKind.ToggleRts:
                    serial.RtsEnable = !serial.RtsEnable;
                    owner.AddTerminal2EffectMessage($"RTS toggled {(serial.RtsEnable ? "ON" : "OFF")}.");
                    break;
                case Terminal2MacroEffectKind.Break:
                    var duration = Math.Max(1, effect.Argument ?? 1000);
                    serial.SetBreakState(true);
                    try
                    {
                        await Task.Delay(TimeSpan.FromTicks((long)duration * 10), cancellationToken);
                    }
                    finally
                    {
                        try { serial.SetBreakState(false); }
                        catch (Exception exception) { owner.AddTerminal2EffectMessage($"Could not clear BREAK after macro effect: {exception.Message}"); }
                    }
                    owner.AddTerminal2EffectMessage($"Sent serial BREAK for approximately {duration} μs.");
                    break;
                default:
                    owner.AddTerminal2EffectMessage($"Unsupported Terminal2 effect: {effect.Kind}.");
                    break;
            }
        }
    }

    private ByteSession GetConnectedByteSession()
    {
        var session = Volatile.Read(ref _byteSession);
        if (session?.Transport?.State != CoreTransportState.Connected || _isStopping)
            throw new InvalidOperationException("The active byte transport is not connected.");
        return session;
    }

    [RelayCommand(CanExecute = nameof(CanReplayTrace))]
    private async Task ReplayTraceAsync()
    {
        var document = _loadedTrace;
        if (document is null)
            return;

        if (!double.TryParse(ReplaySpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            || double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
        {
            OperationMessage = "Replay speed must be a number greater than zero.";
            return;
        }

        _isStarting = true;
        NotifyCommandStates();
        try
        {
            var transport = new TraceReplayTransport(document, speed);
            await StartByteSessionAsync(transport, "TRACE REPLAY");
            OperationMessage = $"Replaying {document.Events.Count} trace events at {speed.ToString(CultureInfo.InvariantCulture)}×; TX writes are checked against the trace.";
        }
        catch (Exception exception)
        {
            TransportState = CoreTransportState.Faulted;
            TransportDetail = exception.Message;
            OperationMessage = $"Replay failed: {exception.Message}";
        }
        finally
        {
            _isStarting = false;
            NotifyCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPauseReplay))]
    private void PauseReplay()
    {
        var replay = ActiveReplay;
        if (replay is null)
            return;
        replay.Pause();
        ReplayControlStatus = $"Replay paused · next RX sequence {replay.NextReceiveSequence?.ToString(CultureInfo.InvariantCulture) ?? "END"}. Seeking leaves TX verification unchanged.";
        NotifyReplayControlStates();
    }

    [RelayCommand(CanExecute = nameof(CanResumeReplay))]
    private void ResumeReplay()
    {
        var replay = ActiveReplay;
        if (replay is null)
            return;
        replay.Resume();
        ReplayControlStatus = $"Replay resumed · next RX sequence {replay.NextReceiveSequence?.ToString(CultureInfo.InvariantCulture) ?? "END"}.";
        NotifyReplayControlStates();
    }

    [RelayCommand(CanExecute = nameof(CanSeekReplay))]
    private void SeekReplay()
    {
        var replay = ActiveReplay;
        if (replay is null || !long.TryParse(ReplaySeekSequence, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 0)
        {
            ReplayControlStatus = "Enter a non-negative trace sequence, then pause replay before seeking.";
            return;
        }

        try
        {
            replay.SeekToSequence(sequence);
            ReplayControlStatus = $"RX cursor moved to the first receive event at or after sequence {sequence}; TX verification remains at its original cursor.";
        }
        catch (Exception exception)
        {
            ReplayControlStatus = $"Replay seek failed: {exception.Message}";
        }
        NotifyReplayControlStates();
    }

    private IByteTransport CreateTransport()
    {
        return TransportType switch
        {
            "TCP client" => new TcpClientByteTransport(Host.Trim(), ParsePort()),
            "TCP server" => new TcpServerByteTransport(ParseAddress(Host), ParsePort()),
            "Serial" => CreateSerialTransport(),
            "Local Modbus simulator" => throw new InvalidOperationException("The local simulator must be started asynchronously."),
            _ => throw new InvalidOperationException($"Unsupported transport type: {TransportType}"),
        };
    }

    private async Task<IByteTransport> CreateTransportAsync()
    {
        if (!IsSimulatorSelected)
            return CreateTransport();

        await StopSimulatorAsync();
        var simulator = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            UnitId = 1,
            FaultPlan = CreateFaultPlan(SimulatorFaultPlan),
        });
        simulator.Device.SetHoldingRegister(0, 0x1234);
        simulator.Device.SetHoldingRegister(1, 0xBEEF);
        simulator.Device.SetInputRegister(0, 42);
        simulator.EventOccurred += OnSimulatorEventOccurred;
        _simulator = simulator;
        try
        {
            await simulator.StartAsync();
            IsSimulatorActive = true;
            SimulatorStatus = $"SIMULATED · loopback {simulator.Host}:{simulator.Port} · HR[0]=0x1234 · HR[1]=0xBEEF · IR[0]=42";
            TransportDetail = "Loopback-only demo simulator; register fixture and traffic are simulated.";
            return new TcpClientByteTransport(IPAddress.Loopback.ToString(), simulator.Port);
        }
        catch
        {
            await StopSimulatorAsync();
            throw;
        }
    }

    private static VirtualDeviceFaultPlan CreateFaultPlan(string selection)
    {
        var rule = selection switch
        {
            "Drop response to request 1" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.DropResponse),
            "Corrupt response byte 8 on request 1" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.CorruptResponse, ByteOffset: 8),
            "Invalid MBAP ID on request 1" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.InvalidFrame),
            "Disconnect on request 1" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.Disconnect),
            "Delay first response 500 ms" => new VirtualDeviceFaultRule(1, VirtualDeviceFaultKind.DelayResponse, TimeSpan.FromMilliseconds(500)),
            _ => null,
        };
        return new VirtualDeviceFaultPlan(rule is null ? [] : [rule]);
    }

    private void OnSimulatorEventOccurred(object? sender, VirtualDeviceEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_simulator is null)
                return;
            SimulatorStatus = $"SIMULATED · {args.Kind}: {args.Message}";
        });
    }

    private async Task StopSimulatorAsync()
    {
        var simulator = _simulator;
        _simulator = null;
        IsSimulatorActive = false;
        if (simulator is null)
        {
            if (SimulatorStatus.StartsWith("SIMULATED", StringComparison.Ordinal))
                SimulatorStatus = "Stopped · loopback-only demo device";
            return;
        }
        simulator.EventOccurred -= OnSimulatorEventOccurred;
        try
        {
            await simulator.StopAsync();
        }
        catch (Exception exception)
        {
            SimulatorStatus = $"Simulator stop error: {exception.Message}";
        }
        finally
        {
            try { await simulator.DisposeAsync(); }
            catch (Exception exception) { SimulatorStatus = $"Simulator cleanup error: {exception.Message}"; }
            IsSimulatorActive = false;
            if (SimulatorStatus.StartsWith("SIMULATED", StringComparison.Ordinal) ||
                SimulatorStatus.StartsWith("Simulator stop error", StringComparison.Ordinal))
                SimulatorStatus = "Stopped · loopback-only demo device";
        }
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new ArgumentException("TCP port must be between 1 and 65535.");

    private int ParsePort() => ParsePort(Port);

    [RelayCommand]
    private void RefreshSerialPorts()
    {
        try
        {
            var ports = SerialPort.GetPortNames()
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AvailableSerialPorts.Clear();
            foreach (var port in ports)
                AvailableSerialPorts.Add(port);
            SerialPortEnumerationStatus = ports.Length == 0
                ? "No serial ports detected · enter a virtual or unknown port name manually."
                : $"{ports.Length} serial port(s) detected · manual port names are also accepted.";
        }
        catch (Exception exception)
        {
            AvailableSerialPorts.Clear();
            SerialPortEnumerationStatus = $"Serial port enumeration failed: {exception.Message} · enter a port name manually.";
        }
    }

    private string StageTerminal2ConnectionSettings(Terminal2Profile profile)
    {
        var issues = new List<string>();
        var warnings = new List<string>();
        var stagedTransport = TransportType;
        var stagedHost = Host;
        var stagedPort = Port;
        var stagedSerialPort = SerialPortName;
        var stagedBaudRate = BaudRate;
        var stagedDataBits = DataBits;
        var stagedParity = SerialParity;
        var stagedStopBits = SerialStopBits;
        var stagedHandshake = SerialHandshake;
        var stagedDtr = InitialDtr;
        var stagedRts = InitialRts;

        switch (profile.ConnectionType)
        {
            case Terminal2ConnectionType.Serial:
                stagedTransport = "Serial";
                stagedSerialPort = profile.SerialPort?.Trim() ?? "";
                if (stagedSerialPort.Length == 0)
                    issues.Add("serial port name is empty");

                if (!int.TryParse(profile.Baudrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baudRate) || baudRate <= 0)
                    issues.Add($"unsupported baud rate '{profile.Baudrate}'");
                else
                    stagedBaudRate = baudRate.ToString(CultureInfo.InvariantCulture);

                if (!int.TryParse(profile.DataBits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dataBits) || dataBits is < 5 or > 8)
                    issues.Add($"unsupported data bits '{profile.DataBits}' (SignalForge supports 5–8)");
                else
                    stagedDataBits = dataBits.ToString(CultureInfo.InvariantCulture);

                if (!Enum.TryParse<Parity>(profile.Parity, true, out var parity) || !Enum.IsDefined(parity))
                    issues.Add($"unsupported parity '{profile.Parity}'");
                else
                    stagedParity = parity.ToString();

                if (!TryMapTerminal2StopBits(profile.StopBits, out var stopBits))
                    issues.Add($"unsupported stop bits '{profile.StopBits}'");
                else
                    stagedStopBits = stopBits.ToString();

                if (!TryMapTerminal2Handshake(profile.Handshaking, out var handshake))
                    issues.Add($"unsupported handshaking '{profile.Handshaking}'");
                else
                {
                    stagedHandshake = handshake.ToString();
                    var canSetRtsManually = handshake is Handshake.None or Handshake.XOnXOff;
                    stagedRts = canSetRtsManually && profile.InitialRTS;
                    if (profile.InitialRTS && !canSetRtsManually)
                        warnings.Add("initial RTS is controlled by the selected hardware handshake and was not applied");
                }

                stagedDtr = profile.InitialDTR;
                break;

            case Terminal2ConnectionType.TCPClient:
                stagedTransport = "TCP client";
                stagedHost = profile.TcpConnectAddress?.Trim() ?? "";
                if (stagedHost.Length == 0)
                    issues.Add("TCP client address is empty");
                if (!TryParsePortValue(profile.TcpConnectPort, out var clientPort))
                    issues.Add($"unsupported TCP client port '{profile.TcpConnectPort}' (use 1–65535)");
                else
                    stagedPort = clientPort.ToString(CultureInfo.InvariantCulture);
                break;

            case Terminal2ConnectionType.TCPServer:
                stagedTransport = "TCP server";
                // Terminal2 listens on IPAddress.Any; mirror that bind address in this form.
                stagedHost = IPAddress.Any.ToString();
                if (!TryParsePortValue(profile.TcpListenPort, out var listenPort))
                    issues.Add($"unsupported TCP listen port '{profile.TcpListenPort}' (use 1–65535)");
                else
                    stagedPort = listenPort.ToString(CultureInfo.InvariantCulture);
                if (profile.RestartServer)
                    warnings.Add("Terminal2 RestartServer has no equivalent in SignalForge and was not applied");
                break;

            default:
                issues.Add($"unsupported connection type '{profile.ConnectionType}'");
                break;
        }

        if (issues.Count > 0)
            return $"Profile settings were not staged: {string.Join("; ", issues)}. The connection form is unchanged.";

        // These are form values read only by the next Connect command. Existing byte
        // transports hold their own constructor options and are never reconfigured here.
        TransportType = stagedTransport;
        Host = stagedHost;
        Port = stagedPort;
        SerialPortName = stagedSerialPort;
        BaudRate = stagedBaudRate;
        DataBits = stagedDataBits;
        SerialParity = stagedParity;
        SerialStopBits = stagedStopBits;
        SerialHandshake = stagedHandshake;
        InitialDtr = stagedDtr;
        InitialRts = stagedRts;

        var activeNote = _byteSession is null
            ? "for the next connection"
            : "for the next connection; the active transport is unchanged";
        var warningText = warnings.Count == 0 ? "" : $" Notes: {string.Join("; ", warnings)}.";
        return $"Profile connection settings staged {activeNote}.{warningText}";
    }

    private static bool TryParsePortValue(string value, out int port) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private static bool TryMapTerminal2StopBits(string value, out StopBits stopBits)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "1":
            case "ONE":
                stopBits = StopBits.One;
                return true;
            case "1.5":
            case "ONEPOINTFIVE":
                stopBits = StopBits.OnePointFive;
                return true;
            case "2":
            case "TWO":
                stopBits = StopBits.Two;
                return true;
            default:
                stopBits = StopBits.None;
                return false;
        }
    }

    private static bool TryMapTerminal2Handshake(string value, out Handshake handshake)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "NONE":
                handshake = Handshake.None;
                return true;
            case "RTS/CTS":
                handshake = Handshake.RequestToSend;
                return true;
            case "XON/XOFF":
            case "XON/OFF":
                handshake = Handshake.XOnXOff;
                return true;
            case "RTS/CTS + XON/XOFF":
            case "RTS/CTS + XON/OFF":
                handshake = Handshake.RequestToSendXOnXOff;
                return true;
            default:
                handshake = Handshake.None;
                return false;
        }
    }

    private IByteTransport CreateSerialTransport()
    {
        if (!int.TryParse(BaudRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) || baud <= 0)
            throw new ArgumentException("Serial baud rate must be a positive integer.");
        if (string.IsNullOrWhiteSpace(SerialPortName))
            throw new ArgumentException("Enter a serial port name, such as COM3 or /dev/ttyUSB0.");
        if (!int.TryParse(DataBits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dataBits) || dataBits is < 5 or > 8)
            throw new ArgumentException("Serial data bits must be between 5 and 8.");
        if (!Enum.TryParse<Parity>(SerialParity, true, out var parity) || !Enum.IsDefined(parity))
            throw new ArgumentException("Choose a supported serial parity.");
        if (!Enum.TryParse<StopBits>(SerialStopBits, true, out var stopBits) || !Enum.IsDefined(stopBits) || stopBits == StopBits.None)
            throw new ArgumentException("Choose One, OnePointFive, or Two stop bits.");
        if (!Enum.TryParse<Handshake>(SerialHandshake, true, out var handshake) || !Enum.IsDefined(handshake))
            throw new ArgumentException("Choose a supported serial handshake.");
        return new SerialPortByteTransport(new SerialTransportOptions(
            SerialPortName.Trim(), baud, dataBits, parity, stopBits, handshake, InitialDtr, InitialRts));
    }

    private static IPAddress ParseAddress(string value)
    {
        if (!IPAddress.TryParse(value.Trim(), out var address))
            throw new ArgumentException("TCP server address must be an IP address, such as 127.0.0.1 or 0.0.0.0.");
        return address;
    }

    private async Task StartByteSessionAsync(IByteTransport transport, string sessionName)
    {
        var outputPath = CreateTracePath();
        RecordingTracePath = outputPath;
        var session = new ByteSession();
        _byteSession = session;
        session.TraceEventCreated += OnTraceEventCreated;
        session.StateChanged += OnSessionTransportStateChanged;
        var sourceLabel = $"{(sessionName == "TRACE REPLAY" ? "REPLAY" : "LIVE")} #{Interlocked.Increment(ref _nextTraceSourceId)}";
        ReserveTimelineSource(sourceLabel);
        Volatile.Write(ref _traceSourceLabel, sourceLabel);
        TransportDetail = $"Starting {transport.DisplayName}";
        OnPropertyChanged(nameof(TraceStateLabel));
        OnPropertyChanged(nameof(TraceEventCount));
        try
        {
            await session.StartAsync(transport, outputPath, sessionName);
            TransportState = session.State;
            TransportDetail = $"{transport.DisplayName} · recording trace";
            OnPropertyChanged(nameof(TraceStateLabel));
            OnPropertyChanged(nameof(TraceEventCount));
        }
        catch
        {
            session.TraceEventCreated -= OnTraceEventCreated;
            session.StateChanged -= OnSessionTransportStateChanged;
            await session.DisposeAsync();
            _byteSession = null;
            await StopSimulatorAsync();
            NotifyCommandStates();
            throw;
        }
        NotifyCommandStates();
        NotifyReplayControlStates();
    }

    private async Task StopByteSessionAsync(string reason)
    {
        var session = _byteSession;
        if (session is null || _isStopping)
            return;

        _terminal2FilterMacroQueue.Clear();
        _isStopping = true;
        await StopTerminal2MacroAsync("Transport is disconnecting.");
        await Terminal2XModem.StopAsync("Transport is disconnecting.");
        await Terminal2FileTransfer.StopAsync("Transport is disconnecting.");
        await StopTerminal2TrafficLogAsync();
        TransportState = CoreTransportState.Closing;
        TransportDetail = "Closing transport and finalizing trace…";
        NotifyCommandStates();
        try
        {
            await session.StopAsync();
            OperationMessage = reason;
        }
        catch (Exception exception)
        {
            OperationMessage = $"Session stopped with a trace or disconnect error: {exception.Message}";
        }
        finally
        {
            await WaitForTraceProjectionDrainAsync();
            Terminal2ReceiveHistory.CompleteSource(Volatile.Read(ref _traceSourceLabel));
            session.TraceEventCreated -= OnTraceEventCreated;
            session.StateChanged -= OnSessionTransportStateChanged;
            try { await session.DisposeAsync(); }
            catch (Exception exception) { OperationMessage = $"Session cleanup error: {exception.Message}"; }
            _byteSession = null;
            await StopSimulatorAsync();
            _isStopping = false;
            TransportState = CoreTransportState.Disconnected;
            TransportDetail = "Disconnected. The session trace has been finalized.";
            OnPropertyChanged(nameof(TraceStateLabel));
            OnPropertyChanged(nameof(TraceEventCount));
            NotifyCommandStates();
            NotifyReplayControlStates();
        }
    }

    private void OnTraceEventCreated(object? sender, TraceByteEventArgs args)
    {
        var trafficLog = Volatile.Read(ref _terminal2TrafficLogWriter);
        if (trafficLog is not null && !trafficLog.TryAppend(args.Event)
            && Interlocked.Exchange(ref _terminal2TrafficLogFailureReported, 1) == 0)
        {
            var failure = trafficLog.Failure;
            Dispatcher.UIThread.Post(() =>
            {
                Terminal2TrafficLogStatus = $"LOG ERROR · {failure?.Message ?? "queue unavailable"}";
                OperationMessage = "Terminal2 traffic log failed; automatic session JSONL capture continues.";
            });
        }

        var projection = new PendingTraceProjection(args.Event, Volatile.Read(ref _traceSourceLabel));
        var pending = Interlocked.Increment(ref _pendingTraceCount);
        if (pending > MaxPendingTraceProjection)
        {
            Interlocked.Decrement(ref _pendingTraceCount);
            Interlocked.Increment(ref _pendingDisplayDrops);
        }
        else
        {
            _pendingTraceProjection.Enqueue(projection);
        }
        ScheduleProjectionDrain();
    }

    private async Task WaitForTraceProjectionDrainAsync()
    {
        while (Volatile.Read(ref _pendingTraceCount) > 0)
            await Task.Delay(1);
    }

    private void OnProtocolTestRunCompleted(object? sender, ProtocolTestRunCompletedEventArgs args)
    {
        void AddTestEvidenceToTimeline()
        {
            var result = args.Result;
            var runNumber = Interlocked.Increment(ref _nextProtocolTestSourceId);
            var source = $"TEST #{runNumber}";
            ReserveTimelineSource(source);
            var startedUtc = DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(result.StartedAtTicks, Stopwatch.GetTimestamp());
            var nextSequence = 0L;
            var evidenceSequences = new List<long>(result.Evidence.Count);

            DateTimeOffset EventUtc(long ticks) => ticks >= result.StartedAtTicks
                ? startedUtc + Stopwatch.GetElapsedTime(result.StartedAtTicks, ticks)
                : startedUtc;

            foreach (var evidence in result.Evidence)
            {
                var sequence = ++nextSequence;
                evidenceSequences.Add(sequence);
                var traceEvent = new TraceEvent(
                    sequence,
                    evidence.MonotonicTicks,
                    EventUtc(evidence.MonotonicTicks),
                    evidence.Direction == ByteDirection.Tx ? TraceEventKind.Transmit : TraceEventKind.Receive,
                    evidence.Bytes.ToArray(),
                    evidence.Direction,
                    args.Endpoint,
                    $"Protocol test step {evidence.StepNumber} · {evidence.StepName} · {evidence.Detail}",
                    $"protocol-test-{runNumber}-step-{evidence.StepNumber}");
                AddTimelineRow(traceEvent, source);
            }

            foreach (var step in result.Steps)
            {
                var sequence = ++nextSequence;
                var traceEvent = new TraceEvent(
                    sequence,
                    step.CompletedAtTicks,
                    EventUtc(step.CompletedAtTicks),
                    TraceEventKind.Assertion,
                    Detail: $"{(step.Passed ? "PASS" : "FAIL")} · {step.Name} · {step.Detail}",
                    CorrelationId: $"protocol-test-{runNumber}-step-{step.StepNumber}");
                AddTimelineRow(traceEvent, source);
            }

            var summarySequence = ++nextSequence;
            var runLabel = result.Cancelled ? "CANCELLED" : result.Passed ? "PASSED" : "FAILED";
            AddTimelineRow(new TraceEvent(
                summarySequence,
                result.CompletedAtTicks,
                EventUtc(result.CompletedAtTicks),
                TraceEventKind.Marker,
                Detail: $"Protocol test {runLabel} · {result.ScenarioName} · {result.PassedStepCount} passed / {result.FailedStepCount} failed · {args.SourceLabel}"), source);

            ProtocolTesting.SetTraceReferences(source, evidenceSequences);
            OperationMessage = $"Protocol test {runLabel.ToLowerInvariant()}; byte evidence and assertions are in trace timeline source {source}.";
            OnPropertyChanged(nameof(TraceStateLabel));
            OnPropertyChanged(nameof(TraceEventCount));
        }

        if (Dispatcher.UIThread.CheckAccess())
            AddTestEvidenceToTimeline();
        else
            Dispatcher.UIThread.Post(AddTestEvidenceToTimeline);
    }

    private void OnProtocolTraceRequested(object? sender, ProtocolTraceRequestedEventArgs args)
    {
        void SelectEvidence()
        {
            SelectedTraceRow = Timeline.FirstOrDefault(row =>
                StringComparer.Ordinal.Equals(row.Source, args.Source) && row.Event.Sequence == args.Sequence);
        }

        if (Dispatcher.UIThread.CheckAccess())
            SelectEvidence();
        else
            Dispatcher.UIThread.Post(SelectEvidence);
    }

    private async void OnTraceComparisonEventRequested(object? sender, TraceComparisonEventRequestedEventArgs args)
    {
        var requestVersion = Interlocked.Increment(ref _traceComparisonNavigationVersion);
        try
        {
            var path = Path.GetFullPath(args.TraceId);
            var document = await TraceReader.LoadAsync(path);
            if (requestVersion != Volatile.Read(ref _traceComparisonNavigationVersion))
                return;

            var eventIndex = -1;
            for (var index = 0; index < document.Events.Count; index++)
            {
                if (document.Events[index].Sequence == args.Sequence)
                {
                    eventIndex = index;
                    break;
                }
            }

            if (eventIndex < 0)
                throw new InvalidDataException($"Trace '{path}' does not contain event sequence {args.Sequence}.");

            _loadedTrace = document;
            TraceFilePath = path;
            ProtocolTesting.SetReplayTrace(document, path);

            var source = $"COMPARE {args.Side.ToUpperInvariant()} · {Path.GetFileName(path)} #{Interlocked.Increment(ref _nextTraceSourceId)}";
            ReserveTimelineSource(source);

            const int maximumWindow = 3000;
            var windowLength = Math.Min(document.Events.Count, maximumWindow);
            var windowStart = Math.Clamp(eventIndex - (windowLength / 2), 0, document.Events.Count - windowLength);
            foreach (var traceEvent in document.Events.Skip(windowStart).Take(windowLength))
            {
                AddTimelineRow(traceEvent, source);
            }
            if (windowStart == 0 && windowLength == document.Events.Count)
                Terminal2ReceiveHistory.CompleteSource(source);

            SelectedTraceRow = Timeline.FirstOrDefault(row =>
                StringComparer.Ordinal.Equals(row.Source, source) && row.Event.Sequence == args.Sequence);
            if (SelectedTraceRow is null)
                throw new InvalidOperationException($"Event {args.Sequence} was outside the timeline's retained display window.");

            OperationMessage = windowLength < document.Events.Count
                ? $"Opened {args.Side} event #{args.Sequence} from {Path.GetFileName(path)} in the main timeline; showing a {windowLength}-event window around it."
                : $"Opened {args.Side} event #{args.Sequence} from {Path.GetFileName(path)} in the main timeline.";
            OnPropertyChanged(nameof(HasLoadedTrace));
            OnPropertyChanged(nameof(TraceStateLabel));
            ReplayTraceCommand.NotifyCanExecuteChanged();
            ScheduleDecoderRebuild();
        }
        catch (Exception exception)
        {
            OperationMessage = $"Could not open the compared trace event: {exception.Message}";
        }
    }

    private void ScheduleProjectionDrain()
    {
        if (Interlocked.CompareExchange(ref _projectionDrainScheduled, 1, 0) != 0)
            return;
        Dispatcher.UIThread.Post(DrainProjectionQueue);
    }

    private void DrainProjectionQueue()
    {
        var drained = 0;
        while (drained < ProjectionDrainBatchSize && _pendingTraceProjection.TryDequeue(out var projection))
        {
            Interlocked.Decrement(ref _pendingTraceCount);
            AddTimelineRow(projection.Event, projection.Source);
            drained++;
        }
        var dropped = Interlocked.Exchange(ref _pendingDisplayDrops, 0);
        if (dropped > 0)
            DroppedDisplayCount += dropped;
        DisplayBacklogCount = Volatile.Read(ref _pendingTraceCount);
        OnPropertyChanged(nameof(TraceStateLabel));
        OnPropertyChanged(nameof(TraceEventCount));

        if (!_pendingTraceProjection.IsEmpty || Volatile.Read(ref _pendingDisplayDrops) > 0)
        {
            Dispatcher.UIThread.Post(DrainProjectionQueue);
            return;
        }
        Interlocked.Exchange(ref _projectionDrainScheduled, 0);
        if (!_pendingTraceProjection.IsEmpty || Volatile.Read(ref _pendingDisplayDrops) > 0)
            ScheduleProjectionDrain();
    }

    private void OnSessionTransportStateChanged(object? sender, TransportStateChangedEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TransportState = args.Current;
            TransportDetail = args.Detail;
            if (args.Exception is not null)
                OperationMessage = args.Exception.Message;
            if (args.Current is CoreTransportState.Disconnected or CoreTransportState.Faulted)
                _terminal2MacroCancellation?.Cancel();
            NotifyCommandStates();
        });
    }

    private void AddTimelineRow(TraceEvent traceEvent, string source)
    {
        Terminal2ReceiveHistory.Append(
            source,
            traceEvent,
            triggerProfileMacros: source.StartsWith("LIVE #", StringComparison.Ordinal));
        var row = new TraceEventRow(traceEvent, source);
        ApplyTerminal2Filter(row);
        var sourceOrder = ReserveTimelineSource(source);
        var insertionIndex = Timeline.Count;
        for (var index = 0; index < Timeline.Count; index++)
        {
            var existing = Timeline[index];
            var existingSourceOrder = ReserveTimelineSource(existing.Source);
            if (existingSourceOrder > sourceOrder ||
                (existingSourceOrder == sourceOrder && existing.Event.Sequence > traceEvent.Sequence))
            {
                insertionIndex = index;
                break;
            }
        }
        Timeline.Insert(insertionIndex, row);
        var matchedRows = Terminal2Filters.MatchedRxRows;
        var unmatchedRows = Terminal2Filters.UnmatchedRxRows;
        if (row.IsRxFilterCandidate)
        {
            if (row.MatchedFilterSlot is null)
                unmatchedRows++;
            else
                matchedRows++;
        }
        if (Timeline.Count > 3000)
        {
            var removed = Timeline[0];
            Timeline.RemoveAt(0);
            TimelineTrimmedRows++;
            if (removed.IsRxFilterCandidate)
            {
                if (removed.MatchedFilterSlot is null)
                    unmatchedRows--;
                else
                    matchedRows--;
            }
            if (ReferenceEquals(SelectedTraceRow, removed))
                SelectedTraceRow = Timeline.LastOrDefault();
        }
        Terminal2Filters.SetMatchCounts(matchedRows, unmatchedRows);
        if (Timeline.Contains(row) && (ReferenceEquals(Timeline.LastOrDefault(), row) || SelectedTraceRow is null))
            SelectedTraceRow = row;
        RefreshRecentTimeline();
        OnPropertyChanged(nameof(TraceEventCount));
        ScheduleDecoderRebuild();
    }

    private void RefreshRecentTimeline()
    {
        RecentTimeline.Clear();
        foreach (var row in Timeline.TakeLast(5).Reverse())
            RecentTimeline.Add(row);
        OnPropertyChanged(nameof(RecentTimelineStatus));
    }

    private void ApplyTerminal2Filter(TraceEventRow row)
    {
        if (!row.IsRxFilterCandidate)
        {
            row.SetTerminal2FilterMatch(null, null, null);
            return;
        }

        var matchIndex = Terminal2Filters.FindFirstMatch(row.FilterText);
        if (matchIndex < 0)
        {
            row.SetTerminal2FilterMatch(null, null, null);
            return;
        }

        var filter = Terminal2Filters.GetFilter(matchIndex);
        row.SetTerminal2FilterMatch(
            matchIndex,
            ColorBrushFromArgb(filter.ForegroundArgb),
            ColorBrushFromArgb(filter.BackgroundArgb));
    }

    private void ReapplyTerminal2Filters()
    {
        var matched = 0;
        var unmatched = 0;
        foreach (var row in Timeline)
        {
            ApplyTerminal2Filter(row);
            if (!row.IsRxFilterCandidate)
                continue;
            if (row.MatchedFilterSlot is null)
                unmatched++;
            else
                matched++;
        }
        Terminal2Filters.SetMatchCounts(matched, unmatched);
    }

    private static IBrush ColorBrushFromArgb(int argb)
    {
        var value = unchecked((uint)argb);
        return new SolidColorBrush(Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value));
    }

    private int ReserveTimelineSource(string source)
    {
        if (_timelineSourceOrder.TryGetValue(source, out var order))
            return order;
        order = ++_nextTimelineSourceOrder;
        _timelineSourceOrder[source] = order;
        return order;
    }

    private void ScheduleDecoderRebuild()
    {
        if (_decoderRebuildQueued)
            return;
        _decoderRebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _decoderRebuildQueued = false;
            var generation = Interlocked.Increment(ref _decoderGeneration);
            var rows = Timeline.ToArray();
            var mode = SelectedDecoderMode;
            var role = SelectedRtuRole;
            _ = Task.Run(() => BuildDecoderResults(rows, mode, role)).ContinueWith(
                (Task<Dictionary<TraceEventRow, DecoderResult>> task) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation != Volatile.Read(ref _decoderGeneration))
                            return;

                        if (task.IsFaulted || task.IsCanceled)
                        {
                            var detail = task.Exception?.GetBaseException().Message ?? "Decoder rebuild was canceled.";
                            foreach (var row in rows)
                                row.SetDecoderResult("Decode failed", detail);
                        }
                        else
                        {
                            foreach (var (row, result) in task.Result)
                                row.SetDecoderResult(result.Status, result.Output);
                        }

                        OnPropertyChanged(nameof(SelectedDecoderStatus));
                        OnPropertyChanged(nameof(SelectedDecoderOutput));
                    });
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        });
    }

    private static Dictionary<TraceEventRow, DecoderResult> BuildDecoderResults(
        IReadOnlyList<TraceEventRow> rows,
        string mode,
        string selectedRole)
    {
        var results = new Dictionary<TraceEventRow, DecoderResult>();
        if (mode == "Raw")
        {
            foreach (var row in rows)
                results[row] = new("Raw view · protocol decoding is off.", "Select a protocol mode to decode complete stream frames.");
            return results;
        }

        foreach (var sourceGroup in rows.GroupBy(row => row.Source, StringComparer.Ordinal))
        {
            var context = new StreamDecodeContext(selectedRole);
            var orderedRows = sourceGroup.OrderBy(row => row.Event.Sequence).ToArray();
            var streamValid = orderedRows.Length > 0 && orderedRows[0].Event.Sequence == 1;
            long? previousSequence = null;
            foreach (var row in orderedRows)
            {
                var traceEvent = row.Event;
                var output = new StringBuilder();
                var diagnostics = new List<string>();
                var status = "No complete frame or issue was reported for this event.";
                var decodedFrameCount = 0;
                var validationIssueCount = 0;

                var gapMessage = previousSequence is { } previous && traceEvent.Sequence != previous + 1
                    ? $"Trace sequence gap after #{previous}; decoder state reset to avoid reconstructing frames across missing display events."
                    : previousSequence is null && traceEvent.Sequence != 1
                        ? $"Trace begins at sequence #{traceEvent.Sequence}; decoder is suspended until a connected boundary."
                        : null;
                previousSequence = traceEvent.Sequence;
                if (gapMessage is not null)
                {
                    context.Reset();
                    streamValid = false;
                    diagnostics.Add(gapMessage);
                }

                if (traceEvent.Kind == TraceEventKind.Connection && IsStreamStart(traceEvent.Detail))
                {
                    context.Reset();
                    streamValid = true;
                    status = "Stream connection boundary · decoder state reset; following events are decoded independently by direction.";
                }
                else if (traceEvent.Kind == TraceEventKind.Connection && IsStreamBoundary(traceEvent.Detail))
                {
                    if (streamValid)
                    {
                        CompleteDirection(context, mode, 0, diagnostics);
                        CompleteDirection(context, mode, 1, diagnostics);
                    }
                    context.Reset();
                    streamValid = false;
                    status = diagnostics.Count > 0
                        ? "Stream boundary · Core decoder reported an incomplete frame."
                        : "Stream boundary · no buffered partial frame.";
                }
                else if (traceEvent.Kind == TraceEventKind.Error &&
                         traceEvent.Detail?.Contains("RX failed", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (streamValid)
                    {
                        CompleteDirection(context, mode, 0, diagnostics);
                        CompleteDirection(context, mode, 1, diagnostics);
                    }
                    context.Reset();
                    streamValid = false;
                    status = diagnostics.Count > 0
                        ? "Receive ended · Core decoder reported an incomplete frame."
                        : "Receive ended · no buffered partial frame.";
                }
                else if (!streamValid)
                {
                    status = "Decoder suspended after a trace gap or mid-stream start; waiting for a connected boundary.";
                }
                else if (traceEvent.Payload is { Length: > 0 } payload && traceEvent.Direction is { } direction)
                {
                    var directionIndex = direction == ByteDirection.Tx ? 0 : 1;
                    var directionName = direction == ByteDirection.Tx ? "TX" : "RX";
                    switch (mode)
                    {
                        case "Modbus TCP":
                        {
                            var batch = context.Tcp[directionIndex].Feed(payload);
                            decodedFrameCount = batch.Frames.Count;
                            validationIssueCount = batch.Issues.Count + batch.Frames.Sum(frame => frame.Issues.Count);
                            foreach (var frame in batch.Frames)
                                output.AppendLine(FormatModbusFrame(frame));
                            foreach (var issue in batch.Issues)
                                diagnostics.Add($"MBAP / framing · {issue.Kind}: {issue.Message}");
                            break;
                        }
                        case "Modbus RTU":
                        {
                            var batch = context.Rtu[directionIndex].Feed(payload);
                            decodedFrameCount = batch.Frames.Count;
                            validationIssueCount = batch.Issues.Count + batch.Frames.Sum(frame => frame.Issues.Count);
                            foreach (var frame in batch.Frames)
                                output.AppendLine(FormatModbusFrame(frame));
                            foreach (var issue in batch.Issues)
                                diagnostics.Add($"RTU framing · {issue.Kind}: {issue.Message}");
                            break;
                        }
                        case "Terminal2 STX-ETX":
                        {
                            var resultsForChunk = context.Terminal2[directionIndex].Feed(payload);
                            decodedFrameCount = resultsForChunk.Count(result => result.IsValid);
                            foreach (var result in resultsForChunk)
                            {
                                if (result.IsValid && result.Value is { } decodedPayload)
                                    output.AppendLine(FormatTerminal2Frame(decodedPayload));
                                else
                                    foreach (var issue in result.Issues)
                                        diagnostics.Add($"Terminal2 · {issue.Code}: {issue.Message}");
                            }
                            validationIssueCount = diagnostics.Count;
                            break;
                        }
                    }

                    if (decodedFrameCount > 0 || validationIssueCount > 0 || diagnostics.Count > 0)
                        status = $"Core decoder completed {decodedFrameCount} frame(s) and reported {Math.Max(validationIssueCount, diagnostics.Count)} issue(s) on {directionName}.";
                    else
                    {
                        var buffered = mode switch
                        {
                            "Modbus TCP" => context.Tcp[directionIndex].BufferedByteCount,
                            "Modbus RTU" => context.Rtu[directionIndex].BufferedByteCount,
                            "Terminal2 STX-ETX" => context.Terminal2[directionIndex].BufferedByteCount,
                            _ => 0,
                        };
                        status = buffered > 0
                            ? $"Buffering {buffered} byte(s) on {directionName}; waiting for a complete {mode} frame."
                            : "No complete frame or issue was reported for this event.";
                    }
                }

                var rendered = new StringBuilder(output.ToString());
                if (diagnostics.Count > 0)
                {
                    if (rendered.Length > 0)
                        rendered.AppendLine();
                    rendered.AppendLine("DIAGNOSTICS");
                    foreach (var diagnostic in diagnostics)
                        rendered.AppendLine($"• {diagnostic}");
                }
                if (rendered.Length == 0)
                {
                    var buffered = traceEvent.Direction is { } pendingDirection
                        ? mode switch
                        {
                            "Modbus TCP" => context.Tcp[pendingDirection == ByteDirection.Tx ? 0 : 1].BufferedByteCount,
                            "Modbus RTU" => context.Rtu[pendingDirection == ByteDirection.Tx ? 0 : 1].BufferedByteCount,
                            "Terminal2 STX-ETX" => context.Terminal2[pendingDirection == ByteDirection.Tx ? 0 : 1].BufferedByteCount,
                            _ => 0,
                        }
                        : 0;
                    rendered.Append(buffered > 0
                        ? $"Buffering {buffered} byte(s); the Core decoder has not reported a complete frame yet."
                        : "No complete frame or decoder issue is associated with this event.");
                }
                results[row] = new(status, rendered.ToString().TrimEnd());
            }
        }

        return results;
    }

    private static void CompleteDirection(
        StreamDecodeContext context,
        string mode,
        int directionIndex,
        ICollection<string> diagnostics)
    {
        if (mode == "Modbus TCP")
        {
            foreach (var issue in context.Tcp[directionIndex].Complete().Issues)
                diagnostics.Add($"MBAP / framing · {issue.Kind}: {issue.Message}");
            return;
        }
        if (mode == "Modbus RTU")
        {
            foreach (var issue in context.Rtu[directionIndex].Complete().Issues)
                diagnostics.Add($"RTU framing · {issue.Kind}: {issue.Message}");
            return;
        }
        if (mode == "Terminal2 STX-ETX")
        {
            var result = context.Terminal2[directionIndex].Complete();
            if (!result.IsValid)
                foreach (var issue in result.Issues)
                    diagnostics.Add($"Terminal2 · {issue.Code}: {issue.Message}");
        }
    }

    private static string FormatModbusFrame(ModbusFrame frame)
    {
        var output = new StringBuilder();
        var validity = frame.IsValid ? "VALID" : "ISSUES REPORTED";
        output.AppendLine($"{(frame.Format == ModbusTransportFormat.Tcp ? "Modbus TCP" : "Modbus RTU")} frame · {validity}");
        if (frame.TransactionId is { } transactionId)
            output.AppendLine($"Transaction ID: 0x{transactionId:X4}");
        if (frame.ProtocolId is { } protocolId)
            output.AppendLine($"MBAP protocol ID: 0x{protocolId:X4} {(protocolId == 0 ? "(valid)" : "(invalid; Modbus TCP requires 0x0000)")}");
        output.AppendLine($"Unit ID: 0x{frame.UnitId:X2} ({frame.UnitId})");
        output.AppendLine($"Function: {frame.FunctionName} · 0x{frame.FunctionCode:X2}");
        if (frame.ExceptionCode is { } exceptionCode)
            output.AppendLine($"Exception code: 0x{exceptionCode:X2}");
        output.AppendLine($"PDU data: {FormatByteSequence(frame.Data.Span)}");
        if (frame.ReceivedCrc is { } received && frame.CalculatedCrc is { } calculated)
            output.AppendLine($"CRC: received 0x{received:X4} · calculated 0x{calculated:X4} · {(received == calculated ? "MATCH" : "MISMATCH")}");
        output.AppendLine("Validation: derived from Core ModbusFrame.IsValid; this decoder does not emit a recognition-confidence score.");
        foreach (var issue in frame.Issues)
            output.AppendLine($"Issue · {issue.Kind}: {issue.Message}");
        return output.ToString();
    }

    private static string FormatTerminal2Frame(byte[] decodedPayload) =>
        $"Terminal2 STX-ETX frame · LRC verified by Core decoder{Environment.NewLine}" +
        $"Decoded payload (DLE unstuffed): {FormatByteSequence(decodedPayload)}{Environment.NewLine}" +
        "Validation: Core Terminal2ValidationResult.IsValid. Recognition confidence is not emitted by this decoder.";

    private static string FormatByteSequence(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? "(empty)" : string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static bool IsStreamBoundary(string? detail) => detail is not null &&
        (detail.Contains("→ Disconnected", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("→ Closing", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("→ Faulted", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("client disconnected", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("listener stopped", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("trace replay closed", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("serial port closed", StringComparison.OrdinalIgnoreCase));

    private static bool IsStreamStart(string? detail) => detail is not null &&
        (detail.Contains("→ Connected", StringComparison.OrdinalIgnoreCase) ||
         detail.Contains("→ Listening", StringComparison.OrdinalIgnoreCase));

    private sealed class StreamDecodeContext
    {
        public ModbusTcpDecoder[] Tcp { get; } = [new(), new()];
        public ModbusRtuDecoder[] Rtu { get; }
        public Terminal2StxEtxStreamDecoder[] Terminal2 { get; } = [new(), new()];

        public StreamDecodeContext(string selectedRole)
        {
            RtuRoleSelection = selectedRole;
            Rtu = [CreateRtu(selectedRole, ByteDirection.Tx), CreateRtu(selectedRole, ByteDirection.Rx)];
        }

        public void Reset()
        {
            for (var index = 0; index < 2; index++)
            {
                Tcp[index] = new ModbusTcpDecoder();
                Rtu[index] = CreateRtu(RtuRoleSelection, index == 0 ? ByteDirection.Tx : ByteDirection.Rx);
                Terminal2[index] = new Terminal2StxEtxStreamDecoder();
            }
        }

        private string RtuRoleSelection { get; }

        private static ModbusRtuDecoder CreateRtu(string selection, ByteDirection direction) =>
            new(selection switch
            {
                "Request" => ModbusRtuFrameRole.Request,
                "Response" => ModbusRtuFrameRole.Response,
                "Unknown" => ModbusRtuFrameRole.Unknown,
                _ => direction == ByteDirection.Tx ? ModbusRtuFrameRole.Request : ModbusRtuFrameRole.Response,
            });
    }

    private sealed record DecoderResult(string Status, string Output);

    private sealed record Terminal2FilterMacroTrigger(string MacroTitle, int FilterSlot);

    private void NotifyCommandStates()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        SendHexCommand.NotifyCanExecuteChanged();
        SendModbusReadExampleCommand.NotifyCanExecuteChanged();
        ReplayTraceCommand.NotifyCanExecuteChanged();
        ToggleTerminal2TrafficLogCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanToggleTerminal2TrafficLog));
        OnPropertyChanged(nameof(CanSendModbusExample));
        Terminal2FileTransfer.NotifySessionStateChanged();
        Terminal2XModem.NotifySessionStateChanged();
        NotifyTerminal2CommandStates();
    }

    private void OnTerminal2FileTransferActivityChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        SendHexCommand.NotifyCanExecuteChanged();
        SendModbusReadExampleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendModbusExample));
        Terminal2XModem.NotifySessionStateChanged();
        NotifyTerminal2CommandStates();
    }

    private void OnTerminal2XModemActivityChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        SendHexCommand.NotifyCanExecuteChanged();
        SendModbusReadExampleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendModbusExample));
        Terminal2FileTransfer.NotifySessionStateChanged();
        NotifyTerminal2CommandStates();
    }

    private void NotifyReplayControlStates()
    {
        OnPropertyChanged(nameof(IsReplayActive));
        OnPropertyChanged(nameof(IsReplayPaused));
        OnPropertyChanged(nameof(CanPauseReplay));
        OnPropertyChanged(nameof(CanResumeReplay));
        OnPropertyChanged(nameof(CanSeekReplay));
        OnPropertyChanged(nameof(ReplayCursorText));
        PauseReplayCommand.NotifyCanExecuteChanged();
        ResumeReplayCommand.NotifyCanExecuteChanged();
        SeekReplayCommand.NotifyCanExecuteChanged();
    }

    private static string CreateTracePath()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SignalForge", "Traces");
        return Path.Combine(folder, $"session-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.jsonl");
    }

    private sealed record PendingTraceProjection(TraceEvent Event, string Source);

    private static byte[] ParseHex(string input)
    {
        var compact = string.Concat(input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Replace(":", "", StringComparison.Ordinal)
                .Replace(",", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Replace("0x", "", StringComparison.OrdinalIgnoreCase)));
        if (compact.Length == 0)
            return [];
        if ((compact.Length & 1) != 0)
            throw new FormatException("Hex input must contain complete byte pairs, for example: 01 A0 FF.");
        try
        {
            return Convert.FromHexString(compact);
        }
        catch (FormatException)
        {
            throw new FormatException("Hex input contains an invalid character. Use byte pairs such as: 01 A0 FF.");
        }
    }
}

public sealed class TraceEventRow : INotifyPropertyChanged
{
    private const int MaximumDisplayedBytes = 4096;
    private int? _matchedFilterSlot;
    private IBrush? _filterForeground;
    private IBrush? _filterBackground;

    public TraceEvent Event { get; }
    public string Source { get; }
    public bool IsRxFilterCandidate => Event.Direction == ByteDirection.Rx;
    public int? MatchedFilterSlot => _matchedFilterSlot;
    public IBrush? FilterForeground => _filterForeground;
    public IBrush? FilterBackground => _filterBackground;
    public string FilterText => Event.Payload is { } payload ? Encoding.Latin1.GetString(payload) : "";
    public string DecoderStatus { get; private set; } = "Decoder state is rebuilding from ordered trace events.";
    public string DecoderOutput { get; private set; } = "";
    public string Timestamp => Event.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Direction => Event.Direction switch
    {
        ByteDirection.Tx => "TX",
        ByteDirection.Rx => "RX",
        _ => Event.Kind.ToString().ToUpperInvariant(),
    };
    public string Summary => Event.Payload is { } bytes
        ? $"{bytes.Length} bytes · {HexPreview(bytes)}"
        : Event.Detail ?? Event.Kind.ToString();
    public string Hex => Event.Payload is { } bytes ? FormatHex(bytes) : "No byte payload in this trace event.";
    public string HexDump => Event.Payload is { } bytes ? FormatHexDump(bytes) : "No byte payload in this trace event.";
    public string Ascii => Event.Payload is { } bytes
        ? new string(bytes.Select(value => value is >= 0x20 and <= 0x7E ? (char)value : '·').ToArray())
        : "";

    public TraceEventRow(TraceEvent traceEvent, string source)
    {
        Event = traceEvent;
        Source = source;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetTerminal2FilterMatch(int? slot, IBrush? foreground, IBrush? background)
    {
        if (_matchedFilterSlot != slot)
        {
            _matchedFilterSlot = slot;
            OnPropertyChanged(nameof(MatchedFilterSlot));
        }
        if (!Equals(_filterForeground, foreground))
        {
            _filterForeground = foreground;
            OnPropertyChanged(nameof(FilterForeground));
        }
        if (!Equals(_filterBackground, background))
        {
            _filterBackground = background;
            OnPropertyChanged(nameof(FilterBackground));
        }
    }

    public void SetDecoderResult(string status, string output)
    {
        DecoderStatus = status;
        DecoderOutput = output;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string HexPreview(byte[] bytes) => string.Join(" ", bytes.Take(12).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)))
        + (bytes.Length > 12 ? " …" : "");

    private static string FormatHex(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "(empty payload)";
        var builder = new StringBuilder(bytes.Length * 3);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0)
                builder.Append(index % 16 == 0 ? Environment.NewLine : ' ');
            builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string FormatHexDump(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "(empty payload)";

        var displayedLength = Math.Min(bytes.Length, MaximumDisplayedBytes);
        var builder = new StringBuilder(displayedLength * 4);
        for (var lineStart = 0; lineStart < displayedLength; lineStart += 16)
        {
            var lineLength = Math.Min(16, displayedLength - lineStart);
            builder.Append(lineStart.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var index = 0; index < 16; index++)
            {
                if (index < lineLength)
                    builder.Append(bytes[lineStart + index].ToString("X2", CultureInfo.InvariantCulture));
                else
                    builder.Append("  ");
                builder.Append(index == 7 ? "  " : " ");
            }
            builder.Append(" | ");
            for (var index = 0; index < lineLength; index++)
            {
                var value = bytes[lineStart + index];
                builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '·');
            }
            builder.AppendLine();
        }

        if (displayedLength < bytes.Length)
            builder.Append($"… {bytes.Length - displayedLength} more byte(s) omitted from this view; the trace retains the complete payload.");
        return builder.ToString();
    }
}
