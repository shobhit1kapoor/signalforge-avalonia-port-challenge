using System.Globalization;
using System.IO;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Sessions;
using SignalForge.Core.Transport;

namespace SignalForge.Desktop.ViewModels;

/// <summary>Serial-only desktop integration for Terminal2's classic XMODEM sender.</summary>
public partial class Terminal2XModemViewModel : ViewModelBase
{
    private const int ReceiveQueueCapacity = 64 * 1024;
    private readonly Func<ByteSession?> _sessionAccessor;
    private readonly Func<bool> _canSendAccessor;
    private readonly Action _activityChanged;
    private CancellationTokenSource? _cancellation;
    private Channel<byte>? _receiveQueue;
    private Task? _runTask;
    private string? _queueOverflow;
    private string _cancelReason = "Stopped by user.";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _maxNakRetries = "5";

    [ObservableProperty]
    private string _acknowledgementTimeoutMs = "2000";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private int _acknowledgedBytes;

    [ObservableProperty]
    private int _totalBytes;

    [ObservableProperty]
    private string _status = "Connect a serial transport and choose a file to send with XMODEM.";

    public bool CanStart => !IsRunning && _canSendAccessor() && IsSerialConnected && !string.IsNullOrWhiteSpace(FilePath);
    public bool CanCancel => IsRunning;
    public bool IsSerialConnected => _sessionAccessor()?.Transport is { Kind: TransportKind.Serial, State: TransportState.Connected };
    public int ProgressMaximum => Math.Max(1, TotalBytes);
    public string ProgressText => TotalBytes == 0
        ? "NO DATA ACKNOWLEDGED"
        : $"{AcknowledgedBytes.ToString(CultureInfo.InvariantCulture)} / {TotalBytes.ToString(CultureInfo.InvariantCulture)} BYTES ACKNOWLEDGED";

    public Terminal2XModemViewModel(Func<ByteSession?> sessionAccessor, Func<bool> canSendAccessor, Action activityChanged)
    {
        _sessionAccessor = sessionAccessor;
        _canSendAccessor = canSendAccessor;
        _activityChanged = activityChanged;
    }

