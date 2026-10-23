using SignalForge.Core.Transport;

namespace SignalForge.Core.Traces;

public enum TraceEventKind
{
    Connection,
    Transmit,
    Receive,
    Marker,
    Assertion,
    Error
}

public sealed record TraceMetadata(
    string SessionName,
    string Transport,
    string Endpoint,
    DateTimeOffset StartedAtUtc,
    long StopwatchFrequency,
    string ApplicationVersion = "0.1.0");

public sealed record TraceEvent(
    long Sequence,
    long MonotonicTicks,
    DateTimeOffset TimestampUtc,
    TraceEventKind Kind,
    byte[]? Payload = null,
    ByteDirection? Direction = null,
    string? Endpoint = null,
    string? Detail = null,
    string? CorrelationId = null)
{
    public static TraceEvent Bytes(long sequence, ByteDirection direction, ByteChunk chunk, string endpoint) =>
        new(sequence, chunk.MonotonicTicks, DateTimeOffset.UtcNow,
            direction == ByteDirection.Tx ? TraceEventKind.Transmit : TraceEventKind.Receive,
            chunk.Bytes.ToArray(), direction, endpoint);
}

public sealed record TraceDocument(TraceMetadata Metadata, IReadOnlyList<TraceEvent> Events);

public sealed class TraceByteEventArgs(TraceEvent traceEvent) : EventArgs
{
    public TraceEvent Event { get; } = traceEvent;
}
