/*
 * STX/ETX/DLE/LRC reference tests derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Protocols.Terminal2;

namespace SignalForge.Core.Tests.Protocols.Terminal2;

public sealed class Terminal2StxEtxCodecTests
{
    // Reference calculation: Terminal2 Encode("A") -> $02A$03$42.
    [Fact]
    public void Plain_ascii_vector_matches_terminal2_wire_bytes()
    {
        var frame = Terminal2StxEtxCodec.Encode(new byte[] { 0x41 });

        Assert.Equal(new byte[] { 0x02, 0x41, 0x03, 0x42 }, frame);
        Assert.True(Terminal2StxEtxCodec.Verify(frame));
        Assert.Equal(new byte[] { 0x41 }, Terminal2StxEtxCodec.Decode(frame).Value);
    }

    // Reference calculation: input $02$03$10 is stuffed as
    // $10$02$10$03$10$10, then ETX and XOR LRC 0x02 are appended.
    [Fact]
    public void Control_bytes_are_dle_stuffed_and_included_in_lrc()
    {
        var frame = Terminal2StxEtxCodec.Encode(new byte[] { 0x02, 0x03, 0x10 });

        Assert.Equal(new byte[] { 0x02, 0x10, 0x02, 0x10, 0x03, 0x10, 0x10, 0x03, 0x02 }, frame);
        var decoded = Terminal2StxEtxCodec.Decode(frame);
        Assert.True(decoded.IsValid, string.Join(Environment.NewLine, decoded.Issues));
        Assert.Equal(new byte[] { 0x02, 0x03, 0x10 }, decoded.Value);
    }

    [Fact]
    public void Bad_lrc_is_reported_with_expected_and_received_bytes()
    {
        var frame = Terminal2StxEtxCodec.Encode(new byte[] { 0x41 });
        frame[^1] ^= 0x01;

        var decoded = Terminal2StxEtxCodec.Decode(frame);

        Assert.False(decoded.IsValid);
        Assert.Equal("stxetx.bad-lrc", decoded.Issues[0].Code);
        Assert.Contains("expected 0x42", decoded.Issues[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_encoding_uses_the_same_send_escape_parser()
    {
        var result = Terminal2StxEtxCodec.EncodeText("A$02");

        Assert.True(result.IsValid);
        Assert.Equal(new byte[] { 0x02, 0x41, 0x10, 0x02, 0x03, 0x50 }, result.Value);
    }

    [Fact]
    public void Stream_decoder_reassembles_fragments_and_emits_multiple_frames()
    {
        var firstFrame = Terminal2StxEtxCodec.Encode(new byte[] { 0x41, 0x02 });
        var secondFrame = Terminal2StxEtxCodec.Encode(new byte[] { 0x42, 0x03 });
        var decoder = new Terminal2StxEtxStreamDecoder();

        Assert.Empty(decoder.Feed(firstFrame.AsSpan(0, 2)));
        var completed = decoder.Feed(firstFrame.AsSpan(2).ToArray().Concat(secondFrame).ToArray());

        Assert.Equal(2, completed.Count);
        Assert.All(completed, frame => Assert.True(frame.IsValid, string.Join(Environment.NewLine, frame.Issues)));
        Assert.Equal(new byte[] { 0x41, 0x02 }, completed[0].Value);
        Assert.Equal(new byte[] { 0x42, 0x03 }, completed[1].Value);
        Assert.Equal(0, decoder.BufferedByteCount);
    }

    [Fact]
    public void End_of_stream_reports_an_incomplete_frame()
    {
        var decoder = new Terminal2StxEtxStreamDecoder();
        Assert.Empty(decoder.Feed(new byte[] { 0x02, 0x41, 0x03 }));

        var result = decoder.Complete();

        Assert.False(result.IsValid);
        Assert.Equal("stxetx.incomplete-frame", result.Issues[0].Code);
        Assert.Equal(0, decoder.BufferedByteCount);
    }
}
