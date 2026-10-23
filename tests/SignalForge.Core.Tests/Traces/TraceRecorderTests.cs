using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Traces;

public sealed class TraceRecorderTests
{
    [Fact]
    public async Task Recorder_RoundTripsExactBytesAndOrderedEvents()
    {
        var folder = Path.Combine(Path.GetTempPath(), "signalforge-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "capture.sft");
        var metadata = new TraceMetadata("fixture", "TCP client", "127.0.0.1:1", DateTimeOffset.UtcNow, 0);

        await using (var recorder = await TraceRecorder.StartAsync(path, metadata))
        {
            await recorder.AppendAsync(TraceEventKind.Connection, "Connected", endpoint: metadata.Endpoint);
            await recorder.AppendBytesAsync(ByteDirection.Rx, new byte[] { 0x00, 0x10, 0x80, 0xFF }, metadata.Endpoint);
            await recorder.AppendBytesAsync(ByteDirection.Tx, new byte[] { 0x02, 0x03 }, metadata.Endpoint);
            await recorder.StopAsync();
        }

        var document = await TraceReader.LoadAsync(path);

        Assert.Equal("fixture", document.Metadata.SessionName);
        Assert.Equal(3, document.Events.Count);
        Assert.Equal(new byte[] { 0x00, 0x10, 0x80, 0xFF }, document.Events[1].Payload);
        Assert.Equal(new byte[] { 0x02, 0x03 }, document.Events[2].Payload);
        Assert.Equal(new long[] { 1, 2, 3 }, document.Events.Select(item => item.Sequence));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task Reader_RejectsTraceWithoutHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sft");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"type\":\"event\"}\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => TraceReader.LoadAsync(path));
    }
}
