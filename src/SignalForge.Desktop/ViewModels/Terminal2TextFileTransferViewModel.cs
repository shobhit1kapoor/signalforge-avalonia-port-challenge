using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Sessions;

namespace SignalForge.Desktop.ViewModels;

/// <summary>Desktop controls for a cancellable Terminal2 text-file byte transfer.</summary>
public partial class Terminal2TextFileTransferViewModel : ViewModelBase
{
    private readonly Func<ByteSession?> _sessionAccessor;
    private readonly Func<bool> _canSendAccessor;
    private readonly Action _activityChanged;
    private Terminal2TextFileTransfer? _activeTransfer;
    private Task? _runTask;
    private string _cancelReason = "Stopped by user.";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _interLineDelayMs = "0";

    [ObservableProperty]
    private bool _sendCr = true;

    [ObservableProperty]
    private bool _sendLf = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private int _completedLines;

    [ObservableProperty]
    private int _totalLines;

    [ObservableProperty]
    private string _status = "Choose a text file and connect a byte transport to send it line by line.";

    public bool CanStart => !IsRunning && _canSendAccessor() && !string.IsNullOrWhiteSpace(FilePath);
    public bool CanPause => IsRunning && !IsPaused;
    public bool CanResume => IsRunning && IsPaused;
    public bool CanCancel => IsRunning;
    public int ProgressMaximum => Math.Max(1, TotalLines);
    public string ProgressText => TotalLines == 0
        ? "NO LINES SENT"
        : $"LINE {CompletedLines.ToString(CultureInfo.InvariantCulture)} / {TotalLines.ToString(CultureInfo.InvariantCulture)}";

    public Terminal2TextFileTransferViewModel(
        Func<ByteSession?> sessionAccessor,
        Func<bool> canSendAccessor,
        Action activityChanged)
    {
        _sessionAccessor = sessionAccessor;
        _canSendAccessor = canSendAccessor;
        _activityChanged = activityChanged;
    }

    partial void OnFilePathChanged(string value) => NotifyCommandStates();

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        _activityChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    partial void OnCompletedLinesChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnTotalLinesChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressMaximum));
    }

    public void NotifySessionStateChanged() => NotifyCommandStates();

    /// <summary>Stops an active transfer before its byte session is disconnected or disposed.</summary>
    public async Task StopAsync(string reason)
    {
        var transfer = _activeTransfer;
        if (transfer is null)
            return;

        _cancelReason = reason;
        Status = $"Cancelling transfer · {reason}";
        transfer.Cancel();
        var running = _runTask;
        if (running is not null)
            await running;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning)
            return;

        var session = _sessionAccessor();
        if (session is null || !_canSendAccessor())
        {
            Status = "Connect a byte transport before starting a text-file transfer.";
            NotifyCommandStates();
            return;
        }

        if (!int.TryParse(InterLineDelayMs, NumberStyles.None, CultureInfo.InvariantCulture, out var delayMs)
            || delayMs is < 0 or > 10_000)
        {
            Status = "Inter-line delay must be a whole number from 0 to 10000 ms.";
            return;
        }

        var path = FilePath.Trim();
        var transfer = new Terminal2TextFileTransfer(
            (bytes, cancellationToken) => new ValueTask(session.SendAsync(bytes, cancellationToken)));
        _activeTransfer = transfer;
        _cancelReason = "Stopped by user.";
        IsPaused = false;
        CompletedLines = 0;
        TotalLines = 0;
        Status = $"Reading {Path.GetFileName(path)}…";
        IsRunning = true;

        try
        {
            var execution = transfer.RunFileAsync(
                path,
                new Terminal2TextFileTransferOptions(SendCr, SendLf, delayMs),
                progress => Dispatcher.UIThread.Post(() =>
                {
                    CompletedLines = progress.CompletedLines;
                    TotalLines = progress.TotalLines;
                    if (IsRunning && !IsPaused)
                        Status = $"Sending {ProgressText} · trace recording captures each transmitted line.";
                }));
            _runTask = execution;
            var result = await execution;
            IsPaused = false;

            Status = result.Status switch
            {
                Terminal2TextFileTransferStatus.Completed =>
                    $"COMPLETE · sent {result.LinesSent} of {result.TotalLines} nonempty line(s).",
                Terminal2TextFileTransferStatus.Cancelled =>
                    $"CANCELLED · sent {result.LinesSent} of {result.TotalLines} line(s) · {_cancelReason}",
                _ => $"FAILED · sent {result.LinesSent} of {result.TotalLines} line(s)."
            };

            if (result.Issues.Count > 0)
            {
                Status += Environment.NewLine + string.Join(Environment.NewLine,
                    result.Issues.Select(FormatIssue));
            }
        }
        catch (Exception exception)
        {
            Status = $"FAILED · sent {CompletedLines} of {TotalLines} line(s): {exception.Message}";
        }
        finally
        {
            _runTask = null;
            _activeTransfer = null;
            IsPaused = false;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (_activeTransfer is null)
            return;
        _activeTransfer.Pause();
        IsPaused = true;
        Status = $"PAUSED · completed {CompletedLines} of {TotalLines} line(s).";
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        if (_activeTransfer is null)
            return;
        _activeTransfer.Resume();
        IsPaused = false;
        Status = $"RESUMED · sending {ProgressText}.";
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cancelReason = "Stopped by user.";
        Status = "Cancelling at the current safe line boundary…";
        _activeTransfer?.Cancel();
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private static string FormatIssue(Terminal2ValidationIssue issue)
    {
        var line = issue.LineNumber is { } lineNumber ? $" · line {lineNumber}" : "";
        var offset = issue.CharacterOffset is { } characterOffset ? $" · offset {characterOffset}" : "";
        return $"{issue.Severity} · {issue.Code}{line}{offset}: {issue.Message}";
    }
}
