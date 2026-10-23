/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Diagnostics;
using SignalForge.Core.Protocols.Terminal2;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Options for Terminal2's line-oriented text-file transfer mode.</summary>
public sealed record Terminal2TextFileTransferOptions(
    bool SendCR = true,
    bool SendLF = true,
    int InterLineDelayMs = 0);

/// <summary>Progress after each line has been sent.</summary>
public sealed record Terminal2TextFileTransferProgress(
    int CompletedLines,
    int TotalLines,
    string? CurrentLine);

public enum Terminal2TextFileTransferStatus
{
    Completed,
    Cancelled,
    Failed
}

/// <summary>Terminal2 file-transfer outcome and any actionable diagnostics.</summary>
public sealed record Terminal2TextFileTransferResult(
    Terminal2TextFileTransferStatus Status,
    int TotalLines,
    int LinesSent,
    IReadOnlyList<Terminal2ValidationIssue> Issues)
{
    public bool IsSuccess => Status == Terminal2TextFileTransferStatus.Completed;
}

/// <summary>
/// Sends nonempty text-file lines through an injected async byte writer. Keep this
/// instance to pause, resume, or cancel the single transfer started on it.
/// </summary>
public sealed class Terminal2TextFileTransfer
{
    private const int MinimumInterLineDelayMs = 0;
    private const int MaximumInterLineDelayMs = 10_000;
    private static readonly char[] LineSeparators = ['\r', '\n'];

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendAsync;
    private readonly CancellationTokenSource _cancelSource = new();
    private readonly object _pauseLock = new();
    private TaskCompletionSource _resumeSignal = CompletedSignal();
    private TaskCompletionSource _pauseSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isPaused;
    private int _started;

