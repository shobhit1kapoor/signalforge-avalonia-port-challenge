using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SignalForge.Core.Protocols.Terminal2;

namespace SignalForge.Core.Simulation;

/// <summary>
/// A loopback-only TCP echo endpoint for Terminal2 STX/ETX/LRC frames.
/// It validates complete frames before echoing their decoded payload.
/// </summary>
public sealed class Terminal2StxEtxEchoServer : IAsyncDisposable
{
    private readonly Terminal2StxEtxEchoServerOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private TcpClient? _activeClient;
    private Task? _acceptTask;
    private int _state = (int)Terminal2StxEtxEchoServerState.Stopped;
    private int _started;
    private int _disposed;
    private int _pendingBadLrc;
    private int _requestCount;
    private int _responseCount;
    private int _badLrcResponseCount;
    private long _bytesReceived;

    public Terminal2StxEtxEchoServer(Terminal2StxEtxEchoServerOptions? options = null)
    {
        _options = options ?? new Terminal2StxEtxEchoServerOptions();
        if (_options.Address is null || !IPAddress.IsLoopback(_options.Address))
            throw new ArgumentException("The STX/ETX echo simulator can bind only to a loopback address.", nameof(options));
        if (_options.Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 0 and 65535.");
        if (_options.MaximumFrameLength < 3)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum frame length must be at least 3 bytes.");

        _listener = new TcpListener(_options.Address, _options.Port);
        _pendingBadLrc = _options.InjectBadLrcOnNextResponse ? 1 : 0;
    }

    public Terminal2StxEtxEchoServerState State => (Terminal2StxEtxEchoServerState)Volatile.Read(ref _state);
    public IPAddress Address => _options.Address;
    public string Host => Address.ToString();
    public IPEndPoint? LocalEndPoint => _listener.LocalEndpoint as IPEndPoint;
    public int Port => LocalEndPoint?.Port ?? _options.Port;
    public int RequestCount => Volatile.Read(ref _requestCount);
    public int ResponseCount => Volatile.Read(ref _responseCount);
    public int BadLrcResponseCount => Volatile.Read(ref _badLrcResponseCount);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Raised on the server's worker thread; handlers should return quickly and not throw.</summary>
    public event EventHandler<Terminal2StxEtxEchoEventArgs>? EventOccurred;

    /// <summary>Starts the listener. The server is single-use and cannot be restarted after StopAsync.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (State == Terminal2StxEtxEchoServerState.Listening)
                return;
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("This echo simulator has already been started or stopped.");

            SetState(Terminal2StxEtxEchoServerState.Starting, "Starting loopback STX/ETX echo listener.");
            try
            {
                _listener.Start();
                SetState(Terminal2StxEtxEchoServerState.Listening, $"Listening on {LocalEndPoint}.");
                Raise(Terminal2StxEtxEchoEventKind.Listening, $"Listening on {LocalEndPoint}.");
                _acceptTask = AcceptLoopAsync(_shutdown.Token);
            }
            catch (Exception exception)
            {
                SetState(Terminal2StxEtxEchoServerState.Faulted, "Could not start loopback STX/ETX listener.", exception);
                Raise(Terminal2StxEtxEchoEventKind.Faulted, exception.Message);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops accepting clients and closes the current client to unblock any pending read.</summary>
    public async Task StopAsync()
    {
        Task? acceptTask;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == Terminal2StxEtxEchoServerState.Stopped)
                return;

            SetState(Terminal2StxEtxEchoServerState.Stopping, "Stopping loopback STX/ETX listener.");
            _shutdown.Cancel();
            _listener.Stop();
            Volatile.Read(ref _activeClient)?.Dispose();
            acceptTask = _acceptTask;
        }
        finally
        {
            _lifecycle.Release();
        }

        if (acceptTask is not null)
            await acceptTask.ConfigureAwait(false);

        SetState(Terminal2StxEtxEchoServerState.Stopped, "Loopback STX/ETX listener stopped.");
        Raise(Terminal2StxEtxEchoEventKind.Stopped, "Loopback STX/ETX listener stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
            _lifecycle.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                client.NoDelay = true;
                Volatile.Write(ref _activeClient, client);
                Raise(Terminal2StxEtxEchoEventKind.ClientConnected, "Loopback client connected.");
                try
                {
                    await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _activeClient, null, client);
                    client.Dispose();
                    Raise(Terminal2StxEtxEchoEventKind.ClientDisconnected, "Loopback client disconnected.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SetState(Terminal2StxEtxEchoServerState.Faulted, "STX/ETX echo accept loop failed.", exception);
            Raise(Terminal2StxEtxEchoEventKind.Faulted, exception.Message);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var decoder = new Terminal2StxEtxStreamDecoder(_options.MaximumFrameLength);
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;

                var received = buffer.AsMemory(0, count).ToArray();
                Interlocked.Add(ref _bytesReceived, count);
                Raise(Terminal2StxEtxEchoEventKind.BytesReceived, $"Received {count} TCP byte(s).", payload: received);

                foreach (var frameResult in decoder.Feed(buffer.AsSpan(0, count)))
                {
                    if (!frameResult.IsValid)
                    {
                        var issues = string.Join(" ", frameResult.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
                        Raise(Terminal2StxEtxEchoEventKind.FrameRejected, issues);
                        continue;
                    }

                    var requestNumber = Interlocked.Increment(ref _requestCount);
                    var requestFrame = Terminal2StxEtxCodec.Encode(frameResult.Value!);
                    Raise(Terminal2StxEtxEchoEventKind.FrameReceived,
                        $"Received complete valid STX/ETX frame #{requestNumber}.", requestNumber, requestFrame);

                    var response = Terminal2StxEtxCodec.Encode(frameResult.Value!);
                    var injectBadLrc = Interlocked.Exchange(ref _pendingBadLrc, 0) == 1;
                    if (injectBadLrc)
                    {
                        response[^1] ^= 0x01;
                        Interlocked.Increment(ref _badLrcResponseCount);
                        Raise(Terminal2StxEtxEchoEventKind.BadLrcInjected,
                            $"Injected one-shot bad LRC into response #{requestNumber}.", requestNumber, response);
                    }

                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _responseCount);
                    Raise(Terminal2StxEtxEchoEventKind.ResponseSent,
                        $"Sent {response.Length}-byte echo response #{requestNumber}.", requestNumber, response);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException exception)
        {
            Raise(Terminal2StxEtxEchoEventKind.ClientError, exception.Message);
        }
        catch (SocketException exception)
        {
            Raise(Terminal2StxEtxEchoEventKind.ClientError, exception.Message);
        }
        finally
        {
            var incomplete = decoder.Complete();
            if (!incomplete.IsValid)
            {
                var issues = string.Join(" ", incomplete.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
                Raise(Terminal2StxEtxEchoEventKind.FrameRejected, issues);
            }
        }
    }

    private void SetState(Terminal2StxEtxEchoServerState state, string message, Exception? exception = null)
    {
        var previous = (Terminal2StxEtxEchoServerState)Interlocked.Exchange(ref _state, (int)state);
        if (previous != state)
        {
            Raise(Terminal2StxEtxEchoEventKind.StateChanged, message,
                previousState: previous, currentState: state, exception: exception);
        }
    }

    private void Raise(
        Terminal2StxEtxEchoEventKind kind,
        string message,
        int? requestNumber = null,
        ReadOnlyMemory<byte> payload = default,
        Terminal2StxEtxEchoServerState? previousState = null,
        Terminal2StxEtxEchoServerState? currentState = null,
        Exception? exception = null)
    {
        var handlers = EventOccurred;
        if (handlers is null)
            return;

        var args = new Terminal2StxEtxEchoEventArgs(
            DateTimeOffset.UtcNow, kind, message, requestNumber, payload, previousState, currentState, exception);
        foreach (EventHandler<Terminal2StxEtxEchoEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception observerException) { Trace.TraceError($"STX/ETX echo observer failed: {observerException}"); }
        }
    }
}

public sealed record Terminal2StxEtxEchoServerOptions
{
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public int Port { get; init; }
    public int MaximumFrameLength { get; init; } = 1024 * 1024;
    /// <summary>Corrupts only the first response to a complete, valid request by flipping LRC bit 0.</summary>
    public bool InjectBadLrcOnNextResponse { get; init; }
}

public enum Terminal2StxEtxEchoServerState
{
    Stopped,
    Starting,
    Listening,
    Stopping,
    Faulted
}

public enum Terminal2StxEtxEchoEventKind
{
    StateChanged,
    Listening,
    ClientConnected,
    BytesReceived,
    FrameReceived,
    ResponseSent,
    BadLrcInjected,
    FrameRejected,
    ClientDisconnected,
    ClientError,
    Stopped,
    Faulted
}

public sealed class Terminal2StxEtxEchoEventArgs(
    DateTimeOffset occurredUtc,
    Terminal2StxEtxEchoEventKind kind,
    string message,
    int? requestNumber = null,
    ReadOnlyMemory<byte> payload = default,
    Terminal2StxEtxEchoServerState? previousState = null,
    Terminal2StxEtxEchoServerState? currentState = null,
    Exception? exception = null) : EventArgs
{
    public DateTimeOffset OccurredUtc { get; } = occurredUtc;
    public Terminal2StxEtxEchoEventKind Kind { get; } = kind;
    public string Message { get; } = message;
    public int? RequestNumber { get; } = requestNumber;
    public ReadOnlyMemory<byte> Payload { get; } = payload.ToArray();
    public Terminal2StxEtxEchoServerState? PreviousState { get; } = previousState;
    public Terminal2StxEtxEchoServerState? CurrentState { get; } = currentState;
    public Exception? Exception { get; } = exception;
}
