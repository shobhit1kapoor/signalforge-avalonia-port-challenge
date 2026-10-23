using System.Net.Sockets;

namespace SignalForge.Core.Transport;

public sealed class TcpClientByteTransport(string host, int port) : IByteTransport
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _readStarted;
    private int _disposed;
    private TransportState _state = TransportState.Disconnected;

    public string DisplayName => $"TCP · {host}:{port}";
    public string Endpoint => _client?.Client.RemoteEndPoint?.ToString() ?? $"{host}:{port}";
    public TransportKind Kind => TransportKind.TcpClient;
    public TransportState State => _state;
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == TransportState.Connected)
                return;

            SetState(TransportState.Connecting, $"Connecting to {host}:{port}");
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                _client = client;
                _stream = client.GetStream();
                Interlocked.Exchange(ref _readStarted, 0);
                SetState(TransportState.Connected, $"Connected to {Endpoint}");
            }
            catch (Exception ex)
            {
                client.Dispose();
                SetState(TransportState.Faulted, $"Could not connect to {host}:{port}", ex);
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
        var stream = _stream ?? throw new InvalidOperationException("Connect before reading.");
        if (Interlocked.Exchange(ref _readStarted, 1) != 0)
            throw new InvalidOperationException("Only one reader may consume a transport session.");

        try
        {
            await foreach (var chunk in TransportReadHelpers.ReadChunksAsync(stream, cancellationToken,
                ex => SetState(TransportState.Faulted, "TCP receive failed", ex)).ConfigureAwait(false))
                yield return chunk;

            if (!cancellationToken.IsCancellationRequested)
                await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _readStarted, 0);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var stream = _stream ?? throw new InvalidOperationException("Transport is not connected.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
            if (_state == TransportState.Disconnected)
                return;

            SetState(TransportState.Closing, "Closing TCP connection");
            _stream?.Dispose();
            _stream = null;
            _client?.Dispose();
            _client = null;
            SetState(TransportState.Disconnected, "Disconnected");
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
        var previous = _state;
        _state = state;
        TransportStateNotifier.Raise(this, StateChanged, previous, state, detail, exception);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
