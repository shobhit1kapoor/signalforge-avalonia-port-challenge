/*
 * STX/ETX/DLE/LRC behavior derived from Terminal2 by Michael Heyns.
 * Copyright (C) 2021-2024 Michael Heyns. Modified for SIGNALFORGE.
 * Licensed under the GNU GPL, version 3 or (at your option) any later version.
 * See the repository LICENSE and external/Terminal2/LICENSE for the full terms.
 */

using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Protocols.Terminal2;

/// <summary>Byte-oriented Terminal2 STX/ETX/DLE/XOR-LRC framing.</summary>
public static class Terminal2StxEtxCodec
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Dle = 0x10;

    /// <summary>
    /// Encodes one payload. LRC is the XOR of the transmitted, DLE-stuffed body
    /// and ETX; STX and the LRC byte are excluded, matching Terminal2.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var frame = new List<byte>(payload.Length + 3);
        frame.Add(Stx);
        byte lrc = 0;
        foreach (var value in payload)
        {
            if (value is Stx or Etx or Dle)
            {
                frame.Add(Dle);
                lrc ^= Dle;
            }
            frame.Add(value);
            lrc ^= value;
        }

        frame.Add(Etx);
        lrc ^= Etx;
        frame.Add(lrc);
        return frame.ToArray();
    }

    /// <summary>Builds a frame from text using Terminal2's UTF-8 and dollar-escape send notation.</summary>
    public static Terminal2ValidationResult<byte[]> EncodeText(string payloadText)
    {
        var parsed = Terminal2SendTextParser.Parse(payloadText);
        if (!parsed.IsValid)
            return new Terminal2ValidationResult<byte[]>(null, parsed.Issues);

        return new Terminal2ValidationResult<byte[]>(Encode(parsed.Value!), parsed.Issues);
    }

    /// <summary>Returns a send-text string whose parsed bytes are exactly the encoded frame.</summary>
    public static Terminal2ValidationResult<string> EncodeAsSendText(string payloadText)
    {
        var frame = EncodeText(payloadText);
        if (!frame.IsValid)
            return new Terminal2ValidationResult<string>(null, frame.Issues);

        return Terminal2ValidationResult<string>.Success(Terminal2SendTextParser.FormatAsHexTokens(frame.Value!));
    }

    public static byte CalculateLrc(ReadOnlySpan<byte> stuffedPayloadAndEtx)
    {
        byte lrc = 0;
        foreach (var value in stuffedPayloadAndEtx)
            lrc ^= value;
        return lrc;
    }

    public static bool Verify(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3 || frame[0] != Stx || frame[^2] != Etx)
            return false;

        return CalculateLrc(frame[1..^1]) == frame[^1];
    }

    /// <summary>Validates and removes framing, then unquotes DLE-prefixed control bytes.</summary>
    public static Terminal2ValidationResult<byte[]> Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3)
            return Terminal2ValidationResult<byte[]>.Failure("stxetx.frame-too-short", "A Terminal2 frame must contain STX, ETX, and LRC.");
        if (frame[0] != Stx)
            return Terminal2ValidationResult<byte[]>.Failure("stxetx.missing-stx", "The first frame byte is not STX.");
        if (frame[^2] != Etx)
            return Terminal2ValidationResult<byte[]>.Failure("stxetx.missing-etx", "The second-to-last frame byte is not ETX.");

        var expected = CalculateLrc(frame[1..^1]);
        if (frame[^1] != expected)
            return Terminal2ValidationResult<byte[]>.Failure(
                "stxetx.bad-lrc",
                $"LRC mismatch: expected 0x{expected:X2}, received 0x{frame[^1]:X2}.");

        var stuffedPayload = frame[1..^2];
        var payload = new List<byte>(stuffedPayload.Length);
        for (var i = 0; i < stuffedPayload.Length; i++)
        {
            var value = stuffedPayload[i];
            if (value != Dle)
            {
                payload.Add(value);
                continue;
            }

            if (i + 1 >= stuffedPayload.Length)
                return Terminal2ValidationResult<byte[]>.Failure("stxetx.dangling-dle", "DLE cannot be the last payload byte.");

            var quoted = stuffedPayload[++i];
            if (quoted is not (Stx or Etx or Dle))
                return Terminal2ValidationResult<byte[]>.Failure(
                    "stxetx.invalid-dle-escape",
                    $"DLE must quote STX, ETX, or DLE; found 0x{quoted:X2}.");

            payload.Add(quoted);
        }

        return Terminal2ValidationResult<byte[]>.Success(payload.ToArray());
    }
}

