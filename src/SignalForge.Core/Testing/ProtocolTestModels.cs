using SignalForge.Core.Transport;

namespace SignalForge.Core.Testing;

public abstract record ProtocolTestStep(string Name, TimeSpan Timeout);

public sealed record SendBytesStep(string StepName, ReadOnlyMemory<byte> Bytes)
    : ProtocolTestStep(StepName, TimeSpan.Zero);

public sealed record ExpectBytesStep(
    string StepName,
    ReadOnlyMemory<byte> Expected,
    ReadOnlyMemory<byte> Mask = default,
    TimeSpan ExpectedTimeout = default)
    : ProtocolTestStep(StepName, ExpectedTimeout == default ? TimeSpan.FromSeconds(2) : ExpectedTimeout);

public sealed record ExpectModbusTcpStep(
    string StepName,
    ushort? TransactionId = null,
    byte? UnitId = null,
    byte? FunctionCode = null,
    byte? ExceptionCode = null,
    TimeSpan ExpectedTimeout = default)
    : ProtocolTestStep(StepName, ExpectedTimeout == default ? TimeSpan.FromSeconds(2) : ExpectedTimeout);

public sealed record DelayTestStep(string StepName, TimeSpan Duration)
    : ProtocolTestStep(StepName, TimeSpan.Zero);

public sealed class ProtocolTestScenario(string name, IEnumerable<ProtocolTestStep> steps, bool disconnectWhenDone = true)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Scenario name is required.", nameof(name)) : name.Trim();
    public IReadOnlyList<ProtocolTestStep> Steps { get; } = steps?.ToArray() ?? throw new ArgumentNullException(nameof(steps));
    public bool DisconnectWhenDone { get; } = disconnectWhenDone;
}

public sealed record ProtocolTestEvidence(
    int StepNumber,
    string StepName,
    ByteDirection Direction,
    long MonotonicTicks,
    ReadOnlyMemory<byte> Bytes,
    string Detail);

public sealed record ProtocolTestStepResult(
    int StepNumber,
    string Name,
    bool Passed,
    string Detail,
    ReadOnlyMemory<byte> Expected = default,
    ReadOnlyMemory<byte> Observed = default,
    long StartedAtTicks = 0,
    long CompletedAtTicks = 0);

public sealed class ProtocolTestResult(
    string scenarioName,
    string transportName,
    IEnumerable<ProtocolTestStepResult> steps,
    IEnumerable<ProtocolTestEvidence> evidence,
    long startedAtTicks,
    long completedAtTicks,
    bool cancelled = false)
{
    public string ScenarioName { get; } = scenarioName;
    public string TransportName { get; } = transportName;
    public IReadOnlyList<ProtocolTestStepResult> Steps { get; } = steps.ToArray();
    public IReadOnlyList<ProtocolTestEvidence> Evidence { get; } = evidence.ToArray();
    public long StartedAtTicks { get; } = startedAtTicks;
    public long CompletedAtTicks { get; } = completedAtTicks;
    public bool Cancelled { get; } = cancelled;
    public bool Passed => !Cancelled && Steps.All(step => step.Passed);
    public int PassedStepCount => Steps.Count(step => step.Passed);
    public int FailedStepCount => Steps.Count(step => !step.Passed);
}
