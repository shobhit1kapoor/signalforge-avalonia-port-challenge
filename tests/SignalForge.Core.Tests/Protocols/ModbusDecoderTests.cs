using SignalForge.Core.Protocols.Modbus;

namespace SignalForge.Core.Tests.Protocols;

public sealed class ModbusDecoderTests
{
    [Fact]
    public void TcpDecoderPreservesFragmentsAndEmitsCoalescedFrames()
    {
        var decoder = new ModbusTcpDecoder();
        var first = ModbusTcpEncoder.Encode(7, 1, 3, [0, 0, 0, 2]);
        var second = ModbusTcpEncoder.Encode(8, 1, 6, [0, 5, 0, 9]);

        Assert.Empty(decoder.Feed(first.AsSpan(0, 4)).Frames);
        var decoded = decoder.Feed(first.AsSpan(4).ToArray().Concat(second).ToArray());

        Assert.Empty(decoded.Issues);
        Assert.Equal(2, decoded.Frames.Count);
        Assert.Equal((ushort)7, decoded.Frames[0].TransactionId);
        Assert.Equal((byte)3, decoded.Frames[0].FunctionCode);
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, decoded.Frames[0].Data.ToArray());
        Assert.Equal((ushort)8, decoded.Frames[1].TransactionId);
        Assert.True(decoded.Frames.All(frame => frame.IsValid));
    }

    [Fact]
    public void TcpDecoderKeepsFramedEvidenceForInvalidProtocolIdentifier()
    {
        var bytes = ModbusTcpEncoder.Encode(1, 1, 3, [0, 0, 0, 1]);
        bytes[3] = 9;
        var decoded = new ModbusTcpDecoder().Feed(bytes);

        var frame = Assert.Single(decoded.Frames);
        Assert.False(frame.IsValid);
        Assert.Contains(frame.Issues, issue => issue.Kind == ModbusDecodeIssueKind.InvalidProtocolIdentifier);
        Assert.Equal((ushort)9, frame.ProtocolId);
    }

    [Fact]
    public void RtuDecoderChecksCrcAndHandlesFragmentedFrames()
    {
        var bytes = ModbusRtuCodec.Encode(1, 3, [0, 4, 0, 2]);
        var decoder = new ModbusRtuDecoder(ModbusRtuFrameRole.Request);

        Assert.Empty(decoder.Feed(bytes.AsSpan(0, 5)).Frames);
        var frame = Assert.Single(decoder.Feed(bytes.AsSpan(5)).Frames);
        Assert.True(frame.IsValid);
        Assert.Equal(bytes, frame.RawBytes.ToArray());

        bytes[^1] ^= 0x80;
        var corrupt = Assert.Single(new ModbusRtuDecoder(ModbusRtuFrameRole.Request).Feed(bytes).Frames);
        Assert.Contains(corrupt.Issues, issue => issue.Kind == ModbusDecodeIssueKind.InvalidChecksum);
    }

    [Fact]
    public void UnknownRtuRoleUsesCrcToInferRequestLength()
    {
        var request = ModbusRtuCodec.Encode(1, 3, [0, 0, 0, 1]);

        var frame = Assert.Single(new ModbusRtuDecoder().Feed(request).Frames);

        Assert.True(frame.IsValid);
        Assert.Equal(request, frame.RawBytes.ToArray());
    }

    [Fact]
    public void Function17LongResponseIsValidatedAsResponseShape()
    {
        var responseData = new byte[] { 10, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var response = ModbusTcpEncoder.Encode(1, 1, 0x17, responseData);

        var frame = Assert.Single(new ModbusTcpDecoder().Feed(response).Frames);

        Assert.True(frame.IsValid);
    }

    [Fact]
    public void TcpDecoderValidatesExceptionPdusAndReportsUnsupportedFunctions()
    {
        var exception = ModbusTcpEncoder.Encode(9, 1, 0x83, [0x02]);
        var validException = Assert.Single(new ModbusTcpDecoder().Feed(exception).Frames);
        Assert.True(validException.IsValid);
        Assert.Equal(new byte[] { 0x02 }, validException.Data.ToArray());

        var malformedException = ModbusTcpEncoder.Encode(10, 1, 0x83, [0x02, 0x03]);
        var invalidException = Assert.Single(new ModbusTcpDecoder().Feed(malformedException).Frames);
        Assert.Contains(invalidException.Issues, issue => issue.Kind == ModbusDecodeIssueKind.InvalidPdu);

        var unknown = ModbusTcpEncoder.Encode(11, 1, 0x41, [0xAA]);
        var unsupported = Assert.Single(new ModbusTcpDecoder().Feed(unknown).Frames);
        Assert.Contains(unsupported.Issues, issue => issue.Kind == ModbusDecodeIssueKind.UnsupportedFunction);
    }

    [Fact]
    public void TcpDecoderResynchronizesAfterInvalidMbapLengthAndReportsIncompleteTail()
    {
        var valid = ModbusTcpEncoder.Encode(12, 1, 3, [0, 0, 0, 1]);
        var decoder = new ModbusTcpDecoder();
        var recovered = decoder.Feed(new byte[] { 0xff }.Concat(valid).ToArray());

        Assert.Contains(recovered.Issues, issue => issue.Kind == ModbusDecodeIssueKind.InvalidLength);
        Assert.Equal((ushort)12, Assert.Single(recovered.Frames).TransactionId);

        Assert.Empty(decoder.Feed(valid.AsSpan(0, 5)).Frames);
        var incomplete = Assert.Single(decoder.Complete().Issues);
        Assert.Equal(ModbusDecodeIssueKind.IncompleteFrame, incomplete.Kind);
    }

    [Fact]
    public void RtuResponseRoleAcceptsReadResponseShapeAndUnknownRoleFindsResponseCrc()
    {
        var response = ModbusRtuCodec.Encode(1, 3, [2, 0x12, 0x34]);

        var roleAware = Assert.Single(new ModbusRtuDecoder(ModbusRtuFrameRole.Response).Feed(response).Frames);
        var inferred = Assert.Single(new ModbusRtuDecoder(ModbusRtuFrameRole.Unknown).Feed(response).Frames);

        Assert.True(roleAware.IsValid);
        Assert.True(inferred.IsValid);
        Assert.Equal(response, inferred.RawBytes.ToArray());
    }

    [Fact]
    public void TcpDecoderRejectsInconsistentMultipleWriteByteCount()
    {
        var malformed = ModbusTcpEncoder.Encode(1, 1, 0x10, [0, 4, 0, 2, 2, 0x12]);
        var invalid = Assert.Single(new ModbusTcpDecoder().Feed(malformed).Frames);
        Assert.Contains(invalid.Issues, issue => issue.Kind == ModbusDecodeIssueKind.InvalidPdu);
    }

    [Fact]
    public void ResponseValidatorUsesResponseShapesAndModbusSingleCoilEncoding()
    {
        Assert.Empty(ModbusPduValidator.ValidateResponse(0x03, [2, 0x12, 0x34]));
        Assert.Contains(ModbusPduValidator.ValidateResponse(0x03, [4, 0x12, 0x34]),
            issue => issue.Kind == ModbusDecodeIssueKind.InvalidPdu);
        Assert.Empty(ModbusPduValidator.ValidateResponse(0x05, [0, 1, 0xff, 0]));
        Assert.Contains(ModbusPduValidator.ValidateResponse(0x05, [0, 1, 0xff, 0xff]),
            issue => issue.Kind == ModbusDecodeIssueKind.InvalidPdu);
        Assert.Empty(ModbusPduValidator.ValidateResponse(0x83, [2]));
        Assert.Contains(ModbusPduValidator.ValidateResponse(0x83, [2, 3]),
            issue => issue.Kind == ModbusDecodeIssueKind.InvalidPdu);
    }
}
