using System.Buffers.Binary;

namespace SignalForge.Core.Protocols.Modbus;

/// <summary>Incremental MBAP decoder. TCP read boundaries are not treated as frame boundaries.</summary>
public sealed class ModbusTcpDecoder
{
    public const int HeaderLength = 7;
    public const int MaximumLengthField = 254;
    private const int MaximumAduLength = 6 + MaximumLengthField;
    private readonly List<byte> _buffer = new();

    public int BufferedByteCount => _buffer.Count;

    public ModbusDecodeBatch Feed(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.IsEmpty)
            _buffer.AddRange(bytes.ToArray());

        var frames = new List<ModbusFrame>();
        var issues = new List<ModbusDecodeIssue>();
        while (_buffer.Count >= HeaderLength)
        {
            var protocolId = (ushort)((_buffer[2] << 8) | _buffer[3]);
            var length = (_buffer[4] << 8) | _buffer[5];
            if (length < 2 || length > MaximumLengthField)
            {
                var evidence = _buffer.Take(HeaderLength).ToArray();
                issues.Add(new(ModbusDecodeIssueKind.InvalidLength,
                    $"MBAP length {length} is outside the legal range 2..{MaximumLengthField}; discarded one byte to resynchronize.", evidence));
                _buffer.RemoveAt(0);
                continue;
            }

            var totalLength = 6 + length;
            if (_buffer.Count < totalLength)
                break;

            var raw = _buffer.GetRange(0, totalLength).ToArray();
            _buffer.RemoveRange(0, totalLength);
            var frameIssues = new List<ModbusDecodeIssue>();
            if (protocolId != 0)
                frameIssues.Add(new(ModbusDecodeIssueKind.InvalidProtocolIdentifier,
                    $"MBAP protocol identifier is {protocolId}; Modbus TCP requires zero.", raw.AsMemory(2, 2)));

            var functionCode = raw[7];
            var pduData = raw.AsMemory(8, totalLength - 8);
            frameIssues.AddRange(ModbusPduValidator.Validate(functionCode, pduData.Span));
            frames.Add(new ModbusFrame(
                ModbusTransportFormat.Tcp,
                raw[6],
                functionCode,
                pduData,
                raw,
                (ushort)((raw[0] << 8) | raw[1]),
                protocolId,
                issues: frameIssues));
        }

        if (_buffer.Count > MaximumAduLength * 4)
        {
            var evidence = _buffer.Take(MaximumAduLength).ToArray();
            issues.Add(new(ModbusDecodeIssueKind.BufferLimitExceeded,
                "Unframed TCP data exceeded the decoder's bounded resynchronization buffer.", evidence));
            _buffer.RemoveRange(0, _buffer.Count - (HeaderLength - 1));
        }

        return new ModbusDecodeBatch(frames, issues);
    }

    public ModbusDecodeBatch Complete()
    {
        if (_buffer.Count == 0)
            return new();
        var evidence = _buffer.ToArray();
        _buffer.Clear();
        return new(issues: [new(ModbusDecodeIssueKind.IncompleteFrame,
            $"Stream ended with {evidence.Length} bytes of an incomplete MBAP frame.", evidence)]);
    }

    public void Reset() => _buffer.Clear();
}

public static class ModbusTcpEncoder
{
    public static byte[] Encode(ushort transactionId, byte unitId, byte functionCode, ReadOnlySpan<byte> data)
    {
        var length = checked(data.Length + 2); // Unit identifier + function code + data.
        if (length > ModbusTcpDecoder.MaximumLengthField)
            throw new ArgumentOutOfRangeException(nameof(data), "Modbus TCP PDU exceeds the MBAP length limit.");

        var result = new byte[6 + length];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), (ushort)length);
        result[6] = unitId;
        result[7] = functionCode;
        data.CopyTo(result.AsSpan(8));
        return result;
    }
}

