using SignalForge.Core.Protocols.UserSchemas;

namespace SignalForge.Core.Tests.Protocols;

public sealed class ProtocolSchemaDecoderTests
{
    [Fact]
    public void MatchingSignatureAndChecksumProducesVerifiedFields()
    {
        byte[] payload = [0xaa, 0x12, 0x34, 0x55];
        payload[3] = unchecked((byte)(payload[0] + payload[1] + payload[2]));
        var schema = new UserProtocolSchema("sensor-v1", 4, exactLength: 4,
            signature: new byte[] { 0xaa },
            fields: [new("reading", 1, 2, ProtocolFieldType.UInt16)],
            checksum: new(ProtocolChecksumAlgorithm.Sum8, 3, 0, 3));

        var result = new ProtocolSchemaDecoder().Decode(payload, [schema]);

        Assert.Equal(ProtocolRecognitionConfidence.Verified, result.Confidence);
        Assert.Equal("sensor-v1", result.SchemaName);
        Assert.True(result.IntegrityValid);
        Assert.Equal((ushort)0x1234, result.Fields.Single().Value);
    }

    [Fact]
    public void LengthOnlySchemaIsHeuristicAndAmbiguousLayoutsRemainUnknown()
    {
        var first = new UserProtocolSchema("candidate-a", 2, exactLength: 2,
            fields: [new("id", 0, 1, ProtocolFieldType.UInt8)]);
        var second = new UserProtocolSchema("candidate-b", 2, exactLength: 2,
            fields: [new("id", 1, 1, ProtocolFieldType.UInt8)]);
        var decoder = new ProtocolSchemaDecoder();

        Assert.Equal(ProtocolRecognitionConfidence.Heuristic, decoder.Decode([1, 2], [first]).Confidence);
        var ambiguous = decoder.Decode([1, 2], [first, second]);
        Assert.Equal(ProtocolRecognitionConfidence.Unknown, ambiguous.Confidence);
        Assert.Equal(2, ambiguous.Candidates.Count);
    }

    [Fact]
    public void ChecksumFailureDemotesSignatureMatchToHeuristic()
    {
        var schema = new UserProtocolSchema("checked", 3, exactLength: 3,
            signature: new byte[] { 0xaa },
            checksum: new(ProtocolChecksumAlgorithm.Xor8, 2, 0, 2));

        var result = new ProtocolSchemaDecoder().Decode([0xaa, 0x01, 0xff], [schema]);

        Assert.Equal(ProtocolRecognitionConfidence.Heuristic, result.Confidence);
        Assert.False(result.IntegrityValid);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void RejectsFieldDefinitionsThatCanFallOutsideAcceptedLength()
    {
        var schema = new UserProtocolSchema("too-short", 1,
            fields: [new("value", 0, 2, ProtocolFieldType.UInt16)]);

        var result = new ProtocolSchemaDecoder().Decode([0, 1], [schema]);

        Assert.Equal(ProtocolRecognitionConfidence.Unknown, result.Confidence);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("does not fit", StringComparison.Ordinal));
    }
}
