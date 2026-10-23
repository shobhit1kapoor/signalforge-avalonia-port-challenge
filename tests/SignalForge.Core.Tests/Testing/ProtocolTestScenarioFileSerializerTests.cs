using SignalForge.Core.Testing;

namespace SignalForge.Core.Tests.Testing;

public sealed class ProtocolTestScenarioFileSerializerTests
{
    [Fact]
    public async Task Published_modbus_scenario_fixture_loads_as_a_runnable_recipe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "modbus-register-read.scenario.json");

        var document = await ProtocolTestScenarioFileSerializer.ReadAsync(path);
        var scenario = ProtocolTestScenarioFileSerializer.ToScenario(document.Scenario);

        Assert.Equal(ProtocolTestScenarioFileSerializer.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal("Local Modbus simulator · loopback only", document.Source);
        Assert.Collection(scenario.Steps,
            send => Assert.Equal("000100000006010300000002", Convert.ToHexString(Assert.IsType<SendBytesStep>(send).Bytes.ToArray())),
            assert => Assert.Equal((byte)3, Assert.IsType<ExpectModbusTcpStep>(assert).FunctionCode));
    }

    [Fact]
    public async Task Scenario_file_round_trips_all_supported_steps_and_source_options()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"signalforge-scenario-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "nested", "scenario.json");
        var scenario = new ProtocolTestScenario("Modbus read", [
            new SendBytesStep("request", new byte[] { 0x00, 0x01, 0x03 }),
            new ExpectBytesStep("masked response", new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xF0, 0xF0 }, TimeSpan.FromMilliseconds(1750)),
            new ExpectModbusTcpStep("check fields", 9, 1, 3, 2, TimeSpan.FromMilliseconds(2200)),
            new DelayTestStep("settle", TimeSpan.FromMilliseconds(30))
        ], disconnectWhenDone: false);
        var document = new ProtocolTestScenarioFile(
            ProtocolTestScenarioFileSerializer.CurrentSchemaVersion,
            DateTimeOffset.Parse("2026-10-23T12:00:00Z"),
            "Local Modbus simulator · loopback only",
            "Corrupt Modbus function code on response 1",
            "1.5",
            ProtocolTestScenarioFileSerializer.ToConfiguration(scenario));

        try
        {
            var savedPath = await ProtocolTestScenarioFileSerializer.WriteAsync(path, document);
            var loaded = await ProtocolTestScenarioFileSerializer.ReadAsync(path);
            var restored = ProtocolTestScenarioFileSerializer.ToScenario(loaded.Scenario);

            Assert.Equal(Path.GetFullPath(path), savedPath);
            Assert.Equal(document.Source, loaded.Source);
            Assert.Equal(document.SimulatorFaultPlan, loaded.SimulatorFaultPlan);
            Assert.Equal(document.ReplaySpeed, loaded.ReplaySpeed);
            Assert.Equal(scenario.Name, restored.Name);
            Assert.False(restored.DisconnectWhenDone);
            Assert.Collection(restored.Steps,
                send => Assert.Equal(new byte[] { 0x00, 0x01, 0x03 }, ((SendBytesStep)send).Bytes.ToArray()),
                expect =>
                {
                    var actual = Assert.IsType<ExpectBytesStep>(expect);
                    Assert.Equal(new byte[] { 0x01, 0x03, 0x02, 0x12, 0x34 }, actual.Expected.ToArray());
                    Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xF0, 0xF0 }, actual.Mask.ToArray());
                    Assert.Equal(TimeSpan.FromMilliseconds(1750), actual.Timeout);
                },
                modbus =>
                {
                    var actual = Assert.IsType<ExpectModbusTcpStep>(modbus);
                    Assert.Equal((ushort)9, actual.TransactionId);
                    Assert.Equal((byte)1, actual.UnitId);
                    Assert.Equal((byte)3, actual.FunctionCode);
                    Assert.Equal((byte)2, actual.ExceptionCode);
                    Assert.Equal(TimeSpan.FromMilliseconds(2200), actual.Timeout);
                },
                delay => Assert.Equal(TimeSpan.FromMilliseconds(30), Assert.IsType<DelayTestStep>(delay).Duration));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Scenario_conversion_rejects_mask_length_mismatch_and_non_sequential_steps()
    {
        var badMask = new ProtocolTestScenarioConfiguration("invalid", true, [
            new ProtocolTestStepConfiguration(1, "expectBytes", "response", 1000,
                ExpectedBytesBase64: Convert.ToBase64String(new byte[] { 0x01, 0x02 }),
                MaskBase64: Convert.ToBase64String(new byte[] { 0xFF }))
        ]);
        var badNumber = new ProtocolTestScenarioConfiguration("invalid", true, [
            new ProtocolTestStepConfiguration(2, "sendBytes", "request", BytesBase64: "AQ==")
        ]);

        Assert.Throws<InvalidDataException>(() => ProtocolTestScenarioFileSerializer.ToScenario(badMask));
        Assert.Throws<InvalidDataException>(() => ProtocolTestScenarioFileSerializer.ToScenario(badNumber));
    }

    [Fact]
    public async Task Reader_rejects_unsupported_schema_without_changing_the_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"signalforge-scenario-version-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "future.json");
        const string content = """{"schemaVersion":99,"savedAtUtc":"2026-10-23T12:00:00Z","source":"Local Modbus simulator · loopback only","simulatorFaultPlan":"None","replaySpeed":"1.0","scenario":{"name":"future","disconnectWhenDone":true,"steps":[{"number":1,"kind":"sendBytes","name":"request","bytesBase64":"AQ=="}]}}""";
        await File.WriteAllTextAsync(path, content);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProtocolTestScenarioFileSerializer.ReadAsync(path));
            Assert.Contains("Unsupported scenario schema version 99", error.Message, StringComparison.Ordinal);
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
