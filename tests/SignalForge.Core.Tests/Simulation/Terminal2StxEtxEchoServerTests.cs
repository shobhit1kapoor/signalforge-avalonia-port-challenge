using System.Net;
using System.Net.Sockets;
using SignalForge.Core.Protocols.Terminal2;
using SignalForge.Core.Simulation;

namespace SignalForge.Core.Tests.Simulation;

public sealed class Terminal2StxEtxEchoServerTests
{
    [Fact]
    public async Task EchoesFragmentedFramesAndInjectsOnlyOneBadLrcResponse()
    {
        await using var simulator = new Terminal2StxEtxEchoServer(new Terminal2StxEtxEchoServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            InjectBadLrcOnNextResponse = true
        });
        var firstFragmentReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        simulator.EventOccurred += (_, args) =>
        {
            if (args.Kind == Terminal2StxEtxEchoEventKind.BytesReceived && simulator.BytesReceived >= 3)
                firstFragmentReceived.TrySetResult();
        };

        await simulator.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(simulator.Address, simulator.Port).WaitAsync(TimeSpan.FromSeconds(3));
        var stream = client.GetStream();

        var firstPayload = new byte[] { (byte)'A', Terminal2StxEtxCodec.Stx, (byte)'B', Terminal2StxEtxCodec.Etx, Terminal2StxEtxCodec.Dle, (byte)'Z' };
        var firstRequest = Terminal2StxEtxCodec.Encode(firstPayload);
        var prefix = firstRequest.AsMemory(0, 3); // Ends immediately after DLE; the quoted control byte arrives later.
        await stream.WriteAsync(prefix);
        await firstFragmentReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, simulator.RequestCount);

        await stream.WriteAsync(firstRequest.AsMemory(prefix.Length));
        var expectedBadResponse = firstRequest.ToArray();
        expectedBadResponse[^1] ^= 0x01;
        var actualBadResponse = await ReadExactlyAsync(stream, expectedBadResponse.Length);
        Assert.Equal(expectedBadResponse, actualBadResponse);
        var badValidation = Terminal2StxEtxCodec.Decode(actualBadResponse);
        Assert.False(badValidation.IsValid);
        Assert.Contains(badValidation.Issues, issue => issue.Code == "stxetx.bad-lrc");

        var secondPayload = new byte[] { (byte)'O', (byte)'K', 0x00, (byte)'!' };
        var secondRequest = Terminal2StxEtxCodec.Encode(secondPayload);
        await stream.WriteAsync(secondRequest);
        var actualGoodResponse = await ReadExactlyAsync(stream, secondRequest.Length);
        Assert.Equal(secondRequest, actualGoodResponse);
        var goodValidation = Terminal2StxEtxCodec.Decode(actualGoodResponse);
        Assert.True(goodValidation.IsValid);
        Assert.Equal(secondPayload, goodValidation.Value);

        Assert.Equal(2, simulator.RequestCount);
        Assert.Equal(2, simulator.ResponseCount);
        Assert.Equal(1, simulator.BadLrcResponseCount);

        await simulator.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Terminal2StxEtxEchoServerState.Stopped, simulator.State);
    }

    [Fact]
    public void SimulatorRejectsNonLoopbackBindings()
    {
        Assert.Throws<ArgumentException>(() => new Terminal2StxEtxEchoServer(new Terminal2StxEtxEchoServerOptions
        {
            Address = IPAddress.Any,
            Port = 0
        }));
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
    {
        var result = new byte[length];
        await stream.ReadExactlyAsync(result).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        return result;
    }
}
