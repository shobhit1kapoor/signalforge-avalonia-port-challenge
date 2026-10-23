using RoyalTerminal.Avalonia.Controls;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;
using SignalForge.Desktop.Services;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Core.Tests.Desktop;

public sealed class ExplorerRecentActivityTests
{
    [Fact]
    public async Task Explorer_activity_shows_newest_five_events_and_selects_the_timeline_row()
    {
        var path = Path.Combine(Path.GetTempPath(), $"signalforge-explorer-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var recorder = await TraceRecorder.StartAsync(path, new TraceMetadata(
                "Explorer test", "TCP client", "127.0.0.1:502", DateTimeOffset.UtcNow, 0)))
            {
                await recorder.AppendAsync(TraceEventKind.Connection, "Connected", endpoint: "127.0.0.1:502");
                for (var index = 1; index <= 6; index++)
                {
                    await recorder.AppendBytesAsync(
                        index % 2 == 0 ? ByteDirection.Rx : ByteDirection.Tx,
                        new byte[] { (byte)index },
                        "127.0.0.1:502");
                }
                await recorder.StopAsync();
            }

            var viewModel = new MainViewModel(new NoopTerminalHost()) { TraceFilePath = path };
            await viewModel.LoadTraceCommand.ExecuteAsync(null);

            Assert.Equal(7, viewModel.Timeline.Count);
            Assert.Equal(5, viewModel.RecentTimeline.Count);
            Assert.Equal(new long[] { 7, 6, 5, 4, 3 }, viewModel.RecentTimeline.Select(row => row.Event.Sequence));
            Assert.Same(viewModel.Timeline[^1], viewModel.RecentTimeline[0]);
            Assert.Same(viewModel.RecentTimeline[0], viewModel.SelectedTraceRow);
            Assert.Contains("select one to inspect it", viewModel.RecentTimelineStatus, StringComparison.Ordinal);

            await viewModel.ShutdownAsync();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path + ".partial"))
                File.Delete(path + ".partial");
        }
    }

    private sealed class NoopTerminalHost : ILocalTerminalSessionHost
    {
        public void Attach(TerminalControl control) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }
    }
}
