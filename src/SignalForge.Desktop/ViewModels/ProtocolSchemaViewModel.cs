using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignalForge.Core.Protocols.UserSchemas;

namespace SignalForge.Desktop.ViewModels;

public partial class ProtocolSchemaViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [ObservableProperty]
    private string _schemaPath = "";

    [ObservableProperty]
    private string _schemaJson = ExampleSchemaJson;

    [ObservableProperty]
    private string _payloadHex = "A5 5A 01 02 12 34 00 00";

    [ObservableProperty]
    private string _status = "Edit or load a schema, enter bytes, then decode to inspect the match confidence.";

    [ObservableProperty]
    private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#748296"));

    public ObservableCollection<ProtocolSchemaFieldRow> Fields { get; } = [];
    public ObservableCollection<string> Evidence { get; } = [];

    private const string ExampleSchemaJson = """
        {
          "name": "Example telemetry packet",
          "minimumLength": 8,
          "maximumLength": 8,
          "exactLength": 8,
          "signatureHex": "A5 5A",
          "signatureOffset": 0,
          "fields": [
            { "name": "Signature", "offset": 0, "length": 2, "type": "bytes", "byteOrder": "bigEndian" },
            { "name": "Version", "offset": 2, "length": 1, "type": "uInt8", "byteOrder": "bigEndian" },
            { "name": "Flags", "offset": 3, "length": 1, "type": "uInt8", "byteOrder": "bigEndian" },
            { "name": "Reading", "offset": 4, "length": 2, "type": "uInt16", "byteOrder": "bigEndian" },
            { "name": "Reserved", "offset": 6, "length": 2, "type": "bytes", "byteOrder": "bigEndian" }
          ],
          "checksum": null
        }
        """;

    [RelayCommand]
    private void Decode()
    {
        Fields.Clear();
        Evidence.Clear();
        try
        {
            var document = JsonSerializer.Deserialize<ProtocolSchemaFileModel>(SchemaJson, JsonOptions)
                ?? throw new FormatException("Schema JSON was empty.");
            var signature = ParseHex(document.SignatureHex ?? "");
            var schema = new UserProtocolSchema(
                document.Name,
                document.MinimumLength,
                document.MaximumLength,
                document.ExactLength,
                signature,
                document.SignatureOffset,
                document.Fields ?? [],
                document.Checksum);
            var payload = ParseHex(PayloadHex);
            var result = new ProtocolSchemaDecoder().Decode(payload, [schema]);

            foreach (var field in result.Fields)
                Fields.Add(new(field.Name, field.Type.ToString(), field.Offset, field.Length, field.DisplayValue));
            foreach (var item in result.Evidence)
                Evidence.Add(item);
            foreach (var item in result.Diagnostics)
                Evidence.Add($"Diagnostic · {item}");

            var integrity = result.IntegrityValid switch { true => " · CHECKSUM VALID", false => " · CHECKSUM INVALID", _ => "" };
            Status = result.Confidence == ProtocolRecognitionConfidence.Unknown
                ? $"UNKNOWN · {string.Join("; ", result.Diagnostics)}"
                : $"{result.Confidence.ToString().ToUpperInvariant()} · {result.SchemaName}{integrity} · {Fields.Count} field(s) decoded.";
            StatusBrush = result.Confidence switch
            {
                ProtocolRecognitionConfidence.Verified => new SolidColorBrush(Color.Parse("#56D4C8")),
                ProtocolRecognitionConfidence.Heuristic => new SolidColorBrush(Color.Parse("#E3B869")),
                _ => new SolidColorBrush(Color.Parse("#DE7979"))
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            Status = $"SCHEMA OR PAYLOAD ERROR · {exception.Message}";
            StatusBrush = new SolidColorBrush(Color.Parse("#DE7979"));
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SchemaPath))
                throw new ArgumentException("Enter a schema JSON file path.");
            var path = Path.GetFullPath(SchemaPath.Trim());
            SchemaJson = await File.ReadAllTextAsync(path);
            Status = $"Loaded schema JSON from {path}. Decode to validate it against the Core schema decoder.";
            StatusBrush = new SolidColorBrush(Color.Parse("#56D4C8"));
        }
        catch (Exception exception)
        {
            Status = $"SCHEMA LOAD ERROR · {exception.Message}";
            StatusBrush = new SolidColorBrush(Color.Parse("#DE7979"));
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        string? temporaryPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(SchemaPath))
                throw new ArgumentException("Enter a schema JSON file path.");
            _ = JsonSerializer.Deserialize<ProtocolSchemaFileModel>(SchemaJson, JsonOptions)
                ?? throw new FormatException("Schema JSON was empty.");
            var path = Path.GetFullPath(SchemaPath.Trim());
            var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The schema path has no parent directory.");
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Schema folder does not exist: {directory}");
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(SchemaJson);
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
            Status = $"Saved schema JSON atomically to {path}.";
            StatusBrush = new SolidColorBrush(Color.Parse("#56D4C8"));
        }
        catch (Exception exception)
        {
            Status = $"SCHEMA SAVE ERROR · {exception.Message}";
            StatusBrush = new SolidColorBrush(Color.Parse("#DE7979"));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static byte[] ParseHex(string text)
    {
        var compact = new string(text.Where(character => !char.IsWhiteSpace(character) && character is not ':' and not ',' and not '-').ToArray());
        if ((compact.Length & 1) != 0)
            throw new FormatException("Hex input must contain complete byte pairs.");
        return Convert.FromHexString(compact);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

}

public sealed record ProtocolSchemaFileModel(
    string Name,
    int MinimumLength,
    int? MaximumLength,
    int? ExactLength,
    string? SignatureHex,
    int SignatureOffset,
    ProtocolFieldDefinition[]? Fields,
    ProtocolChecksumDefinition? Checksum);

public sealed record ProtocolSchemaFieldRow(string Name, string Type, int Offset, int Length, string Value)
{
    public string Range => $"{Offset.ToString(CultureInfo.InvariantCulture)} + {Length.ToString(CultureInfo.InvariantCulture)}";
}
