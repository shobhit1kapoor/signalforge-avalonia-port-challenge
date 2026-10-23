using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SignalForge.Core.Sessions;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Integration;

public sealed class TcpServerSessionAcceptanceTests
{
    [Fact]
    public async Task ServerSessionPreservesExactBytesAcrossSequentialClientsAndRecordsTrace()
    {
        var folder = Path.Combine(Path.GetTempPath(), "signalforge-tcp-server", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var tracePath = Path.Combine(folder, "server-session.sft");
        var port = ReserveLoopbackPort();
        var transport = new TcpServerByteTransport(IPAddress.Loopback, port);
        await using var session = new ByteSession();
        var receivedBytes = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        session.BytesReceived += (_, args) =>
        {
            foreach (var value in args.Chunk.Bytes.Span)
                receivedBytes.Writer.TryWrite(value);
        };

        try
        {
            await session.StartAsync(transport, tracePath, "tcp-server-acceptance");

            var firstIncoming = new byte[] { 0x00, 0x02, 0x7F, 0x80, 0xFE, 0xFF };
            var firstOutgoing = new byte[] { 0x06, 0x15, 0x00, 0xA5 };
            using (var firstClient = new TcpClient())
            {
                await firstClient.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
                await WaitForStateAsync(transport, TransportState.Connected);
                await firstClient.GetStream().WriteAsync(firstIncoming);
                Assert.Equal(firstIncoming, await ReadExactBytesAsync(receivedBytes.Reader, firstIncoming.Length));

                await session.SendAsync(firstOutgoing);
                var firstResponse = new byte[firstOutgoing.Length];
                await firstClient.GetStream().ReadExactlyAsync(firstResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(firstOutgoing, firstResponse);
            }

            await WaitForStateAsync(transport, TransportState.Listening);

            var secondIncoming = new byte[] { 0x53, 0x46, 0x00, 0x11 };
            var secondOutgoing = new byte[] { 0x90, 0x00, 0x01, 0xFE, 0xFD };
            using (var secondClient = new TcpClient())
            {
                await secondClient.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
                await WaitForStateAsync(transport, TransportState.Connected);
                await secondClient.GetStream().WriteAsync(secondIncoming);
                Assert.Equal(secondIncoming, await ReadExactBytesAsync(receivedBytes.Reader, secondIncoming.Length));

                await session.SendAsync(secondOutgoing);
                var secondResponse = new byte[secondOutgoing.Length];
                await secondClient.GetStream().ReadExactlyAsync(secondResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(secondOutgoing, secondResponse);
            }

            await WaitForStateAsync(transport, TransportState.Listening);
            await session.StopAsync();

            var trace = await TraceReader.LoadAsync(tracePath);
            Assert.Equal(firstOutgoing.Concat(secondOutgoing), trace.Events
                .Where(item => item.Kind == TraceEventKind.Transmit)
                .SelectMany(item => item.Payload ?? Array.Empty<byte>()));
            Assert.Equal(firstIncoming.Concat(secondIncoming), trace.Events
                .Where(item => item.Kind == TraceEventKind.Receive)
                .SelectMany(item => item.Payload ?? Array.Empty<byte>()));
            Assert.Equal(Enumerable.Range(1, trace.Events.Count).Select(value => (long)value),
                trace.Events.Select(item => item.Sequence));
            Assert.Contains(trace.Events, item =>
                item.Kind == TraceEventKind.Connection && item.Detail?.Contains("Connected", StringComparison.Ordinal) == true);
            Assert.Contains(trace.Events, item =>
                item.Kind == TraceEventKind.Connection && item.Detail?.Contains("Disconnected", StringComparison.Ordinal) == true);
            Assert.Equal(TransportState.Disconnected, transport.State);
        }
        finally
        {
            await session.StopAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DisconnectStopsAnActiveServerReadWithoutFaultingTheTransport()
    {
        var port = ReserveLoopbackPort();
        var transport = new TcpServerByteTransport(IPAddress.Loopback, port);
        await using var ownedTransport = transport;
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await transport.ConnectAsync();

        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in transport.ReadAllAsync())
            {
                if (!chunk.Bytes.IsEmpty)
                    received.TrySetResult(true);
            }
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
        await WaitForStateAsync(transport, TransportState.Connected);
        await client.GetStream().WriteAsync(new byte[] { 0xA5 });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await transport.DisconnectAsync();
        await reader.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TransportState.Disconnected, transport.State);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForStateAsync(TcpServerByteTransport transport, TransportState target)
    {
        if (transport.State == target)
            return;

        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<TransportStateChangedEventArgs> handler = (_, args) =>
        {
            if (args.Current == target)
                reached.TrySetResult(true);
        };
        transport.StateChanged += handler;
        try
        {
            if (transport.State == target)
                reached.TrySetResult(true);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            transport.StateChanged -= handler;
        }
    }

    private static async Task<byte[]> ReadExactBytesAsync(ChannelReader<byte> reader, int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        return bytes;
    }
}
