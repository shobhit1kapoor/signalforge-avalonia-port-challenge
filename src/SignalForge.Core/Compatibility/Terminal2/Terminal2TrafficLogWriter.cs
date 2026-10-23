using System.Globalization;
using System.Text;
using System.Threading.Channels;
using SignalForge.Core.Traces;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Writes an explicitly enabled, bounded, human-readable TX/RX traffic log.</summary>
public sealed class Terminal2TrafficLogWriter : IAsyncDisposable
{
    private const int QueueCapacity = 1024;
    private static readonly byte[] Footer = Encoding.UTF8.GetBytes("# -- END ----------------------------------------------\n");
    private readonly Channel<TraceEvent> _pending = Channel.CreateBounded<TraceEvent>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly string _directory;
    private readonly string _fileStem;
    private readonly int _maximumFileSize;
    private readonly byte[] _header;
    private FileStream? _stream;
    private Task? _writerTask;
    private string _currentFilePath = string.Empty;
    private long _currentFileLength;
    private int _nextPart;
    private int _stopped;
    private Exception? _failure;

    private Terminal2TrafficLogWriter(string directory, string fileStem, int maximumFileSize)
    {
        _directory = directory;
        _fileStem = fileStem;
        _maximumFileSize = maximumFileSize;
        _header = Encoding.UTF8.GetBytes(
            $"# SIGNALFORGE Terminal2 traffic log\n# Started {DateTimeOffset.UtcNow:O}\n");
    }

    public string CurrentFilePath => Volatile.Read(ref _currentFilePath);
    public Exception? Failure => Volatile.Read(ref _failure);
    public bool IsActive => Volatile.Read(ref _stopped) == 0 && Failure is null;

    public static async Task<Terminal2TrafficLogWriter> StartAsync(
        string directory,
        string prefix,
        int maximumFileSize = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumFileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFileSize), "Maximum log size must be zero or greater.");

        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException($"The configured Terminal2 log directory does not exist: {fullDirectory}");

        var safePrefix = SanitizePrefix(prefix);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var writer = new Terminal2TrafficLogWriter(fullDirectory, safePrefix + timestamp, maximumFileSize);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            writer.OpenNextPart();
            await writer.WriteBytesAsync(writer._header, cancellationToken).ConfigureAwait(false);
            writer._writerTask = writer.WriterLoopAsync();
            return writer;
        }
        catch
        {
            if (writer._stream is not null)
                await writer._stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Queues a snapshot of an RX or TX event; other trace events are ignored.</summary>
    public bool TryAppend(TraceEvent traceEvent)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);
        if (Volatile.Read(ref _stopped) != 0)
            return true;
        if (Failure is not null)
            return false;
        if (traceEvent.Kind is not (TraceEventKind.Receive or TraceEventKind.Transmit) || traceEvent.Payload is null)
            return true;

        var snapshot = traceEvent with { Payload = traceEvent.Payload.ToArray() };
        if (_pending.Writer.TryWrite(snapshot))
            return true;

        var failure = new IOException($"Terminal2 traffic log queue exceeded its {QueueCapacity}-event limit; the session trace remains intact.");
        Interlocked.CompareExchange(ref _failure, failure, null);
        _pending.Writer.TryComplete(failure);
        return false;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            _pending.Writer.TryComplete();

        if (_writerTask is { } writerTask)
            await writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _stream?.Dispose();
        }
    }

    private async Task WriterLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var traceEvent in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
                await WriteEventAsync(traceEvent).ConfigureAwait(false);

            await WriteBytesAsync(Footer).ConfigureAwait(false);
            if (_stream is not null)
                await _stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            Interlocked.CompareExchange(ref _failure, exception, null);
        }
        finally
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
        }

        if (failure is not null)
            throw new IOException("Terminal2 traffic log could not be completed.", failure);
    }

    private async Task WriteEventAsync(TraceEvent traceEvent)
    {
        var direction = traceEvent.Kind == TraceEventKind.Transmit ? "TX" : "RX";
        var hex = Convert.ToHexString(traceEvent.Payload!);
        var line = Encoding.UTF8.GetBytes(
            $"{traceEvent.TimestampUtc.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\t{direction}\t{hex}\n");

        if (_maximumFileSize > 0 && _currentFileLength > _header.Length
            && _currentFileLength + line.Length > _maximumFileSize)
        {
            await WriteBytesAsync(Footer).ConfigureAwait(false);
            if (_stream is not null)
            {
                await _stream.FlushAsync().ConfigureAwait(false);
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            OpenNextPart();
            await WriteBytesAsync(_header).ConfigureAwait(false);
        }

        await WriteBytesAsync(line).ConfigureAwait(false);
    }

    private async Task WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(Terminal2TrafficLogWriter));
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        _currentFileLength += bytes.Length;
    }

    private void OpenNextPart()
    {
        while (true)
        {
            var part = _nextPart++;
            var suffix = part == 0 ? string.Empty : $"_{part:000}";
            var path = Path.Combine(_directory, _fileStem + suffix + ".log");
            try
            {
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                _currentFileLength = 0;
                Volatile.Write(ref _currentFilePath, path);
                return;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Keep an existing log intact and advance to another unique segment.
            }
        }
    }

    private static string SanitizePrefix(string? prefix)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        foreach (var character in "<>:\"/\\|?*")
            invalid.Add(character);

        var safe = new string((prefix ?? string.Empty)
            .Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character)
            .ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            return "Terminal2_";
        return safe;
    }
}
