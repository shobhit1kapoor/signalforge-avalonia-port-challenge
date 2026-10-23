using SignalForge.Desktop.ViewModels;

namespace SignalForge.Core.Tests.Desktop;

public sealed class ProtocolTestingLrcIntegrationTests
{
    [Fact]
    public async Task Terminal2SimulatorPassesValidFrameAndReportsBadLrcAsFailure()
    {
        var viewModel = new ProtocolTestingViewModel();
        var completedRuns = new List<ProtocolTestRunCompletedEventArgs>();
        viewModel.RunCompleted += (_, args) => completedRuns.Add(args);

        viewModel.LoadStxEtxLrcExampleCommand.Execute(null);
        Assert.True(viewModel.IsTerminal2SimulatorSource);
        await viewModel.RunTestCommand.ExecuteAsync(null);

        Assert.Equal("PASS", viewModel.RunState);
        var goodLrc = Assert.Single(viewModel.Results, row => row.Name == "Validate Terminal2 LRC");
        Assert.True(goodLrc.Passed);
        Assert.Contains("Valid Terminal2 LRC", goodLrc.Detail);
        Assert.Contains(viewModel.Evidence, row => row.Direction == SignalForge.Core.Transport.ByteDirection.Tx);
        Assert.Contains(viewModel.Evidence, row => row.Direction == SignalForge.Core.Transport.ByteDirection.Rx);

        viewModel.SelectedSimulatorFaultPlan = "Corrupt Terminal2 LRC on response 1";
        await viewModel.RunTestCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", viewModel.RunState);
        var badLrc = Assert.Single(viewModel.Results, row => row.Name == "Validate Terminal2 LRC");
        Assert.False(badLrc.Passed);
        Assert.Contains("stxetx.bad-lrc", badLrc.Detail);
        Assert.Contains("LRC mismatch", badLrc.Detail);
        Assert.Equal(2, completedRuns.Count);
        Assert.True(completedRuns[0].Result.Passed);
        Assert.False(completedRuns[1].Result.Passed);
        Assert.Contains(completedRuns[1].Result.Evidence, item => item.Direction == SignalForge.Core.Transport.ByteDirection.Rx);
    }
}
