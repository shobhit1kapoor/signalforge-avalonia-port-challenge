using System.IO.Ports;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Transport;

public sealed class SerialPortByteTransportTests
{
    [Fact]
    public async Task DisplayNameUsesConfiguredSerialFraming()
    {
        await using var transport = new SerialPortByteTransport(new SerialTransportOptions(
            PortName: "COM12",
            BaudRate: 57_600,
            DataBits: 7,
            Parity: Parity.Even,
            StopBits: StopBits.Two));

        Assert.Equal("Serial · COM12 · 57600 7E2", transport.DisplayName);
    }
}
