/*
 * Compatibility tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Protocols.Terminal2;

public sealed class Terminal2SendTextParserTests
{
    [Fact]
    public void Parses_utf8_dollar_bytes_and_escaped_dollar_in_source_order()
    {
        var result = Terminal2SendTextParser.Parse("Hi$$/$0D$ffé");

        Assert.True(result.IsValid);
        Assert.Equal(new byte[] { (byte)'H', (byte)'i', (byte)'$', (byte)'/', 0x0D, 0xFF, 0xC3, 0xA9 }, result.Value);
    }

    [Fact]
    public void Double_dollar_is_literal_and_does_not_start_a_hex_escape()
    {
        var result = Terminal2SendTextParser.Parse("$$AB");

        Assert.True(result.IsValid);
        Assert.Equal(new byte[] { (byte)'$', (byte)'A', (byte)'B' }, result.Value);
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$0")]
    [InlineData("$XZ")]
    [InlineData("$A?")]
    public void Malformed_dollar_escapes_are_rejected_with_a_visible_error(string text)
    {
        var result = Terminal2SendTextParser.Parse(text);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
        Assert.StartsWith("send-text.", result.Issues[0].Code, StringComparison.Ordinal);
        Assert.Equal(0, result.Issues[0].CharacterOffset);
    }
}
