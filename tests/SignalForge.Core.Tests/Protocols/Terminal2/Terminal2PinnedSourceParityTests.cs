/*
 * Differential compatibility tests for Terminal2's send and STX/ETX pipelines.
 * Original source Copyright (C) 2022-2023 Michael Heyns; tests modified for SIGNALFORGE.
 * Licensed under GNU GPL version 3 or (at your option) any later version.
 * Pinned reference and source hashes are documented in Terminal2PinnedSourceReference.cs.
 */

using SignalForge.Core.Compatibility.Terminal2;
using SignalForge.Core.Protocols.Terminal2;

namespace SignalForge.Core.Tests.Protocols.Terminal2;

public sealed class Terminal2PinnedSourceParityTests
{
    public static IEnumerable<object[]> ValidSendTextCorpus() =>
    [
        ["Plain ASCII text"],
        ["A$20B$41"],
        ["$$"],
        ["$$$41"],
        ["$02$03$10"],
        ["Data:$0D$0A$FF"],
        ["$41$$B$42"]
    ];

    [Theory]
    [MemberData(nameof(ValidSendTextCorpus))]
    public void Send_text_parser_matches_pinned_frm_main_for_valid_corpus(string sendText)
    {
        var expected = Terminal2PinnedSourceReference.SendDollarString(sendText);
        var actual = Terminal2SendTextParser.Parse(sendText);

        Assert.True(actual.IsValid, string.Join(Environment.NewLine, actual.Issues));
        Assert.Equal(expected, actual.Value);
    }

    [Theory]
    [MemberData(nameof(ValidSendTextCorpus))]
    public void Encoded_text_matches_pinned_stxetx_and_send_pipeline_for_valid_corpus(string sendText)
    {
        var expectedPayload = Terminal2PinnedSourceReference.SendDollarString(sendText);
        var expectedFrame = Terminal2PinnedSourceReference.EncodePayloadBytes(expectedPayload);
        var actual = Terminal2StxEtxCodec.EncodeText(sendText);

        Assert.True(actual.IsValid, string.Join(Environment.NewLine, actual.Issues));
        Assert.Equal(expectedFrame, actual.Value);
        Assert.Equal(expectedFrame, Terminal2StxEtxCodec.Encode(expectedPayload));
        Assert.True(Terminal2StxEtxCodec.Verify(expectedFrame));
        // Original Verify also interprets an in-band literal '$' as send notation,
        // so compare it when the raw payload contains no dollar byte.
        if (!expectedPayload.Contains((byte)'$'))
            Assert.True(Terminal2PinnedSourceReference.Verify(expectedFrame));
    }

    [Theory]
    [InlineData("Plain ASCII text")]
    [InlineData("A$20B$41")]
    [InlineData("$02$03")]
    [InlineData("Data:$0D$0A$FF")]
    public void Valid_frame_verify_and_decode_match_pinned_stxetx_behavior(string sendText)
    {
        var payload = Terminal2PinnedSourceReference.SendDollarString(sendText);
        var frame = Terminal2StxEtxCodec.Encode(payload);

        Assert.Equal(Terminal2PinnedSourceReference.Verify(frame), Terminal2StxEtxCodec.Verify(frame));
        Assert.True(Terminal2PinnedSourceReference.Verify(frame));
        var expectedPayload = Terminal2PinnedSourceReference.Decode(frame);
        var actualPayload = Terminal2StxEtxCodec.Decode(frame);

        Assert.True(actualPayload.IsValid, string.Join(Environment.NewLine, actualPayload.Issues));
        Assert.Equal(expectedPayload, actualPayload.Value);
        Assert.Equal(payload, actualPayload.Value);
    }

    [Fact]
    public void Dle_self_quote_matches_pinned_encode_verify_and_decode_behavior()
    {
        var payload = Terminal2PinnedSourceReference.SendDollarString("$02$03$10");
        var expectedFrame = Terminal2PinnedSourceReference.EncodePayloadBytes(payload);
        var actualFrame = Terminal2StxEtxCodec.EncodeText("$02$03$10");

        Assert.True(actualFrame.IsValid, string.Join(Environment.NewLine, actualFrame.Issues));
        Assert.Equal(expectedFrame, actualFrame.Value);
        Assert.True(Terminal2PinnedSourceReference.Verify(expectedFrame));
        Assert.True(Terminal2StxEtxCodec.Verify(expectedFrame));

        // The source's in-place RemoveDLE loop removes the quoting DLE while
        // retaining the second DLE of a self-quoted pair.
        Assert.Equal(new byte[] { 0x02, 0x03, 0x10 }, Terminal2PinnedSourceReference.Decode(expectedFrame));
        var migratedDecoded = Terminal2StxEtxCodec.Decode(expectedFrame);
        Assert.True(migratedDecoded.IsValid, string.Join(Environment.NewLine, migratedDecoded.Issues));
        Assert.Equal(new byte[] { 0x02, 0x03, 0x10 }, migratedDecoded.Value);
    }
}
