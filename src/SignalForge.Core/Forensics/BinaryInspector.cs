namespace SignalForge.Core.Forensics;

public enum HexPatternDiagnosticCode
{
    NullPattern,
    EmptyPattern,
    InvalidCharacter,
    OddNumberOfDigits
}

public sealed record HexPatternDiagnostic(
    HexPatternDiagnosticCode Code,
    string Message,
    int? CharacterIndex = null);

public sealed class HexPatternSearchResult(
    IEnumerable<int> offsets,
    HexPatternDiagnostic? diagnostic)
{
    public IReadOnlyList<int> Offsets { get; } = Array.AsReadOnly(offsets.ToArray());
    public HexPatternDiagnostic? Diagnostic { get; } = diagnostic;
    public bool IsValid => Diagnostic is null;
    public bool Found => IsValid && Offsets.Count > 0;
}

public enum Base64DecodeErrorCode
{
    NullInput,
    InvalidFormat
}

public sealed record Base64DecodeError(Base64DecodeErrorCode Code, string Message);

public sealed record Base64DecodeResult(bool IsSuccess, byte[]? Bytes, Base64DecodeError? Error);

/// <summary>Small helpers for inspecting and transforming captured binary data.</summary>
public static class BinaryInspector
{
    /// <summary>
    /// Finds left-to-right, non-overlapping occurrences of a hexadecimal byte pattern.
    /// Whitespace in the pattern is ignored; all other non-hex characters are diagnosed.
    /// </summary>
    public static HexPatternSearchResult FindHexPattern(ReadOnlySpan<byte> data, string? hexPattern)
    {
        if (hexPattern is null)
            return Invalid(HexPatternDiagnosticCode.NullPattern, "Hex pattern cannot be null.");

        var pattern = new List<byte>();
        int? highNibble = null;
        for (var index = 0; index < hexPattern.Length; index++)
        {
            var character = hexPattern[index];
            if (char.IsWhiteSpace(character))
                continue;

            var nibble = HexValue(character);
            if (nibble < 0)
                return Invalid(HexPatternDiagnosticCode.InvalidCharacter,
                    $"Hex pattern contains invalid character '{character}' at index {index}.", index);

            if (highNibble is null)
                highNibble = nibble;
            else
            {
                pattern.Add((byte)((highNibble.Value << 4) | nibble));
                highNibble = null;
            }
        }

        if (pattern.Count == 0 && highNibble is null)
            return Invalid(HexPatternDiagnosticCode.EmptyPattern, "Hex pattern must contain at least one byte.");
        if (highNibble is not null)
            return Invalid(HexPatternDiagnosticCode.OddNumberOfDigits, "Hex pattern must contain an even number of digits.");

        var patternBytes = pattern.ToArray();
        var offsets = new List<int>();
        for (var offset = 0; offset <= data.Length - patternBytes.Length;)
        {
            if (data.Slice(offset, patternBytes.Length).SequenceEqual(patternBytes))
            {
                offsets.Add(offset);
                offset += patternBytes.Length;
            }
            else
            {
                offset++;
            }
        }

        return new(offsets, null);
    }

    /// <summary>Returns a new array with a source range replaced by the provided bytes.</summary>
    public static byte[] ReplaceRange(
        ReadOnlySpan<byte> source,
        int offset,
        int length,
        ReadOnlySpan<byte> replacement)
    {
        if (offset < 0 || offset > source.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be within the source, including its end boundary.");
        if (length < 0 || length > source.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(length), "Replacement range must fit within the source.");

        var result = new byte[checked(source.Length - length + replacement.Length)];
        source[..offset].CopyTo(result);
        replacement.CopyTo(result.AsSpan(offset));
        source[(offset + length)..].CopyTo(result.AsSpan(offset + replacement.Length));
        return result;
    }

    public static string EncodeBase64(ReadOnlySpan<byte> data) => Convert.ToBase64String(data);

    /// <summary>Decodes Base64 while returning an explicit error result for null or malformed input.</summary>
    public static Base64DecodeResult DecodeBase64(string? encoded)
    {
        if (encoded is null)
            return new(false, null, new(Base64DecodeErrorCode.NullInput, "Base64 input cannot be null."));

        try
        {
            return new(true, Convert.FromBase64String(encoded), null);
        }
        catch (FormatException)
        {
            return new(false, null, new(Base64DecodeErrorCode.InvalidFormat, "Input is not valid Base64."));
        }
    }

    private static HexPatternSearchResult Invalid(
        HexPatternDiagnosticCode code,
        string message,
        int? characterIndex = null) =>
        new([], new(code, message, characterIndex));

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1
    };
}
