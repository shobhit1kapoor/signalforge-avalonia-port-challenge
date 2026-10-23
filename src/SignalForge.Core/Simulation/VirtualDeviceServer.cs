using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SignalForge.Core.Protocols.Modbus;

namespace SignalForge.Core.Simulation;

/// <summary>A deterministic Modbus TCP device hosted by a real loopback TCP listener.</summary>
public sealed class VirtualDeviceServer : IAsyncDisposable
{
    private readonly VirtualDeviceServerOptions _options;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Task? _acceptTask;
    private int _nextConnectionId;
    private int _requestNumber;
    private int _state = (int)VirtualDeviceServerState.Stopped;
    private int _disposed;
    private VirtualDeviceFaultPlan _faultPlan;

    public VirtualDeviceServer(VirtualDeviceServerOptions? options = null)
    {
        _options = options ?? new VirtualDeviceServerOptions();
        if (_options.Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 0 and 65535.");
        Device = new VirtualModbusDevice(_options.UnitId);
        _faultPlan = _options.FaultPlan ?? new VirtualDeviceFaultPlan();
        _listener = new TcpListener(_options.Address, _options.Port);
    }

    public VirtualModbusDevice Device { get; }
    public VirtualDeviceServerState State => (VirtualDeviceServerState)Volatile.Read(ref _state);
    public IPEndPoint? LocalEndPoint => _listener.LocalEndpoint as IPEndPoint;
    public string Host => LocalEndPoint?.Address.ToString() ?? _options.Address.ToString();
    public int Port => LocalEndPoint?.Port ?? _options.Port;
    public VirtualDeviceSnapshot Snapshot => Device.Snapshot();
    public VirtualDeviceFaultPlan FaultPlan
    {
        get => Volatile.Read(ref _faultPlan);
        set => Volatile.Write(ref _faultPlan, value ?? throw new ArgumentNullException(nameof(value)));
    }
    public event EventHandler<VirtualDeviceEventArgs>? EventOccurred;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is VirtualDeviceServerState.Listening or VirtualDeviceServerState.ClientConnected or VirtualDeviceServerState.Processing)
                return;
            SetState(VirtualDeviceServerState.Starting, "Starting virtual Modbus TCP listener.");
            _listener.Start();
            SetState(VirtualDeviceServerState.Listening, $"Listening on {LocalEndPoint}.");
            Raise(VirtualDeviceEventKind.Listening, $"Listening on {LocalEndPoint}.");
            _acceptTask = AcceptLoopAsync(_shutdown.Token);
        }
        catch (Exception ex)
        {
            SetState(VirtualDeviceServerState.Faulted, "Could not start virtual device listener.", ex);
            Raise(VirtualDeviceEventKind.Faulted, ex.Message);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == VirtualDeviceServerState.Stopped)
                return;
            SetState(VirtualDeviceServerState.Stopped, "Stopping virtual device listener.");
            _shutdown.Cancel();
            _listener.Stop();
            foreach (var client in _clients.Values)
                client.Dispose();
            var tasks = _connections.Values.Append(_acceptTask ?? Task.CompletedTask).ToArray();
            try { await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* Per-connection failures are surfaced as events. */ }
            _connections.Clear();
            _clients.Clear();
            Raise(VirtualDeviceEventKind.Stopped, "Virtual device listener stopped.");
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
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _lifecycle.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                _clients[connectionId] = client;
                SetState(VirtualDeviceServerState.ClientConnected, $"Client {connectionId} connected.");
                Raise(VirtualDeviceEventKind.ClientConnected, $"Client {connectionId} connected.", connectionId);
                var task = HandleClientAsync(connectionId, client, cancellationToken);
                _connections[connectionId] = task;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetState(VirtualDeviceServerState.Faulted, "Virtual device accept loop failed.", ex);
            Raise(VirtualDeviceEventKind.Faulted, ex.Message);
        }
    }

    private async Task HandleClientAsync(int connectionId, TcpClient client, CancellationToken cancellationToken)
    {
        var decoder = new ModbusTcpDecoder();
        var buffer = new byte[4096];
        try
        {
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;
                var batch = decoder.Feed(buffer.AsSpan(0, count));
                foreach (var issue in batch.Issues)
                    Raise(VirtualDeviceEventKind.ProtocolError, issue.Message, connectionId, payload: issue.Evidence);
                foreach (var request in batch.Frames)
                {
                    var requestNumber = Interlocked.Increment(ref _requestNumber);
                    Raise(VirtualDeviceEventKind.RequestReceived, $"Received {request.FunctionName} request #{requestNumber}.",
                        connectionId, requestNumber, request.RawBytes);
                    if (request.Issues.Any(issue => issue.Kind != ModbusDecodeIssueKind.UnsupportedFunction))
                    {
                        Raise(VirtualDeviceEventKind.ProtocolError, "Ignored structurally invalid Modbus TCP request.",
                            connectionId, requestNumber, request.RawBytes);
                        continue;
                    }

                    SetState(VirtualDeviceServerState.Processing, $"Processing request #{requestNumber}.");
                    var response = Device.HandleRequest(request);
                    if (response is null)
                    {
                        Raise(VirtualDeviceEventKind.ProtocolError, "Request unit ID or transport did not match this virtual device.",
                            connectionId, requestNumber, request.RawBytes);
                        continue;
                    }

                    var drop = false;
                    var disconnect = false;
                    foreach (var fault in FaultPlan.ForRequest(requestNumber))
                    {
                        switch (fault.Kind)
                        {
                            case VirtualDeviceFaultKind.DelayResponse:
                                Raise(VirtualDeviceEventKind.FaultApplied, $"Delaying response by {fault.Delay}.", connectionId, requestNumber);
                                await Task.Delay(fault.Delay, cancellationToken).ConfigureAwait(false);
                                break;
                            case VirtualDeviceFaultKind.DropResponse:
                                drop = true;
                                Raise(VirtualDeviceEventKind.ResponseDropped, "Dropped response by configured fault.", connectionId, requestNumber, response);
                                break;
                            case VirtualDeviceFaultKind.Disconnect:
                                disconnect = true;
                                Raise(VirtualDeviceEventKind.FaultApplied, "Disconnected client by configured fault.", connectionId, requestNumber);
                                break;
                            case VirtualDeviceFaultKind.CorruptResponse:
                                if ((uint)fault.ByteOffset < (uint)response.Length)
                                {
                                    response[fault.ByteOffset] ^= fault.XorMask;
                                    Raise(VirtualDeviceEventKind.FaultApplied, $"Corrupted response byte {fault.ByteOffset} with XOR 0x{fault.XorMask:X2}.", connectionId, requestNumber, response);
                                }
                                else
                                {
                                    Raise(VirtualDeviceEventKind.ProtocolError, $"Corruption offset {fault.ByteOffset} is outside the {response.Length}-byte response.", connectionId, requestNumber);
                                }
                                break;
                            case VirtualDeviceFaultKind.InvalidFrame:
                                response[2] = 0;
                                response[3] = 1;
                                Raise(VirtualDeviceEventKind.FaultApplied, "Set the MBAP protocol identifier to an invalid nonzero value.", connectionId, requestNumber, response);
                                break;
                        }
                    }
                    if (disconnect)
                        return;
                    if (drop)
                        continue;
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    Raise(VirtualDeviceEventKind.ResponseSent, $"Sent {response.Length}-byte response.", connectionId, requestNumber, response);
                }
                if (State == VirtualDeviceServerState.Processing)
                    SetState(VirtualDeviceServerState.ClientConnected, $"Client {connectionId} is connected.");
            }
            var incomplete = decoder.Complete();
            foreach (var issue in incomplete.Issues)
                Raise(VirtualDeviceEventKind.ProtocolError, issue.Message, connectionId, payload: issue.Evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException ex) { Raise(VirtualDeviceEventKind.ProtocolError, ex.Message, connectionId); }
        catch (SocketException ex) { Raise(VirtualDeviceEventKind.ProtocolError, ex.Message, connectionId); }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Raise(VirtualDeviceEventKind.Faulted, ex.Message, connectionId);
        }
        finally
        {
            _clients.TryRemove(connectionId, out _);
            _connections.TryRemove(connectionId, out _);
            client.Dispose();
            Raise(VirtualDeviceEventKind.ClientDisconnected, $"Client {connectionId} disconnected.", connectionId);
            if (State is not (VirtualDeviceServerState.Stopped or VirtualDeviceServerState.Faulted) && _clients.IsEmpty)
                SetState(VirtualDeviceServerState.Listening, "No clients are connected.");
        }
    }

    private void SetState(VirtualDeviceServerState next, string message, Exception? exception = null)
    {
        var previous = (VirtualDeviceServerState)Interlocked.Exchange(ref _state, (int)next);
        if (previous != next)
            Raise(VirtualDeviceEventKind.StateChanged, $"{previous} → {next}: {message}",
                previousState: previous, currentState: next);
    }

    private void Raise(VirtualDeviceEventKind kind, string message, int? connectionId = null,
        int? requestNumber = null, ReadOnlyMemory<byte> payload = default,
        VirtualDeviceServerState? previousState = null, VirtualDeviceServerState? currentState = null)
    {
        var handlers = EventOccurred;
        if (handlers is null)
            return;
        var args = new VirtualDeviceEventArgs(Stopwatch.GetTimestamp(), kind, message, connectionId, requestNumber, payload,
            previousState, currentState);
        foreach (EventHandler<VirtualDeviceEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex) { Trace.TraceError($"Virtual-device observer failed: {ex}"); }
        }
    }
}
