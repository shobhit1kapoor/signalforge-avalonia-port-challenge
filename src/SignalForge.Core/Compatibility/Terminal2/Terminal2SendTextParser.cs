/*
 * Terminal2 compatibility support derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using System.Globalization;
using System.Text;

namespace SignalForge.Core.Compatibility.Terminal2;

/// <summary>
/// Parses Terminal2 send text: ordinary text is UTF-8, $HH inserts one byte,
/// and $$ inserts one literal dollar sign.
/// </summary>
public static class Terminal2SendTextParser
{
    public static Terminal2ValidationResult<byte[]> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var utf8 = Encoding.UTF8.GetBytes(text);
        var output = new List<byte>(utf8.Length);
        for (var i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] != (byte)'$')
            {
                output.Add(utf8[i]);
                continue;
            }

            if (i + 1 < utf8.Length && utf8[i + 1] == (byte)'$')
            {
                output.Add((byte)'$');
                i++;
                continue;
            }

            if (i + 2 >= utf8.Length)
            {
                return Terminal2ValidationResult<byte[]>.Failure(
                    "send-text.incomplete-escape",
                    "A dollar escape must be followed by exactly two hexadecimal digits, or a second dollar for a literal dollar.",
                    i);
            }

            var high = HexValue(utf8[i + 1]);
            var low = HexValue(utf8[i + 2]);
            if (high < 0 || low < 0)
            {
                return Terminal2ValidationResult<byte[]>.Failure(
                    "send-text.invalid-escape",
                    $"Invalid byte escape at character byte offset {i}; expected $HH with hexadecimal digits.",
                    i);
            }

            output.Add((byte)((high << 4) | low));
            i += 2;
        }

        return Terminal2ValidationResult<byte[]>.Success(output.ToArray());
    }

    public static string FormatAsHexTokens(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder(bytes.Length * 3);
        foreach (var value in bytes)
            output.Append('$').Append(value.ToString("X2", CultureInfo.InvariantCulture));
        return output.ToString();
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => -1
    };
}
