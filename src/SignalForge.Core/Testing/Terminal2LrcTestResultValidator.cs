using SignalForge.Core.Protocols.Terminal2;
using SignalForge.Core.Transport;
using SignalForge.Core.Compatibility.Terminal2;

namespace SignalForge.Core.Testing;

/// <summary>Validates a single Terminal2 response frame across arbitrary RX chunk boundaries.</summary>
public static class Terminal2LrcTestResultValidator
{
    public static ProtocolTestResult AddValidationStep(ProtocolTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var decoder = new Terminal2StxEtxStreamDecoder();
        var frames = new List<Terminal2ValidationResult<byte[]>>();
        var receivedBytes = 0;
        foreach (var evidence in result.Evidence)
        {
            if (evidence.Direction != ByteDirection.Rx)
                continue;

            receivedBytes = checked(receivedBytes + evidence.Bytes.Length);
            frames.AddRange(decoder.Feed(evidence.Bytes.Span));
        }

        var incomplete = decoder.Complete();
        Terminal2ValidationResult<byte[]> validation;
        if (frames.Any(frame => !frame.IsValid))
        {
            validation = frames.First(frame => !frame.IsValid);
        }
        else if (!incomplete.IsValid)
        {
            validation = incomplete;
        }
        else if (frames.Count == 0)
        {
            validation = Terminal2ValidationResult<byte[]>.Failure(
                receivedBytes == 0 ? "stxetx.no-response" : "stxetx.no-frame",
                receivedBytes == 0
                    ? "No response bytes were received for Terminal2 LRC validation."
                    : "Received bytes did not contain a complete Terminal2 frame.");
        }
        else if (frames.Count != 1)
        {
            validation = Terminal2ValidationResult<byte[]>.Failure(
                "stxetx.unexpected-frame-count",
                $"Expected exactly one Terminal2 response frame; received {frames.Count}.");
        }
        else
        {
            validation = frames[0];
        }

        var passed = validation.IsValid;
        var detail = passed
            ? $"Valid Terminal2 LRC. Decoded payload: {FormatHex(validation.Value!)}"
            : string.Join(" ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var step = new ProtocolTestStepResult(
            result.Steps.Count + 1,
            "Validate Terminal2 LRC",
            passed,
            detail,
            StartedAtTicks: now,
            CompletedAtTicks: now);

        return new ProtocolTestResult(
            result.ScenarioName,
            result.TransportName,
            result.Steps.Append(step),
            result.Evidence,
            result.StartedAtTicks,
            Math.Max(result.CompletedAtTicks, now),
            result.Cancelled);
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? "(empty)" : Convert.ToHexString(bytes);
}
