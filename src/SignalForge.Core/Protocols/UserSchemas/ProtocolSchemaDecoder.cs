using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SignalForge.Core.Protocols.Modbus;

namespace SignalForge.Core.Protocols.UserSchemas;

public sealed class ProtocolSchemaDecoder
{
    public ProtocolSchemaDecodeResult Decode(ReadOnlySpan<byte> payload, IEnumerable<UserProtocolSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var candidates = new List<Candidate>();
        var diagnostics = new List<string>();

        foreach (var schema in schemas)
        {
            if (schema is null)
                continue;

            var validation = ValidateDefinition(schema);
            if (validation is not null)
            {
                diagnostics.Add($"Schema '{schema.Name}' is invalid: {validation}");
                continue;
            }

            if (payload.Length < schema.MinimumLength ||
                (schema.MaximumLength is int max && payload.Length > max) ||
                (schema.ExactLength is int exact && payload.Length != exact))
                continue;

            if (!schema.Signature.IsEmpty)
            {
                if (!payload.Slice(schema.SignatureOffset, schema.Signature.Length).SequenceEqual(schema.Signature.Span))
                    continue;
            }

            var integrity = schema.Checksum is null ? (bool?)null : VerifyChecksum(payload, schema.Checksum);
            var verified = !schema.Signature.IsEmpty && integrity != false;
            var evidence = new List<string>
            {
                $"Payload length {payload.Length} satisfies schema '{schema.Name}'."
            };
            if (!schema.Signature.IsEmpty)
                evidence.Add($"Signature {Convert.ToHexString(schema.Signature.Span)} matches at offset {schema.SignatureOffset}.");
            else
                evidence.Add("No identifying signature was configured; recognition is based on the declared length and field layout.");
            if (schema.Checksum is not null)
                evidence.Add(integrity == true ? "Configured checksum validated." : "Configured checksum did not validate.");

            candidates.Add(new Candidate(schema, verified, integrity, evidence));
        }

        if (candidates.Count == 0)
        {
            if (diagnostics.Count == 0)
                diagnostics.Add("No schema matched the payload length and configured signature.");
            return new(ProtocolRecognitionConfidence.Unknown, diagnostics: diagnostics);
        }

        var strongest = candidates.Any(candidate => candidate.Verified)
            ? candidates.Where(candidate => candidate.Verified).ToArray()
            : candidates.ToArray();
        if (strongest.Length != 1)
        {
            var names = strongest.Select(candidate => candidate.Schema.Name).Order(StringComparer.Ordinal).ToArray();
            return new(ProtocolRecognitionConfidence.Unknown,
                diagnostics: diagnostics.Append("Multiple equally supported schemas matched; no packet layout was selected."),
                candidates: names);
        }

        var selected = strongest[0];
        var confidence = selected.Verified ? ProtocolRecognitionConfidence.Verified : ProtocolRecognitionConfidence.Heuristic;
        var decodedFields = new List<DecodedProtocolField>();
        foreach (var field in selected.Schema.Fields)
        {
            var value = DecodeField(payload.Slice(field.Offset, field.Length), field);
            decodedFields.Add(new(field.Name, field.Type, field.Offset, field.Length, value.Value, value.Display));
        }

        var resultDiagnostics = new List<string>(diagnostics);
        if (selected.IntegrityValid == false)
        {
            resultDiagnostics.Add("Fields are decoded from a schema candidate, but checksum failure prevents verified recognition.");
            confidence = ProtocolRecognitionConfidence.Heuristic;
        }

        return new(confidence, selected.Schema.Name, decodedFields, selected.Evidence, resultDiagnostics, selected.IntegrityValid);
    }

