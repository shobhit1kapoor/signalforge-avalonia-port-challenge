using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SignalForge.Core.Simulation;
using SignalForge.Core.Testing;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Testing;

public sealed class ProtocolTestReportTests
{
    [Fact]
    public void FactoryIncludesScenarioOutcomeByteEvidenceAndTimelineReferences()
    {
        var started = Stopwatch.GetTimestamp();
        var completed = started + Stopwatch.Frequency / 10;
        var request = new byte[] { 0x00, 0x01, 0x02 };
        var response = new byte[] { 0xA1, 0xB2 };
        var scenario = new ProtocolTestScenario("report example", [
            new SendBytesStep("send request", request),
            new ExpectBytesStep("read response", response, new byte[] { 0xFF, 0xF0 }, TimeSpan.FromMilliseconds(250)),
            new DelayTestStep("settle", TimeSpan.FromMilliseconds(12))
        ], disconnectWhenDone: false);
        var result = new ProtocolTestResult("report example", "loopback client", [
            new(1, "send request", true, "Sent 3 bytes.", request, request, started, started + 1),
            new(2, "read response", true, "Received the expected 2 bytes.", response, response, started + 2, completed)
        ], [
            new(1, "send request", ByteDirection.Tx, started + 1, request, "Sent bytes."),
            new(2, "read response", ByteDirection.Rx, completed, response, "Received transport chunk.")
        ], started, completed);
        var created = DateTimeOffset.Parse("2026-10-23T12:00:00Z");

        var report = ProtocolTestReportFactory.Create(scenario, result, "Local simulator", "127.0.0.1:5020", [
            new ProtocolTestTimelineReference("TEST #7", 1),
            new ProtocolTestTimelineReference("TEST #7", 2)
        ], created, "scenario-source-reference");

        Assert.Equal(ProtocolTestReportFactory.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal(ProtocolTestReportState.Passed, report.State);
        Assert.Equal("Local simulator", report.Source);
        Assert.Equal("scenario-source-reference", report.SourceReference);
        Assert.Equal("127.0.0.1:5020", report.Endpoint);
        Assert.Equal("report example", report.Scenario.Name);
        Assert.False(report.Scenario.DisconnectWhenDone);
        Assert.Collection(report.Scenario.Steps,
            send => Assert.Equal(Convert.ToBase64String(request), send.BytesBase64),
            expect =>
            {
                Assert.Equal("expectBytes", expect.Kind);
                Assert.Equal(250, expect.TimeoutMilliseconds);
                Assert.Equal(Convert.ToBase64String(response), expect.ExpectedBytesBase64);
                Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF, 0xF0 }), expect.MaskBase64);
            },
            delay => Assert.Equal(12, delay.DelayMilliseconds));
        Assert.Equal(2, report.StepResults.Count);
        Assert.Equal(2, report.Evidence.Count);
        Assert.Equal(Convert.ToBase64String(request), report.Evidence[0].BytesBase64);
        Assert.Equal("000102", report.Evidence[0].Hex);
        Assert.Equal(new ProtocolTestTimelineReference("TEST #7", 2), report.Evidence[1].Timeline);
    }

    [Fact]
    public async Task WriterAtomicallyReplacesTargetWithVersionedSelfContainedJson()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"signalforge-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "run.json");
            await File.WriteAllTextAsync(path, "old report");
            var report = CreateSampleReport();

            var writtenPath = await ProtocolTestReportWriter.WriteAsync(path, report);

            Assert.Equal(Path.GetFullPath(path), writtenPath);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = document.RootElement;
            Assert.Equal(ProtocolTestReportFactory.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("passed", root.GetProperty("state").GetString());
            var evidence = root.GetProperty("evidence")[0];
            Assert.Equal(Convert.ToBase64String(new byte[] { 0xCA, 0xFE }), evidence.GetProperty("bytesBase64").GetString());
            Assert.Equal("CAFE", evidence.GetProperty("hex").GetString());
            Assert.Equal("TEST #4", evidence.GetProperty("timeline").GetProperty("source").GetString());
            Assert.Equal(9, evidence.GetProperty("timeline").GetProperty("eventSequence").GetInt64());
            Assert.Equal("modbusTcpLoopback", root.GetProperty("sourceConfiguration").GetProperty("kind").GetString());
            Assert.Equal(1, root.GetProperty("sourceConfiguration").GetProperty("unitId").GetByte());
            Assert.Equal("corruptResponse", root.GetProperty("sourceConfiguration").GetProperty("faults")[0].GetProperty("kind").GetString());
            Assert.Equal(9, root.GetProperty("sourceConfiguration").GetProperty("faults")[0].GetProperty("byteOffset").GetInt32());
            Assert.Equal(0x1234, root.GetProperty("sourceConfiguration").GetProperty("initialDeviceState").GetProperty("holdingRegisters").GetProperty("0").GetUInt16());
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task WriterReportsMissingParentFolderClearly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"signalforge-missing-{Guid.NewGuid():N}", "run.json");

        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => ProtocolTestReportWriter.WriteAsync(missing, CreateSampleReport()));

        Assert.Contains("report folder does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryMarksFailedAssertionAsFailed()
    {
        var now = Stopwatch.GetTimestamp();
        var scenario = new ProtocolTestScenario("mismatch", [new ExpectBytesStep("expect", new byte[] { 0x01 })]);
        var result = new ProtocolTestResult("mismatch", "fixture", [
            new(1, "expect", false, "Observed byte differed.", new byte[] { 0x01 }, new byte[] { 0x02 }, now, now)
        ], [], now, now);

        var report = ProtocolTestReportFactory.Create(scenario, result, "Replay", "trace:fixture");

        Assert.Equal(ProtocolTestReportState.Failed, report.State);
        Assert.False(report.StepResults[0].Passed);
        Assert.Equal(Convert.ToBase64String(new byte[] { 0x02 }), report.StepResults[0].ObservedBytesBase64);
    }

    [Fact]
    public async Task ReportRunnerReturnsCapturedPartialEvidenceOnCancellation()
    {
        await using var transport = new PartialThenBlockingTransport();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = new byte[] { 0x01, 0x02 };
        var scenario = new ProtocolTestScenario("cancelled run", [
            new SendBytesStep("send", request),
            new ExpectBytesStep("partial receive", new byte[] { 0xCA, 0xFE }, ExpectedTimeout: TimeSpan.FromSeconds(3))
        ]);

        var result = await new ProtocolTestRunner().RunForReportAsync(transport, scenario, cancellation.Token);
        var report = ProtocolTestReportFactory.Create(scenario, result, "Offline fixture", transport.Endpoint);

        Assert.True(result.Cancelled);
        Assert.False(result.Passed);
        Assert.Equal(ProtocolTestReportState.Cancelled, report.State);
        Assert.Contains(report.Evidence, item => item.Direction == ByteDirection.Tx && item.BytesBase64 == Convert.ToBase64String(request));
        Assert.Contains(report.Evidence, item => item.Direction == ByteDirection.Rx && item.BytesBase64 == Convert.ToBase64String(new byte[] { 0xCA }));
    }

    private static ProtocolTestRunReport CreateSampleReport()
    {
        var now = Stopwatch.GetTimestamp();
        var bytes = new byte[] { 0xCA, 0xFE };
        var scenario = new ProtocolTestScenario("sample", [new SendBytesStep("send", bytes)]);
        var result = new ProtocolTestResult("sample", "fixture", [
            new(1, "send", true, "sent", bytes, bytes, now, now)
        ], [
            new(1, "send", ByteDirection.Tx, now, bytes, "sent")
        ], now, now);
        var initialState = new VirtualDeviceSnapshot(
            1,
            new Dictionary<ushort, bool>(),
            new Dictionary<ushort, bool>(),
            new Dictionary<ushort, ushort> { [0] = 0x1234 },
            new Dictionary<ushort, ushort>());
        var sourceConfiguration = new ProtocolTestSourceConfiguration("modbusTcpLoopback", 1, [
            new ProtocolTestFaultConfiguration(VirtualDeviceFaultKind.CorruptResponse, RequestNumber: 1, ByteOffset: 9, XorMask: 1)
        ], initialState);
        return ProtocolTestReportFactory.Create(scenario, result, "Simulator", "fixture://sample", [
            new ProtocolTestTimelineReference("TEST #4", 9)
        ], sourceConfiguration: sourceConfiguration);
    }

    private sealed class PartialThenBlockingTransport : IByteTransport
    {
        private TransportState _state = TransportState.Disconnected;

        public string DisplayName => "Partial receive fixture";
        public string Endpoint => "fixture://partial-receive";
        public TransportKind Kind => TransportKind.Simulator;
        public TransportState State => _state;
        public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(TransportState.Connected, "Connected.");
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<ByteChunk> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ByteChunk(Stopwatch.GetTimestamp(), new byte[] { 0xCA });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(TransportState.Disconnected, "Disconnected.");
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
