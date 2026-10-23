/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>Bounds the XMODEM sender's response waits and NAK retransmissions.</summary>
public sealed record Terminal2XModemOptions(
    int MaxNakRetries = 5,
    TimeSpan? AcknowledgementTimeout = null)
{
    public const int MaximumNakRetries = 20;
    public static TimeSpan DefaultAcknowledgementTimeout { get; } = TimeSpan.FromSeconds(2);
    public static TimeSpan MaximumAcknowledgementTimeout { get; } = TimeSpan.FromSeconds(60);

    public TimeSpan EffectiveAcknowledgementTimeout =>
        AcknowledgementTimeout ?? DefaultAcknowledgementTimeout;
}

/// <summary>Progress after a data block has been acknowledged.</summary>
public sealed record Terminal2XModemProgress(
    int AcknowledgedBytes,
    int TotalBytes,
    int AcknowledgedBlocks,
    int TotalBlocks,
    byte BlockNumber);

public enum Terminal2XModemStatus
{
    Completed,
    Cancelled,
    Failed
}

public enum Terminal2XModemIssueCode
{
    InvalidOptions,
    SendFailed,
    ReadFailed,
    AcknowledgementTimedOut,
    UnexpectedResponse,
    NakRetryLimitExceeded,
    EndOfTransmissionNotAcknowledged,
    ProgressCallbackFailed
}

public enum Terminal2XModemStage
{
    DataBlock,
    EndOfTransmission
}

/// <summary>Typed XMODEM diagnostic with the relevant stage and response context.</summary>
public sealed record Terminal2XModemIssue(
    Terminal2XModemIssueCode Code,
    string Message,
    Terminal2XModemStage? Stage = null,
    byte? BlockNumber = null,
    byte? Response = null,
    Exception? Exception = null);

/// <summary>Result of a Terminal2-compatible 128-byte XMODEM send.</summary>
public sealed record Terminal2XModemResult(
    Terminal2XModemStatus Status,
    int TotalBytes,
    int AcknowledgedBytes,
    int TotalBlocks,
    int AcknowledgedBlocks,
    IReadOnlyList<Terminal2XModemIssue> Issues)
{
    public bool IsSuccess => Status == Terminal2XModemStatus.Completed;
}

/// <summary>
/// Builds and sends classic 128-byte XMODEM blocks through injected asynchronous byte
/// delegates. A zero-length payload deliberately sends one all-zero data block, matching
/// Terminal2's do/while sender. This type does not perform the receiver's initial 'C'/'NAK'
/// handshake; the caller starts sending once its receiver is ready.
/// </summary>
public static class Terminal2XModemSender
{
    private const byte Soh = 0x01;
    private const byte Eot = 0x04;
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;
    private const byte CrcRequest = (byte)'C';
    private const int DataBlockLength = 128;
    private const int PacketLength = 3 + DataBlockLength + 2;

    /// <summary>
    /// Sends one or more XMODEM blocks, awaiting ACK for each and for EOT. NAK retries
    /// resend the exact same block, and every response read is bounded by the configured
    /// acknowledgement timeout. The injected reader must observe its cancellation token.
    /// </summary>
    public static async Task<Terminal2XModemResult> SendAsync(
        ReadOnlyMemory<byte> data,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendAsync,
        Func<CancellationToken, ValueTask<byte>> readByteAsync,
        Terminal2XModemOptions? options = null,
        Action<Terminal2XModemProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentNullException.ThrowIfNull(readByteAsync);
        options ??= new Terminal2XModemOptions();

        var invalidOptions = Validate(options);
        if (invalidOptions is not null)
            return Failed(data.Length, 0, 0, invalidOptions);

        var totalBlocks = Math.Max(1, (int)((data.Length + (long)DataBlockLength - 1) / DataBlockLength));
        var acknowledgedBytes = 0;
        var acknowledgedBlocks = 0;
        byte blockNumber = 0;

        try
        {
            for (var blockIndex = 0; blockIndex < totalBlocks; blockIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                blockNumber = unchecked((byte)(blockNumber + 1));
                var dataOffset = blockIndex * DataBlockLength;
                var bytesInBlock = Math.Min(DataBlockLength, data.Length - dataOffset);
                var packet = BuildPacket(data.Span.Slice(dataOffset, bytesInBlock), blockNumber);

                var retries = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await sendAsync(packet, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                            new Terminal2XModemIssue(
                                Terminal2XModemIssueCode.SendFailed,
                                $"Could not send XMODEM block {blockNumber}: {exception.Message}",
                                Terminal2XModemStage.DataBlock,
                                blockNumber,
                                Exception: exception));
                    }

                    var response = await ReadResponseAsync(
                        readByteAsync,
                        options.EffectiveAcknowledgementTimeout,
                        cancellationToken).ConfigureAwait(false);

                    if (response.Kind == ResponseKind.Ack)
                        break;

                    if (response.Kind == ResponseKind.Nak)
                    {
                        if (retries >= options.MaxNakRetries)
                        {
                            return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                                new Terminal2XModemIssue(
                                    Terminal2XModemIssueCode.NakRetryLimitExceeded,
                                    $"XMODEM block {blockNumber} was rejected {retries + 1} times; retry limit is {options.MaxNakRetries}.",
                                    Terminal2XModemStage.DataBlock,
                                    blockNumber,
                                    Nak));
                        }

                        retries++;
                        continue;
                    }

                    if (response.Kind == ResponseKind.TimedOut)
                    {
                        return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                            new Terminal2XModemIssue(
                                Terminal2XModemIssueCode.AcknowledgementTimedOut,
                                $"Timed out waiting for the acknowledgement of XMODEM block {blockNumber}.",
                                Terminal2XModemStage.DataBlock,
                                blockNumber));
                    }

