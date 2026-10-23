using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalForge.Core.Testing;

/// <summary>A reusable, versioned protocol-test recipe, without the evidence from a run.</summary>
public sealed record ProtocolTestScenarioFile(
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    string Source,
    string SimulatorFaultPlan,
    string ReplaySpeed,
    ProtocolTestScenarioConfiguration Scenario);

/// <summary>Converts protocol-test recipes and reads/writes their portable JSON representation.</summary>
public static class ProtocolTestScenarioFileSerializer
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 1_048_576;
    public const int MaximumSteps = 256;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static ProtocolTestScenarioConfiguration ToConfiguration(ProtocolTestScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var steps = scenario.Steps.Select((step, index) => step switch
        {
            SendBytesStep send => new ProtocolTestStepConfiguration(index + 1, "sendBytes", send.Name,
                BytesBase64: Convert.ToBase64String(send.Bytes.ToArray())),
            ExpectBytesStep expect => new ProtocolTestStepConfiguration(index + 1, "expectBytes", expect.Name,
                expect.Timeout.TotalMilliseconds,
                ExpectedBytesBase64: Convert.ToBase64String(expect.Expected.ToArray()),
                MaskBase64: Convert.ToBase64String(expect.Mask.ToArray())),
            ExpectModbusTcpStep expect => new ProtocolTestStepConfiguration(index + 1, "expectModbusTcp", expect.Name,
                expect.Timeout.TotalMilliseconds,
                TransactionId: expect.TransactionId,
                UnitId: expect.UnitId,
                FunctionCode: expect.FunctionCode,
                ExceptionCode: expect.ExceptionCode),
            DelayTestStep delay => new ProtocolTestStepConfiguration(index + 1, "delay", delay.Name,
                DelayMilliseconds: delay.Duration.TotalMilliseconds),
            _ => throw new NotSupportedException($"Cannot save unsupported test step type {step.GetType().Name}.")
        }).ToArray();

        return new ProtocolTestScenarioConfiguration(scenario.Name, scenario.DisconnectWhenDone, steps);
    }

    public static ProtocolTestScenario ToScenario(ProtocolTestScenarioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.Name))
            throw new InvalidDataException("Scenario name is required.");
        if (configuration.Name.Length > 256)
            throw new InvalidDataException("Scenario name exceeds the 256-character limit.");
        if (configuration.Steps is null || configuration.Steps.Count is 0 or > MaximumSteps)
            throw new InvalidDataException($"A scenario must contain between 1 and {MaximumSteps} steps.");

        var steps = new List<ProtocolTestStep>(configuration.Steps.Count);
        var totalPayloadBytes = 0L;
        for (var index = 0; index < configuration.Steps.Count; index++)
        {
            var step = configuration.Steps[index]
                ?? throw new InvalidDataException($"Step {index + 1} is missing.");
            if (step.Number != index + 1)
                throw new InvalidDataException($"Step numbers must be sequential; expected {index + 1}.");
            if (string.IsNullOrWhiteSpace(step.Name))
                throw new InvalidDataException($"Step {index + 1} needs a name.");
            if (step.Name.Length > 256)
                throw new InvalidDataException($"Step {index + 1} name exceeds the 256-character limit.");

            ProtocolTestStep convertedStep = step.Kind switch
            {
                "sendBytes" => CreateSendStep(step, index + 1),
                "expectBytes" => CreateExpectBytesStep(step, index + 1),
                "expectModbusTcp" => CreateModbusStep(step, index + 1),
                "delay" => new DelayTestStep(step.Name, ReadDuration(step.DelayMilliseconds, $"step {index + 1} delay", allowZero: true, maximumMilliseconds: 300_000)),
                _ => throw new InvalidDataException($"Step {index + 1} has unsupported kind '{step.Kind}'.")
            };
            totalPayloadBytes += convertedStep switch
            {
                SendBytesStep send => send.Bytes.Length,
                ExpectBytesStep expect => (long)expect.Expected.Length + expect.Mask.Length,
                _ => 0
            };
            if (totalPayloadBytes > MaximumFileBytes / 2)
                throw new InvalidDataException("Scenario payloads exceed the supported aggregate byte limit.");
            steps.Add(convertedStep);
        }

        return new ProtocolTestScenario(configuration.Name, steps, configuration.DisconnectWhenDone);
    }

    public static async Task<string> WriteAsync(
        string path,
        ProtocolTestScenarioFile scenarioFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(scenarioFile);
        ValidateFile(scenarioFile);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new DirectoryNotFoundException("Scenario file path has no parent folder.");
        Directory.CreateDirectory(directory);
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
                await JsonSerializer.SerializeAsync(stream, scenarioFile, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var size = new FileInfo(tempPath).Length;
            if (size > MaximumFileBytes)
                throw new InvalidDataException($"Scenario file exceeds the {MaximumFileBytes:N0}-byte size limit.");
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

    public static async Task<ProtocolTestScenarioFile> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("Scenario file was not found.", fullPath);
        if (info.Length > MaximumFileBytes)
            throw new InvalidDataException($"Scenario file exceeds the {MaximumFileBytes:N0}-byte size limit.");

        await using var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 16 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException($"Scenario file exceeds the {MaximumFileBytes:N0}-byte size limit.");
        var scenarioFile = await JsonSerializer.DeserializeAsync<ProtocolTestScenarioFile>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Scenario file is empty.");
        ValidateFile(scenarioFile);
        return scenarioFile;
    }

    private static ExpectBytesStep CreateExpectBytesStep(ProtocolTestStepConfiguration step, int number)
    {
        var expected = DecodeBytes(step.ExpectedBytesBase64, $"step {number} expected bytes");
        var mask = DecodeBytes(step.MaskBase64, $"step {number} mask");
        if (mask.Length != 0 && mask.Length != expected.Length)
            throw new InvalidDataException($"Step {number} mask length must match expected bytes.");
        return new ExpectBytesStep(step.Name, expected, mask, ReadDuration(step.TimeoutMilliseconds, $"step {number} timeout", allowZero: false, maximumMilliseconds: 120_000));
    }

    private static ExpectModbusTcpStep CreateModbusStep(ProtocolTestStepConfiguration step, int number) =>
        new(step.Name, step.TransactionId, step.UnitId, step.FunctionCode, step.ExceptionCode,
            ReadDuration(step.TimeoutMilliseconds, $"step {number} timeout", allowZero: false, maximumMilliseconds: 120_000));

    private static SendBytesStep CreateSendStep(ProtocolTestStepConfiguration step, int number)
    {
        var bytes = DecodeBytes(step.BytesBase64, $"step {number} bytes");
        if (bytes.Length == 0)
            throw new InvalidDataException($"Step {number} must send at least one byte.");
        return new SendBytesStep(step.Name, bytes);
    }

    private static byte[] DecodeBytes(string? value, string description)
    {
        if (value is null)
            throw new InvalidDataException($"{description} are missing.");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > MaximumFileBytes / 2)
                throw new InvalidDataException($"{description} exceed the allowed size.");
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{description} are not valid Base64.", exception);
        }
    }

    private static TimeSpan ReadDuration(double? milliseconds, string description, bool allowZero, int maximumMilliseconds)
    {
        if (milliseconds is not { } value || double.IsNaN(value) || double.IsInfinity(value)
            || value < (allowZero ? 0 : double.Epsilon) || value > maximumMilliseconds || value != Math.Truncate(value))
            throw new InvalidDataException($"{description} must be an integer number of milliseconds, {(allowZero ? "at least 0" : "greater than 0")}, and no more than {maximumMilliseconds:N0} ms.");
        return TimeSpan.FromMilliseconds(value);
    }

    private static void ValidateFile(ProtocolTestScenarioFile file)
    {
        if (file.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported scenario schema version {file.SchemaVersion}; expected {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(file.Source) || file.Source.Length > 128)
            throw new InvalidDataException("Scenario source is required and must be at most 128 characters.");
        if (string.IsNullOrWhiteSpace(file.SimulatorFaultPlan) || file.SimulatorFaultPlan.Length > 128
            || string.IsNullOrWhiteSpace(file.ReplaySpeed) || file.ReplaySpeed.Length > 32)
            throw new InvalidDataException("Scenario source options are incomplete.");
        _ = ToScenario(file.Scenario ?? throw new InvalidDataException("Scenario definition is missing."));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            MaxDepth = 32
        };
    }
}
