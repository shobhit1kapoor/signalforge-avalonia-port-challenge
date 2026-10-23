using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SignalForge.Core.Simulation;
using SignalForge.Core.Transport;

namespace SignalForge.Core.Testing;

public enum ProtocolTestReportState
{
    Passed,
    Failed,
    Cancelled
}

/// <summary>A versioned, self-contained JSON report for one protocol test run.</summary>
public sealed record ProtocolTestRunReport(
    int SchemaVersion,
    Guid RunId,
    DateTimeOffset CreatedAtUtc,
    ProtocolTestReportState State,
    string Source,
    string? SourceReference,
    string Endpoint,
    string TransportName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ProtocolTestScenarioConfiguration Scenario,
    IReadOnlyList<ProtocolTestReportStepResult> StepResults,
    IReadOnlyList<ProtocolTestReportEvidence> Evidence,
    ProtocolTestSourceConfiguration? SourceConfiguration = null);

/// <summary>Reproduction inputs for the local source used by a protocol test.</summary>
public sealed record ProtocolTestSourceConfiguration(
    string Kind,
    byte? UnitId,
    IReadOnlyList<ProtocolTestFaultConfiguration> Faults,
    VirtualDeviceSnapshot? InitialDeviceState = null);

public sealed record ProtocolTestFaultConfiguration(
    VirtualDeviceFaultKind Kind,
    int? RequestNumber = null,
    double? DelayMilliseconds = null,
    int? ByteOffset = null,
    byte? XorMask = null,
    string? Description = null);

public sealed record ProtocolTestScenarioConfiguration(
    string Name,
    bool DisconnectWhenDone,
    IReadOnlyList<ProtocolTestStepConfiguration> Steps);

/// <summary>
/// Flat, extensible step representation. Byte arrays are base64 encoded, including
/// empty arrays, so configuration can be reconstructed without text parsing.
/// </summary>
public sealed record ProtocolTestStepConfiguration(
    int Number,
    string Kind,
    string Name,
    double? TimeoutMilliseconds = null,
    string? BytesBase64 = null,
    string? ExpectedBytesBase64 = null,
    string? MaskBase64 = null,
    ushort? TransactionId = null,
    byte? UnitId = null,
    byte? FunctionCode = null,
    byte? ExceptionCode = null,
    double? DelayMilliseconds = null);

public sealed record ProtocolTestReportStepResult(
    int StepNumber,
    string Name,
    bool Passed,
    string Detail,
    string ExpectedBytesBase64,
    string ObservedBytesBase64,
    double? StartedAfterMilliseconds,
    double? CompletedAfterMilliseconds);

public sealed record ProtocolTestReportEvidence(
    int EvidenceNumber,
    int StepNumber,
    string StepName,
    ByteDirection Direction,
    double ElapsedMilliseconds,
    int ByteCount,
    string BytesBase64,
    string Hex,
    string Detail,
    ProtocolTestTimelineReference? Timeline);

/// <summary>An explicit link from captured bytes to the application's trace timeline.</summary>
public sealed record ProtocolTestTimelineReference(string Source, long EventSequence);

public static class ProtocolTestReportFactory
{
    public const int CurrentSchemaVersion = 2;

    public static ProtocolTestRunReport Create(
        ProtocolTestScenario scenario,
        ProtocolTestResult result,
        string source,
        string endpoint,
        IReadOnlyList<ProtocolTestTimelineReference?>? evidenceTimeline = null,
        DateTimeOffset? createdAtUtc = null,
        string? sourceReference = null,
        ProtocolTestSourceConfiguration? sourceConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(endpoint);

        var created = (createdAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var currentTicks = Stopwatch.GetTimestamp();
        var startedUtc = created - Stopwatch.GetElapsedTime(result.StartedAtTicks, currentTicks);

        DateTimeOffset UtcAt(long ticks) => ticks <= result.StartedAtTicks
            ? startedUtc
            : startedUtc + Stopwatch.GetElapsedTime(result.StartedAtTicks, ticks);

        double? ElapsedAt(long ticks) => ticks <= 0
            ? null
            : Math.Max(0, Stopwatch.GetElapsedTime(result.StartedAtTicks, ticks).TotalMilliseconds);

        var configuredSteps = scenario.Steps.Select((step, index) => ToConfiguration(index + 1, step)).ToArray();
        var stepResults = result.Steps.Select(step => new ProtocolTestReportStepResult(
            step.StepNumber,
            step.Name,
            step.Passed,
            step.Detail,
            Convert.ToBase64String(step.Expected.ToArray()),
            Convert.ToBase64String(step.Observed.ToArray()),
            ElapsedAt(step.StartedAtTicks),
            ElapsedAt(step.CompletedAtTicks))).ToArray();

        var evidence = result.Evidence.Select((item, index) =>
        {
            var bytes = item.Bytes.ToArray();
            var timeline = evidenceTimeline is not null && index < evidenceTimeline.Count
                ? evidenceTimeline[index]
                : null;
            return new ProtocolTestReportEvidence(
                index + 1,
                item.StepNumber,
                item.StepName,
                item.Direction,
                ElapsedAt(item.MonotonicTicks) ?? 0,
                bytes.Length,
                Convert.ToBase64String(bytes),
                Convert.ToHexString(bytes),
                item.Detail,
                timeline);
        }).ToArray();

        return new ProtocolTestRunReport(
            CurrentSchemaVersion,
            Guid.NewGuid(),
            created,
            result.Cancelled ? ProtocolTestReportState.Cancelled
                : result.Passed ? ProtocolTestReportState.Passed
                : ProtocolTestReportState.Failed,
            source,
            sourceReference,
            endpoint,
            result.TransportName,
            startedUtc,
            UtcAt(result.CompletedAtTicks),
            new ProtocolTestScenarioConfiguration(scenario.Name, scenario.DisconnectWhenDone, configuredSteps),
            stepResults,
            evidence,
            sourceConfiguration);
    }

    private static ProtocolTestStepConfiguration ToConfiguration(int number, ProtocolTestStep step) => step switch
    {
        SendBytesStep send => new(number, "sendBytes", send.Name, BytesBase64: Convert.ToBase64String(send.Bytes.ToArray())),
        ExpectBytesStep expect => new(number, "expectBytes", expect.Name,
            expect.Timeout.TotalMilliseconds,
            ExpectedBytesBase64: Convert.ToBase64String(expect.Expected.ToArray()),
            MaskBase64: Convert.ToBase64String(expect.Mask.ToArray())),
        ExpectModbusTcpStep expect => new(number, "expectModbusTcp", expect.Name,
            expect.Timeout.TotalMilliseconds,
            TransactionId: expect.TransactionId,
            UnitId: expect.UnitId,
            FunctionCode: expect.FunctionCode,
            ExceptionCode: expect.ExceptionCode),
        DelayTestStep delay => new(number, "delay", delay.Name, DelayMilliseconds: delay.Duration.TotalMilliseconds),
        _ => throw new NotSupportedException($"Cannot export unsupported test step type {step.GetType().Name}.")
    };
}

/// <summary>Writes reports using a same-directory temporary file and atomic replacement.</summary>
public static class ProtocolTestReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<string> WriteAsync(
        string path,
        ProtocolTestRunReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(report), "Report schema version must be positive.");

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The report folder does not exist: {directory ?? fullPath}");

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Preserve the original write or replace exception.
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
