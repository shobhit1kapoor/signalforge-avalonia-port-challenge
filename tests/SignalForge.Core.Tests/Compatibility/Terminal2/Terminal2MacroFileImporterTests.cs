/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Compatibility.Terminal2;

public sealed class Terminal2MacroFileImporterTests
{
    [Fact]
    public void V2_tmf_pairs_match_terminal2_import_fields_and_new_profile_defaults()
    {
        const string text = "# Terminal macro file v2 - export\r\nStart motor\r\n$41$0D\r\nStop motor\r\n$42\r\nUnpaired title\r\n";

        var result = Terminal2MacroFileImporter.Import(text, "Imported macros");

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        var profile = result.Value!;
        Assert.Equal("Imported macros", profile.Name);
        Assert.Equal(Terminal2ConnectionType.TCPServer, profile.ConnectionType);
        Assert.Equal(@"C:\Temp", profile.LogDirectory);
        Assert.True(profile.ShowAscii);
        Assert.True(profile.ShowInputTimestamp);
        Assert.Equal("ERROR", profile.Filters[10].Text);
        Assert.Equal(1, profile.Filters[10].Mode);
        Assert.Equal("Start motor", profile.Macros[0]!.Title);
        Assert.Equal("$41", profile.Macros[0]!.Text);
        Assert.True(profile.Macros[0]!.AddCR);
        Assert.False(profile.Macros[0]!.AddLF);
        Assert.Equal(0, profile.Macros[0]!.DelayBetweenLinesMs);
        Assert.Equal("Stop motor", profile.Macros[1]!.Title);
        Assert.Equal("$42", profile.Macros[1]!.Text);
        Assert.False(profile.Macros[1]!.AddCR);
        Assert.Equal(string.Empty, profile.Macros[2]!.Title);
    }

    [Fact]
    public void Tmf_with_invalid_header_fails_visibly_without_creating_a_profile()
    {
        var result = Terminal2MacroFileImporter.Import("not a tmf\nTitle\nText", "Broken");

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue => issue.Code == "tmf.invalid-header");
    }

    [Fact]
    public void Trailing_line_terminator_does_not_create_an_extra_empty_macro_pair()
    {
        var result = Terminal2MacroFileImporter.Import("# Terminal macro file v2\nOnly a title\n", "Odd");

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.Value!.Macros[0]!.Title);
    }
}
