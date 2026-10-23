using System.Runtime.CompilerServices;

namespace SignalForge.Core.Transport;

public enum TransportKind
{
    Serial,
    TcpClient,
    TcpServer,
    Simulator,
    Replay
}

public enum ByteDirection
{
    Tx,
    Rx
}

public enum TransportState
{
    Disconnected,
    Connecting,
    Listening,
    Connected,
    Closing,
    Faulted
}

public sealed class TransportStateChangedEventArgs(
    TransportState previous,
    TransportState current,
    string detail,
    Exception? exception = null) : EventArgs
{
    public TransportState Previous { get; } = previous;
    public TransportState Current { get; } = current;
    public string Detail { get; } = detail;
    public Exception? Exception { get; } = exception;
}

/// <summary>A copied RX chunk. The timestamp is in the Stopwatch.GetTimestamp domain.</summary>
public sealed record ByteChunk(long MonotonicTicks, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Owns one byte-stream session. Implementations must preserve bytes and ordering,
/// serialize concurrent writes, and never convert data to text.
/// </summary>
public interface IByteTransport : IAsyncDisposable
{
    string DisplayName { get; }
    string Endpoint { get; }
    TransportKind Kind { get; }
    TransportState State { get; }
    event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<ByteChunk> ReadAllAsync(CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface ISerialLineControl
{
    bool? CtsHolding { get; }
    bool? DsrHolding { get; }
    bool? CDHolding { get; }
    bool? RingHolding { get; }
    bool DtrEnable { get; set; }
    bool RtsEnable { get; set; }
    void SetBreakState(bool enabled);
}

internal static class TransportStateNotifier
{
    public static void Raise(
        object sender,
        EventHandler<TransportStateChangedEventArgs>? handlers,
        TransportState previous,
        TransportState current,
        string detail,
        Exception? exception = null)
    {
        if (handlers is null)
            return;

        var args = new TransportStateChangedEventArgs(previous, current, detail, exception);
        foreach (EventHandler<TransportStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, args);
            }
            catch
            {
                // Observer failure must not interrupt transport cleanup or byte delivery.
            }
        }
    }
}

internal static class TransportReadHelpers
{
    public static async IAsyncEnumerable<ByteChunk> ReadChunksAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<Exception>? onError = null)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int count;
            try
            {
                count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                throw;
            }

            if (count == 0)
                yield break;

            var copy = buffer.AsMemory(0, count).ToArray();
            yield return new ByteChunk(System.Diagnostics.Stopwatch.GetTimestamp(), copy);
        }
    }
}
