using System.Text.Json;

namespace SignalForge.Core.Traces;

public static class TraceReader
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<TraceDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var events = new List<TraceEvent>();
        TraceMetadata? metadata = null;
        long previousSequence = 0;
        var lineNumber = 0;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            TraceLine? record;
            try
            {
                record = JsonSerializer.Deserialize<TraceLine>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Trace line {lineNumber} is not valid JSON.", ex);
            }

            if (record is null || record.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Trace line {lineNumber} uses an unsupported schema version.");

            if (record.Type == "header" && metadata is null && record.Metadata is not null)
            {
                metadata = record.Metadata;
                continue;
            }

            if (record.Type == "event" && record.Event is { } traceEvent && metadata is not null)
            {
                if (traceEvent.Sequence != previousSequence + 1)
                    throw new InvalidDataException($"Trace event sequence is discontinuous at line {lineNumber}.");
                previousSequence = traceEvent.Sequence;
                events.Add(traceEvent);
                continue;
            }

            throw new InvalidDataException($"Trace line {lineNumber} has an unexpected record type or order.");
        }

        if (metadata is null)
            throw new InvalidDataException("Trace header is missing.");

        return new TraceDocument(metadata, events);
    }

    private sealed record TraceLine(int SchemaVersion, string Type, TraceMetadata? Metadata, TraceEvent? Event);
}
