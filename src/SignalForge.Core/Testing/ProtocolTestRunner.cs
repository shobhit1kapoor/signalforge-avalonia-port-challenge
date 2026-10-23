using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using SignalForge.Core.Protocols.Modbus;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Testing;

/// <summary>Runs deterministic byte-level assertions over an existing byte transport.</summary>
public sealed class ProtocolTestRunner
{
    private const int RxChunkBufferCapacity = 32;

    public Task<ProtocolTestResult> RunAsync(
        IByteTransport transport,
        ProtocolTestScenario scenario,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(transport, scenario, cancellationToken, captureCancellation: false);

    /// <summary>
    /// Runs a scenario and returns partial results when the caller cancels it. Use this
    /// when the caller needs to persist a cancelled run as an auditable report.
    /// </summary>
    public Task<ProtocolTestResult> RunForReportAsync(
        IByteTransport transport,
        ProtocolTestScenario scenario,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(transport, scenario, cancellationToken, captureCancellation: true);

    private async Task<ProtocolTestResult> RunCoreAsync(
        IByteTransport transport,
        ProtocolTestScenario scenario,
        CancellationToken cancellationToken,
        bool captureCancellation)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Steps.Any(step => step is null))
            throw new ArgumentException("A test scenario cannot contain null steps.", nameof(scenario));

        var started = Stopwatch.GetTimestamp();
        var results = new List<ProtocolTestStepResult>();
        var evidence = new List<ProtocolTestEvidence>();
        var connectedHere = transport.State != TransportState.Connected;
        var cancelled = false;
        Exception? receiveFailure = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? readTask = null;
        try
        {
            if (connectedHere)
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var channel = Channel.CreateBounded<ByteChunk>(new BoundedChannelOptions(RxChunkBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            readTask = PumpAsync(transport, channel.Writer, lifetime.Token);
            var pending = new List<byte>();
            var stop = false;
            for (var index = 0; index < scenario.Steps.Count && !stop; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = scenario.Steps[index];
                var number = index + 1;
                var stepStarted = Stopwatch.GetTimestamp();
                try
                {
                    switch (step)
                    {
                        case SendBytesStep send:
                            await transport.WriteAsync(send.Bytes, cancellationToken).ConfigureAwait(false);
                            evidence.Add(new(number, send.Name, ByteDirection.Tx, Stopwatch.GetTimestamp(), send.Bytes.ToArray(), "Sent bytes."));
                            results.Add(new(number, send.Name, true, $"Sent {send.Bytes.Length} bytes.", send.Bytes, send.Bytes,
                                stepStarted, Stopwatch.GetTimestamp()));
                            break;

                        case ExpectBytesStep expect:
                        {
                            if (!expect.Mask.IsEmpty && expect.Mask.Length != expect.Expected.Length)
                                throw new InvalidOperationException("Byte assertion mask length must match expected bytes.");
                            using var timeout = CreateTimeout(step.Timeout, cancellationToken);
                            var observed = await ReadExactlyAsync(expect.Expected.Length, pending, channel.Reader, evidence,
                                number, step.Name, timeout.Token).ConfigureAwait(false);
                            var passed = MaskedEquals(expect.Expected.Span, observed, expect.Mask.Span);
                            results.Add(new(number, step.Name, passed,
                                passed ? $"Received the expected {observed.Length} bytes." : "Received bytes did not match the expected value under the configured mask.",
                                expect.Expected, observed, stepStarted, Stopwatch.GetTimestamp()));
                            stop = !passed;
                            break;
                        }

                        case ExpectModbusTcpStep expect:
                        {
                            using var timeout = CreateTimeout(step.Timeout, cancellationToken);
                            var header = await ReadExactlyAsync(7, pending, channel.Reader, evidence,
                                number, step.Name, timeout.Token).ConfigureAwait(false);
                            var mbapLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                            if (mbapLength is < 2 or > 254)
                                throw new InvalidDataException($"Received invalid MBAP length {mbapLength}.");
                            var tail = await ReadExactlyAsync(mbapLength - 1, pending, channel.Reader, evidence,
                                number, step.Name, timeout.Token).ConfigureAwait(false);
                            var response = new byte[header.Length + tail.Length];
                            header.CopyTo(response, 0);
                            tail.CopyTo(response, header.Length);
                            var transaction = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2));
                            var protocol = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
                            var unit = response[6];
                            var function = response[7];
                            var isException = (function & 0x80) != 0;
                            var exceptionCode = isException && response.Length > 8 ? response[8] : (byte?)null;
                            var checks = new List<string>();
                            if (protocol != 0) checks.Add($"protocol identifier {protocol} is invalid; expected 0");
                            if (expect.TransactionId is ushort tid && transaction != tid) checks.Add($"transaction {transaction} != {tid}");
                            if (expect.UnitId is byte unitId && unit != unitId) checks.Add($"unit {unit} != {unitId}");
                            if (expect.FunctionCode is byte fc && (function & 0x7f) != (fc & 0x7f)) checks.Add($"function 0x{function:X2} != 0x{fc:X2}");
                            if (expect.ExceptionCode is byte ec && exceptionCode != ec) checks.Add($"exception {exceptionCode?.ToString() ?? "none"} != {ec}");
                            if (expect.ExceptionCode is null && isException) checks.Add($"unexpected Modbus exception {exceptionCode?.ToString() ?? "unknown"}");
                            checks.AddRange(ModbusPduValidator.ValidateResponse(function, response.AsSpan(8))
                                .Select(issue => issue.Message));
                            var passed = checks.Count == 0;
                            results.Add(new(number, step.Name, passed,
                                passed ? "Received a Modbus TCP response matching all configured assertions." : string.Join("; ", checks),
                                default, response, stepStarted, Stopwatch.GetTimestamp()));
                            stop = !passed;
                            break;
                        }

                        case DelayTestStep delay:
                            if (delay.Duration < TimeSpan.Zero)
                                throw new ArgumentOutOfRangeException(nameof(delay.Duration));
                            await Task.Delay(delay.Duration, cancellationToken).ConfigureAwait(false);
                            results.Add(new(number, step.Name, true, $"Waited {delay.Duration}.", StartedAtTicks: stepStarted,
                                CompletedAtTicks: Stopwatch.GetTimestamp()));
                            break;

                        default:
                            throw new NotSupportedException($"Unsupported test step type {step.GetType().Name}.");
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && step.Timeout > TimeSpan.Zero)
                {
                    results.Add(new(number, step.Name, false, $"Timed out after {step.Timeout}.", StartedAtTicks: stepStarted,
                        CompletedAtTicks: Stopwatch.GetTimestamp()));
                    stop = true;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ChannelClosedException or InvalidOperationException)
                {
                    results.Add(new(number, step.Name, false, ex.Message, StartedAtTicks: stepStarted,
                        CompletedAtTicks: Stopwatch.GetTimestamp()));
                    stop = true;
                }
            }
        }
        catch (OperationCanceledException) when (captureCancellation && cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            lifetime.Cancel();
            if (readTask is not null)
            {
                try { await readTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception exception) { receiveFailure = exception; }
            }
            if (scenario.DisconnectWhenDone || connectedHere)
            {
                try { await transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }

        // A send-only scenario has no expectation step to observe a failed RX
        // pump. Preserve that transport failure as a failed result instead of
        // allowing all completed sends to produce a misleading PASS.
        if (receiveFailure is not null && results.All(result => result.Passed))
        {
            var failedAt = Stopwatch.GetTimestamp();
            results.Add(new(results.Count + 1, "Receive pump", false,
                $"Transport receive failed: {receiveFailure.GetBaseException().Message}",
                StartedAtTicks: failedAt, CompletedAtTicks: Stopwatch.GetTimestamp()));
        }

        return new ProtocolTestResult(scenario.Name, transport.DisplayName, results, evidence, started,
            Stopwatch.GetTimestamp(), cancelled);
    }

    private static async Task PumpAsync(IByteTransport transport, ChannelWriter<ByteChunk> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in transport.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
        finally { writer.TryComplete(); }
    }

    private static async Task<byte[]> ReadExactlyAsync(
        int count,
        List<byte> pending,
        ChannelReader<ByteChunk> reader,
        ICollection<ProtocolTestEvidence> evidence,
        int stepNumber,
        string stepName,
        CancellationToken cancellationToken)
    {
        while (pending.Count < count)
        {
            ByteChunk chunk;
            try
            {
                chunk = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException closed)
            {
                var failure = closed.InnerException;
                if (failure is null && reader.Completion.IsFaulted)
                {
                    try { await reader.Completion.ConfigureAwait(false); }
                    catch (Exception completionError) { failure = completionError; }
                }

                if (failure is not null)
                    throw new IOException($"Transport receive failed: {failure.Message}", failure);
                throw new EndOfStreamException("Transport stream ended before the expected bytes arrived.");
            }
            pending.AddRange(chunk.Bytes.ToArray());
            evidence.Add(new(stepNumber, stepName, ByteDirection.Rx, chunk.MonotonicTicks,
                chunk.Bytes.ToArray(), "Received transport chunk."));
        }
        var result = pending.Take(count).ToArray();
        pending.RemoveRange(0, count);
        return result;
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Receive assertions require a positive timeout.");
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static bool MaskedEquals(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, ReadOnlySpan<byte> mask)
    {
        if (expected.Length != actual.Length)
            return false;
        for (var index = 0; index < expected.Length; index++)
        {
            var maskByte = mask.IsEmpty ? (byte)0xff : mask[index];
            if ((expected[index] & maskByte) != (actual[index] & maskByte))
                return false;
        }
        return true;
    }
}
