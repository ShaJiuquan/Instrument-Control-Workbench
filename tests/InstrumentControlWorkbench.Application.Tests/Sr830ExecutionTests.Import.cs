using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Sr830ExecutionTests
{
    [Theory]
    [InlineData("sr830.set_sensitivity", "sensitivity_v", .005)]
    [InlineData("sr830.set_sensitivity", "sensitivity", .005)]
    [InlineData("sr830.set_time_constant", "time_constant_s", .3)]
    [InlineData("sr830.set_time_constant", "time_constant", .3)]
    [InlineData("sr830.set_filter_slope", "filter_slope_db", 18)]
    [InlineData("sr830.set_filter_slope", "filter_slope", 18)]
    public async Task PpmsPassiveSettingsAliasesExecuteThroughOwnedSession(string name, string key, double value)
    {
        var b = new Bench(); var command = ImportCommand(b, name, new JsonObject { [key] = value }); var step = Assert.IsType<SetParameterStep>(command.Root);
        await b.Runs.StartPlanAsync(b.Plan(step, b.Frame()), Token); var frame = Assert.Single(b.Runs.LatestDataSet!.Rows);
        var suffix = name.EndsWith("sensitivity", StringComparison.Ordinal) ? ".sensitivity_v" : name.EndsWith("time_constant", StringComparison.Ordinal) ? ".time_constant_s" : ".filter_slope_db";
        Assert.Equal(value, frame.Values[new("read" + suffix)].Value); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("a", Sr830InputMode.VoltageA)]
    [InlineData("a-b", Sr830InputMode.VoltageAB)]
    [InlineData("i-1m", Sr830InputMode.Current1M)]
    [InlineData("i-100m", Sr830InputMode.Current100M)]
    public async Task PpmsInputAliasPreservesExactModeAndRawVoltageUnits(string mode, Sr830InputMode expected)
    {
        var b = new Bench(); var imported = ImportCommand(b, "sr830.set_input_config", new JsonObject { ["config"] = mode });
        await b.Runs.StartPlanAsync(b.Plan(imported.Root, b.Frame(input: expected)), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal((int)expected, row.Values[new("read.input_mode")].Value); Assert.Equal(PhysicalUnit.Volt, row.Values[new("read.x_v")].Unit);
    }
    [Theory]
    [InlineData("numeric")]
    [InlineData("input")]
    public void AmbiguousPpmsParameterAliasesDoNotReachTheDriver(string kind)
    {
        var b = new Bench(); var p = kind == "numeric" ? new JsonObject { ["sensitivity_v"] = .005, ["sensitivity"] = .005 } : new JsonObject { ["input_config"] = "a", ["config"] = "a" };
        Assert.Throws<FormatException>(() => ImportCommand(b, kind == "numeric" ? "sr830.set_sensitivity" : "sr830.set_input_config", p)); Assert.Equal(0, b.Meter.Connections);
    }
    [Fact]
    public void PassiveProfileCannotExecuteReferenceOscillatorCommands()
    {
        var b = new Bench(); Assert.Throws<FormatException>(() => ImportCommand(b, "sr830.configure_ref", new JsonObject { ["amplitude_v"] = .01 }));
        Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(b.Plan(new SourceOutputStep(new("on"), b.Meter.Descriptor.Id, true)))); Assert.Equal(0, b.Meter.Connections);
    }
    [Fact]
    public async Task Current100MRejectsAutomaticGainChangeBeforeConfigurationAndFrameRead()
    {
        var b = new Bench(); var plan = b.Plan(b.Write("input", Sr830Parameters.InputMode, (int)Sr830InputMode.Current100M), b.Write("sensitivity", Sr830Parameters.Sensitivity, .02), b.Frame(input: Sr830InputMode.Current100M));
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(0, b.Meter.Reads); Assert.Equal(.01, b.Meter.Settings.Sensitivity.Value); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ExplicitConfiguredLockInBuildsWithoutIoAndRejectsHardwareFallback()
    {
        var config = new DeviceConfiguration(new("sr830"), "SR830", "Passive input simulator", ConfiguredDeviceCategory.LockIn, new SimulationDeviceConnection()); var catalog = new DeviceConfigurationCatalog(null, [config], [KeyValuePair.Create("sr830", config.Id)]);
        var factory = new ConfiguredDeviceFactory(); var runtime = new ConfiguredDeviceRuntime(catalog, factory, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), new NotificationHub());
        Assert.True(runtime.Registry.SupportsSr830Frame(config.Id)); Assert.Empty(runtime.Sources); Assert.False((await runtime.Registry.GetStatusAsync(config.Id, Token)).Connected);
        var run = new MeasurementRunService(runtime.Registry, new NotificationHub()); await run.StartPlanAsync(new("Configured input", new ReadSr830FrameStep(new("read"), config.Id, Sr830InputMode.VoltageAB, new("raw"))), Token); Assert.Single(run.LatestDataSet!.Rows);
        Assert.Throws<NotSupportedException>(() => factory.Validate(new DeviceConfiguration(config.Id, config.Model, config.DisplayName, config.Category, new TcpDeviceConnection("127.0.0.1", 1234, "\n", TimeSpan.FromSeconds(1)))));
    }
    private static ExperimentPlan ImportCommand(Bench b, string name, JsonObject parameters) => new PpmsExperimentPlanImporter().Parse(new JsonObject { ["type"] = "command", ["name"] = name, ["source_line"] = 9, ["params"] = parameters }.ToJsonString(), new ExperimentImportBindings([KeyValuePair.Create("sr830", b.Meter.Descriptor.Id)], []));
}
