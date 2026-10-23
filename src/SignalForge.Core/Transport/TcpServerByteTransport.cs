using System.Net;
using System.Net.Sockets;

namespace SignalForge.Core.Transport;

public sealed class TcpServerByteTransport(IPAddress address, int port) : IByteTransport
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerLifetime;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private string? _connectedEndpoint;
    private int _readStarted;
    private int _disposed;
    private int _state = (int)TransportState.Disconnected;

    public string DisplayName => $"TCP listener · {address}:{port}";
    public string Endpoint => Volatile.Read(ref _connectedEndpoint) ?? $"{address}:{port}";
    public TransportKind Kind => TransportKind.TcpServer;
    public TransportState State => (TransportState)Volatile.Read(ref _state);
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is TransportState.Listening or TransportState.Connected)
                return;

            SetState(TransportState.Connecting, $"Starting listener on {address}:{port}");
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                _listener = listener;
                _listenerLifetime = new CancellationTokenSource();
                SetState(TransportState.Listening, $"Listening on {listener.LocalEndpoint}");
            }
            catch (Exception ex)
            {
                listener.Stop();
                SetState(TransportState.Faulted, $"Could not listen on {address}:{port}", ex);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async IAsyncEnumerable<ByteChunk> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var listener = _listener ?? throw new InvalidOperationException("Start listening before reading.");
        var listenerLifetime = _listenerLifetime ?? throw new InvalidOperationException("Start listening before reading.");
        using var linkedLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, listenerLifetime.Token);
        var readToken = linkedLifetime.Token;
        if (Interlocked.Exchange(ref _readStarted, 1) != 0)
            throw new InvalidOperationException("Only one reader may consume a transport session.");

        try
        {
            while (!readToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(readToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (readToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (readToken.IsCancellationRequested)
                {
                    // TcpListener.Stop may wake AcceptTcpClientAsync with a socket
                    // error. Cancellation makes that an orderly listener shutdown.
                    break;
                }
                catch (Exception ex)
                {
                    SetState(TransportState.Faulted, "TCP accept failed", ex);
                    throw;
                }
                client.NoDelay = true;
                var connectedEndpoint = client.Client.RemoteEndPoint?.ToString();
                _client = client;
                Volatile.Write(ref _connectedEndpoint, connectedEndpoint);
                var stream = client.GetStream();
                _stream = stream;
                SetState(TransportState.Connected, $"Accepted {Endpoint}");

                try
                {
                    var buffer = new byte[16 * 1024];
                    while (!readToken.IsCancellationRequested)
                    {
                        int count;
                        try
                        {
                            count = await stream.ReadAsync(buffer.AsMemory(), readToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (readToken.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception) when (readToken.IsCancellationRequested)
                        {
                            // Some stream implementations report disposal after cancellation.
                            break;
                        }
                        catch (Exception ex)
                        {
                            SetState(TransportState.Faulted, "TCP server receive failed", ex);
                            throw;
                        }

                        if (count == 0)
                            break;

                        var copy = buffer.AsMemory(0, count).ToArray();
                        yield return new ByteChunk(System.Diagnostics.Stopwatch.GetTimestamp(), copy);
                    }
                }
                finally
                {
                    stream.Dispose();
                    client.Dispose();
                    if (ReferenceEquals(_stream, stream))
                        _stream = null;
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                        Volatile.Write(ref _connectedEndpoint, null);
                    }
                }

                if (ReferenceEquals(_listener, listener) && State == TransportState.Connected)
                    SetState(TransportState.Listening, "Client disconnected; waiting for another connection");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _readStarted, 0);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var stream = _stream ?? throw new InvalidOperationException("No TCP client is connected.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (State is not (TransportState.Closing or TransportState.Disconnected))
                SetState(TransportState.Faulted, "TCP transmit failed", ex);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TransportState.Disconnected)
                return;

            SetState(TransportState.Closing, "Stopping TCP listener");
            var listenerLifetime = _listenerLifetime;
            _listenerLifetime = null;
            listenerLifetime?.Cancel();
            _listener?.Stop();
            _listener = null;
            _stream?.Dispose();
            _stream = null;
            _client?.Dispose();
            _client = null;
            Volatile.Write(ref _connectedEndpoint, null);
            SetState(TransportState.Disconnected, "Listener stopped");
            listenerLifetime?.Dispose();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _writeGate.Dispose();
    }

    private void SetState(TransportState state, string detail, Exception? exception = null)
    {
        var previous = (TransportState)Interlocked.Exchange(ref _state, (int)state);
        TransportStateNotifier.Raise(this, StateChanged, previous, state, detail, exception);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
