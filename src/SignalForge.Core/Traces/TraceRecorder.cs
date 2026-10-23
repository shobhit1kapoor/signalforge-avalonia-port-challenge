using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Traces;

/// <summary>Durable, bounded, ordered JSONL recorder for byte sessions.</summary>
public sealed class TraceRecorder : IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private readonly Channel<PendingEvent> _pending = Channel.CreateBounded<PendingEvent>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly string _finalPath;
    private readonly string _partialPath;
    private readonly Task _writerTask;
    private long _sequence;
    private int _stopped;

    private TraceRecorder(string path, TraceMetadata metadata)
    {
        _finalPath = System.IO.Path.GetFullPath(path);
        _partialPath = _finalPath + ".partial";
        Metadata = metadata with { StopwatchFrequency = Stopwatch.Frequency };
        _writerTask = WriterLoopAsync();
    }

    public TraceMetadata Metadata { get; }
    public string Path => _finalPath;

    public static async Task<TraceRecorder> StartAsync(
        string path,
        TraceMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var recorder = new TraceRecorder(fullPath, metadata);
        try
        {
            await recorder.AppendLineAsync(new TraceLine(SchemaVersion, "header", recorder.Metadata, null), cancellationToken)
                .ConfigureAwait(false);
            return recorder;
        }
        catch
        {
            await recorder.AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TraceEvent> AppendAsync(
        TraceEventKind kind,
        string? detail = null,
        byte[]? payload = null,
        ByteDirection? direction = null,
        string? endpoint = null,
        long? monotonicTicks = null,
        DateTimeOffset? timestampUtc = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stopped) != 0)
            throw new InvalidOperationException("The trace recorder has stopped.");

        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopped) != 0)
                throw new InvalidOperationException("The trace recorder has stopped.");

            var traceEvent = new TraceEvent(
                Interlocked.Increment(ref _sequence),
                monotonicTicks ?? Stopwatch.GetTimestamp(),
                timestampUtc ?? DateTimeOffset.UtcNow,
                kind,
                payload?.ToArray(),
                direction,
                endpoint,
                detail,
                correlationId);
            // Once a sequence number is assigned, finish enqueueing it even if the caller
            // cancels; otherwise a later event could leave a sequence gap in the trace.
            await AppendLineAsync(new TraceLine(SchemaVersion, "event", null, traceEvent), CancellationToken.None)
                .ConfigureAwait(false);
            return traceEvent;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public Task<TraceEvent> AppendBytesAsync(
        ByteDirection direction,
        ReadOnlyMemory<byte> bytes,
        string? endpoint = null,
        long? monotonicTicks = null,
        CancellationToken cancellationToken = default) =>
        AppendAsync(
            direction == ByteDirection.Tx ? TraceEventKind.Transmit : TraceEventKind.Receive,
            payload: bytes.ToArray(),
            direction: direction,
            endpoint: endpoint,
            monotonicTicks: monotonicTicks,
            cancellationToken: cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            _pending.Writer.TryComplete();
            await _writerTask.ConfigureAwait(false);
            File.Move(_partialPath, _finalPath, overwrite: true);
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _stopped) == 0)
            await StopAsync().ConfigureAwait(false);
        _appendGate.Dispose();
    }

    private async Task AppendLineAsync(TraceLine line, CancellationToken cancellationToken)
    {
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pending.Writer.WriteAsync(new PendingEvent(line, acknowledgement), cancellationToken).ConfigureAwait(false);
        await acknowledgement.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriterLoopAsync()
    {
        PendingEvent? current = null;
        try
        {
            await using var stream = new FileStream(
                _partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true);

            await foreach (var pending in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                current = pending;
                var json = JsonSerializer.Serialize(pending.Line, JsonOptions);
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                pending.Acknowledgement.TrySetResult();
                current = null;
            }
        }
        catch (Exception ex)
        {
            current?.Acknowledgement.TrySetException(ex);
            while (_pending.Reader.TryRead(out var pending))
                pending.Acknowledgement.TrySetException(ex);
            throw;
        }
    }

    private async Task AbortAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _pending.Writer.TryComplete();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // The original start/write exception is the actionable failure.
        }
        _appendGate.Dispose();
    }

    private sealed record TraceLine(int SchemaVersion, string Type, TraceMetadata? Metadata, TraceEvent? Event);
    private sealed record PendingEvent(TraceLine Line, TaskCompletionSource Acknowledgement);
}
