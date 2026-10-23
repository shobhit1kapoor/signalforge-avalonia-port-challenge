using SignalForge.Core.Forensics;
using SignalForge.Core.Protocols.Modbus;
using SignalForge.Core.Sessions;
using SignalForge.Core.Simulation;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Integration;

public sealed class EngineeringSessionAcceptanceTests
{
    [Fact]
    public async Task GoodAndCorruptedLoopbackSessionsRecordReplayAndCompareExactBytes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "signalforge-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var request = ModbusTcpEncoder.Encode(17, 1, 3, [0x00, 0x0C, 0x00, 0x01]);
            var goodPath = Path.Combine(folder, "good.sft");
            var badPath = Path.Combine(folder, "bad.sft");

            var (goodTrace, goodFrame) = await CaptureReadAsync(goodPath, request, faultPlan: null);
            var (badTrace, badFrame) = await CaptureReadAsync(badPath, request,
                new VirtualDeviceFaultPlan([new(1, VirtualDeviceFaultKind.CorruptResponse, ByteOffset: 9, XorMask: 1)]));

            Assert.Empty(goodFrame.Issues);
            Assert.Equal(new byte[] { 0x02, 0xCA, 0xFE }, goodFrame.Data.ToArray());
            Assert.Empty(badFrame.Issues); // Modbus TCP has no checksum; forensic byte comparison finds this corruption.
            Assert.NotEqual(goodFrame.Data.ToArray(), badFrame.Data.ToArray());

            var comparison = ForensicSessionComparer.Compare(ToObservations(goodTrace), ToObservations(badTrace));
            var changedResponse = Assert.Single(comparison.Observations, item => item.Kind == ForensicChangeKind.Changed);
            Assert.Equal(9, changedResponse.Bytes!.FirstDivergenceOffset);

            await using var replay = new TraceReplayTransport(goodTrace, playbackSpeed: 10_000);
            await replay.ConnectAsync();
            await replay.WriteAsync(request);
            var replayedResponse = new List<byte>();
            await foreach (var chunk in replay.ReadAllAsync())
                replayedResponse.AddRange(chunk.Bytes.ToArray());
            Assert.Equal(goodFrame.RawBytes.ToArray(), replayedResponse);
            Assert.Equal(0L, replay.RemainingTransmitByteCount);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<(TraceDocument Trace, ModbusFrame Frame)> CaptureReadAsync(
        string tracePath,
        byte[] request,
        VirtualDeviceFaultPlan? faultPlan)
    {
        await using var server = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = faultPlan ?? new VirtualDeviceFaultPlan()
        });
        server.Device.SetHoldingRegister(12, 0xCAFE);
        await server.StartAsync();

        await using var session = new ByteSession();
        var decoder = new ModbusTcpDecoder();
        var frameReady = new TaskCompletionSource<ModbusFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BytesReceived += (_, args) =>
        {
            var batch = decoder.Feed(args.Chunk.Bytes.Span);
            if (batch.Frames.Count > 0)
                frameReady.TrySetResult(batch.Frames[0]);
        };

        await session.StartAsync(new TcpClientByteTransport(server.Host, server.Port), tracePath,
            faultPlan is null ? "good-loopback" : "corrupted-loopback");
        await session.SendAsync(request);
        var frame = await frameReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopAsync();
        return (await TraceReader.LoadAsync(tracePath), frame);
    }

    private static IEnumerable<ForensicObservation> ToObservations(TraceDocument trace) =>
        trace.Events
            .Where(item => item.Kind is TraceEventKind.Transmit or TraceEventKind.Receive)
            .Select(item => new ForensicObservation(
                item.Direction?.ToString() ?? item.Kind.ToString(),
                item.Kind.ToString(),
                item.MonotonicTicks,
                item.Payload ?? Array.Empty<byte>(),
                item.Direction,
                checked((int)item.Sequence)));
}
