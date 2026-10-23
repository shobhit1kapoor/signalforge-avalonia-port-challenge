using System.Text.Json;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Core.Tests.Forensics;

public sealed class TraceComparisonViewModelTests
{
    [Fact]
    public async Task CompareCommandSurfacesFirstByteAndTimingDivergenceWithTraceReferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SignalForge-trace-comparison", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var expectedPath = Path.Combine(directory, "expected.jsonl");
        var actualPath = Path.Combine(directory, "actual.jsonl");

        try
        {
            const long expectedFrequency = 10_000;
            const long actualFrequency = 1_000_000;
            await WriteTraceAsync(expectedPath, "expected", expectedFrequency,
            [
                new(TraceEventKind.Connection, "connected", 0, null, null),
                new(TraceEventKind.Transmit, "read request", expectedFrequency, [0x01, 0x03], ByteDirection.Tx),
                new(TraceEventKind.Receive, "read response", expectedFrequency * 3, [0x11], ByteDirection.Rx)
            ]);
            await WriteTraceAsync(actualPath, "actual", actualFrequency,
            [
                new(TraceEventKind.Connection, "connected", 0, null, null),
                new(TraceEventKind.Transmit, "read request", actualFrequency * 2, [0x01, 0x04], ByteDirection.Tx),
                new(TraceEventKind.Receive, "read response", actualFrequency * 3, [0x11], ByteDirection.Rx)
            ]);

            var viewModel = new TraceComparisonViewModel
            {
                ExpectedTracePath = expectedPath,
                ActualTracePath = actualPath
            };
            await viewModel.CompareCommand.ExecuteAsync(null);

            Assert.Contains("FIRST DIVERGENCE · Observation 1", viewModel.FirstDivergenceSummary);
            Assert.Contains("expected.jsonl · #2", viewModel.FirstDivergenceSummary);
            Assert.Contains("actual.jsonl · #2", viewModel.FirstDivergenceSummary);
            Assert.Contains("offset 1", viewModel.FirstDivergenceSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FIRST TIMING DIVERGENCE · Observation 1", viewModel.FirstTimingDivergenceSummary);
            Assert.Contains("+1000.00 ms", viewModel.FirstTimingDivergenceSummary);
            Assert.Contains("actual.jsonl · #2", viewModel.FirstTimingDivergenceSummary);
            Assert.Contains("payload first differs at offset 1: 0x03 → 0x04", viewModel.RootCauseHint);
            Assert.Contains("actual event is later by 1,000.00 ms", viewModel.RootCauseHint);
            Assert.Contains("do not identify the responsible device, transport, or application layer", viewModel.RootCauseHint);
            Assert.Equal(100d / 3d, viewModel.Rows[1].ExpectedTimelinePercent, 6);
            Assert.Equal(200d / 3d, viewModel.Rows[1].ActualTimelinePercent, 6);
            Assert.Equal("1,000.00 ms", viewModel.Rows[1].ExpectedTimeLabel);
            Assert.Equal("2,000.00 ms", viewModel.Rows[1].ActualTimeLabel);
            Assert.Equal("UNCHANGED", viewModel.Rows[2].State);
            Assert.Equal("3,000.00 ms", viewModel.Rows[2].ExpectedTimeLabel);
            Assert.Contains("2 unchanged · 1 changed", viewModel.Status);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingExpectedEventHintSeparatesTraceEvidenceFromPossibleCaptureGap()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SignalForge-trace-comparison", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var expectedPath = Path.Combine(directory, "expected.jsonl");
        var actualPath = Path.Combine(directory, "actual.jsonl");

        try
        {
            var frequency = System.Diagnostics.Stopwatch.Frequency;
            await WriteTraceAsync(expectedPath, "expected", frequency,
            [
                new(TraceEventKind.Connection, "connected", 0, null, null),
                new(TraceEventKind.Receive, "reply", frequency, [0x01, 0x03], ByteDirection.Rx)
            ]);
            await WriteTraceAsync(actualPath, "actual", frequency,
            [
                new(TraceEventKind.Connection, "connected", 0, null, null)
            ]);

            var viewModel = new TraceComparisonViewModel
            {
                ExpectedTracePath = expectedPath,
                ActualTracePath = actualPath
            };
            await viewModel.CompareCommand.ExecuteAsync(null);

            Assert.Contains("reply is present", viewModel.RootCauseHint);
            Assert.Contains("no aligned event", viewModel.RootCauseHint);
            Assert.Contains("cannot distinguish device silence from a capture gap", viewModel.RootCauseHint);
            Assert.Equal(100d, viewModel.Rows[1].ExpectedTimelinePercent);
            Assert.Equal(0d, viewModel.Rows[1].ActualTimelinePercent);
            Assert.Equal("—", viewModel.Rows[1].ActualTimeLabel);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteTraceAsync(
        string path,
        string sessionName,
        long frequency,
        IReadOnlyList<(TraceEventKind Kind, string Detail, long Ticks, byte[]? Payload, ByteDirection? Direction)> events)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var metadata = new TraceMetadata(sessionName, "TcpClient", "127.0.0.1:1", DateTimeOffset.UtcNow, frequency);
        await using var writer = new StreamWriter(path, append: false);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new TraceLine(1, "header", metadata, null), jsonOptions));
        long sequence = 0;
        foreach (var item in events)
        {
            var traceEvent = new TraceEvent(
                ++sequence,
                item.Ticks,
                DateTimeOffset.UtcNow,
                item.Kind,
                item.Payload,
                item.Direction,
                "127.0.0.1:1",
                item.Detail);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new TraceLine(1, "event", null, traceEvent), jsonOptions));
        }
    }

    private sealed record TraceLine(int SchemaVersion, string Type, TraceMetadata? Metadata, TraceEvent? Event);
}