    public Terminal2TextFileTransfer(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendAsync)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        _sendAsync = sendAsync;
    }

    /// <summary>Pauses before the next line or during an inter-line delay.</summary>
    public void Pause()
    {
        lock (_pauseLock)
        {
            if (_isPaused)
                return;

            _isPaused = true;
            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pauseSignal.TrySetResult();
        }
    }

    /// <summary>Resumes a paused transfer.</summary>
    public void Resume()
    {
        lock (_pauseLock)
        {
            if (!_isPaused)
                return;

            _isPaused = false;
            _resumeSignal.TrySetResult();
            _pauseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Cancels the transfer, including a read, pause, or delay in progress.</summary>
    public void Cancel() => _cancelSource.Cancel();

    /// <summary>
    /// Reads a file using File.ReadAllTextAsync decoding behavior, splits on CR or LF while
    /// removing empty entries, and sends each remaining line with the selected CR then LF
    /// suffix. The legacy loop waits after each line, including the final line; cancellation
    /// and pause remain responsive during waits.
    /// </summary>
    public async Task<Terminal2TextFileTransferResult> RunFileAsync(
        string filePath,
        Terminal2TextFileTransferOptions options,
        Action<Terminal2TextFileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Failure(
                "transfer.already-started",
                "A Terminal2 text-file transfer instance can run only once.");
        }

        if (options.InterLineDelayMs is < MinimumInterLineDelayMs or > MaximumInterLineDelayMs)
        {
            return Failure(
                "transfer.delay-out-of-range",
                $"Inter-line delay must be between {MinimumInterLineDelayMs} and {MaximumInterLineDelayMs} milliseconds.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cancelSource.Token);
        var token = linkedCancellation.Token;

        string contents;
        try
        {
            contents = await File.ReadAllTextAsync(filePath, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Cancelled(0, 0);
        }
        catch (Exception exception) when (IsFileReadException(exception))
        {
            return Failure(
                "transfer.read-failed",
                $"Could not read text file '{filePath}': {exception.Message}");
        }

        var lines = contents.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
        var progressIssue = ReportProgress(progress, new Terminal2TextFileTransferProgress(0, lines.Length, null));
        if (progressIssue is not null)
            return Failed(lines.Length, 0, progressIssue);

        var linesSent = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await WaitUntilResumedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var parsedLine = Terminal2SendTextParser.Parse(lines[lineIndex]);
                if (!parsedLine.IsValid)
                {
                    var parserIssue = parsedLine.Issues.First(issue => issue.Severity == Terminal2ValidationSeverity.Error);
                    var issue = new Terminal2ValidationIssue(
                        "transfer.invalid-send-text",
                        $"Line {lineIndex + 1}: {parserIssue.Message}",
                        Terminal2ValidationSeverity.Error,
                        LineNumber: lineIndex + 1,
                        CharacterOffset: parserIssue.CharacterOffset,
                        Field: "Line");
                    return Failed(lines.Length, linesSent, issue);
                }

                var bytes = AppendLineEnding(parsedLine.Value!, options.SendCR, options.SendLF);
                await _sendAsync(bytes, token).ConfigureAwait(false);
                linesSent++;
                progressIssue = ReportProgress(
                    progress,
                    new Terminal2TextFileTransferProgress(linesSent, lines.Length, lines[lineIndex]));
                if (progressIssue is not null)
                    return Failed(lines.Length, linesSent, progressIssue);

                token.ThrowIfCancellationRequested();
                await DelayWithPauseAsync(options.InterLineDelayMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Cancelled(lines.Length, linesSent);
            }
            catch (Exception exception)
            {
                return Failed(
                    lines.Length,
                    linesSent,
                    new Terminal2ValidationIssue(
                        "transfer.send-failed",
                        $"Could not send line {lineIndex + 1}: {exception.Message}",
                        LineNumber: lineIndex + 1,
                        Field: "Line"));
            }
        }

        return new Terminal2TextFileTransferResult(
            Terminal2TextFileTransferStatus.Completed,
            lines.Length,
            linesSent,
            Array.Empty<Terminal2ValidationIssue>());
    }

    private async Task DelayWithPauseAsync(int delayMs, CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromMilliseconds(delayMs);
        while (remaining > TimeSpan.Zero)
        {
            await WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Task pauseTask;
            lock (_pauseLock)
            {
                if (_isPaused)
                    continue;
                pauseTask = _pauseSignal.Task;
            }

            var delayTask = Task.Delay(remaining, cancellationToken);
            var startedAt = Stopwatch.GetTimestamp();
            var completedTask = await Task.WhenAny(delayTask, pauseTask).ConfigureAwait(false);
            remaining -= Stopwatch.GetElapsedTime(startedAt);

            if (completedTask == delayTask)
            {
                await delayTask.ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task WaitUntilResumedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task resumeTask;
            lock (_pauseLock)
            {
                if (!_isPaused)
                    return;

                resumeTask = _resumeSignal.Task;
            }

            await resumeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] AppendLineEnding(ReadOnlySpan<byte> line, bool sendCR, bool sendLF)
    {
        var suffixLength = (sendCR ? 1 : 0) + (sendLF ? 1 : 0);
        var output = new byte[line.Length + suffixLength];
        line.CopyTo(output);

        var index = line.Length;
        if (sendCR)
            output[index++] = 0x0D;
        if (sendLF)
            output[index] = 0x0A;

        return output;
    }

    private static bool IsFileReadException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException;

    private static Terminal2ValidationIssue? ReportProgress(
        Action<Terminal2TextFileTransferProgress>? progress,
        Terminal2TextFileTransferProgress update)
    {
        if (progress is null)
            return null;

        try
        {
            progress(update);
            return null;
        }
        catch (Exception exception)
        {
            return new Terminal2ValidationIssue(
                "transfer.progress-failed",
                $"Could not report text-file transfer progress: {exception.Message}",
                LineNumber: update.CompletedLines == 0 ? null : update.CompletedLines,
                Field: "Progress");
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private static Terminal2TextFileTransferResult Cancelled(int totalLines, int linesSent) =>
        new(
            Terminal2TextFileTransferStatus.Cancelled,
            totalLines,
            linesSent,
            Array.Empty<Terminal2ValidationIssue>());

    private static Terminal2TextFileTransferResult Failure(string code, string message) =>
        Failed(0, 0, new Terminal2ValidationIssue(code, message));

    private static Terminal2TextFileTransferResult Failed(
        int totalLines,
        int linesSent,
        Terminal2ValidationIssue issue) =>
        new(
            Terminal2TextFileTransferStatus.Failed,
            totalLines,
            linesSent,
            new[] { issue });
}
