using System.Diagnostics;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Traces;

/// <summary>
/// Replays recorded RX events at a selectable pace and checks client writes against
/// the recorded TX byte stream in trace order. It compares bytes rather than write-call boundaries.
/// </summary>
public sealed class TraceReplayTransport : IByteTransport
{
    private readonly TraceDocument _document;
    private readonly TraceEvent[] _receiveEvents;
    private readonly TraceEvent[] _transmitEvents;
    private readonly double _playbackSpeed;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _playbackSync = new();
    private TaskCompletionSource<bool> _controlChanged = NewSignal();
    private bool _paused;
    private int _receiveEventIndex;
    private long _controlVersion;
    private bool _playbackClockInitialized;
    private long _firstReceiveTick;
    private long _previousOffsetTicks;
    private long _replayStartedTimestamp;
    private long? _pauseStartedTimestamp;
    private int _receiveStarted;
    private int _disposed;
    private int _connectCount;
    private int _state = (int)TransportState.Disconnected;
    private int _transmitEventIndex;
    private int _transmitEventOffset;
    private long _transmittedByteCount;

    public TraceReplayTransport(TraceDocument document, double playbackSpeed = 1.0)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (double.IsNaN(playbackSpeed) || double.IsInfinity(playbackSpeed) || playbackSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(playbackSpeed), "Playback speed must be a finite value greater than zero.");
        if (document.Metadata.StopwatchFrequency <= 0)
            throw new ArgumentException("Trace stopwatch frequency must be positive.", nameof(document));

