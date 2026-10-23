using SignalForge.Core.Protocols.Terminal2;
using SignalForge.Core.Testing;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Tests.Testing;

public sealed class Terminal2LrcTestResultValidatorTests
{
    [Fact]
    public void Fragmented_response_is_reassembled_before_lrc_validation()
    {
        var frame = Terminal2StxEtxCodec.Encode([0x41, 0x02, 0x42]);
        var result = CreateResult(
            frame.AsMemory(0, 2),
            frame.AsMemory(2, 2),
            frame.AsMemory(4));

        var validated = Terminal2LrcTestResultValidator.AddValidationStep(result);

        var lrcStep = Assert.Single(validated.Steps, step => step.Name == "Validate Terminal2 LRC");
        Assert.True(lrcStep.Passed, lrcStep.Detail);
        Assert.Contains("410242", lrcStep.Detail);
    }

    [Fact]
    public void Fragmented_bad_lrc_is_reported_after_frame_reassembly()
    {
        var frame = Terminal2StxEtxCodec.Encode([0x41, 0x42]);
        frame[^1] ^= 0x01;
        var result = CreateResult(frame.AsMemory(0, 4), frame.AsMemory(4));

        var validated = Terminal2LrcTestResultValidator.AddValidationStep(result);

        var lrcStep = Assert.Single(validated.Steps, step => step.Name == "Validate Terminal2 LRC");
        Assert.False(lrcStep.Passed);
        Assert.Contains("stxetx.bad-lrc", lrcStep.Detail);
    }

    [Fact]
    public void Missing_or_multiple_response_frames_do_not_pass_lrc_validation()
    {
        var valid = Terminal2StxEtxCodec.Encode([0x41]);
        var noResponse = Terminal2LrcTestResultValidator.AddValidationStep(CreateResult());
        var twoFrames = Terminal2LrcTestResultValidator.AddValidationStep(CreateResult(valid, valid));

        Assert.False(Assert.Single(noResponse.Steps).Passed);
        Assert.False(Assert.Single(twoFrames.Steps).Passed);
        Assert.Contains("exactly one", Assert.Single(twoFrames.Steps).Detail);
    }

    private static ProtocolTestResult CreateResult(params ReadOnlyMemory<byte>[] chunks)
    {
        var evidence = chunks.Select((chunk, index) => new ProtocolTestEvidence(
            1, "response", ByteDirection.Rx, index + 1, chunk, "test chunk"));
        return new ProtocolTestResult("Terminal2 LRC", "fixture", [], evidence, 1, 2);
    }
}
