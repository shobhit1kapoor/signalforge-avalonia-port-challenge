using System.Collections.Concurrent;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Sessions;

/// <summary>Owns transport, receive loop, and durable trace for one engineering session.</summary>
public sealed class ByteSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<Task> _stateWrites = new();
    private readonly object _eventGate = new();
    private IByteTransport? _transport;
    private TraceRecorder? _recorder;
    private Task? _receiveTask;
    private Exception? _traceFailure;
    private int _stopped;

    public event EventHandler<ByteChunkEventArgs>? BytesReceived;
    public event EventHandler<TraceByteEventArgs>? TraceEventCreated;
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    public IByteTransport? Transport => _transport;
    public TransportState State => _transport?.State ?? TransportState.Disconnected;
    public string? TracePath => _recorder?.Path;

    public async Task StartAsync(
        IByteTransport transport,
        string tracePath,
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (_transport is not null)
            throw new InvalidOperationException("This byte session has already been started.");

        TraceRecorder recorder;
        try
        {
            recorder = await TraceRecorder.StartAsync(
                tracePath,
                new TraceMetadata(sessionName, transport.Kind.ToString(), transport.Endpoint, DateTimeOffset.UtcNow, 0),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _transport = transport;
        _recorder = recorder;
        transport.StateChanged += OnTransportStateChanged;

        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _receiveTask = ReceiveLoopAsync(_lifetime.Token);
        }
        catch (Exception ex)
        {
            await RecordErrorAsync($"Connect failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        if (bytes.IsEmpty)
            return;

        var transport = _transport ?? throw new InvalidOperationException("Start the session before sending.");
        var recorder = _recorder ?? throw new InvalidOperationException("Trace recording is unavailable.");
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var traceEvent = await recorder.AppendBytesAsync(ByteDirection.Tx, bytes, transport.Endpoint,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RaiseTraceEvent(traceEvent);

            try
            {
                await transport.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                var completed = await recorder.AppendAsync(TraceEventKind.Marker, "TX write completed",
                    endpoint: transport.Endpoint, correlationId: traceEvent.Sequence.ToString(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                RaiseTraceEvent(completed);
            }
            catch (Exception ex)
            {
                await RecordErrorAsync($"TX write failed; the transport may have accepted a prefix: {ex.Message}",
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        // Wait for an in-flight send to finish before disconnecting so the recorded
        // request and transport result cannot race trace finalization.
        await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _lifetime.Cancel();
            var transport = _transport;
            if (transport is not null)
            {
                try
                {
                    await transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await RecordErrorAsync($"Disconnect failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    transport.StateChanged -= OnTransportStateChanged;
                }
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    await RecordErrorAsync($"Receive loop failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }

        await DrainStateWritesAsync().ConfigureAwait(false);
        if (_recorder is not null)
            await _recorder.StopAsync(CancellationToken.None).ConfigureAwait(false);

        if (_traceFailure is not null)
            throw new IOException("A transport state could not be recorded to the trace.", _traceFailure);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_transport is not null)
                await _transport.DisposeAsync().ConfigureAwait(false);
            if (_recorder is not null)
                await _recorder.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            _sendGate.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport is not initialized.");
        var recorder = _recorder ?? throw new InvalidOperationException("Trace recorder is not initialized.");
        try
        {
            await foreach (var chunk in transport.ReadAllAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var traceEvent = await recorder.AppendBytesAsync(ByteDirection.Rx, chunk.Bytes, transport.Endpoint,
                    chunk.MonotonicTicks, cancellationToken).ConfigureAwait(false);
                RaiseTraceEvent(traceEvent);
                RaiseBytesReceived(chunk);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the transport can surface as an I/O/object-disposed exception on
            // some stream implementations after session cancellation has been requested.
        }
        catch (Exception ex)
        {
            await RecordErrorAsync($"RX failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            RaiseStateChanged(new TransportStateChangedEventArgs(transport.State, TransportState.Faulted,
                $"Receive failed: {ex.Message}", ex));
            throw;
        }
    }

    private void OnTransportStateChanged(object? sender, TransportStateChangedEventArgs args)
    {
        RaiseStateChanged(args);
        var recorder = _recorder;
        var transport = _transport;
        if (recorder is null || transport is null)
            return;

        var task = RecordStateAsync(recorder, transport, args);
        _stateWrites.Enqueue(task);
    }

    private async Task RecordStateAsync(
        TraceRecorder recorder,
        IByteTransport transport,
        TransportStateChangedEventArgs args)
    {
        try
        {
            var traceEvent = await recorder.AppendAsync(TraceEventKind.Connection,
                $"{args.Previous} → {args.Current}: {args.Detail}",
                endpoint: transport.Endpoint,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            RaiseTraceEvent(traceEvent);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _traceFailure, ex, null);
        }
    }

    private async Task RecordErrorAsync(string detail, CancellationToken cancellationToken)
    {
        var recorder = _recorder;
        if (recorder is null)
            return;
        try
        {
            var traceEvent = await recorder.AppendAsync(TraceEventKind.Error, detail,
                endpoint: _transport?.Endpoint, cancellationToken: cancellationToken).ConfigureAwait(false);
            RaiseTraceEvent(traceEvent);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _traceFailure, ex, null);
        }
    }

    private async Task DrainStateWritesAsync()
    {
        while (_stateWrites.TryDequeue(out var task))
            await task.ConfigureAwait(false);
    }

    private void RaiseBytesReceived(ByteChunk chunk)
    {
        var handlers = BytesReceived;
        if (handlers is null)
            return;
        var args = new ByteChunkEventArgs(chunk);
        foreach (EventHandler<ByteChunkEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch { }
        }
    }

    private void RaiseTraceEvent(TraceEvent traceEvent)
    {
        var handlers = TraceEventCreated;
        if (handlers is null)
            return;
        var args = new TraceByteEventArgs(traceEvent);
        foreach (EventHandler<TraceByteEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch { }
        }
    }

    private void RaiseStateChanged(TransportStateChangedEventArgs args)
    {
        var handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (EventHandler<TransportStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch { }
        }
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
}

public sealed class ByteChunkEventArgs(ByteChunk chunk) : EventArgs
{
    public ByteChunk Chunk { get; } = chunk;
}