        _document = document;
        _playbackSpeed = playbackSpeed;
        _receiveEvents = document.Events.Where(e => e.Kind == TraceEventKind.Receive && e.Payload is not null).ToArray();
        _transmitEvents = document.Events.Where(e => e.Kind == TraceEventKind.Transmit && e.Payload is not null).ToArray();
        ExpectedTransmitByteCount = _transmitEvents.Sum(e => (long)e.Payload!.Length);
    }

    public string DisplayName => $"Trace replay · {_document.Metadata.SessionName}";
    public string Endpoint => $"trace:{_document.Metadata.Endpoint}";
    public TransportKind Kind => TransportKind.Replay;
    public TransportState State => (TransportState)Volatile.Read(ref _state);
    public long ExpectedTransmitByteCount { get; }
    public long TransmittedByteCount => Interlocked.Read(ref _transmittedByteCount);
    public long RemainingTransmitByteCount => Math.Max(0, ExpectedTransmitByteCount - TransmittedByteCount);
    /// <summary>Number of recorded RX events available to this replay.</summary>
    public int ReceiveEventCount => _receiveEvents.Length;
    /// <summary>Index of the next RX event to be emitted.</summary>
    public int NextReceiveEventIndex
    {
        get { lock (_playbackSync) return _receiveEventIndex; }
    }
    /// <summary>Trace sequence number of the next RX event, or null when playback is at the end.</summary>
    public long? NextReceiveSequence
    {
        get
        {
            lock (_playbackSync)
                return _receiveEventIndex < _receiveEvents.Length ? _receiveEvents[_receiveEventIndex].Sequence : null;
        }
    }
    public bool IsPaused
    {
        get { lock (_playbackSync) return _paused; }
    }
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Pauses RX scheduling at an event boundary. An RX chunk already committed for
    /// delivery cannot be recalled; later chunks wait until <see cref="Resume"/>.
    /// </summary>
    public void Pause()
    {
        ThrowIfDisposed();
        lock (_playbackSync)
        {
            if (_paused)
                return;
            _paused = true;
            _pauseStartedTimestamp = Stopwatch.GetTimestamp();
            PulseControlLocked();
        }
    }

    /// <summary>Resumes RX scheduling and excludes paused wall-clock time from replay timing.</summary>
    public void Resume()
    {
        ThrowIfDisposed();
        lock (_playbackSync)
        {
            if (!_paused)
                return;
            var now = Stopwatch.GetTimestamp();
            if (_playbackClockInitialized && _pauseStartedTimestamp is { } pausedAt && now > pausedAt)
                _replayStartedTimestamp = SaturatingAdd(_replayStartedTimestamp, now - pausedAt);
            _pauseStartedTimestamp = null;
            _paused = false;
            PulseControlLocked();
        }
    }

    /// <summary>
    /// Moves the RX cursor to the first recorded Receive event whose trace sequence
    /// is at least <paramref name="sequence"/>. If RX playback is active, pause it
    /// first. Seeking resets RX timing to the selected event and never changes the
    /// ordered TX verification cursor or its byte counters.
    /// </summary>
    public void SeekToSequence(long sequence)
    {
        ThrowIfDisposed();
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), "Trace sequence must be non-negative.");

        lock (_playbackSync)
        {
            if (State == TransportState.Connected && Volatile.Read(ref _receiveStarted) != 0 && !_paused)
                throw new InvalidOperationException("Pause RX playback before seeking while its reader is active.");

            var index = Array.FindIndex(_receiveEvents, item => item.Sequence >= sequence);
            _receiveEventIndex = index < 0 ? _receiveEvents.Length : index;
            ResetPlaybackClockLocked();
            PulseControlLocked();
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == TransportState.Connected)
                return;
            if (State != TransportState.Disconnected)
                throw new InvalidOperationException($"Cannot connect while replay state is {State}.");
            if (Interlocked.Increment(ref _connectCount) != 1)
                throw new InvalidOperationException("A trace replay transport is single-use; create another instance to replay again.");

            SetState(TransportState.Connecting, $"Opening trace captured at {_document.Metadata.StartedAtUtc:O}");
            SetState(TransportState.Connected,
                $"Replaying {_receiveEvents.Length} RX chunks; checking {_transmitEvents.Length} TX chunks");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async IAsyncEnumerable<ByteChunk> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (_playbackSync)
        {
            if (State != TransportState.Connected)
                throw new InvalidOperationException("Connect the replay transport before reading.");
            if (Interlocked.Exchange(ref _receiveStarted, 1) != 0)
                throw new InvalidOperationException("Only one reader may consume a replay session.");
        }

        using var linkedLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linkedLifetime.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TraceEvent item;
                int eventIndex;
                long controlVersion;
                long replayStarted;
                long offsetTicks;
                Task controlSignal;
                bool paused;
                bool awaitingTransmit;
                bool atEnd;
                lock (_playbackSync)
                {
                    if (State != TransportState.Connected)
                        throw new OperationCanceledException("Replay transport disconnected.", token);

                    paused = _paused;
                    atEnd = !paused && _receiveEventIndex >= _receiveEvents.Length;
                    controlSignal = _controlChanged.Task;
                    awaitingTransmit = !paused && !atEnd
                        && NextTransmitSequenceLocked() is { } nextTransmitSequence
                        && nextTransmitSequence < _receiveEvents[_receiveEventIndex].Sequence;
                    if (paused || awaitingTransmit)
                    {
                        item = null!;
                        eventIndex = _receiveEventIndex;
                        controlVersion = _controlVersion;
                        replayStarted = 0;
                        offsetTicks = 0;
                    }
                    else
                    {
                        eventIndex = _receiveEventIndex;
                        item = atEnd ? null! : _receiveEvents[eventIndex];
                        if (atEnd)
                        {
                            controlVersion = _controlVersion;
                            replayStarted = 0;
                            offsetTicks = 0;
                        }
                        else
                        {
                            if (!_playbackClockInitialized)
                            {
                                _firstReceiveTick = item.MonotonicTicks;
                                _previousOffsetTicks = 0;
                                _replayStartedTimestamp = Stopwatch.GetTimestamp();
                                _playbackClockInitialized = true;
                            }

                            offsetTicks = item.MonotonicTicks >= _firstReceiveTick
                                ? Math.Max(_previousOffsetTicks, item.MonotonicTicks - _firstReceiveTick)
                                : _previousOffsetTicks;
                            replayStarted = _replayStartedTimestamp;
                            controlVersion = _controlVersion;
                        }
                    }
                }

                if (atEnd)
                {
                    // Writes may be in flight on a separate application task while
                    // the reader reaches the end of RX playback. Serialize the final
                    // completeness check with those writes so a completed request is
                    // not reported as missing mid-write.
                    await _writeGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (RemainingTransmitByteCount > 0)
                            throw CreateIncompleteReplayException();
                    }
                    finally
                    {
                        _writeGate.Release();
                    }

                    yield break;
                }

                if (paused)
                {
                    await controlSignal.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                if (awaitingTransmit)
                {
                    await controlSignal.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                if (await DelayToRecordedOffsetOrControlChangeAsync(replayStarted, offsetTicks, controlSignal, token)
                    .ConfigureAwait(false))
                    continue;

                byte[] payload;
                lock (_playbackSync)
                {
                    token.ThrowIfCancellationRequested();
                    if (State != TransportState.Connected)
                        throw new OperationCanceledException("Replay transport disconnected.", token);
                    if (_paused || controlVersion != _controlVersion || eventIndex != _receiveEventIndex)
                        continue;
                    _receiveEventIndex++;
                    _previousOffsetTicks = offsetTicks;
                    payload = item.Payload!.ToArray();
                }
                yield return new ByteChunk(Stopwatch.GetTimestamp(), payload);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _receiveStarted, 0);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State != TransportState.Connected)
            throw new InvalidOperationException("The replay transport is not connected.");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State != TransportState.Connected)
                throw new InvalidOperationException("The replay transport is not connected.");
            cancellationToken.ThrowIfCancellationRequested();
            var actualBytes = bytes.ToArray();
            lock (_playbackSync)
            {
                var expectedEventIndex = _transmitEventIndex;
                var expectedEventOffset = _transmitEventOffset;
                var nextReceiveSequence = _receiveEventIndex < _receiveEvents.Length
                    ? _receiveEvents[_receiveEventIndex].Sequence
                    : (long?)null;
                for (var i = 0; i < actualBytes.Length; i++)
                {
                    var nextTransmitEvent = PeekExpectedTransmitEvent(expectedEventIndex, expectedEventOffset);
                    if (nextTransmitEvent is null)
                    {
                        throw new TraceReplayMismatchException(
                            TransmittedByteCount + i,
                            expected: null,
                            actualBytes[i],
                            "The application sent bytes beyond the recorded TX stream.");
                    }

                    if (nextReceiveSequence is { } receiveSequence && nextTransmitEvent.Sequence >= receiveSequence)
                    {
                        throw new TraceReplayMismatchException(
                            TransmittedByteCount + i,
                            expected: null,
                            actualBytes[i],
                            $"The application sent TX bytes from trace event {nextTransmitEvent.Sequence} before the recorded RX event {receiveSequence}.");
                    }

                    if (!TryReadExpectedByte(ref expectedEventIndex, ref expectedEventOffset,
                        out var expected, out var traceSequence))
                    {
                        throw new TraceReplayMismatchException(
                            TransmittedByteCount + i,
                            expected: null,
                            actualBytes[i],
                            "The application sent bytes beyond the recorded TX stream.");
                    }

                    if (expected != actualBytes[i])
                    {
                        throw new TraceReplayMismatchException(
                            TransmittedByteCount + i,
                            expected,
                            actualBytes[i],
                            $"TX diverged at byte {TransmittedByteCount + i} while replaying trace event {traceSequence}.");
                    }
                }

                _transmitEventIndex = expectedEventIndex;
                _transmitEventOffset = expectedEventOffset;
                Interlocked.Add(ref _transmittedByteCount, actualBytes.Length);
                if (actualBytes.Length > 0)
                {
                    PulseControlLocked();
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TransportState.Disconnected)
                return;

            SetState(TransportState.Closing,
                $"Replay stopped with {RemainingTransmitByteCount} recorded TX bytes unconsumed");
            _lifetime.Cancel();
            lock (_playbackSync)
            {
                _paused = false;
                PulseControlLocked();
            }
            SetState(TransportState.Disconnected, "Trace replay closed");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync().ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        _writeGate.Release();
        _lifetime.Dispose();
        _writeGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task<bool> DelayToRecordedOffsetOrControlChangeAsync(
        long replayStarted,
        long offsetTicks,
        Task controlSignal,
        CancellationToken cancellationToken)
    {
        if (_playbackSpeed >= 10_000)
            return controlSignal.IsCompleted;

        var elapsedSeconds = (double)offsetTicks / _document.Metadata.StopwatchFrequency / _playbackSpeed;
        var elapsedTargetTicks = elapsedSeconds * Stopwatch.Frequency;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (controlSignal.IsCompleted)
                return true;

            var availableTicks = long.MaxValue - replayStarted;
            var clampedElapsedTicks = elapsedTargetTicks >= availableTicks
                ? availableTicks
                : (long)Math.Max(0, elapsedTargetTicks);
            var targetTicks = SaturatingAdd(replayStarted, clampedElapsedTicks);
            var remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                return false;

            var delayMilliseconds = Math.Max(1, Math.Min(
                remainingTicks * 1000.0 / Stopwatch.Frequency,
                int.MaxValue - 1));
            var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(delay, delayCancellation.Token);
            var winner = await Task.WhenAny(delayTask, controlSignal).ConfigureAwait(false);
            if (winner == controlSignal)
            {
                delayCancellation.Cancel();
                try { await delayTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested) { }
                return true;
            }

            await delayTask.ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PulseControlLocked()
    {
        var previous = _controlChanged;
        _controlChanged = NewSignal();
        _controlVersion++;
        previous.TrySetResult(true);
    }

    private void ResetPlaybackClockLocked()
    {
        _playbackClockInitialized = false;
        _firstReceiveTick = 0;
        _previousOffsetTicks = 0;
        _replayStartedTimestamp = 0;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private bool TryReadExpectedByte(ref int eventIndex, ref int eventOffset, out byte value, out long sequence)
    {
        while (eventIndex < _transmitEvents.Length)
        {
            var item = _transmitEvents[eventIndex];
            var payload = item.Payload!;
            if (eventOffset < payload.Length)
            {
                value = payload[eventOffset];
                sequence = item.Sequence;
                eventOffset++;
                if (eventOffset == payload.Length)
                {
                    eventIndex++;
                    eventOffset = 0;
                }
                return true;
            }

            eventIndex++;
            eventOffset = 0;
        }

        value = default;
        sequence = 0;
        return false;
    }

    private long? NextTransmitSequenceLocked()
    {
        var next = PeekExpectedTransmitEvent(_transmitEventIndex, _transmitEventOffset);
        return next?.Sequence;
    }

    private TraceEvent? PeekExpectedTransmitEvent(int eventIndex, int eventOffset)
    {
        while (eventIndex < _transmitEvents.Length)
        {
            var item = _transmitEvents[eventIndex];
            if (eventOffset < item.Payload!.Length)
                return item;
            eventIndex++;
            eventOffset = 0;
        }

        return null;
    }

    private TraceReplayIncompleteException CreateIncompleteReplayException()
    {
        var nextEventIndex = _transmitEventIndex;
        while (nextEventIndex < _transmitEvents.Length)
        {
            var payload = _transmitEvents[nextEventIndex].Payload!;
            if (_transmitEventOffset < payload.Length)
                break;
            nextEventIndex++;
        }

        var nextSequence = nextEventIndex < _transmitEvents.Length
            ? _transmitEvents[nextEventIndex].Sequence
            : (long?)null;
        return new TraceReplayIncompleteException(
            ExpectedTransmitByteCount,
            TransmittedByteCount,
            nextSequence);
    }

    private void SetState(TransportState next, string detail, Exception? exception = null)
    {
        var previous = (TransportState)Interlocked.Exchange(ref _state, (int)next);
        TransportStateNotifier.Raise(this, StateChanged, previous, next, detail, exception);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed class TraceReplayMismatchException(
    long byteOffset,
    byte? expected,
    byte actual,
    string message) : IOException(message)
{
    public long ByteOffset { get; } = byteOffset;
    public byte? Expected { get; } = expected;
    public byte Actual { get; } = actual;
}

/// <summary>Raised when RX playback reaches the trace end before all expected TX bytes were written.</summary>
public sealed class TraceReplayIncompleteException(
    long expectedTransmitByteCount,
    long transmittedByteCount,
    long? nextExpectedTransmitSequence)
    : IOException($"Trace replay ended with {Math.Max(0, expectedTransmitByteCount - transmittedByteCount)} of " +
                  $"{expectedTransmitByteCount} recorded TX bytes unconsumed.")
{
    public long ExpectedTransmitByteCount { get; } = expectedTransmitByteCount;
    public long TransmittedByteCount { get; } = transmittedByteCount;
    public long RemainingTransmitByteCount { get; } = Math.Max(0, expectedTransmitByteCount - transmittedByteCount);
    public long? NextExpectedTransmitSequence { get; } = nextExpectedTransmitSequence;
}
