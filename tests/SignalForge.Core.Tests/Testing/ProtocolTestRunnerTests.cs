using SignalForge.Core.Protocols.Modbus;
using SignalForge.Core.Simulation;
using SignalForge.Core.Testing;
using SignalForge.Core.Transport;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SignalForge.Core.Tests.Testing;

public sealed class ProtocolTestRunnerTests
{
    [Fact]
    public async Task RunnerCapturesTxRxAndHonorsMaskedByteAssertions()
    {
        await using var server = new VirtualDeviceServer();
        server.Device.SetHoldingRegister(0, 0x12ab);
        await server.StartAsync();
        await using var transport = new TcpClientByteTransport(server.Host, server.Port);
        var request = ModbusTcpEncoder.Encode(9, 1, 3, [0, 0, 0, 1]);
        var expected = ModbusTcpEncoder.Encode(9, 1, 3, [2, 0x12, 0]);
        var mask = Enumerable.Repeat((byte)0xff, expected.Length).ToArray();
        mask[10] = 0x00; // Ignore only the register's low byte.
        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("masked read", [
                new SendBytesStep("request", request),
                new ExpectBytesStep("response", expected, mask, TimeSpan.FromSeconds(1))
            ]));

        Assert.True(result.Passed, string.Join("; ", result.Steps.Select(step => step.Detail)));
        Assert.Contains(result.Evidence, item => item.Direction == ByteDirection.Tx && item.Bytes.Span.SequenceEqual(request));
        Assert.Contains(result.Evidence, item => item.Direction == ByteDirection.Rx && item.Bytes.Length > 0);
    }

    [Fact]
    public async Task RunnerStopsAtFirstFailedAssertionAndDoesNotInventLaterSteps()
    {
        await using var server = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = new([new(1, VirtualDeviceFaultKind.DropResponse)])
        });
        await server.StartAsync();
        await using var transport = new TcpClientByteTransport(server.Host, server.Port);
        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("bounded", [
                new SendBytesStep("send", ModbusTcpEncoder.Encode(1, 1, 3, [0, 0, 0, 1])),
                new ExpectModbusTcpStep("wait", ExpectedTimeout: TimeSpan.FromMilliseconds(100)),
                new SendBytesStep("not reached", new byte[] { 1 })
            ]));

        Assert.False(result.Passed);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(1, result.FailedStepCount);
    }

    [Theory]
    [InlineData(VirtualDeviceFaultKind.InvalidFrame, 0, "protocol identifier")]
    [InlineData(VirtualDeviceFaultKind.CorruptResponse, 8, "byte-count")]
    public async Task ModbusTcpExpectationRejectsInvalidProtocolAndMalformedResponsePdu(
        VirtualDeviceFaultKind faultKind, int byteOffset, string expectedDiagnostic)
    {
        await using var server = new VirtualDeviceServer(new VirtualDeviceServerOptions
        {
            FaultPlan = new([new(1, faultKind, ByteOffset: byteOffset)])
        });
        await server.StartAsync();
        await using var transport = new TcpClientByteTransport(server.Host, server.Port);
        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("validate Modbus response", [
                new SendBytesStep("send read", ModbusTcpEncoder.Encode(7, 1, 3, [0, 0, 0, 1])),
                new ExpectModbusTcpStep("validate response", ExpectedTimeout: TimeSpan.FromSeconds(1))
            ]));

        Assert.False(result.Passed);
        var failure = Assert.Single(result.Steps, step => !step.Passed);
        Assert.Contains(expectedDiagnostic, failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunnerSurfacesTransportReaderFailureInExpectationResult()
    {
        await using var transport = new FailingReadTransport();

        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("reader failure", [
                new ExpectBytesStep("receive", new byte[] { 0xca, 0xfe }, ExpectedTimeout: TimeSpan.FromSeconds(1))
            ]));

        var failedStep = Assert.Single(result.Steps);
        Assert.False(failedStep.Passed);
        Assert.Contains("Transport receive failed", failedStep.Detail, StringComparison.Ordinal);
        Assert.Contains("synthetic receive failure", failedStep.Detail, StringComparison.Ordinal);
        Assert.Contains(result.Evidence, item => item.Direction == ByteDirection.Rx && item.Bytes.Span.SequenceEqual(new byte[] { 0xca }));
    }

    [Fact]
    public async Task SendOnlyScenarioFailsWhenReceivePumpFaults()
    {
        await using var transport = new FailingReadTransport();

        var result = await new ProtocolTestRunner().RunAsync(transport,
            new ProtocolTestScenario("send only with reader failure", [
                new SendBytesStep("send", new byte[] { 0x01, 0x02 })
            ]));

        Assert.False(result.Passed);
        Assert.Contains(result.Steps, step => !step.Passed
            && step.Detail.Contains("synthetic receive failure", StringComparison.Ordinal));
    }

    private sealed class FailingReadTransport : IByteTransport
    {
        private TransportState _state = TransportState.Disconnected;
        private readonly TaskCompletionSource _readerFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string DisplayName => "Failing read fixture";
        public string Endpoint => "fixture://reader-failure";
        public TransportKind Kind => TransportKind.Simulator;
        public TransportState State => _state;
        public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(TransportState.Connected, "Fixture connected.");
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<ByteChunk> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ByteChunk(Stopwatch.GetTimestamp(), new byte[] { 0xca });
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("synthetic receive failure");
            }
            finally
            {
                _readerFailed.TrySetResult();
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            await _readerFailed.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(TransportState.Disconnected, "Fixture disconnected.");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void SetState(TransportState state, string detail)
        {
            var previous = _state;
            _state = state;
            StateChanged?.Invoke(this, new TransportStateChangedEventArgs(previous, state, detail));
        }
    }
}