                    if (response.Kind == ResponseKind.ReadFailed)
                    {
                        return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                            new Terminal2XModemIssue(
                                Terminal2XModemIssueCode.ReadFailed,
                                $"Could not read the response for XMODEM block {blockNumber}: {response.Exception!.Message}",
                                Terminal2XModemStage.DataBlock,
                                blockNumber,
                                Exception: response.Exception));
                    }

                    return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                        new Terminal2XModemIssue(
                            Terminal2XModemIssueCode.UnexpectedResponse,
                            $"Unexpected response 0x{response.Value:X2} while waiting for XMODEM block {blockNumber} acknowledgement.",
                            Terminal2XModemStage.DataBlock,
                            blockNumber,
                            response.Value));
                }

                acknowledgedBytes += bytesInBlock;
                acknowledgedBlocks++;
                var progressIssue = ReportProgress(progress,
                    new Terminal2XModemProgress(acknowledgedBytes, data.Length, acknowledgedBlocks, totalBlocks, blockNumber));
                if (progressIssue is not null)
                    return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks, progressIssue);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await sendAsync(new byte[] { Eot }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                    new Terminal2XModemIssue(
                        Terminal2XModemIssueCode.SendFailed,
                        $"Could not send XMODEM end-of-transmission: {exception.Message}",
                        Terminal2XModemStage.EndOfTransmission,
                        Exception: exception));
            }

            var eotResponse = await ReadResponseAsync(
                readByteAsync,
                options.EffectiveAcknowledgementTimeout,
                cancellationToken,
                ignoreCrcRequest: false).ConfigureAwait(false);

            if (eotResponse.Kind == ResponseKind.Ack)
            {
                return new Terminal2XModemResult(
                    Terminal2XModemStatus.Completed,
                    data.Length,
                    acknowledgedBytes,
                    totalBlocks,
                    acknowledgedBlocks,
                    Array.Empty<Terminal2XModemIssue>());
            }

            if (eotResponse.Kind == ResponseKind.TimedOut)
            {
                return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                    new Terminal2XModemIssue(
                        Terminal2XModemIssueCode.AcknowledgementTimedOut,
                        "Timed out waiting for the XMODEM end-of-transmission acknowledgement.",
                        Terminal2XModemStage.EndOfTransmission));
            }

            if (eotResponse.Kind == ResponseKind.ReadFailed)
            {
                return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                    new Terminal2XModemIssue(
                        Terminal2XModemIssueCode.ReadFailed,
                        $"Could not read the XMODEM end-of-transmission response: {eotResponse.Exception!.Message}",
                        Terminal2XModemStage.EndOfTransmission,
                        Exception: eotResponse.Exception));
            }

            return Failed(data.Length, acknowledgedBytes, acknowledgedBlocks,
                new Terminal2XModemIssue(
                    Terminal2XModemIssueCode.EndOfTransmissionNotAcknowledged,
                    $"XMODEM end-of-transmission was not acknowledged (response 0x{eotResponse.Value:X2}).",
                    Terminal2XModemStage.EndOfTransmission,
                    Response: eotResponse.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Terminal2XModemResult(
                Terminal2XModemStatus.Cancelled,
                data.Length,
                acknowledgedBytes,
                totalBlocks,
                acknowledgedBlocks,
                Array.Empty<Terminal2XModemIssue>());
        }
    }

    /// <summary>Calculates the CRC-16/XMODEM value using polynomial 0x1021 and initial value 0.</summary>
    public static ushort CalculateCrc16Xmodem(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc = (ushort)(crc ^ (value << 8));
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    private static byte[] BuildPacket(ReadOnlySpan<byte> payload, byte blockNumber)
    {
        var packet = new byte[PacketLength];
        packet[0] = Soh;
        packet[1] = blockNumber;
        packet[2] = unchecked((byte)(255 - blockNumber));
        payload.CopyTo(packet.AsSpan(3, DataBlockLength));
        var crc = CalculateCrc16Xmodem(packet.AsSpan(3, DataBlockLength));
        packet[^2] = (byte)(crc >> 8);
        packet[^1] = (byte)crc;
        return packet;
    }

    private static async ValueTask<Response> ReadResponseAsync(
        Func<CancellationToken, ValueTask<byte>> readByteAsync,
        TimeSpan acknowledgementTimeout,
        CancellationToken cancellationToken,
        bool ignoreCrcRequest = true)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte value;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(acknowledgementTimeout);
            try
            {
                value = await readByteAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                return new(ResponseKind.TimedOut);
            }
            catch (TimeoutException)
            {
                return new(ResponseKind.TimedOut);
            }
            catch (Exception exception)
            {
                return new(ResponseKind.ReadFailed, Exception: exception);
            }

            // Terminal2's SendBlock discards every CRC-request byte before interpreting a response.
            if (ignoreCrcRequest && value == CrcRequest)
                continue;
            if (value == Ack)
                return new(ResponseKind.Ack, value);
            if (value == Nak)
                return new(ResponseKind.Nak, value);
            return new(ResponseKind.Unexpected, value);
        }
    }

    private static Terminal2XModemIssue? Validate(Terminal2XModemOptions options)
    {
        if (options.MaxNakRetries is < 0 or > Terminal2XModemOptions.MaximumNakRetries)
        {
            return new Terminal2XModemIssue(
                Terminal2XModemIssueCode.InvalidOptions,
                $"Maximum NAK retries must be between 0 and {Terminal2XModemOptions.MaximumNakRetries}.");
        }

        var timeout = options.EffectiveAcknowledgementTimeout;
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > Terminal2XModemOptions.MaximumAcknowledgementTimeout)
        {
            return new Terminal2XModemIssue(
                Terminal2XModemIssueCode.InvalidOptions,
                $"Acknowledgement timeout must be between 1 ms and {Terminal2XModemOptions.MaximumAcknowledgementTimeout.TotalSeconds:0} seconds.");
        }

        return null;
    }

    private static Terminal2XModemIssue? ReportProgress(
        Action<Terminal2XModemProgress>? progress,
        Terminal2XModemProgress update)
    {
        if (progress is null)
            return null;

        try
        {
            progress(update);
            return null;
        }
        catch (Exception exception)
        {
            return new Terminal2XModemIssue(
                Terminal2XModemIssueCode.ProgressCallbackFailed,
                $"Could not report XMODEM progress: {exception.Message}",
                Terminal2XModemStage.DataBlock,
                update.BlockNumber,
                Exception: exception);
        }
    }

    private static Terminal2XModemResult Failed(
        int totalBytes,
        int acknowledgedBytes,
        int acknowledgedBlocks,
        Terminal2XModemIssue issue)
    {
        var totalBlocks = Math.Max(1, (int)((totalBytes + (long)DataBlockLength - 1) / DataBlockLength));
        return new Terminal2XModemResult(
            Terminal2XModemStatus.Failed,
            totalBytes,
            acknowledgedBytes,
            totalBlocks,
            acknowledgedBlocks,
            new[] { issue });
    }

    private enum ResponseKind
    {
        Ack,
        Nak,
        TimedOut,
        Unexpected,
        ReadFailed
    }

    private sealed record Response(
        ResponseKind Kind,
        byte Value = 0,
        Exception? Exception = null);
}
