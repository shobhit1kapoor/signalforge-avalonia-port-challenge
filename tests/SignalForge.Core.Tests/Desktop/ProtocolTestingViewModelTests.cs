using System.Text.Json;
using SignalForge.Desktop.ViewModels;
using SignalForge.Core.Simulation;

namespace SignalForge.Core.Tests.Desktop;

public sealed class ProtocolTestingViewModelTests
{
    [Fact]
    public async Task Local_simulator_test_shows_pass_then_explains_corrupted_function_code()
    {
        var viewModel = new ProtocolTestingViewModel();

        await viewModel.RunTestCommand.ExecuteAsync(null);

        Assert.Equal("PASS", viewModel.RunState);
        Assert.Equal(2, viewModel.Results.Count);
        Assert.All(viewModel.Results, result => Assert.True(result.Passed, result.Detail));
        Assert.Contains(viewModel.Evidence, evidence => evidence.DirectionLabel == "RX");

        viewModel.SelectedSimulatorFaultPlan = "Corrupt Modbus function code on response 1";
        Assert.Contains("Modbus TCP has no checksum", viewModel.SimulatorFaultDescription, StringComparison.Ordinal);

        await viewModel.RunTestCommand.ExecuteAsync(null);

        Assert.Equal("FAIL", viewModel.RunState);
        Assert.Equal(2, viewModel.Results.Count);
        Assert.True(viewModel.Results[0].Passed, viewModel.Results[0].Detail);
        Assert.False(viewModel.Results[1].Passed);
        Assert.Contains("function 0x02 != 0x03", viewModel.Results[1].Detail, StringComparison.Ordinal);
        Assert.Contains(viewModel.Evidence, evidence => evidence.DirectionLabel == "RX" && evidence.Hex.Contains("01 02 04 12 34 BE EF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Modbus_simulator_renders_real_state_transitions_and_exports_fault_setup()
    {
        var viewModel = new ProtocolTestingViewModel(dispatchToUi: action => action());
        viewModel.SelectedSimulatorFaultPlan = "Corrupt Modbus function code on response 1";
        var folder = Path.Combine(Path.GetTempPath(), $"signalforge-device-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        viewModel.ReportPath = Path.Combine(folder, "faulted-run.json");

        try
        {
            await viewModel.RunTestCommand.ExecuteAsync(null);

            Assert.Equal("FAIL", viewModel.RunState);
            Assert.Equal(VirtualDeviceServerState.Stopped, viewModel.DeviceServerState);
            Assert.Contains(viewModel.DeviceActivity, item => item.Transition == "Stopped → Starting");
            Assert.Contains(viewModel.DeviceActivity, item => item.Transition == "Starting → Listening");
            Assert.Contains(viewModel.DeviceActivity, item => item.Transition == "ClientConnected → Processing");
            Assert.Contains(viewModel.DeviceActivity, item => item.Kind == "RequestReceived" && item.PayloadHex.Contains("01 03", StringComparison.Ordinal));
            Assert.Contains(viewModel.DeviceActivity, item => item.Kind == "FaultApplied" && item.PayloadHex.Contains("02", StringComparison.Ordinal));

            await viewModel.ExportReportCommand.ExecuteAsync(null);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(viewModel.ReportPath));
            var source = document.RootElement.GetProperty("sourceConfiguration");
            Assert.Equal("modbusTcpLoopback", source.GetProperty("kind").GetString());
            Assert.Equal(1, source.GetProperty("unitId").GetByte());
            var fault = Assert.Single(source.GetProperty("faults").EnumerateArray());
            Assert.Equal("corruptResponse", fault.GetProperty("kind").GetString());
            Assert.Equal(1, fault.GetProperty("requestNumber").GetInt32());
            Assert.Equal(7, fault.GetProperty("byteOffset").GetInt32());
            Assert.Equal(1, fault.GetProperty("xorMask").GetByte());
            Assert.Equal(0x1234, source.GetProperty("initialDeviceState").GetProperty("holdingRegisters").GetProperty("0").GetUInt16());
            Assert.Equal(0xBEEF, source.GetProperty("initialDeviceState").GetProperty("holdingRegisters").GetProperty("1").GetUInt16());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
