using System.Net;
using System.Net.Sockets;
using System.Text;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Sessions;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Integration;

public sealed class Terminal2TextFileTransferIntegrationTests
{
    [Fact]
    public async Task Text_file_transfer_sends_bytes_through_tcp_byte_session_and_records_each_line()
    {
        var folder = Path.Combine(Path.GetTempPath(), "signalforge-transfer-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var filePath = Path.Combine(folder, "commands.txt");
        var tracePath = Path.Combine(folder, "transfer.sft");
        await File.WriteAllTextAsync(filePath, "A$00\n\nB$$", new UTF8Encoding(false));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var acceptTask = listener.AcceptTcpClientAsync();
        await using var session = new ByteSession();
        await session.StartAsync(new TcpClientByteTransport("127.0.0.1", endpoint.Port), tracePath, "terminal2-file-transfer");
        using var peer = await acceptTask.WaitAsync(TimeSpan.FromSeconds(3));

        var transfer = new Terminal2TextFileTransfer(
            (bytes, cancellationToken) => new ValueTask(session.SendAsync(bytes, cancellationToken)));
        var result = await transfer.RunFileAsync(filePath, new(SendCR: true, SendLF: true));
        var expectedWireBytes = new byte[] { (byte)'A', 0x00, 0x0D, 0x0A, (byte)'B', (byte)'$', 0x0D, 0x0A };
        var actualWireBytes = new byte[expectedWireBytes.Length];
        await peer.GetStream().ReadExactlyAsync(actualWireBytes).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopAsync();

        Assert.Equal(Terminal2TextFileTransferStatus.Completed, result.Status);
        Assert.Equal(2, result.LinesSent);
        Assert.Equal(expectedWireBytes, actualWireBytes);
        var trace = await TraceReader.LoadAsync(tracePath);
        var transmitted = trace.Events.Where(item => item.Direction == ByteDirection.Tx).ToArray();
        Assert.Equal(2, transmitted.Length);
        Assert.Equal(new byte[] { (byte)'A', 0x00, 0x0D, 0x0A }, transmitted[0].Payload);
        Assert.Equal(new byte[] { (byte)'B', (byte)'$', 0x0D, 0x0A }, transmitted[1].Payload);

        await session.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }
}
