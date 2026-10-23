/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2XModemSenderTests
{
    [Fact]
    public void Crc_matches_the_published_xmodem_check_vector()
    {
        Assert.Equal(0x31C3, Terminal2XModemSender.CalculateCrc16Xmodem("123456789"u8));
        Assert.Equal(0x0000, Terminal2XModemSender.CalculateCrc16Xmodem(new byte[128]));
    }

    [Fact]
    public async Task Empty_payload_sends_one_zero_padded_block_then_eot()
    {
        // Pinned Terminal2 source: FrmMain.cs:2156-2198 uses do/while and zero-fills all 128 bytes.
        var sent = new List<byte[]>();
        var progress = new List<Terminal2XModemProgress>();
        var result = await Terminal2XModemSender.SendAsync(
            ReadOnlyMemory<byte>.Empty,
            Collect(sent),
            ReadResponses(0x06, 0x06),
            progress: progress.Add);

        Assert.Equal(Terminal2XModemStatus.Completed, result.Status);
        Assert.Equal(0, result.TotalBytes);
        Assert.Equal(0, result.AcknowledgedBytes);
        Assert.Equal(1, result.TotalBlocks);
        Assert.Equal(1, result.AcknowledgedBlocks);
        Assert.Equal(2, sent.Count);
        Assert.Equal(133, sent[0].Length);
        Assert.Equal(new byte[] { 0x01, 0x01, 0xFE }, sent[0][..3]);
        Assert.All(sent[0][3..131], value => Assert.Equal((byte)0, value));
        Assert.Equal(new byte[] { 0x00, 0x00 }, sent[0][131..]);
        Assert.Equal(new byte[] { 0x04 }, sent[1]);
        Assert.Equal(new Terminal2XModemProgress(0, 0, 1, 1, 1), Assert.Single(progress));
    }

    [Fact]
    public async Task Builds_128_byte_packets_with_block_complement_crc_and_progress()
    {
        // Pinned Terminal2 source: FrmMain.cs:2161-2177 builds SOH/block/~block/data/CRC-hi/CRC-lo.
        var data = Enumerable.Range(0, 130).Select(value => (byte)value).ToArray();
        var sent = new List<byte[]>();
        var progress = new List<Terminal2XModemProgress>();
        // Like SendBlock, CRC request bytes are discarded before ACK/NAK interpretation.
        var result = await Terminal2XModemSender.SendAsync(
            data,
            Collect(sent),
            ReadResponses((byte)'C', 0x06, (byte)'C', 0x06, 0x06),
            progress: progress.Add);

        Assert.Equal(Terminal2XModemStatus.Completed, result.Status);
        Assert.Equal(130, result.AcknowledgedBytes);
        Assert.Equal(2, result.TotalBlocks);
        Assert.Equal(2, result.AcknowledgedBlocks);
        Assert.Equal(3, sent.Count);
        Assert.Equal(new byte[] { 0x01, 0x01, 0xFE }, sent[0][..3]);
        Assert.Equal(data[..128], sent[0][3..131]);
        Assert.Equal(Crc(sent[0]), sent[0][131..]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0xFD }, sent[1][..3]);
        Assert.Equal(data[128..], sent[1][3..5]);
        Assert.All(sent[1][5..131], value => Assert.Equal((byte)0, value));
        Assert.Equal(Crc(sent[1]), sent[1][131..]);
        Assert.Equal(new byte[] { 0x04 }, sent[2]);
        Assert.Equal(new Terminal2XModemProgress(128, 130, 1, 2, 1), progress[0]);
        Assert.Equal(new Terminal2XModemProgress(130, 130, 2, 2, 2), progress[1]);
    }

    [Fact]
    public async Task Block_number_wraps_after_255_and_complement_is_byte_sized()
    {
        var data = new byte[256 * 128];
        var sent = new List<byte[]>();
        var result = await Terminal2XModemSender.SendAsync(
            data,
            Collect(sent),
            ReadResponses(Enumerable.Repeat((byte)0x06, 257).ToArray()));

        Assert.Equal(Terminal2XModemStatus.Completed, result.Status);
        Assert.Equal(257, sent.Count);
        Assert.Equal(new byte[] { 0x01, 0xFF, 0x00 }, sent[254][..3]);
        Assert.Equal(new byte[] { 0x01, 0x00, 0xFF }, sent[255][..3]);
        Assert.Equal(new byte[] { 0x04 }, sent[256]);
    }

    [Fact]
    public async Task Nak_resends_identical_block_up_to_the_configured_retry_bound()
    {
        var sent = new List<byte[]>();
        var result = await Terminal2XModemSender.SendAsync(
            new byte[] { 0x41 },
            Collect(sent),
            ReadResponses(0x15, 0x15, 0x15),
            new Terminal2XModemOptions(MaxNakRetries: 2));

        Assert.Equal(Terminal2XModemStatus.Failed, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(Terminal2XModemIssueCode.NakRetryLimitExceeded, issue.Code);
        Assert.Equal(Terminal2XModemStage.DataBlock, issue.Stage);
        Assert.Equal((byte)1, issue.BlockNumber);
        Assert.Equal((byte)0x15, issue.Response);
        Assert.Equal(3, sent.Count);
        Assert.All(sent, packet => Assert.Equal(sent[0], packet));
        Assert.Equal(0, result.AcknowledgedBytes);
        Assert.Equal(0, result.AcknowledgedBlocks);
    }

    [Fact]
    public async Task Nak_retry_can_succeed_without_advancing_the_block()
    {
        var sent = new List<byte[]>();
        var progress = new List<Terminal2XModemProgress>();
        var result = await Terminal2XModemSender.SendAsync(
            new byte[] { 0x41 },
            Collect(sent),
            ReadResponses(0x15, 0x06, 0x06),
            new Terminal2XModemOptions(MaxNakRetries: 1),
            progress.Add);

        Assert.Equal(Terminal2XModemStatus.Completed, result.Status);
        Assert.Equal(3, sent.Count);
        Assert.Equal(sent[0], sent[1]);
        Assert.Equal(new byte[] { 0x04 }, sent[2]);
        Assert.Equal(new Terminal2XModemProgress(1, 1, 1, 1, 1), Assert.Single(progress));
    }

    [Fact]
    public async Task Ack_timeout_returns_typed_failure()
    {
        var result = await Terminal2XModemSender.SendAsync(
            new byte[] { 0x41 },
            Collect([]),
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            },
            new Terminal2XModemOptions(AcknowledgementTimeout: TimeSpan.FromMilliseconds(30)));

        Assert.Equal(Terminal2XModemStatus.Failed, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(Terminal2XModemIssueCode.AcknowledgementTimedOut, issue.Code);
        Assert.Equal(Terminal2XModemStage.DataBlock, issue.Stage);
        Assert.Equal((byte)1, issue.BlockNumber);
    }

    [Fact]
    public async Task Cancellation_during_response_wait_returns_cancelled_without_eot()
    {
        var sent = new List<byte[]>();
        var packetSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = Terminal2XModemSender.SendAsync(
            new byte[] { 0x41 },
            (packet, _) =>
            {
                sent.Add(packet.ToArray());
                packetSent.TrySetResult();
                return ValueTask.CompletedTask;
            },
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            },
            cancellationToken: cancellation.Token);

        await packetSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await run;

        Assert.Equal(Terminal2XModemStatus.Cancelled, result.Status);
        Assert.Empty(result.Issues);
        Assert.Single(sent);
        Assert.Equal(new byte[] { 0x01, 0x01, 0xFE }, sent[0][..3]);
    }

    [Fact]
    public async Task End_of_transmission_requires_acknowledgement()
    {
        var sent = new List<byte[]>();
        var result = await Terminal2XModemSender.SendAsync(
            new byte[] { 0x41 },
            Collect(sent),
            ReadResponses(0x06, 0x15));

        Assert.Equal(Terminal2XModemStatus.Failed, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(Terminal2XModemIssueCode.EndOfTransmissionNotAcknowledged, issue.Code);
        Assert.Equal(Terminal2XModemStage.EndOfTransmission, issue.Stage);
        Assert.Equal((byte)0x15, issue.Response);
        Assert.Equal(new byte[] { 0x04 }, sent[1]);
    }

    [Fact]
    public async Task Eot_response_does_not_ignore_crc_request_bytes()
    {
        var result = await Terminal2XModemSender.SendAsync(
            new byte[] { 0x41 },
            Collect([]),
            ReadResponses(0x06, (byte)'C'));

        Assert.Equal(Terminal2XModemStatus.Failed, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(Terminal2XModemIssueCode.EndOfTransmissionNotAcknowledged, issue.Code);
        Assert.Equal((byte)'C', issue.Response);
    }

    [Fact]
    public async Task Invalid_retry_bound_is_rejected_before_any_send()
    {
        var sent = new List<byte[]>();
        var result = await Terminal2XModemSender.SendAsync(
            new byte[] { 0x41 },
            Collect(sent),
            ReadResponses(),
            new Terminal2XModemOptions(MaxNakRetries: Terminal2XModemOptions.MaximumNakRetries + 1));

        Assert.Equal(Terminal2XModemStatus.Failed, result.Status);
        Assert.Equal(Terminal2XModemIssueCode.InvalidOptions, Assert.Single(result.Issues).Code);
        Assert.Empty(sent);
    }

    private static Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> Collect(List<byte[]> sent) =>
        (packet, _) =>
        {
            sent.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        };

    private static Func<CancellationToken, ValueTask<byte>> ReadResponses(params byte[] responses)
    {
        var queue = new Queue<byte>(responses);
        return _ => ValueTask.FromResult(queue.Dequeue());
    }

    private static byte[] Crc(byte[] packet) =>
    [
        (byte)(Terminal2XModemSender.CalculateCrc16Xmodem(packet.AsSpan(3, 128)) >> 8),
        (byte)Terminal2XModemSender.CalculateCrc16Xmodem(packet.AsSpan(3, 128))
    ];
}
