using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Forensics;
using SignalForge.Core.Traces;

namespace SignalForge.Desktop.ViewModels;

/// <summary>Non-destructive search and conversion tools for the selected trace event.</summary>
public partial class BinaryInspectorToolsViewModel : ViewModelBase
{
    private byte[] _payload = [];

    [ObservableProperty]
    private string _eventLabel = "Select a byte event from the timeline.";

    [ObservableProperty]
    private string _hexPattern = "";

    [ObservableProperty]
    private string _searchResults = "No search has been run.";

    [ObservableProperty]
    private string _replaceOffset = "0";

    [ObservableProperty]
    private string _replaceLength = "1";

    [ObservableProperty]
    private string _replacementHex = "";

    [ObservableProperty]
    private string _replacementPreview = "Replacement preview has not been generated.";

    [ObservableProperty]
    private string _base64Text = "";

    [ObservableProperty]
    private string _decodedHex = "";

    [ObservableProperty]
    private string _status = "Tools operate on a copy. Captured bytes and saved traces are never modified.";

    public string PayloadSizeText => $"{_payload.Length.ToString(CultureInfo.InvariantCulture)} captured byte(s)";
    public string AsciiReferenceTable { get; } = BuildAsciiReferenceTable();

    public void Load(TraceEvent? traceEvent)
    {
        _payload = traceEvent?.Payload?.ToArray() ?? [];
        EventLabel = traceEvent is null
            ? "Select a byte event from the timeline."
            : $"{traceEvent.Direction?.ToString().ToUpperInvariant() ?? traceEvent.Kind.ToString().ToUpperInvariant()} · {traceEvent.Endpoint ?? "endpoint unavailable"} · {traceEvent.TimestampUtc.ToLocalTime():HH:mm:ss.fff}";
        OnPropertyChanged(nameof(PayloadSizeText));
        SearchResults = _payload.Length == 0 ? "Selected event has no byte payload." : "No search has been run.";
        ReplacementPreview = "Replacement preview has not been generated.";
        DecodedHex = "";
        Base64Text = _payload.Length == 0 ? "" : BinaryInspector.EncodeBase64(_payload);
        Status = _payload.Length == 0
            ? "Select a byte-carrying RX or TX event to use binary tools."
            : "Captured bytes loaded. Search and transforms leave the original trace unchanged.";
    }

    [RelayCommand]
    private void SearchHex()
    {
        var result = BinaryInspector.FindHexPattern(_payload, HexPattern);
        if (result.Diagnostic is { } diagnostic)
        {
            SearchResults = diagnostic.Message;
            Status = $"HEX SEARCH · {diagnostic.Code}";
            return;
        }

        SearchResults = result.Found
            ? $"{result.Offsets.Count} non-overlapping match(es) at byte offset(s): {string.Join(", ", result.Offsets)}"
            : "No matches in this event.";
        Status = $"HEX SEARCH · checked {PayloadSizeText}.";
    }

    [RelayCommand]
    private void PreviewReplacement()
    {
        if (!int.TryParse(ReplaceOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || !int.TryParse(ReplaceLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            Status = "Replace offset and length must be whole, nonnegative byte counts.";
            return;
        }

        if (!TryParseHex(ReplacementHex, out var replacement, out var error))
        {
            Status = $"Replacement preview failed: {error}";
            return;
        }

        try
        {
            var preview = BinaryInspector.ReplaceRange(_payload, offset, length, replacement);
            ReplacementPreview = FormatHex(preview);
            Status = $"PREVIEW ONLY · {preview.Length.ToString(CultureInfo.InvariantCulture)} byte(s) · original event remains unchanged.";
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Status = $"Replacement preview failed: {exception.Message}";
        }
        catch (OverflowException)
        {
            Status = "Replacement preview failed: resulting payload would be too large.";
        }
    }

    [RelayCommand]
    private void DecodeBase64()
    {
        var result = BinaryInspector.DecodeBase64(Base64Text);
        if (!result.IsSuccess)
        {
            DecodedHex = "";
            Status = $"BASE64 DECODE · {result.Error?.Message ?? "Input could not be decoded."}";
            return;
        }

        DecodedHex = FormatHex(result.Bytes!);
        Status = $"BASE64 DECODE · produced {result.Bytes!.Length.ToString(CultureInfo.InvariantCulture)} byte(s); preview only.";
    }

    [RelayCommand]
    private void EncodeSelectedBytes()
    {
        if (_payload.Length == 0)
        {
            Status = "Select a byte event before encoding Base64.";
            return;
        }

        Base64Text = BinaryInspector.EncodeBase64(_payload);
        Status = $"BASE64 ENCODE · encoded {PayloadSizeText}.";
    }

    private static bool TryParseHex(string input, out byte[] bytes, out string error)
    {
        var compact = string.Concat(input.Where(character => !char.IsWhiteSpace(character) && character is not (':' or ',' or '-')));
        if (compact.Length == 0)
        {
            bytes = [];
            error = "enter replacement bytes as hex pairs (an empty value deletes the selected range).";
            return true;
        }

        if ((compact.Length & 1) != 0)
        {
            bytes = [];
            error = "hex input must contain complete byte pairs.";
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(compact);
            error = "";
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            error = "hex input contains an invalid character.";
            return false;
        }
    }

    private static string FormatHex(IEnumerable<byte> bytes)
    {
        var materialized = bytes.ToArray();
        if (materialized.Length == 0)
            return "(empty payload)";
        return string.Join(" ", materialized.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string BuildAsciiReferenceTable()
    {
        var controlNames = new[]
        {
            "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL",
            "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
            "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB",
            "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US"
        };
        var rows = Enumerable.Range(0, 16).Select(row => string.Join("  ", Enumerable.Range(0, 8).Select(column =>
        {
            var value = row * 8 + column;
            var label = value < 32 ? controlNames[value] : value == 127 ? "DEL" : $"'{(char)value}'";
            return $"{value:X2} {label}";
        })));
        return string.Join(Environment.NewLine, rows);
    }
}
