using System.IO.Ports;

namespace SignalForge.Core.Transport;

public sealed class SerialPortByteTransport(SerialTransportOptions options) : IByteTransport, ISerialLineControl
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SerialPort? _port;
    private int _readStarted;
    private int _disposed;
    private TransportState _state = TransportState.Disconnected;

    public string DisplayName =>
        $"Serial · {options.PortName} · {options.BaudRate} {options.DataBits}{FormatParity(options.Parity)}{FormatStopBits(options.StopBits)}";
    public string Endpoint => options.PortName;
    public TransportKind Kind => TransportKind.Serial;
    public TransportState State => _state;
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;
    public bool? CtsHolding => _port?.IsOpen == true ? _port.CtsHolding : null;
    public bool? DsrHolding => _port?.IsOpen == true ? _port.DsrHolding : null;
    public bool? CDHolding => _port?.IsOpen == true ? _port.CDHolding : null;
    // System.IO.Ports exposes RI changes as events on some platforms, but not as a queryable level.
    public bool? RingHolding => null;

    public bool DtrEnable
    {
        get => _port?.DtrEnable ?? false;
        set => Port.DtrEnable = value;
    }

    public bool RtsEnable
    {
        get => _port?.RtsEnable ?? false;
        set => Port.RtsEnable = value;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == TransportState.Connected)
                return;

            SetState(TransportState.Connecting, $"Opening {options.PortName}");
            var port = new SerialPort(options.PortName, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
            {
                Handshake = options.Handshake,
                ReadBufferSize = 100_000,
                WriteBufferSize = 100_000,
                ReadTimeout = 1_000,
                WriteTimeout = 1_000,
                DtrEnable = options.InitialDtr,
                RtsEnable = options.InitialRts
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
                _port = port;
                Interlocked.Exchange(ref _readStarted, 0);
                SetState(TransportState.Connected, $"Opened {options.PortName}");
            }
            catch (Exception ex)
            {
                port.Dispose();
                SetState(TransportState.Faulted, $"Could not open {options.PortName}", ex);
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
        var port = _port ?? throw new InvalidOperationException("Open the serial port before reading.");
        if (Interlocked.Exchange(ref _readStarted, 1) != 0)
            throw new InvalidOperationException("Only one reader may consume a transport session.");

        try
        {
            await foreach (var chunk in TransportReadHelpers.ReadChunksAsync(port.BaseStream, cancellationToken,
                ex => SetState(TransportState.Faulted, "Serial receive failed", ex)).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            Interlocked.Exchange(ref _readStarted, 0);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var port = _port ?? throw new InvalidOperationException("Serial port is not open.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await port.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(TransportState.Faulted, "Serial transmit failed", ex);
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

            SetState(TransportState.Closing, $"Closing {options.PortName}");
            _port?.Dispose();
            _port = null;
            SetState(TransportState.Disconnected, "Serial port closed");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void SetBreakState(bool enabled) => Port.BreakState = enabled;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _writeGate.Dispose();
    }

    private SerialPort Port => _port is { IsOpen: true } port
        ? port
        : throw new InvalidOperationException("Serial port is not open.");

    private static char FormatParity(Parity parity) => parity switch
    {
        Parity.None => 'N',
        Parity.Odd => 'O',
        Parity.Even => 'E',
        Parity.Mark => 'M',
        Parity.Space => 'S',
        _ => '?'
    };

    private static string FormatStopBits(StopBits stopBits) => stopBits switch
    {
        StopBits.One => "1",
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        StopBits.None => "0",
        _ => "?"
    };

    private void SetState(TransportState state, string detail, Exception? exception = null)
    {
        var previous = _state;
        _state = state;
        TransportStateNotifier.Raise(this, StateChanged, previous, state, detail, exception);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed record SerialTransportOptions(
    string PortName,
    int BaudRate = 115_200,
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One,
    Handshake Handshake = Handshake.None,
    bool InitialDtr = false,
    bool InitialRts = false);
