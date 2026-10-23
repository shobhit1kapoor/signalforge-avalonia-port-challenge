using SignalForge.Core.Forensics;

namespace SignalForge.Core.Tests.Forensics;

public sealed class BinaryInspectorTests
{
    [Fact]
    public void HexSearchReturnsAllLeftToRightNonOverlappingOffsets()
    {
        byte[] data = [0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xbb];

        var result = BinaryInspector.FindHexPattern(data, "aa AA");

        Assert.True(result.IsValid);
        Assert.True(result.Found);
        Assert.Equal(new[] { 0, 2 }, result.Offsets);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void HexSearchTreatsEmptyDataAsValidAndReportsNoMatches()
    {
        var result = BinaryInspector.FindHexPattern(ReadOnlySpan<byte>.Empty, "00 FF");

        Assert.True(result.IsValid);
        Assert.False(result.Found);
        Assert.Empty(result.Offsets);
        Assert.Null(result.Diagnostic);
    }

    [Theory]
    [InlineData("", HexPatternDiagnosticCode.EmptyPattern)]
    [InlineData("   \t", HexPatternDiagnosticCode.EmptyPattern)]
    [InlineData("0G", HexPatternDiagnosticCode.InvalidCharacter)]
    [InlineData("ABC", HexPatternDiagnosticCode.OddNumberOfDigits)]
    public void HexSearchReturnsTypedPatternDiagnostics(string pattern, HexPatternDiagnosticCode expectedCode)
    {
        var result = BinaryInspector.FindHexPattern([0x00], pattern);

        Assert.False(result.IsValid);
        Assert.False(result.Found);
        Assert.Empty(result.Offsets);
        Assert.Equal(expectedCode, result.Diagnostic?.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic?.Message));
    }

    [Fact]
    public void HexSearchReportsNullPatternSeparately()
    {
        var result = BinaryInspector.FindHexPattern([0x00], null);

        Assert.Equal(HexPatternDiagnosticCode.NullPattern, result.Diagnostic?.Code);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ReplaceRangeReturnsNewBufferAndLeavesSourceUntouched()
    {
        byte[] source = [0x00, 0xff, 0x80, 0x00];
        var original = source.ToArray();

        var result = BinaryInspector.ReplaceRange(source, 1, 2, [0x01, 0x02, 0x03]);

        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x00 }, result);
        Assert.Equal(original, source);
        Assert.NotSame(source, result);
    }

    [Fact]
    public void ReplaceRangeSupportsEmptyInputAndBoundaryInsertAndDelete()
    {
        var inserted = BinaryInspector.ReplaceRange(ReadOnlySpan<byte>.Empty, 0, 0, [0xfe, 0x00]);
        var appended = BinaryInspector.ReplaceRange([0x10, 0x20], 2, 0, [0x30]);
        var deleted = BinaryInspector.ReplaceRange([0x10, 0x20, 0x30], 0, 3, ReadOnlySpan<byte>.Empty);

        Assert.Equal(new byte[] { 0xfe, 0x00 }, inserted);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, appended);
        Assert.Empty(deleted);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public void ReplaceRangeRejectsOutOfBoundsRanges(int offset, int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryInspector.ReplaceRange([0x10, 0x20], offset, length, [0xff]));
    }

    [Fact]
    public void Base64RoundTripsBinaryAndEmptyPayloads()
    {
        byte[] binary = [0x00, 0xff, 0x80, 0x01];
        var encoded = BinaryInspector.EncodeBase64(binary);
        var decoded = BinaryInspector.DecodeBase64(encoded);
        var emptyEncoded = BinaryInspector.EncodeBase64(ReadOnlySpan<byte>.Empty);
        var emptyDecoded = BinaryInspector.DecodeBase64(emptyEncoded);

        Assert.True(decoded.IsSuccess);
        Assert.Null(decoded.Error);
        Assert.Equal(binary, decoded.Bytes);
        Assert.True(emptyDecoded.IsSuccess);
        Assert.Empty(emptyDecoded.Bytes!);
    }

    [Theory]
    [InlineData("%%%", Base64DecodeErrorCode.InvalidFormat)]
    [InlineData("AQI", Base64DecodeErrorCode.InvalidFormat)]
    public void Base64DecodeReturnsTypedFormatErrors(string encoded, Base64DecodeErrorCode expectedCode)
    {
        var result = BinaryInspector.DecodeBase64(encoded);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Bytes);
        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Error?.Message));
    }

    [Fact]
    public void Base64DecodeDistinguishesNullFromMalformedInput()
    {
        var result = BinaryInspector.DecodeBase64(null);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Bytes);
        Assert.Equal(Base64DecodeErrorCode.NullInput, result.Error?.Code);
    }
}
