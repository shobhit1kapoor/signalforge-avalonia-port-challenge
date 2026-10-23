using System.Text;
using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Traces;
using SignalForge.Core.Transport;
using SignalForge.Desktop.ViewModels;

namespace SignalForge.Core.Tests.Desktop;

public sealed class Terminal2ReceiveHistoryViewModelTests
{
    [Fact]
    public void Reassembles_fragmented_lines_applies_filters_and_searches_complete_history()
    {
        var filters = new Terminal2FiltersViewModel();
        filters.Slots[0].IsEnabled = true;
        filters.Slots[0].Text = "FAULT";
        var viewModel = new Terminal2ReceiveHistoryViewModel(filters);

        viewModel.Append("LIVE #1", Receive(1, 10, "FAU"));
        Assert.Equal("FAU", viewModel.CurrentLinePreview);
        Assert.Empty(viewModel.Lines);

        viewModel.Append("LIVE #1", Receive(2, 20, "LT\r"));
        Assert.Equal("FAULT\\x0D", viewModel.CurrentLinePreview);
        viewModel.Append("LIVE #1", Receive(3, 30, "\nOK\n"));

        Assert.Equal(2, viewModel.Lines.Count);
        Assert.Equal("FAULT", viewModel.Lines[0].Text);
        Assert.Equal("CRLF", viewModel.Lines[0].EndingLabel);
        Assert.Equal("46 41 55 4C 54 0D 0A", viewModel.Lines[0].Hex);
        Assert.Equal("F01", viewModel.Lines[0].FilterLabel);
        Assert.Equal("OK", viewModel.Lines[1].Text);
        Assert.Equal("LF", viewModel.Lines[1].EndingLabel);

        viewModel.SearchText = "ok";
        viewModel.SearchNextCommand.Execute(null);
        Assert.Same(viewModel.Lines[1], viewModel.SelectedLine);
        viewModel.SearchText = "FAULT";
        viewModel.SearchPreviousCommand.Execute(null);
        Assert.Same(viewModel.Lines[0], viewModel.SelectedLine);
    }

    [Fact]
    public void Profile_filter_macro_triggers_once_per_live_completed_line_and_uses_first_match()
    {
        var profile = new Terminal2Profile();
        profile.Filters[0].Mode = 1;
        profile.Filters[0].Text = "READY";
        profile.Filters[0].Macro = "Reply";
        profile.Filters[1].Mode = 1;
        profile.Filters[1].Text = "READY";
        profile.Filters[1].Macro = "Second reply";

        var filters = new Terminal2FiltersViewModel();
        filters.LoadProfile(profile);
        Assert.Equal("Reply", filters.Slots[0].MacroTitle);
        var viewModel = new Terminal2ReceiveHistoryViewModel(filters);
        var triggers = new List<(string Title, int Slot)>();
        viewModel.FilterMacroTriggered += (title, slot) => triggers.Add((title, slot));

        viewModel.Append("LIVE #1", Receive(1, 10, "REA"), triggerProfileMacros: true);
        viewModel.Append("LIVE #1", Receive(2, 20, "DY\r"), triggerProfileMacros: true);
        Assert.Empty(triggers);
        viewModel.Append("LIVE #1", Receive(3, 30, "\nOTHER\nREADY\r\n"), triggerProfileMacros: true);

        Assert.Equal(new[] { ("Reply", 0), ("Reply", 0) }, triggers);
        Assert.Equal(new[] { "READY", "OTHER", "READY" }, viewModel.Lines.Select(line => line.Text));

        viewModel.Append("TRACE #1", Receive(4, 40, "READY\n"));
        Assert.Equal(2, triggers.Count);
    }

    [Fact]
    public void Filter_macro_title_edits_update_the_loaded_profile()
    {
        var profile = new Terminal2Profile();
        profile.Filters[0].Macro = "Old title";
        var filters = new Terminal2FiltersViewModel();
        filters.LoadProfile(profile);

        filters.Slots[0].MacroTitle = "New title";

        Assert.Equal("New title", profile.Filters[0].Macro);
    }

    [Fact]
    public void Stop_at_freezes_only_the_line_display_and_resume_flushes_queued_lines()
    {
        var viewModel = new Terminal2ReceiveHistoryViewModel(new Terminal2FiltersViewModel())
        {
            StopAtEnabled = true,
            StopAtText = "STOP HERE"
        };

        viewModel.Append("LIVE #1", Receive(1, 10, "before\nSTOP HERE\n"));

        Assert.True(viewModel.IsFrozen);
        Assert.Equal(new[] { "before", "STOP HERE" }, viewModel.Lines.Select(line => line.Text));
        Assert.Contains("Stop-at matched", viewModel.Status, StringComparison.Ordinal);

        viewModel.Append("LIVE #1", Receive(2, 20, "after one\nafter two\n"));
        Assert.Equal(2, viewModel.Lines.Count);
        Assert.Contains("2 PENDING", viewModel.LineCountText, StringComparison.Ordinal);

        viewModel.ToggleFreezeCommand.Execute(null);

        Assert.False(viewModel.IsFrozen);
        Assert.Equal(new[] { "before", "STOP HERE", "after one", "after two" }, viewModel.Lines.Select(line => line.Text));
        Assert.Contains("Display resumed", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_freeze_keeps_partial_preview_stable_until_resume()
    {
        var viewModel = new Terminal2ReceiveHistoryViewModel(new Terminal2FiltersViewModel());
        viewModel.Append("LIVE #1", Receive(1, 10, "one\npart"));
        Assert.Equal("part", viewModel.CurrentLinePreview);

        viewModel.ToggleFreezeCommand.Execute(null);
        viewModel.Append("LIVE #1", Receive(2, 20, "ial\n"));

        Assert.Equal("part", viewModel.CurrentLinePreview);
        Assert.Equal(new[] { "one" }, viewModel.Lines.Select(line => line.Text));
        Assert.Contains("1 PENDING", viewModel.LineCountText, StringComparison.Ordinal);

        viewModel.ToggleFreezeCommand.Execute(null);

        Assert.Equal(new[] { "one", "partial" }, viewModel.Lines.Select(line => line.Text));
        Assert.Equal("No incomplete receive line.", viewModel.CurrentLinePreview);
    }

    private static TraceEvent Receive(long sequence, long ticks, string text) => new(
        sequence,
        ticks,
        DateTimeOffset.UnixEpoch.AddMilliseconds(ticks),
        TraceEventKind.Receive,
        Encoding.Latin1.GetBytes(text),
        ByteDirection.Rx,
        "loopback:test");
}
