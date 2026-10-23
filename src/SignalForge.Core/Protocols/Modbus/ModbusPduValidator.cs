namespace SignalForge.Core.Protocols.Modbus;

public static class ModbusPduValidator
{
    public static IReadOnlyList<ModbusDecodeIssue> Validate(byte functionCode, ReadOnlySpan<byte> data)
    {
        var issues = new List<ModbusDecodeIssue>();
        if ((functionCode & 0x80) != 0)
        {
            if (data.Length != 1)
                issues.Add(new(ModbusDecodeIssueKind.InvalidPdu, "An exception response must contain exactly one exception-code byte."));
            return issues;
        }

        switch (functionCode)
        {
            case 0x01:
            case 0x02:
            case 0x03:
            case 0x04:
                // Read requests contain start-address + quantity. Read responses contain byte-count + data.
                if (data.Length == 4)
                    break;
                if (data.Length < 1 || data[0] != data.Length - 1)
                    issues.Add(new(ModbusDecodeIssueKind.InvalidPdu,
                        "Read response byte-count does not match its data length."));
                break;
            case 0x05:
            case 0x06:
                RequireLength(data, 4, functionCode, issues);
                ValidateSingleCoilValue(functionCode, data, issues);
                break;
            case 0x0f:
            case 0x10:
                if (data.Length == 4) // write response echo
                    break;
                if (data.Length < 5 || data[4] != data.Length - 5)
                    issues.Add(new(ModbusDecodeIssueKind.InvalidPdu,
                        "Multiple-write byte-count does not match the supplied values."));
                break;
            case 0x16:
                RequireLength(data, 6, functionCode, issues);
                break;
            case 0x17:
                // A capture can contain either request or response direction. Validate against
                // both shapes so a long response is not mistaken for a malformed request.
                var isResponse = data.Length >= 1 && data[0] == data.Length - 1;
                var isRequest = data.Length >= 11 && data[8] == data.Length - 9;
                if (!isResponse && !isRequest)
                    issues.Add(new(ModbusDecodeIssueKind.InvalidPdu,
                        "Read/write byte-count does not match either the request values or response data."));
                break;
            default:
                issues.Add(new(ModbusDecodeIssueKind.UnsupportedFunction,
                    $"Function 0x{functionCode:X2} is structurally framed but not decoded by the built-in Modbus inspector."));
                break;
        }

        return issues;
    }

    /// <summary>Validates a response PDU when the caller knows the frame direction.</summary>
    public static IReadOnlyList<ModbusDecodeIssue> ValidateResponse(byte functionCode, ReadOnlySpan<byte> data)
    {
        if ((functionCode & 0x80) != 0)
            return Validate(functionCode, data);

        var issues = new List<ModbusDecodeIssue>();
        switch (functionCode)
        {
            case 0x01:
            case 0x02:
            case 0x03:
            case 0x04:
            case 0x17:
                ValidateReadResponse(data, functionCode, issues);
                if (functionCode is 0x03 or 0x04 or 0x17 && data.Length > 0 && (data[0] & 1) != 0)
                    issues.Add(new(ModbusDecodeIssueKind.InvalidPdu,
                        "Register-read response byte-count must be even."));
                break;
            case 0x05:
                RequireLength(data, 4, functionCode, issues);
                ValidateSingleCoilValue(functionCode, data, issues);
                break;
            case 0x06:
            case 0x0f:
            case 0x10:
                RequireLength(data, 4, functionCode, issues);
                break;
            case 0x16:
                RequireLength(data, 6, functionCode, issues);
                break;
            default:
                issues.Add(new(ModbusDecodeIssueKind.UnsupportedFunction,
                    $"Function 0x{functionCode:X2} does not have a built-in response validator."));
                break;
        }

        return issues;
    }

    private static void ValidateReadResponse(ReadOnlySpan<byte> data, byte functionCode,
        ICollection<ModbusDecodeIssue> issues)
    {
        if (data.Length < 1 || data[0] != data.Length - 1)
            issues.Add(new(ModbusDecodeIssueKind.InvalidPdu,
                $"Function 0x{functionCode:X2} response byte-count does not match its data length."));
    }

    private static void ValidateSingleCoilValue(byte functionCode, ReadOnlySpan<byte> data,
        ICollection<ModbusDecodeIssue> issues)
    {
        if (functionCode == 0x05 && data.Length == 4 &&
            !(data[2] == 0x00 && data[3] == 0x00) &&
            !(data[2] == 0xff && data[3] == 0x00))
        {
            issues.Add(new(ModbusDecodeIssueKind.InvalidPdu,
                "Single-coil value must be 0x0000 or 0xff00."));
        }
    }

    private static void RequireLength(ReadOnlySpan<byte> data, int expected, byte functionCode, ICollection<ModbusDecodeIssue> issues)
    {
        if (data.Length != expected)
            issues.Add(new(ModbusDecodeIssueKind.InvalidPdu,
                $"Function 0x{functionCode:X2} requires {expected} data bytes; received {data.Length}."));
    }
}