    partial void OnFilePathChanged(string value) => NotifyCommandStates();
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        _activityChanged();
    }

    partial void OnAcknowledgedBytesChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnTotalBytesChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressMaximum));
    }

    public void NotifySessionStateChanged() => NotifyCommandStates();

    /// <summary>Cancels and joins an active transfer before its session is closed.</summary>
    public async Task StopAsync(string reason)
    {
        if (_cancellation is null)
            return;

        Volatile.Write(ref _cancelReason, reason);
        Status = $"Cancelling XMODEM · {reason}";
        TryCancel(_cancellation);
        if (_runTask is { } running)
            await running;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning)
            return;

        var session = _sessionAccessor();
        if (session?.Transport is not { Kind: TransportKind.Serial, State: TransportState.Connected } || !_canSendAccessor())
        {
            Status = "XMODEM requires a connected serial transport.";
            NotifyCommandStates();
            return;
        }

        if (!int.TryParse(MaxNakRetries, NumberStyles.None, CultureInfo.InvariantCulture, out var retries)
            || retries is < 0 or > Terminal2XModemOptions.MaximumNakRetries)
        {
            Status = $"Maximum NAK retries must be a whole number from 0 to {Terminal2XModemOptions.MaximumNakRetries}.";
            return;
        }

        if (!int.TryParse(AcknowledgementTimeoutMs, NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutMs)
            || timeoutMs is < 1 or > 60_000)
        {
            Status = "ACK timeout must be a whole number from 1 to 60000 ms.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _queueOverflow = null;
        Volatile.Write(ref _cancelReason, "Stopped by user.");
        AcknowledgedBytes = 0;
        Status = "Preparing XMODEM input; other sends are paused until this operation completes.";
        IsRunning = true;
        _runTask = RunAsync(session, FilePath.Trim(), retries, timeoutMs, cancellation);
        await _runTask;
    }

    private async Task RunAsync(ByteSession session, string path, int retries, int timeoutMs, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        Channel<byte>? queue = null;
        var subscribed = false;
        try
        {
            var payload = await File.ReadAllBytesAsync(path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (session.Transport is not { Kind: TransportKind.Serial, State: TransportState.Connected })
            {
                Status = "FAILED · the serial transport disconnected while the file was being read.";
                return;
            }

            var activeQueue = Channel.CreateBounded<byte>(new BoundedChannelOptions(ReceiveQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            queue = activeQueue;
            _receiveQueue = activeQueue;
            TotalBytes = payload.Length;
            Status = $"Sending {Path.GetFileName(path)} · waiting for receiver responses.";
            session.BytesReceived += OnBytesReceived;
            session.StateChanged += OnSessionStateChanged;
            subscribed = true;

            var execution = Terminal2XModemSender.SendAsync(
                payload,
                (bytes, token) => new ValueTask(session.SendAsync(bytes, token)),
                token => activeQueue.Reader.ReadAsync(token),
                new Terminal2XModemOptions(retries, TimeSpan.FromMilliseconds(timeoutMs)),
                progress => Dispatcher.UIThread.Post(() =>
                {
                    AcknowledgedBytes = progress.AcknowledgedBytes;
                    Status = $"Sending block {progress.BlockNumber} · {ProgressText}.";
                }),
                cancellation.Token);
            await CompleteAsync(execution);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = $"CANCELLED · {Volatile.Read(ref _cancelReason)}";
        }
        catch (Exception exception)
        {
            Status = $"FAILED · could not run XMODEM: {exception.Message}";
        }
        finally
        {
            if (subscribed)
            {
                session.BytesReceived -= OnBytesReceived;
                session.StateChanged -= OnSessionStateChanged;
            }
            queue?.Writer.TryComplete();
            if (ReferenceEquals(_cancellation, cancellation))
                _cancellation = null;
            cancellation.Dispose();
            _receiveQueue = null;
            _runTask = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        Volatile.Write(ref _cancelReason, "Stopped by user.");
        Status = "Cancelling XMODEM at the current byte boundary…";
        TryCancel(_cancellation);
    }

    private async Task CompleteAsync(Task<Terminal2XModemResult> execution)
    {
        var result = await execution;
        if (Volatile.Read(ref _queueOverflow) is { } overflow)
        {
            Status = $"FAILED · {overflow}";
            return;
        }

        AcknowledgedBytes = result.AcknowledgedBytes;
        Status = result.Status switch
        {
            Terminal2XModemStatus.Completed =>
                $"COMPLETE · {result.AcknowledgedBlocks} block(s), {result.AcknowledgedBytes} payload byte(s) acknowledged, EOT accepted.",
            Terminal2XModemStatus.Cancelled => $"CANCELLED · {result.AcknowledgedBlocks} block(s) acknowledged · {Volatile.Read(ref _cancelReason)}",
            _ => $"FAILED · {result.AcknowledgedBlocks} block(s) acknowledged."
        };
        if (result.Issues.Count > 0)
            Status += Environment.NewLine + string.Join(Environment.NewLine, result.Issues.Select(FormatIssue));
    }

    private void OnBytesReceived(object? sender, ByteChunkEventArgs args)
    {
        var queue = _receiveQueue;
        if (queue is null || Volatile.Read(ref _queueOverflow) is not null)
            return;

        foreach (var value in args.Chunk.Bytes.Span)
        {
            if (queue.Writer.TryWrite(value))
                continue;

            var overflow = $"The serial receive queue exceeded {ReceiveQueueCapacity} bytes. Transfer was cancelled to avoid dropping protocol responses.";
            if (Interlocked.CompareExchange(ref _queueOverflow, overflow, null) is null)
            {
                TryCancel(_cancellation);
                Dispatcher.UIThread.Post(() => Status = $"FAILED · {overflow}");
            }
            return;
        }
    }

    private void OnSessionStateChanged(object? sender, TransportStateChangedEventArgs args)
    {
        if (args.Current == TransportState.Connected)
            return;

        Volatile.Write(ref _cancelReason, $"Serial transport state changed to {args.Current}.");
        TryCancel(_cancellation);
        Dispatcher.UIThread.Post(NotifyCommandStates);
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(IsSerialConnected));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static string FormatIssue(Terminal2XModemIssue issue)
    {
        var block = issue.BlockNumber is { } number ? $" · block {number}" : "";
        var response = issue.Response is { } value ? $" · response 0x{value:X2}" : "";
        return $"{issue.Code}{block}{response}: {issue.Message}";
    }
}