/// <summary>Incremental frame scanner for arbitrary TCP/serial read chunking.</summary>
public sealed class Terminal2StxEtxStreamDecoder
{
    private readonly List<byte> _buffer = new();

    public Terminal2StxEtxStreamDecoder(int maximumFrameLength = 1024 * 1024)
    {
        if (maximumFrameLength < 3)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameLength));
        MaximumFrameLength = maximumFrameLength;
    }

    public int MaximumFrameLength { get; }
    public int BufferedByteCount => _buffer.Count;

    /// <summary>Consumes bytes and returns any complete valid or invalid candidate frames.</summary>
    public IReadOnlyList<Terminal2ValidationResult<byte[]>> Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            _buffer.Add(value);

        var results = new List<Terminal2ValidationResult<byte[]>>();
        while (_buffer.Count > 0)
        {
            var start = _buffer.IndexOf(Terminal2StxEtxCodec.Stx);
            if (start < 0)
            {
                results.Add(Terminal2ValidationResult<byte[]>.Failure(
                    "stxetx.data-outside-frame",
                    $"Discarded {_buffer.Count} byte(s) before the next STX."));
                _buffer.Clear();
                break;
            }

            if (start > 0)
            {
                results.Add(Terminal2ValidationResult<byte[]>.Failure(
                    "stxetx.data-outside-frame",
                    $"Discarded {start} byte(s) before STX."));
                _buffer.RemoveRange(0, start);
            }

            var escaped = false;
            var completed = false;
            for (var i = 1; i < _buffer.Count; i++)
            {
                var value = _buffer[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (value == Terminal2StxEtxCodec.Dle)
                {
                    escaped = true;
                    continue;
                }

                if (value == Terminal2StxEtxCodec.Stx)
                {
                    results.Add(Terminal2ValidationResult<byte[]>.Failure(
                        "stxetx.unescaped-stx",
                        "A new unescaped STX appeared before ETX; the incomplete candidate was discarded."));
                    _buffer.RemoveRange(0, i);
                    completed = true;
                    break;
                }

                if (value != Terminal2StxEtxCodec.Etx)
                    continue;

                if (i + 1 >= _buffer.Count)
                    break;

                var frameLength = i + 2;
                var candidate = _buffer.GetRange(0, frameLength).ToArray();
                results.Add(Terminal2StxEtxCodec.Decode(candidate));
                _buffer.RemoveRange(0, frameLength);
                completed = true;
                break;
            }

            if (completed)
                continue;

            if (_buffer.Count > MaximumFrameLength)
            {
                results.Add(Terminal2ValidationResult<byte[]>.Failure(
                    "stxetx.frame-too-large",
                    $"An incomplete frame exceeded the configured limit of {MaximumFrameLength} bytes."));
                var nextStart = _buffer.IndexOf(Terminal2StxEtxCodec.Stx, 1);
                if (nextStart < 0)
                    _buffer.Clear();
                else
                    _buffer.RemoveRange(0, nextStart);
                continue;
            }

            break;
        }

        return results;
    }

    /// <summary>Reports and discards an incomplete suffix at end-of-stream.</summary>
    public Terminal2ValidationResult<byte[]> Complete()
    {
        if (_buffer.Count == 0)
            return Terminal2ValidationResult<byte[]>.Success(Array.Empty<byte>());

        var count = _buffer.Count;
        _buffer.Clear();
        return Terminal2ValidationResult<byte[]>.Failure(
            "stxetx.incomplete-frame",
            $"The stream ended with {count} byte(s) of an incomplete frame.");
    }
}
