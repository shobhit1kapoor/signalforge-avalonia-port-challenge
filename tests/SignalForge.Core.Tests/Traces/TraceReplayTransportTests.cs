using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Traces;

public sealed class TraceReplayTransportTests
{
    [Fact]
    public async Task Replay_EmitsExactReceiveBytesAndMatchesTransmitAcrossDifferentWriteBoundaries()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0x10, 0x20]),
            new TraceEvent(2, 1_100, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x00, 0x80, 0xFF]),
            new TraceEvent(3, 1_200, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0x30, 0x40]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);

        await replay.ConnectAsync();
        await replay.WriteAsync(new byte[] { 0x10 });
        await replay.WriteAsync(new byte[] { 0x20 });
        var received = new List<byte>();
        await using var reader = replay.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        received.AddRange(reader.Current.Bytes.ToArray());
        await replay.WriteAsync(new byte[] { 0x30 });
        await replay.WriteAsync(new byte[] { 0x40 });
        while (await reader.MoveNextAsync())
            received.AddRange(reader.Current.Bytes.ToArray());

        Assert.Equal(new byte[] { 0x00, 0x80, 0xFF }, received);
        Assert.Equal(4L, replay.TransmittedByteCount);
        Assert.Equal(0L, replay.RemainingTransmitByteCount);
    }

    [Fact]
    public async Task Replay_MismatchReportsFirstDivergenceWithoutConsumingExpectedBytes()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xAA, 0xBB]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();

        var mismatch = await Assert.ThrowsAsync<TraceReplayMismatchException>(
            async () => await replay.WriteAsync(new byte[] { 0xAA, 0x00 }));

        Assert.Equal(1, mismatch.ByteOffset);
        Assert.Equal((byte)0xBB, mismatch.Expected);
        Assert.Equal((byte)0x00, mismatch.Actual);
        Assert.Equal(0L, replay.TransmittedByteCount);
        await replay.WriteAsync(new byte[] { 0xAA, 0xBB });
        Assert.Equal(0L, replay.RemainingTransmitByteCount);
    }

    [Fact]
    public async Task Replay_EndOfReceivePlaybackFailsWhenRecordedTransmitBytesRemain()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]),
            new TraceEvent(2, 1_100, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xAA, 0xBB, 0xCC]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();

        var incomplete = await Assert.ThrowsAsync<TraceReplayIncompleteException>(async () =>
        {
            await foreach (var _ in replay.ReadAllAsync()) { }
        });

        Assert.Equal(3L, incomplete.ExpectedTransmitByteCount);
        Assert.Equal(0L, incomplete.TransmittedByteCount);
        Assert.Equal(3L, incomplete.RemainingTransmitByteCount);
        Assert.Equal(2L, incomplete.NextExpectedTransmitSequence);
        Assert.Equal(3L, replay.RemainingTransmitByteCount);
    }

    [Fact]
    public async Task Replay_WaitsForRecordedTransmitBeforeEmittingLaterReceive()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xA5]),
            new TraceEvent(2, 1_100, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();
        await using var reader = replay.ReadAllAsync().GetAsyncEnumerator();

        var pendingReceive = reader.MoveNextAsync().AsTask();
        await Task.Delay(40);
        Assert.False(pendingReceive.IsCompleted, "An RX event must wait for earlier recorded TX bytes.");

        await replay.WriteAsync(new byte[] { 0xA5 });
        Assert.True(await pendingReceive.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(new byte[] { 0x10 }, reader.Current.Bytes.ToArray());
    }

    [Fact]
    public async Task Replay_WriteCannotCrossARecordedReceiveBoundary()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xA1]),
            new TraceEvent(2, 1_100, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]),
            new TraceEvent(3, 1_200, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xB1]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();

        var mismatch = await Assert.ThrowsAsync<TraceReplayMismatchException>(
            async () => await replay.WriteAsync(new byte[] { 0xA1, 0xB1 }));
        Assert.Equal(1, mismatch.ByteOffset);
        Assert.Null(mismatch.Expected);
        Assert.Equal((byte)0xB1, mismatch.Actual);
        Assert.Equal(0L, replay.TransmittedByteCount);

        await replay.WriteAsync(new byte[] { 0xA1 });
        await using var reader = replay.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new byte[] { 0x10 }, reader.Current.Bytes.ToArray());
        await replay.WriteAsync(new byte[] { 0xB1 });
        Assert.False(await reader.MoveNextAsync());
        Assert.Equal(0L, replay.RemainingTransmitByteCount);
    }

    [Fact]
    public async Task Replay_AllowsTransmitResponseToTheLastReceiveBeforeCompleting()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]),
            new TraceEvent(2, 1_100, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xA5, 0x5A]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();

        var received = new List<byte>();
        await foreach (var chunk in replay.ReadAllAsync())
        {
            received.AddRange(chunk.Bytes.ToArray());
            await replay.WriteAsync(new byte[] { 0xA5, 0x5A });
        }

        Assert.Equal(new byte[] { 0x10 }, received);
        Assert.Equal(0L, replay.RemainingTransmitByteCount);
    }

    [Fact]
    public async Task Replay_ReportsUnconsumedTransmitWhenTraceHasNoReceiveEvents()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0x7E]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();

        var incomplete = await Assert.ThrowsAsync<TraceReplayIncompleteException>(async () =>
        {
            await foreach (var _ in replay.ReadAllAsync()) { }
        });

        Assert.Equal(1L, incomplete.RemainingTransmitByteCount);
        Assert.Equal(1L, incomplete.NextExpectedTransmitSequence);
    }

    [Fact]
    public async Task Replay_PauseAndResumeFreezeRecordedReceiveSchedule()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]),
            new TraceEvent(2, 301_000, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x20]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 1);
        await replay.ConnectAsync();
        await using var reader = replay.ReadAllAsync().GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(new byte[] { 0x10 }, reader.Current.Bytes.ToArray());

        var nextEvent = reader.MoveNextAsync().AsTask();
        await Task.Delay(35);
        replay.Pause();
        await Task.Delay(350);
        Assert.True(replay.IsPaused);
        Assert.False(nextEvent.IsCompleted, "A paused replay must not emit a scheduled RX event.");

        replay.Resume();
        Assert.True(await nextEvent.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(new byte[] { 0x20 }, reader.Current.Bytes.ToArray());
        Assert.False(replay.IsPaused);
    }

    [Fact]
    public async Task Replay_SeekMovesReceiveCursorButPreservesTransmitVerification()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xA1, 0xA2]),
            new TraceEvent(2, 1_100, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]),
            new TraceEvent(3, 1_200, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0xB1]),
            new TraceEvent(4, 1_300, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x20]),
            new TraceEvent(5, 1_400, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x30]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();
        Assert.Equal(3, replay.ReceiveEventCount);
        Assert.Equal(0, replay.NextReceiveEventIndex);

        await replay.WriteAsync(new byte[] { 0xA1 });
        replay.Pause();
        replay.SeekToSequence(4);
        Assert.Equal(1, replay.NextReceiveEventIndex);
        Assert.Equal(4, replay.NextReceiveSequence);
        await replay.WriteAsync(new byte[] { 0xA2, 0xB1 });
        Assert.Equal(3L, replay.TransmittedByteCount);
        Assert.Equal(0L, replay.RemainingTransmitByteCount);

        replay.Resume();
        var received = new List<byte>();
        await foreach (var chunk in replay.ReadAllAsync())
            received.AddRange(chunk.Bytes.ToArray());

        Assert.Equal(new byte[] { 0x20, 0x30 }, received);
        Assert.Equal(replay.ReceiveEventCount, replay.NextReceiveEventIndex);
        Assert.Null(replay.NextReceiveSequence);
    }

    [Fact]
    public async Task Replay_RequiresPauseBeforeSeekingAnActiveReader()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]),
            new TraceEvent(2, 1_100, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x20]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 10_000);
        await replay.ConnectAsync();
        await using var reader = replay.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        Assert.Throws<InvalidOperationException>(() => replay.SeekToSequence(2));
        replay.Pause();
        replay.SeekToSequence(2);
        Assert.Equal(2, replay.NextReceiveSequence);
        replay.Resume();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new byte[] { 0x20 }, reader.Current.Bytes.ToArray());
    }

    [Fact]
    public async Task Replay_DisconnectCancelsAWaitForTheNextReceiveEvent()
    {
        var document = CreateDocument(
            new TraceEvent(1, 1_000, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x10]),
            new TraceEvent(2, 6_001_000, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x20]));
        await using var replay = new TraceReplayTransport(document, playbackSpeed: 1);
        await replay.ConnectAsync();
        await using var reader = replay.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        var nextEvent = reader.MoveNextAsync().AsTask();
        await Task.Delay(35);
        await replay.DisconnectAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await nextEvent.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(TransportState.Disconnected, replay.State);
    }

    private static TraceDocument CreateDocument(params TraceEvent[] events) =>
        new(new TraceMetadata("replay test", "TCP", "fixture", DateTimeOffset.UtcNow, StopwatchFrequency: 1_000_000), events);
}