/// <summary>Incremental RTU decoder. Configure role when traffic direction is known.</summary>
public sealed class ModbusRtuDecoder(ModbusRtuFrameRole role = ModbusRtuFrameRole.Unknown)
{
    public const int MaximumAduLength = 256;
    private readonly List<byte> _buffer = new();
    public ModbusRtuFrameRole Role { get; } = role;
    public int BufferedByteCount => _buffer.Count;

    public ModbusDecodeBatch Feed(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.IsEmpty)
            _buffer.AddRange(bytes.ToArray());

        var frames = new List<ModbusFrame>();
        var issues = new List<ModbusDecodeIssue>();
        while (_buffer.Count >= 2)
        {
            var function = _buffer[1];
            var expectedLength = GetExpectedLength(function);
            if (expectedLength < 0)
            {
                if (Role == ModbusRtuFrameRole.Unknown && TryFindUnknownLength(out var foundLength))
                {
                    ConsumeFrame(foundLength, frames);
                    continue;
                }

                if (_buffer.Count >= MaximumAduLength)
                {
                    var evidence = _buffer.Take(MaximumAduLength).ToArray();
                    issues.Add(new(ModbusDecodeIssueKind.UnsupportedFunction,
                        $"Cannot determine the RTU frame length for function 0x{function:X2}; discarded one byte to resynchronize.", evidence));
                    _buffer.RemoveAt(0);
                    continue;
                }
                break;
            }

            if (expectedLength == 0) // Length depends on a byte-count field not yet received.
                break;

            if (_buffer.Count < expectedLength)
                break;

            if (Role == ModbusRtuFrameRole.Unknown && IsDirectionAmbiguous(function))
            {
                var candidate = GetAlternateLength(function);
                if (candidate > 0 && candidate != expectedLength && _buffer.Count < candidate &&
                    !HasValidCrc(expectedLength))
                    break;
                if (candidate > 0 && candidate != expectedLength && _buffer.Count >= candidate &&
                    HasValidCrc(candidate) && (!HasValidCrc(expectedLength) || candidate < expectedLength))
                {
                    ConsumeFrame(candidate, frames);
                    continue;
                }
            }

            ConsumeFrame(expectedLength, frames);
        }

        if (_buffer.Count > MaximumAduLength * 2)
        {
            var evidence = _buffer.Take(MaximumAduLength).ToArray();
            issues.Add(new(ModbusDecodeIssueKind.BufferLimitExceeded,
                "Unframed RTU data exceeded the decoder's bounded resynchronization buffer.", evidence));
            _buffer.RemoveRange(0, _buffer.Count - 1);
        }

