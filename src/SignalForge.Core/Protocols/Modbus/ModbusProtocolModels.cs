namespace SignalForge.Core.Protocols.Modbus;

public enum ModbusTransportFormat
{
    Tcp,
    Rtu
}

public enum ModbusDecodeIssueKind
{
    InvalidLength,
    InvalidProtocolIdentifier,
    InvalidPdu,
    InvalidChecksum,
    UnsupportedFunction,
    IncompleteFrame,
    BufferLimitExceeded
}

public sealed record ModbusDecodeIssue(
    ModbusDecodeIssueKind Kind,
    string Message,
    ReadOnlyMemory<byte> Evidence = default);

/// <summary>A complete Modbus ADU. Data contains PDU bytes after the function code.</summary>
public sealed record ModbusFrame
{
    public ModbusFrame(
        ModbusTransportFormat format,
        byte unitId,
        byte functionCode,
        ReadOnlyMemory<byte> data,
        ReadOnlyMemory<byte> rawBytes,
        ushort? transactionId = null,
        ushort? protocolId = null,
        ushort? receivedCrc = null,
        ushort? calculatedCrc = null,
        IReadOnlyList<ModbusDecodeIssue>? issues = null)
    {
        Format = format;
        UnitId = unitId;
        FunctionCode = functionCode;
        Data = data.ToArray();
        RawBytes = rawBytes.ToArray();
        TransactionId = transactionId;
        ProtocolId = protocolId;
        ReceivedCrc = receivedCrc;
        CalculatedCrc = calculatedCrc;
        Issues = issues?.ToArray() ?? Array.Empty<ModbusDecodeIssue>();
    }

    public ModbusTransportFormat Format { get; }
    public byte UnitId { get; }
    public byte FunctionCode { get; }
    public ReadOnlyMemory<byte> Data { get; }
    public ReadOnlyMemory<byte> RawBytes { get; }
    public ushort? TransactionId { get; }
    public ushort? ProtocolId { get; }
    public ushort? ReceivedCrc { get; }
    public ushort? CalculatedCrc { get; }
    public IReadOnlyList<ModbusDecodeIssue> Issues { get; }
    public bool IsException => (FunctionCode & 0x80) != 0;
    public byte BaseFunctionCode => (byte)(FunctionCode & 0x7f);
    public byte? ExceptionCode => IsException && Data.Length > 0 ? Data.Span[0] : null;
    public bool IsValid => Issues.Count == 0;
    public string FunctionName => ModbusFunctionCodes.GetName(BaseFunctionCode);
}

public sealed class ModbusDecodeBatch
{
    public ModbusDecodeBatch(IEnumerable<ModbusFrame>? frames = null, IEnumerable<ModbusDecodeIssue>? issues = null)
    {
        Frames = frames?.ToArray() ?? Array.Empty<ModbusFrame>();
        Issues = issues?.ToArray() ?? Array.Empty<ModbusDecodeIssue>();
    }

    public IReadOnlyList<ModbusFrame> Frames { get; }
    /// <summary>Framing/parser issues that could not be attached to a complete frame.</summary>
    public IReadOnlyList<ModbusDecodeIssue> Issues { get; }
}

public enum ModbusRtuFrameRole
{
    /// <summary>Use function-specific lengths for the known transmit/receive direction.</summary>
    Request,
    Response,
    /// <summary>Infer a frame only when a candidate length is supported by a valid CRC.</summary>
    Unknown
}

public static class ModbusFunctionCodes
{
    public static string GetName(byte functionCode) => functionCode switch
    {
        0x01 => "Read Coils",
        0x02 => "Read Discrete Inputs",
        0x03 => "Read Holding Registers",
        0x04 => "Read Input Registers",
        0x05 => "Write Single Coil",
        0x06 => "Write Single Register",
        0x0f => "Write Multiple Coils",
        0x10 => "Write Multiple Registers",
        0x16 => "Mask Write Register",
        0x17 => "Read/Write Multiple Registers",
        _ => $"Function 0x{functionCode:X2}"
    };
}
