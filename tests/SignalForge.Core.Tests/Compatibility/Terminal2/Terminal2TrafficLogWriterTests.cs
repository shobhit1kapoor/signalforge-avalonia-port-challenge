using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2TrafficLogWriterTests
{
    [Fact]
    public async Task Writer_RecordsExactTxAndRxHexAndClosesCleanly()
    {
        var directory = Directory.CreateTempSubdirectory("signalforge-terminal2-log-");
        try
        {
            await using var writer = await Terminal2TrafficLogWriter.StartAsync(directory.FullName, "fixture/");
            var path = writer.CurrentFilePath;
            Assert.True(writer.TryAppend(new TraceEvent(1, 10, DateTimeOffset.UtcNow, TraceEventKind.Transmit, [0x00, 0xAF])));
            Assert.True(writer.TryAppend(new TraceEvent(2, 20, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x80, 0xFF])));

            await writer.StopAsync();
            var contents = await File.ReadAllTextAsync(path);

            Assert.Contains("TX\t00AF", contents, StringComparison.Ordinal);
            Assert.Contains("RX\t80FF", contents, StringComparison.Ordinal);
            Assert.Contains("# -- END", contents, StringComparison.Ordinal);
            Assert.EndsWith(".log", path, StringComparison.Ordinal);
            Assert.StartsWith(directory.FullName, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(writer.TryAppend(new TraceEvent(3, 30, DateTimeOffset.UtcNow, TraceEventKind.Receive, [0x01])));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Writer_RotatesWithoutLosingQueuedEventsAtConfiguredMaximumSize()
    {
        var directory = Directory.CreateTempSubdirectory("signalforge-terminal2-log-rotation-");
        try
        {
            await using var writer = await Terminal2TrafficLogWriter.StartAsync(directory.FullName, "bounded_", maximumFileSize: 150);
            var timestamp = DateTimeOffset.UtcNow;
            for (var index = 1; index <= 3; index++)
            {
                var bytes = Enumerable.Range(index * 16, 10).Select(value => (byte)value).ToArray();
                Assert.True(writer.TryAppend(new TraceEvent(index, index, timestamp, TraceEventKind.Receive, bytes)));
            }

            await writer.StopAsync();
            var paths = Directory.GetFiles(directory.FullName, "*.log");
            Assert.True(paths.Length >= 2);
            var contents = string.Join("\n", await Task.WhenAll(paths.Select(path => File.ReadAllTextAsync(path))));
            Assert.Contains("10111213141516171819", contents, StringComparison.Ordinal);
            Assert.Contains("20212223242526272829", contents, StringComparison.Ordinal);
            Assert.Contains("30313233343536373839", contents, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Writer_RejectsMissingDirectoryInsteadOfCreatingAnUnexpectedPath()
    {
        var root = Directory.CreateTempSubdirectory("signalforge-terminal2-log-missing-");
        var missingDirectory = Path.Combine(root.FullName, "not-created");
        try
        {
            await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
                await Terminal2TrafficLogWriter.StartAsync(missingDirectory, "fixture_"));
            Assert.False(Directory.Exists(missingDirectory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
