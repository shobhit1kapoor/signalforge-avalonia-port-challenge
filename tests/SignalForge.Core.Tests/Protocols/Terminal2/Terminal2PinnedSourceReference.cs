/*
 * Test-only behavioral reference transcribed from Terminal2 by Michael Heyns.
 * Original work Copyright (C) 2022-2023 Michael Heyns; modified for SIGNALFORGE tests.
 * Licensed under GNU GPL version 3 or (at your option) any later version.
 *
 * Pinned source: external/Terminal2 commit
 * d3adbd301963b729885cc41fb75b3a80bcdf5472
 * StxEtx.cs blob b78e58e9e83c29c845af312bb9415a443d0888be
 * FrmMain.cs blob dd2dfcb796fb879176492ff21e2fc0b4f5efdc89
 * This test reference copies only the valid-input algorithms from
 * STXETX.Encode/Verify/Decode and FrmMain.SendDollarString.
 */

using System.Globalization;
using System.Text;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Tests.Protocols.Terminal2;

internal static class Terminal2PinnedSourceReference
{
    /// <summary>
    /// Source-derived copy of Filters.FindApplicableFilter from the pinned
    /// Terminal2 checkout. Keep the culture-sensitive casing and substring
    /// order intact so the compatibility test compares the actual legacy
    /// matching contract rather than an idealized version of it.
    /// </summary>
    public static int FindApplicableFilter(
        IReadOnlyList<Terminal2Filter> filters,
        string text,
        int offset,
        bool ignoreCase)
    {
        if (offset >= text.Length)
            return -1;

        if (ignoreCase)
        {
            for (var index = 0; index < filters.Count; index++)
            {
                if (filters[index].Text.Length == 0)
                    continue;

                if (filters[index].Mode == 0 && text.ToLower().Substring(offset).StartsWith(filters[index].Text.ToLower()))
                    return index;

                if (filters[index].Mode == 1 && text.ToLower().Substring(offset).Contains(filters[index].Text.ToLower()))
                    return index;
            }
        }
        else
        {
            for (var index = 0; index < filters.Count; index++)
            {
                if (filters[index].Text.Length == 0)
                    continue;

                if (filters[index].Mode == 0 && text.Substring(offset).StartsWith(filters[index].Text))
                    return index;

                if (filters[index].Mode == 1 && text.Substring(offset).Contains(filters[index].Text))
                    return index;
            }
        }

        return -1;
    }

    public static byte[] SendDollarString(string command)
    {
        var input = Encoding.UTF8.GetBytes(command);
        var output = new List<byte>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (i < input.Length - 1 && input[i] == '$' && input[i + 1] == '$')
            {
                output.Add(input[i]);
                i++;
            }
            else if (i < input.Length - 2 && input[i] == '$' && IsHex(input[i + 1]) && IsHex(input[i + 2]))
            {
                var value = (GetHex(input[i + 1]) << 4) | GetHex(input[i + 2]);
                output.Add((byte)value);
                i += 2;
            }
            else
            {
                output.Add(input[i]);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Runs the original string-based STXETX.Encode and then its normal
    /// SendDollarString transport path. Payload bytes are represented as $HH
    /// tokens because STXETX.Encode consumes that notation before the sender
    /// resolves it to wire bytes.
    /// </summary>
    public static byte[] EncodePayloadBytes(byte[] payload)
    {
        var payloadNotation = string.Concat(payload.Select(value => "$" + value.ToString("X2", CultureInfo.InvariantCulture)));
        return SendDollarString(EncodeStxEtx(payloadNotation));
    }

    public static bool Verify(byte[] frame)
    {
        if (frame.Length < 3 || frame[0] != 0x02 || frame[^2] != 0x03)
            return false;

        var body = BytesToLegacyString(frame.AsSpan(1, frame.Length - 2));
        return frame[^1] == CalculateLrc(body, body.Length);
    }

    public static byte[] Decode(byte[] frame)
    {
        if (!Verify(frame))
            return Array.Empty<byte>();

        var payload = BytesToLegacyString(frame.AsSpan(1, frame.Length - 3));
        var index = 0;
        var length = payload.Length;
        while (index < length)
        {
            if (payload[index] == 0x10)
            {
                payload = payload.Remove(index, 1);
                length = payload.Length;
            }

            index++;
        }

        return payload.Select(value => (byte)value).ToArray();
    }

    private static string EncodeStxEtx(string message)
    {
        message = InsertDle(message) + "$03";
        var lrc = CalculateLrc(message, message.Length);
        return "$02" + message + "$" + lrc.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static string InsertDle(string message)
    {
        var index = 0;
        var length = message.Length;
        while (index < length)
        {
            var value = message[index];
            if (message[index] == '$' && IsHex(message[index + 1]) && IsHex(message[index + 2]))
            {
                value = (char)((GetHex(message[index + 1]) << 4) | GetHex(message[index + 2]));
                if (value is (char)0x02 or (char)0x03 or (char)0x10)
                {
                    message = message.Insert(index, "$10");
                    length = message.Length;
                    index += 5; // Original comment: onto last character of the token.
                }
            }

            index++;
        }

        return message;
    }

    private static byte CalculateLrc(string message, int length)
    {
        byte lrc = 0;
        for (var index = 0; index < length; index++)
        {
            if (message[index] == '$' && IsHex(message[index + 1]) && IsHex(message[index + 2]))
            {
                var value = (GetHex(message[index + 1]) << 4) | GetHex(message[index + 2]);
                lrc ^= (byte)value;
                index += 2;
            }
            else
            {
                lrc ^= (byte)message[index];
            }
        }

        return lrc;
    }

    // Faithfully retains the pinned source's unconditional true fallback. The
    // corpus intentionally uses valid hexadecimal tokens, so defect cases are
    // left to the dedicated migration validation tests.
    private static bool IsHex(byte value)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
            return true;
        if (value is >= (byte)'A' and <= (byte)'F')
            return true;
        if (value is >= (byte)'a' and <= (byte)'h')
            return true;
        return true;
    }

    private static bool IsHex(char value) => IsHex((byte)value);

    private static int GetHex(byte value)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
            return value - (byte)'0';
        if (value is >= (byte)'A' and <= (byte)'F')
            return value - (byte)'A' + 10;
        if (value is >= (byte)'a' and <= (byte)'h')
            return value - (byte)'a' + 10;
        return 0;
    }

    private static int GetHex(char value) => GetHex((byte)value);

    private static string BytesToLegacyString(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, state) =>
        {
            for (var index = 0; index < state.Length; index++)
                chars[index] = (char)state[index];
        });
    }
}
