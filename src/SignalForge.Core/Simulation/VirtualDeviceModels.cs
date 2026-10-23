using System.Net;

namespace SignalForge.Core.Simulation;

public enum VirtualDeviceServerState
{
    Stopped,
    Starting,
    Listening,
    ClientConnected,
    Processing,
    Faulted
}

public enum VirtualDeviceEventKind
{
    StateChanged,
    Listening,
    ClientConnected,
    ClientDisconnected,
    RequestReceived,
    ResponseSent,
    ResponseDropped,
    FaultApplied,
    ProtocolError,
    Stopped,
    Faulted
}

public enum VirtualDeviceFaultKind
{
    DelayResponse,
    DropResponse,
    CorruptResponse,
    Disconnect,
    InvalidFrame
}

public sealed record VirtualDeviceFaultRule(
    int RequestNumber,
    VirtualDeviceFaultKind Kind,
    TimeSpan Delay = default,
    int ByteOffset = 0,
    byte XorMask = 0x01);

public sealed class VirtualDeviceFaultPlan
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<VirtualDeviceFaultRule>> _rules;

    public VirtualDeviceFaultPlan(IEnumerable<VirtualDeviceFaultRule>? rules = null)
    {
        var materialized = rules?.ToArray() ?? Array.Empty<VirtualDeviceFaultRule>();
        if (materialized.Any(rule => rule.RequestNumber < 1))
            throw new ArgumentOutOfRangeException(nameof(rules), "Fault request numbers are one-based and must be positive.");
        if (materialized.Any(rule => rule.Delay < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(rules), "Fault delays cannot be negative.");
        _rules = materialized.GroupBy(rule => rule.RequestNumber)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VirtualDeviceFaultRule>)group.ToArray());
    }

    public IReadOnlyList<VirtualDeviceFaultRule> ForRequest(int requestNumber) =>
        _rules.TryGetValue(requestNumber, out var rules) ? rules : Array.Empty<VirtualDeviceFaultRule>();

    public IReadOnlyList<VirtualDeviceFaultRule> Rules => _rules.Values
        .SelectMany(rules => rules)
        .OrderBy(rule => rule.RequestNumber)
        .ToArray();
}

public sealed record VirtualDeviceServerOptions
{
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public int Port { get; init; }
    public byte UnitId { get; init; } = 1;
    public VirtualDeviceFaultPlan FaultPlan { get; init; } = new();
}

public sealed record VirtualDeviceSnapshot(
    byte UnitId,
    IReadOnlyDictionary<ushort, bool> Coils,
    IReadOnlyDictionary<ushort, bool> DiscreteInputs,
    IReadOnlyDictionary<ushort, ushort> HoldingRegisters,
    IReadOnlyDictionary<ushort, ushort> InputRegisters);

public sealed class VirtualDeviceEventArgs(
    long monotonicTicks,
    VirtualDeviceEventKind kind,
    string message,
    int? connectionId = null,
    int? requestNumber = null,
    ReadOnlyMemory<byte> payload = default,
    VirtualDeviceServerState? previousState = null,
    VirtualDeviceServerState? currentState = null) : EventArgs
{
    public long MonotonicTicks { get; } = monotonicTicks;
    public VirtualDeviceEventKind Kind { get; } = kind;
    public string Message { get; } = message;
    public int? ConnectionId { get; } = connectionId;
    public int? RequestNumber { get; } = requestNumber;
    public ReadOnlyMemory<byte> Payload { get; } = payload.ToArray();
    public VirtualDeviceServerState? PreviousState { get; } = previousState;
    public VirtualDeviceServerState? CurrentState { get; } = currentState;
}