    private static string? ValidateDefinition(UserProtocolSchema schema)
    {
        if (schema.MinimumLength < 0 || schema.MaximumLength < 0 || schema.ExactLength < 0)
            return "length constraints cannot be negative";
        if (schema.MaximumLength is int max && max < schema.MinimumLength)
            return "maximum length is smaller than minimum length";
        if (schema.ExactLength is int exact && (exact < schema.MinimumLength || (schema.MaximumLength is int maxLen && exact > maxLen)))
            return "exact length falls outside the configured range";
        if (schema.SignatureOffset < 0)
            return "signature offset cannot be negative";
        var shortestAcceptedLength = schema.ExactLength ?? schema.MinimumLength;
        if ((long)schema.SignatureOffset + schema.Signature.Length > shortestAcceptedLength)
            return "signature does not fit every accepted frame length";
        if (schema.Fields.Any(field => string.IsNullOrWhiteSpace(field.Name) || field.Offset < 0 || field.Length <= 0))
            return "fields require a name, non-negative offset, and positive length";
        foreach (var field in schema.Fields)
        {
            if (!Enum.IsDefined(field.Type) || !Enum.IsDefined(field.ByteOrder))
                return $"field '{field.Name}' has an unsupported type or byte order";
            if ((long)field.Offset + field.Length > shortestAcceptedLength)
                return $"field '{field.Name}' does not fit every accepted frame length";
            if (RequiredWidth(field.Type) is int width && field.Length != width)
                return $"field '{field.Name}' has length {field.Length}; {field.Type} requires {width}";
        }
        if (schema.Checksum is { } checksum)
        {
            var valueLength = checksum.Algorithm == ProtocolChecksumAlgorithm.ModbusCrc16 ? 2 : 1;
            if (checksum.ValueOffset < 0 || checksum.DataOffset < 0 || checksum.DataLength < 0)
                return "checksum offsets and length cannot be negative";
            if (!Enum.IsDefined(checksum.Algorithm) || !Enum.IsDefined(checksum.ByteOrder))
                return "checksum algorithm or byte order is unsupported";
            if ((long)checksum.ValueOffset + valueLength > shortestAcceptedLength ||
                (long)checksum.DataOffset + checksum.DataLength > shortestAcceptedLength)
                return "checksum range does not fit every accepted frame length";
        }
        return null;
    }

    private static int? RequiredWidth(ProtocolFieldType type) => type switch
    {
        ProtocolFieldType.Boolean or ProtocolFieldType.UInt8 or ProtocolFieldType.Int8 => 1,
        ProtocolFieldType.UInt16 or ProtocolFieldType.Int16 => 2,
        ProtocolFieldType.UInt32 or ProtocolFieldType.Int32 or ProtocolFieldType.Float32 => 4,
        ProtocolFieldType.UInt64 or ProtocolFieldType.Int64 or ProtocolFieldType.Float64 => 8,
        _ => null
    };

    private static (object Value, string Display) DecodeField(ReadOnlySpan<byte> bytes, ProtocolFieldDefinition field)
    {
        object value = field.Type switch
        {
            ProtocolFieldType.Bytes => bytes.ToArray(),
            ProtocolFieldType.Ascii => Encoding.ASCII.GetString(bytes),
            ProtocolFieldType.Boolean => bytes[0] != 0,
            ProtocolFieldType.UInt8 => bytes[0],
            ProtocolFieldType.Int8 => unchecked((sbyte)bytes[0]),
            ProtocolFieldType.UInt16 => ReadUInt16(bytes, field.ByteOrder),
            ProtocolFieldType.Int16 => unchecked((short)ReadUInt16(bytes, field.ByteOrder)),
            ProtocolFieldType.UInt32 => ReadUInt32(bytes, field.ByteOrder),
            ProtocolFieldType.Int32 => unchecked((int)ReadUInt32(bytes, field.ByteOrder)),
            ProtocolFieldType.UInt64 => ReadUInt64(bytes, field.ByteOrder),
            ProtocolFieldType.Int64 => unchecked((long)ReadUInt64(bytes, field.ByteOrder)),
            ProtocolFieldType.Float32 => BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32(bytes, field.ByteOrder))),
            ProtocolFieldType.Float64 => BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64(bytes, field.ByteOrder))),
            _ => throw new ArgumentOutOfRangeException(nameof(field.Type))
        };
        var display = value switch
        {
            byte[] data => Convert.ToHexString(data),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
        return (value, display);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ProtocolByteOrder order) =>
        order == ProtocolByteOrder.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ProtocolByteOrder order) =>
        order == ProtocolByteOrder.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, ProtocolByteOrder order) =>
        order == ProtocolByteOrder.BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes);

    private static bool VerifyChecksum(ReadOnlySpan<byte> payload, ProtocolChecksumDefinition checksum)
    {
        var width = checksum.Algorithm == ProtocolChecksumAlgorithm.ModbusCrc16 ? 2 : 1;
        if (checksum.ValueOffset + width > payload.Length || checksum.DataOffset + checksum.DataLength > payload.Length)
            return false;

        var data = payload.Slice(checksum.DataOffset, checksum.DataLength);
        if (checksum.Algorithm == ProtocolChecksumAlgorithm.ModbusCrc16)
        {
            var actual = ModbusRtuCodec.CalculateCrc(data);
            var expected = checksum.ByteOrder == ProtocolByteOrder.LittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(checksum.ValueOffset, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(checksum.ValueOffset, 2));
            return actual == expected;
        }

        byte value = 0;
        foreach (var item in data)
            value = checksum.Algorithm == ProtocolChecksumAlgorithm.Sum8 ? unchecked((byte)(value + item)) : (byte)(value ^ item);
        return payload[checksum.ValueOffset] == value;
    }

    private sealed record Candidate(
        UserProtocolSchema Schema,
        bool Verified,
        bool? IntegrityValid,
        IReadOnlyList<string> Evidence);
}