        return new ModbusDecodeBatch(frames, issues);
    }

    public ModbusDecodeBatch Complete()
    {
        if (_buffer.Count == 0)
            return new();
        var evidence = _buffer.ToArray();
        _buffer.Clear();
        return new(issues: [new(ModbusDecodeIssueKind.IncompleteFrame,
            $"Stream ended with {evidence.Length} bytes of an incomplete RTU frame.", evidence)]);
    }

    public void Reset() => _buffer.Clear();

    private int GetExpectedLength(byte function)
    {
        if ((function & 0x80) != 0)
            return 5;

        if (Role == ModbusRtuFrameRole.Response)
        {
            if (function is 0x01 or 0x02 or 0x03 or 0x04 or 0x17)
                return _buffer.Count >= 3 ? 5 + _buffer[2] : 0;
            return function switch
            {
                0x05 or 0x06 or 0x0f or 0x10 => 8,
                0x16 => 10,
                _ => -1
            };
        }

        if (Role == ModbusRtuFrameRole.Request)
        {
            return function switch
            {
                0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 => 8,
                0x0f or 0x10 => _buffer.Count >= 7 ? 9 + _buffer[6] : 0,
                0x16 => 10,
                0x17 => _buffer.Count >= 11 ? 13 + _buffer[10] : 0,
                _ => -1
            };
        }

        return function switch
        {
            0x05 or 0x06 => 8,
            0x16 => 10,
            0x01 or 0x02 or 0x03 or 0x04 or 0x17 => _buffer.Count >= 3 ? 5 + _buffer[2] : 0,
            0x0f or 0x10 => 8,
            _ => -1
        };
    }

    private int GetAlternateLength(byte function)
    {
        if (function is 0x01 or 0x02 or 0x03 or 0x04)
        {
            if (Role == ModbusRtuFrameRole.Unknown)
                return 8;
        }
        if (function is 0x0f or 0x10)
        {
            if (_buffer.Count >= 7)
                return 9 + _buffer[6];
        }
        if (function == 0x17 && _buffer.Count >= 11)
            return 13 + _buffer[10];
        return 0;
    }

    private bool IsDirectionAmbiguous(byte function) => function is 0x01 or 0x02 or 0x03 or 0x04 or 0x0f or 0x10 or 0x17;

    private bool TryFindUnknownLength(out int length)
    {
        var maximum = Math.Min(_buffer.Count, MaximumAduLength);
        for (var candidate = 5; candidate <= maximum; candidate++)
        {
            if (HasValidCrc(candidate))
            {
                length = candidate;
                return true;
            }
        }
        length = 0;
        return false;
    }

    private bool HasValidCrc(int length)
    {
        if (length < 4 || _buffer.Count < length)
            return false;
        var crc = ModbusRtuCodec.CalculateCrc(CollectionsMarshalHelper.AsSpan(_buffer, length - 2));
        return _buffer[length - 2] == (byte)(crc & 0xff) && _buffer[length - 1] == (byte)(crc >> 8);
    }

    private void ConsumeFrame(int length, ICollection<ModbusFrame> frames)
    {
        var raw = _buffer.GetRange(0, length).ToArray();
        _buffer.RemoveRange(0, length);
        var received = (ushort)(raw[^2] | (raw[^1] << 8));
        var calculated = ModbusRtuCodec.CalculateCrc(raw.AsSpan(0, raw.Length - 2));
        var frameIssues = new List<ModbusDecodeIssue>();
        if (received != calculated)
            frameIssues.Add(new(ModbusDecodeIssueKind.InvalidChecksum,
                $"RTU CRC mismatch: received 0x{received:X4}, calculated 0x{calculated:X4}.", raw.AsMemory(raw.Length - 2)));

        var function = raw[1];
        var data = raw.AsMemory(2, raw.Length - 4);
        frameIssues.AddRange(ModbusPduValidator.Validate(function, data.Span));
        frames.Add(new ModbusFrame(ModbusTransportFormat.Rtu, raw[0], function, data, raw,
            receivedCrc: received, calculatedCrc: calculated, issues: frameIssues));
    }
}

public static class ModbusRtuCodec
{
    public static ushort CalculateCrc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xa001 : crc >> 1);
        }
        return crc;
    }

    public static byte[] Encode(byte unitId, byte functionCode, ReadOnlySpan<byte> data)
    {
        if (data.Length > ModbusRtuDecoder.MaximumAduLength - 4)
            throw new ArgumentOutOfRangeException(nameof(data));
        var result = new byte[data.Length + 4];
        result[0] = unitId;
        result[1] = functionCode;
        data.CopyTo(result.AsSpan(2));
        var crc = CalculateCrc(result.AsSpan(0, result.Length - 2));
        result[^2] = (byte)(crc & 0xff);
        result[^1] = (byte)(crc >> 8);
        return result;
    }
}

internal static class CollectionsMarshalHelper
{
    public static ReadOnlySpan<byte> AsSpan(List<byte> bytes, int length)
    {
        // List<T> does not expose its backing span publicly; materialize the small (<=256-byte) RTU header.
        return bytes.Take(length).ToArray();
    }
}
