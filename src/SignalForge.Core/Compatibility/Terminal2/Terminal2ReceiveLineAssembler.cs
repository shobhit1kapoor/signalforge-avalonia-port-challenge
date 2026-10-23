/*
 * Terminal2 receive-line compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

namespace SignalForge.Core.Compatibility.Terminal2;

public enum Terminal2ReceiveLineEnding
{
    None,
    CarriageReturn,
    LineFeed,
    CarriageReturnLineFeed
}

/// <summary>
/// One completed Terminal2-style receive line. <see cref="Bytes"/> contains the exact
/// original payload bytes, including its line ending when present.
/// </summary>
public sealed class Terminal2ReceiveLine
{
    internal Terminal2ReceiveLine(
        byte[] bytes,
        Terminal2ReceiveLineEnding ending,
        long firstMonotonicTicks,
        long lastMonotonicTicks,
        int lineNumber)
    {
        Bytes = bytes;
        Ending = ending;
        FirstMonotonicTicks = firstMonotonicTicks;
        LastMonotonicTicks = lastMonotonicTicks;
        LineNumber = lineNumber;
    }

    /// <summary>Exact bytes from the first byte through the line-ending bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The delimiter that ended this line; None indicates an end-of-stream flush.</summary>
    public Terminal2ReceiveLineEnding Ending { get; }

    /// <summary>Bytes without the CR/LF delimiter.</summary>
    public ReadOnlyMemory<byte> Content
    {
        get
        {
            var delimiterLength = Ending switch
            {
                Terminal2ReceiveLineEnding.CarriageReturnLineFeed => 2,
                Terminal2ReceiveLineEnding.CarriageReturn or Terminal2ReceiveLineEnding.LineFeed => 1,
                _ => 0
            };
            return Bytes[..(Bytes.Length - delimiterLength)];
        }
    }

    /// <summary>Monotonic timestamp of the first byte, including its delimiter if empty.</summary>
    public long FirstMonotonicTicks { get; }

    /// <summary>Monotonic timestamp of the final byte, including the delimiter when present.</summary>
    public long LastMonotonicTicks { get; }

    /// <summary>One-based line number since the last assembler reset.</summary>
    public int LineNumber { get; }
}

/// <summary>Completed lines and an optional visible assembly failure.</summary>
public sealed record Terminal2ReceiveLineAssemblyResult(
    IReadOnlyList<Terminal2ReceiveLine> Lines,
    Terminal2ValidationIssue? Issue = null)
{
    public bool IsSuccess => Issue is null;
}

/// <summary>A copied view of the assembler's current incomplete line state.</summary>
public sealed class Terminal2ReceiveLineSnapshot
{
    internal Terminal2ReceiveLineSnapshot(
        byte[] bytes,
        long? firstMonotonicTicks,
        long? lastMonotonicTicks,
        bool isCarriageReturnPending)
    {
        Bytes = bytes;
        FirstMonotonicTicks = firstMonotonicTicks;
        LastMonotonicTicks = lastMonotonicTicks;
        IsCarriageReturnPending = isCarriageReturnPending;
    }

    /// <summary>Exact buffered bytes, including a pending CR delimiter when present.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Timestamp of the first byte in the current incomplete line, if any.</summary>
    public long? FirstMonotonicTicks { get; }

    /// <summary>Timestamp of the most recent byte in the current incomplete line, if any.</summary>
    public long? LastMonotonicTicks { get; }

    /// <summary>True when the buffered line ends in a CR awaiting possible LF.</summary>
    public bool IsCarriageReturnPending { get; }
}

/// <summary>
/// Assembles arbitrary RX chunks into exact-byte lines. CR, LF and CRLF each end a line;
/// a CR is held until the next byte so a CRLF split across chunks is emitted as one ending.
/// A size failure is sticky until <see cref="Reset"/> and never emits a truncated line.
/// </summary>
public sealed class Terminal2ReceiveLineAssembler
{
    private readonly List<byte> _lineBytes = new();
    private bool _pendingCarriageReturn;
    private bool _isCompleted;
    private Terminal2ValidationIssue? _failure;
    private long? _firstMonotonicTicks;
    private long? _lastMonotonicTicks;
    private int _completedLineCount;

    public Terminal2ReceiveLineAssembler(int maximumLineBytes = 1024 * 1024)
    {
        if (maximumLineBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLineBytes));

        MaximumLineBytes = maximumLineBytes;
    }

    /// <summary>Maximum raw bytes per line, including the delimiter.</summary>
    public int MaximumLineBytes { get; }

    /// <summary>Number of bytes retained for the current, incomplete line.</summary>
    public int BufferedByteCount => _lineBytes.Count;

    public bool IsFaulted => _failure is not null;
    public bool IsCompleted => _isCompleted;

    /// <summary>
    /// Returns a point-in-time copy of the incomplete line. Mutating memory obtained
    /// from the snapshot cannot change the assembler's internal buffer.
    /// </summary>
    public Terminal2ReceiveLineSnapshot GetSnapshot() => new(
        _lineBytes.ToArray(),
        _firstMonotonicTicks,
        _lastMonotonicTicks,
        _pendingCarriageReturn);

    /// <summary>
    /// Adds one received chunk. All bytes in a chunk inherit its monotonic timestamp,
    /// matching the source transport's chunk-level timing evidence.
    /// </summary>
    public Terminal2ReceiveLineAssemblyResult Append(ReadOnlySpan<byte> bytes, long monotonicTicks)
    {
        if (_failure is not null)
            return new Terminal2ReceiveLineAssemblyResult(Array.Empty<Terminal2ReceiveLine>(), _failure);
        if (_isCompleted)
        {
            return new Terminal2ReceiveLineAssemblyResult(
                Array.Empty<Terminal2ReceiveLine>(),
                new Terminal2ValidationIssue(
                    "terminal2.receive-lines-already-completed",
                    "The receive-line assembler has completed; call Reset before appending another stream.",
                    Field: "ReceiveLine"));
        }

        var completed = new List<Terminal2ReceiveLine>();
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];

            if (_pendingCarriageReturn)
            {
                if (value == 0x0A)
                {
                    if (!TryAppend(value, monotonicTicks, index))
                        return new Terminal2ReceiveLineAssemblyResult(completed, _failure);

                    Emit(Terminal2ReceiveLineEnding.CarriageReturnLineFeed, completed);
                    _pendingCarriageReturn = false;
                    continue;
                }

                Emit(Terminal2ReceiveLineEnding.CarriageReturn, completed);
                _pendingCarriageReturn = false;
            }

            if (!TryAppend(value, monotonicTicks, index))
                return new Terminal2ReceiveLineAssemblyResult(completed, _failure);

            if (value == 0x0D)
            {
                _pendingCarriageReturn = true;
            }
            else if (value == 0x0A)
            {
                Emit(Terminal2ReceiveLineEnding.LineFeed, completed);
            }
        }

        return new Terminal2ReceiveLineAssemblyResult(completed);
    }

    /// <summary>
    /// Flushes a final CR-delimited line or any unterminated suffix at end-of-stream.
    /// Calling Complete more than once is safe and returns no additional lines.
    /// </summary>
    public Terminal2ReceiveLineAssemblyResult Complete()
    {
        if (_failure is not null)
            return new Terminal2ReceiveLineAssemblyResult(Array.Empty<Terminal2ReceiveLine>(), _failure);
        if (_isCompleted)
            return new Terminal2ReceiveLineAssemblyResult(Array.Empty<Terminal2ReceiveLine>());

        _isCompleted = true;
        if (_pendingCarriageReturn)
        {
            var line = new List<Terminal2ReceiveLine>(1);
            Emit(Terminal2ReceiveLineEnding.CarriageReturn, line);
            return new Terminal2ReceiveLineAssemblyResult(line);
        }

        if (_lineBytes.Count == 0)
            return new Terminal2ReceiveLineAssemblyResult(Array.Empty<Terminal2ReceiveLine>());

        var finalLine = new List<Terminal2ReceiveLine>(1);
        Emit(Terminal2ReceiveLineEnding.None, finalLine);
        return new Terminal2ReceiveLineAssemblyResult(finalLine);
    }

    /// <summary>Begins a fresh stream and discards any incomplete buffered line.</summary>
    public void Reset()
    {
        _lineBytes.Clear();
        _pendingCarriageReturn = false;
        _isCompleted = false;
        _failure = null;
        _firstMonotonicTicks = null;
        _lastMonotonicTicks = null;
        _completedLineCount = 0;
    }

    private bool TryAppend(
        byte value,
        long monotonicTicks,
        int chunkByteOffset)
    {
        if (_lineBytes.Count >= MaximumLineBytes)
        {
            _failure = new Terminal2ValidationIssue(
                "terminal2.receive-line-too-long",
                $"Receive line {_completedLineCount + 1} exceeded the {MaximumLineBytes}-byte limit " +
                $"at byte {chunkByteOffset} of the current chunk. The line was not emitted or truncated; " +
                "the assembler is faulted until it is reset.",
                LineNumber: _completedLineCount + 1,
                Field: "ReceiveLine");
            return false;
        }

        if (_lineBytes.Count == 0)
            _firstMonotonicTicks = monotonicTicks;

        _lineBytes.Add(value);
        _lastMonotonicTicks = monotonicTicks;
        return true;
    }

    private void Emit(Terminal2ReceiveLineEnding ending, List<Terminal2ReceiveLine> completed)
    {
        if (_lineBytes.Count == 0 || _firstMonotonicTicks is null || _lastMonotonicTicks is null)
            throw new InvalidOperationException("A receive line must contain at least one source byte.");

        completed.Add(new Terminal2ReceiveLine(
            _lineBytes.ToArray(),
            ending,
            _firstMonotonicTicks.Value,
            _lastMonotonicTicks.Value,
            ++_completedLineCount));
        _lineBytes.Clear();
        _firstMonotonicTicks = null;
        _lastMonotonicTicks = null;
    }
}
