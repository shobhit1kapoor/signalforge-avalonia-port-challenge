namespace SignalForge.Core.Protocols.UserSchemas;

public enum ProtocolRecognitionConfidence
{
    Unknown,
    Heuristic,
    Verified
}

public enum ProtocolFieldType
{
    Bytes,
    Ascii,
    Boolean,
    UInt8,
    Int8,
    UInt16,
    Int16,
    UInt32,
    Int32,
    UInt64,
    Int64,
    Float32,
    Float64
}

public enum ProtocolByteOrder
{
    BigEndian,
    LittleEndian
}

public enum ProtocolChecksumAlgorithm
{
    Sum8,
    Xor8,
    ModbusCrc16
}

public sealed record ProtocolFieldDefinition(
    string Name,
    int Offset,
    int Length,
    ProtocolFieldType Type,
    ProtocolByteOrder ByteOrder = ProtocolByteOrder.BigEndian);

/// <summary>Checksum is calculated over [DataOffset, DataOffset + DataLength) and compared at ValueOffset.</summary>
public sealed record ProtocolChecksumDefinition(
    ProtocolChecksumAlgorithm Algorithm,
    int ValueOffset,
    int DataOffset,
    int DataLength,
    ProtocolByteOrder ByteOrder = ProtocolByteOrder.LittleEndian);

/// <summary>
/// A user-authored packet layout. A matching signature is direct evidence; a layout that matches
/// only by length is deliberately reported as heuristic.
/// </summary>
public sealed class UserProtocolSchema
{
    public UserProtocolSchema(
        string name,
        int minimumLength,
        int? maximumLength = null,
        int? exactLength = null,
        ReadOnlyMemory<byte> signature = default,
        int signatureOffset = 0,
        IEnumerable<ProtocolFieldDefinition>? fields = null,
        ProtocolChecksumDefinition? checksum = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Schema name is required.", nameof(name)) : name.Trim();
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        ExactLength = exactLength;
        Signature = signature.ToArray();
        SignatureOffset = signatureOffset;
        Fields = fields?.ToArray() ?? Array.Empty<ProtocolFieldDefinition>();
        Checksum = checksum;
    }

    public string Name { get; }
    public int MinimumLength { get; }
    public int? MaximumLength { get; }
    public int? ExactLength { get; }
    public ReadOnlyMemory<byte> Signature { get; }
    public int SignatureOffset { get; }
    public IReadOnlyList<ProtocolFieldDefinition> Fields { get; }
    public ProtocolChecksumDefinition? Checksum { get; }
}

public sealed record DecodedProtocolField(
    string Name,
    ProtocolFieldType Type,
    int Offset,
    int Length,
    object Value,
    string DisplayValue);

public sealed class ProtocolSchemaDecodeResult
{
    public ProtocolSchemaDecodeResult(
        ProtocolRecognitionConfidence confidence,
        string? schemaName = null,
        IEnumerable<DecodedProtocolField>? fields = null,
        IEnumerable<string>? evidence = null,
        IEnumerable<string>? diagnostics = null,
        bool? integrityValid = null,
        IEnumerable<string>? candidates = null)
    {
        Confidence = confidence;
        SchemaName = schemaName;
        Fields = fields?.ToArray() ?? Array.Empty<DecodedProtocolField>();
        Evidence = evidence?.ToArray() ?? Array.Empty<string>();
        Diagnostics = diagnostics?.ToArray() ?? Array.Empty<string>();
        IntegrityValid = integrityValid;
        Candidates = candidates?.ToArray() ?? Array.Empty<string>();
    }

    public ProtocolRecognitionConfidence Confidence { get; }
    public string? SchemaName { get; }
    public IReadOnlyList<DecodedProtocolField> Fields { get; }
    public IReadOnlyList<string> Evidence { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool? IntegrityValid { get; }
    public IReadOnlyList<string> Candidates { get; }
}
