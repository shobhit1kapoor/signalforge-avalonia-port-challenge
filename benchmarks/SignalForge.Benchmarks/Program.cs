using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SignalForge.Core.Protocols.Modbus;

const int defaultIterations = 250_000;
var iterations = args.Length == 1
    ? int.Parse(args[0], NumberStyles.None, CultureInfo.InvariantCulture)
    : defaultIterations;
if (iterations is < 1_000 or > 10_000_000)
    throw new ArgumentOutOfRangeException(nameof(iterations), "Choose between 1000 and 10000000 iterations.");

Console.WriteLine($"SIGNALFORGE protocol microbenchmarks · {DateTimeOffset.UtcNow:O}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Processors: {Environment.ProcessorCount}");
Console.WriteLine($"Iterations per sample: {iterations.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine("Scope: in-process codec microbenchmarks only; excludes transports, UI, and storage.");
Console.WriteLine();

var request = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x02 };
var decoder = new ModbusTcpDecoder();
var tcpResult = Measure("Modbus TCP decode (12-byte request)", iterations, () =>
{
    var frames = 0;
    for (var index = 0; index < iterations; index++)
    {
        var batch = decoder.Feed(request);
        if (batch.Issues.Count != 0 || batch.Frames.Count != 1 || !batch.Frames[0].IsValid)
            throw new InvalidOperationException("Decoder output changed during the benchmark.");
        frames += batch.Frames.Count;
    }
    return frames;
});

var rtuRequest = ModbusRtuCodec.Encode(1, 3, [0x00, 0x00, 0x00, 0x02]);
var rtuResult = Measure("Modbus RTU CRC (8-byte request)", iterations, () =>
{
    var checksum = 0;
    for (var index = 0; index < iterations; index++)
        checksum ^= ModbusRtuCodec.CalculateCrc(rtuRequest.AsSpan(0, rtuRequest.Length - 2));
    return checksum;
});

Print(tcpResult);
Print(rtuResult);

static Measurement Measure(string name, int iterations, Func<int> operation)
{
    _ = operation();
    var samples = new List<Sample>(5);
    for (var sample = 0; sample < 5; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var checksum = operation();
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        samples.Add(new(stopwatch.Elapsed.TotalSeconds, allocated, checksum));
    }

    var seconds = samples.Select(item => item.Seconds).Order().ElementAt(samples.Count / 2);
    var allocatedBytes = samples.Select(item => item.AllocatedBytes).Order().ElementAt(samples.Count / 2);
    return new Measurement(name, iterations, seconds, allocatedBytes,
        samples.All(item => item.Checksum == samples[0].Checksum));
}

static void Print(Measurement measurement)
{
    var operationsPerSecond = measurement.Iterations / measurement.Seconds;
    var nanosecondsPerOperation = measurement.Seconds * 1_000_000_000 / measurement.Iterations;
    var bytesPerOperation = (double)measurement.AllocatedBytes / measurement.Iterations;
    Console.WriteLine(measurement.Name);
    Console.WriteLine($"  median sample: {operationsPerSecond.ToString("N0", CultureInfo.InvariantCulture)} operations/s");
    Console.WriteLine($"  median latency: {nanosecondsPerOperation.ToString("N1", CultureInfo.InvariantCulture)} ns/operation");
    Console.WriteLine($"  allocated: {bytesPerOperation.ToString("N1", CultureInfo.InvariantCulture)} bytes/operation");
    Console.WriteLine($"  deterministic checksum: {(measurement.Deterministic ? "yes" : "NO")}");
}

sealed record Sample(double Seconds, long AllocatedBytes, int Checksum);
sealed record Measurement(string Name, int Iterations, double Seconds, long AllocatedBytes, bool Deterministic);
