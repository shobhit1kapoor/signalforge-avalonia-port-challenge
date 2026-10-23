using System.Collections.Concurrent;
using SignalForge.Core.Protocols.Modbus;
using SignalForge.Core.Simulation;
using SignalForge.Core.Testing;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Simulation;

public sealed class VirtualDeviceServerTests
{
    [Fact]
    public async Task RealTcpServerReadsRegistersAndAppliesConfiguredDelay()
    {
        var events = new ConcurrentQueue<VirtualDeviceEventArgs>();
        await using var server = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = new([new(1, VirtualDeviceFaultKind.DelayResponse, TimeSpan.FromMilliseconds(35))])
        });
        server.EventOccurred += (_, args) => events.Enqueue(args);
        server.Device.SetHoldingRegister(12, 0xCAFE);
        await server.StartAsync();

        await using var transport = new TcpClientByteTransport(server.Host, server.Port);
        var request = ModbusTcpEncoder.Encode(41, 1, 3, [0, 12, 0, 1]);
        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("read register", [
                new SendBytesStep("send", request),
                new ExpectModbusTcpStep("response", TransactionId: 41, UnitId: 1, FunctionCode: 3)
            ]));

        Assert.True(result.Passed, string.Join("; ", result.Steps.Select(step => step.Detail)));
        Assert.Contains(events, item => item.Kind == VirtualDeviceEventKind.FaultApplied);
        var transitions = events.Where(item => item.Kind == VirtualDeviceEventKind.StateChanged).ToArray();
        Assert.Contains(transitions, item => item.PreviousState == VirtualDeviceServerState.Stopped && item.CurrentState == VirtualDeviceServerState.Starting);
        Assert.Contains(transitions, item => item.PreviousState == VirtualDeviceServerState.Starting && item.CurrentState == VirtualDeviceServerState.Listening);
        Assert.Contains(transitions, item => item.PreviousState == VirtualDeviceServerState.ClientConnected && item.CurrentState == VirtualDeviceServerState.Processing);
        Assert.All(transitions, item => Assert.NotNull(item.CurrentState));
        Assert.Equal((ushort)0xCAFE, server.Snapshot.HoldingRegisters[12]);
    }

    [Fact]
    public async Task DropFaultProducesTimeoutFailureAndEvidence()
    {
        var events = new ConcurrentQueue<VirtualDeviceEventArgs>();
        await using var server = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = new([new(1, VirtualDeviceFaultKind.DropResponse)])
        });
        server.EventOccurred += (_, args) => events.Enqueue(args);
        await server.StartAsync();
        await using var transport = new TcpClientByteTransport(server.Host, server.Port);
        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("drop response", [
                new SendBytesStep("send", ModbusTcpEncoder.Encode(1, 1, 3, [0, 0, 0, 1])),
                new ExpectModbusTcpStep("response timeout", ExpectedTimeout: TimeSpan.FromMilliseconds(150))
            ]));

        Assert.False(result.Passed);
        Assert.Contains(result.Steps, step => step.Detail.Contains("Timed out", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Kind == VirtualDeviceEventKind.ResponseDropped);
    }

    [Fact]
    public async Task CorruptAndDisconnectFaultsAreObservableByClient()
    {
        await using (var corruptServer = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = new([new(1, VirtualDeviceFaultKind.CorruptResponse, ByteOffset: 9)])
        }))
        {
            await corruptServer.StartAsync();
            await using var transport = new TcpClientByteTransport(corruptServer.Host, corruptServer.Port);
            var request = ModbusTcpEncoder.Encode(2, 1, 3, [0, 0, 0, 1]);
            var expected = ModbusTcpEncoder.Encode(2, 1, 3, [2, 0, 0]);
            var result = await new ProtocolTestRunner().RunAsync(transport,
                new ProtocolTestScenario("corrupt bytes", [
                    new SendBytesStep("send", request),
                    new ExpectBytesStep("match response", expected, ExpectedTimeout: TimeSpan.FromSeconds(1))
                ]));
            Assert.False(result.Passed);
            Assert.True(result.Steps.Single(step => !step.Passed).Observed.Length > 0);
        }

        await using var disconnectServer = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = new([new(1, VirtualDeviceFaultKind.Disconnect)])
        });
        await disconnectServer.StartAsync();
        await using var disconnectedTransport = new TcpClientByteTransport(disconnectServer.Host, disconnectServer.Port);
        var disconnectedResult = await new ProtocolTestRunner().RunAsync(disconnectedTransport,
            new ProtocolTestScenario("disconnect", [
                new SendBytesStep("send", ModbusTcpEncoder.Encode(3, 1, 3, [0, 0, 0, 1])),
                new ExpectModbusTcpStep("response absent", ExpectedTimeout: TimeSpan.FromSeconds(1))
            ]));
        Assert.False(disconnectedResult.Passed);
        Assert.Contains(disconnectedResult.Steps, step =>
            step.Detail.Contains("ended before the expected bytes arrived", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidFrameFaultIsReturnedExactlyAndExplainedByModbusDecoder()
    {
        var events = new ConcurrentQueue<VirtualDeviceEventArgs>();
        await using var server = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = new([new(1, VirtualDeviceFaultKind.InvalidFrame)])
        });
        server.EventOccurred += (_, args) => events.Enqueue(args);
        await server.StartAsync();

        var request = ModbusTcpEncoder.Encode(52, 1, 3, [0, 0, 0, 1]);
        var expected = ModbusTcpEncoder.Encode(52, 1, 3, [2, 0, 0]);
        expected[3] = 1;
        await using var transport = new TcpClientByteTransport(server.Host, server.Port);
        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("invalid MBAP identifier", [
                new SendBytesStep("send request", request),
                new ExpectBytesStep("receive deliberately invalid response", expected,
                    ExpectedTimeout: TimeSpan.FromSeconds(1))
            ]));

        Assert.True(result.Passed, string.Join("; ", result.Steps.Select(step => step.Detail)));
        var injected = Assert.Single(events, item => item.Kind == VirtualDeviceEventKind.FaultApplied);
        Assert.Equal(expected, injected.Payload.ToArray());
        Assert.Contains(events, item => item.Kind == VirtualDeviceEventKind.ResponseSent);

        var frame = Assert.Single(new ModbusTcpDecoder().Feed(injected.Payload.Span).Frames);
        Assert.False(frame.IsValid);
        Assert.Contains(frame.Issues, issue => issue.Kind == ModbusDecodeIssueKind.InvalidProtocolIdentifier);
        Assert.Equal((ushort)1, frame.ProtocolId);
    }
}
