using SignalForge.Core.Protocols.Modbus;

namespace SignalForge.Core.Simulation;

/// <summary>Deterministic in-memory Modbus data model used by the virtual TCP device.</summary>
public sealed class VirtualModbusDevice(byte unitId = 1)
{
    private readonly object _gate = new();
    private readonly bool[] _coils = new bool[ushort.MaxValue + 1];
    private readonly bool[] _discreteInputs = new bool[ushort.MaxValue + 1];
    private readonly ushort[] _holdingRegisters = new ushort[ushort.MaxValue + 1];
    private readonly ushort[] _inputRegisters = new ushort[ushort.MaxValue + 1];

    public byte UnitId { get; } = unitId;

    public void SetCoil(ushort address, bool value)
    {
        lock (_gate) _coils[address] = value;
    }

    public void SetDiscreteInput(ushort address, bool value)
    {
        lock (_gate) _discreteInputs[address] = value;
    }

    public void SetHoldingRegister(ushort address, ushort value)
    {
        lock (_gate) _holdingRegisters[address] = value;
    }

    public void SetInputRegister(ushort address, ushort value)
    {
        lock (_gate) _inputRegisters[address] = value;
    }

    public VirtualDeviceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new VirtualDeviceSnapshot(UnitId,
                NonDefault(_coils), NonDefault(_discreteInputs),
                NonDefault(_holdingRegisters), NonDefault(_inputRegisters));
        }
    }

    public byte[]? HandleRequest(ModbusFrame request)
    {
        if (request.Format != ModbusTransportFormat.Tcp || request.TransactionId is null || request.UnitId != UnitId)
            return null;

        var data = request.Data.Span;
        var function = request.FunctionCode;
        byte[] pduData = Array.Empty<byte>();
        byte? exception = null;
        lock (_gate)
        {
            switch (function)
            {
                case 0x01:
                case 0x02:
                    if (data.Length != 4) { exception = 3; break; }
                    var bitStart = ReadUInt16(data, 0);
                    var bitCount = ReadUInt16(data, 2);
                    if (bitCount is 0 or > 2000) { exception = 3; break; }
                    if (!RangeFits(bitStart, bitCount)) { exception = 2; break; }
                    var bits = function == 0x01 ? _coils : _discreteInputs;
                    var bitBytes = new byte[(bitCount + 7) / 8];
                    for (var i = 0; i < bitCount; i++)
                        if (bits[bitStart + i]) bitBytes[i / 8] |= (byte)(1 << (i % 8));
                    pduData = [checked((byte)bitBytes.Length), .. bitBytes];
                    break;

                case 0x03:
                case 0x04:
                    if (data.Length != 4) { exception = 3; break; }
                    var regStart = ReadUInt16(data, 0);
                    var regCount = ReadUInt16(data, 2);
                    if (regCount is 0 or > 125) { exception = 3; break; }
                    if (!RangeFits(regStart, regCount)) { exception = 2; break; }
                    var registers = function == 0x03 ? _holdingRegisters : _inputRegisters;
                    pduData = new byte[1 + regCount * 2];
                    pduData[0] = checked((byte)(regCount * 2));
                    for (var i = 0; i < regCount; i++)
                        WriteUInt16(pduData, 1 + (i * 2), registers[regStart + i]);
                    break;

                case 0x05:
                    if (data.Length != 4) { exception = 3; break; }
                    var coilAddress = ReadUInt16(data, 0);
                    var coilValue = ReadUInt16(data, 2);
                    if (coilValue is not (0x0000 or 0xff00)) { exception = 3; break; }
                    _coils[coilAddress] = coilValue == 0xff00;
                    pduData = data.ToArray();
                    break;

                case 0x06:
                    if (data.Length != 4) { exception = 3; break; }
                    var holdingAddress = ReadUInt16(data, 0);
                    _holdingRegisters[holdingAddress] = ReadUInt16(data, 2);
                    pduData = data.ToArray();
                    break;

                case 0x0f:
                    if (data.Length < 5) { exception = 3; break; }
                    var writeCoilStart = ReadUInt16(data, 0);
                    var writeCoilCount = ReadUInt16(data, 2);
                    var coilByteCount = data[4];
                    if (writeCoilCount is 0 or > 1968 || coilByteCount != (writeCoilCount + 7) / 8 || data.Length != 5 + coilByteCount)
                    { exception = 3; break; }
                    if (!RangeFits(writeCoilStart, writeCoilCount)) { exception = 2; break; }
                    for (var i = 0; i < writeCoilCount; i++)
                        _coils[writeCoilStart + i] = (data[5 + i / 8] & (1 << (i % 8))) != 0;
                    pduData = data[..4].ToArray();
                    break;

                case 0x10:
                    if (data.Length < 5) { exception = 3; break; }
                    var writeRegStart = ReadUInt16(data, 0);
                    var writeRegCount = ReadUInt16(data, 2);
                    var regByteCount = data[4];
                    if (writeRegCount is 0 or > 123 || regByteCount != writeRegCount * 2 || data.Length != 5 + regByteCount)
                    { exception = 3; break; }
                    if (!RangeFits(writeRegStart, writeRegCount)) { exception = 2; break; }
                    for (var i = 0; i < writeRegCount; i++)
                        _holdingRegisters[writeRegStart + i] = ReadUInt16(data, 5 + i * 2);
                    pduData = data[..4].ToArray();
                    break;

                case 0x16:
                    if (data.Length != 6) { exception = 3; break; }
                    var maskAddress = ReadUInt16(data, 0);
                    var andMask = ReadUInt16(data, 2);
                    var orMask = ReadUInt16(data, 4);
                    _holdingRegisters[maskAddress] = (ushort)((_holdingRegisters[maskAddress] & andMask) | (orMask & ~andMask));
                    pduData = data.ToArray();
                    break;

                case 0x17:
                    if (data.Length < 11) { exception = 3; break; }
                    var readStart = ReadUInt16(data, 0);
                    var readCount = ReadUInt16(data, 2);
                    var writeStart = ReadUInt16(data, 4);
                    var writeCount = ReadUInt16(data, 6);
                    var writeByteCount = data[8];
                    if (readCount is 0 or > 125 || writeCount is 0 or > 121 || writeByteCount != writeCount * 2 || data.Length != 9 + writeByteCount)
                    { exception = 3; break; }
                    if (!RangeFits(readStart, readCount) || !RangeFits(writeStart, writeCount)) { exception = 2; break; }
                    for (var i = 0; i < writeCount; i++)
                        _holdingRegisters[writeStart + i] = ReadUInt16(data, 9 + i * 2);
                    pduData = new byte[1 + readCount * 2];
                    pduData[0] = checked((byte)(readCount * 2));
                    for (var i = 0; i < readCount; i++)
                        WriteUInt16(pduData, 1 + i * 2, _holdingRegisters[readStart + i]);
                    break;

                default:
                    exception = 1;
                    pduData = Array.Empty<byte>();
                    break;
            }
        }

        if (exception is byte exceptionCode)
            return ModbusTcpEncoder.Encode(request.TransactionId.Value, UnitId, (byte)(function | 0x80), [exceptionCode]);
        return ModbusTcpEncoder.Encode(request.TransactionId.Value, UnitId, function, pduData);
    }

    private static bool RangeFits(ushort start, ushort count) => (uint)start + count <= ushort.MaxValue + 1u;
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
    private static void WriteUInt16(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static IReadOnlyDictionary<ushort, bool> NonDefault(bool[] source)
    {
        var result = new Dictionary<ushort, bool>();
        for (var i = 0; i < source.Length; i++)
            if (source[i]) result[(ushort)i] = true;
        return result;
    }

    private static IReadOnlyDictionary<ushort, ushort> NonDefault(ushort[] source)
    {
        var result = new Dictionary<ushort, ushort>();
        for (var i = 0; i < source.Length; i++)
            if (source[i] != 0) result[(ushort)i] = source[i];
        return result;
    }
}
