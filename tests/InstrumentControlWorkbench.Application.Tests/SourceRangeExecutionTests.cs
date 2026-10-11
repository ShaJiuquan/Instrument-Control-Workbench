using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class SourceRangeExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Fact]
    public async Task PublishedExampleRunsAgainstTheConfiguredSimulatorWithoutConnectingUnrelatedDevices()
    {
        var started = await new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new())
            .StartAsync(Path.Combine(AppContext.BaseDirectory, "devices", "workbench-devices.json"), null, Token);
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "source-range-scan.plan.json"), Token);
        await started.Runs.StartPlanAsync(plan, Token); Assert.Equal(3, started.Runs.LatestDataSet!.Rows.Count);
        var device = Assert.Single(started.Runs.LatestDataSet.Instruments); Assert.Equal("Manual", device.Configuration["source_range_mode"]); Assert.Equal(new("smu-primary"), device.InstrumentId);
        Assert.Equal(new(0.2, PhysicalUnit.Volt), await started.Devices.Registry.ReadAsync(device.InstrumentId, Keithley2450Parameters.ManualVoltageRange.Id, Token));
        foreach (var other in started.Devices.Registry.Devices.Where(d => d.Id != device.InstrumentId)) Assert.False((await started.Devices.Registry.GetStatusAsync(other.Id, Token)).Connected);
        Assert.False(started.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, 200, 1)]
    [InlineData(SourceFunction.Current, 1, 100)]
    public async Task StaticComplianceCannotExceedHighRangeCapabilityEvenWhenLevelsChange(SourceFunction function, double range, double limit)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var scan = b.Scan(function, range);
        var parameter = function == SourceFunction.Voltage ? Keithley2450Parameters.CurrentCompliance : Keithley2450Parameters.VoltageCompliance;
        var plan = new ExperimentPlan("Range-limited compliance", new SequenceStep(new("outer"), [new SetParameterStep(new("limit"), new(b.Source.Id, parameter.Id), new ConstantValue(new(limit, parameter.Unit))), scan.Root]), scan.Preparations);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task SavedManualRangePlanExecutesAcrossActualTcpThenReopensItsRangeAndObservations(SourceFunction function)
    {
        var folder = Path.Combine(Path.GetTempPath(), "source-range-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(peer, archive);
            var plan = b.Scan(function, 0.2); var path = Path.Combine(folder, "plan.json"); await b.Documents.SaveAsync(path, plan, Token);
            var restored = await b.Documents.LoadAsync(path, Token); Assert.Equal(plan.Preparations[0].ManualSourceRange, restored.Preparations[0].ManualSourceRange);
            await b.Runs.StartPlanAsync(restored, Token); var data = b.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count); Assert.False(peer.Output);
            var prefix = function == SourceFunction.Voltage ? ":SOUR:VOLT:RANG " : ":SOUR:CURR:RANG "; Assert.Single(peer.Server.Commands, c => c.StartsWith(prefix, StringComparison.Ordinal));
            var actual = await b.Source.GetSnapshotAsync(Token); Assert.Equal(restored.Preparations[0].ManualSourceRange, actual.Source.ManualRange);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(data.Id, Token);
            var device = Assert.Single(saved.DataSet.Instruments); Assert.Equal("Manual", device.Configuration["source_range_mode"]);
            var rangeId = function == SourceFunction.Voltage ? Keithley2450Parameters.ManualVoltageRange.Id : Keithley2450Parameters.ManualCurrentRange.Id;
            Assert.Equal(actual.Source.ManualRange!.Value, Assert.Single(device.Parameters, p => p.Descriptor.Id == rangeId).Value);
            Assert.All(saved.DataSet.Rows, row => Assert.Equal(peer.ActualSource, row.Values[new(function == SourceFunction.Voltage ? "voltage" : "current")].Value));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); Assert.Contains(rangeId.Value, await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
            await b.Source.DisconnectAsync(Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public void AutomaticPreparationRetainsTheOldNativeDocumentShape()
    {
        var plan = new ExperimentPlan("Auto", new DelayStep(new("delay"), TimeSpan.Zero), [new(new("smu"), SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
        var doc = new JsonExperimentPlanDocument(); var text = doc.Serialize(plan); Assert.DoesNotContain("manualSourceRange", text); Assert.Null(doc.Parse(text).Preparations[0].ManualSourceRange);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRangeBoundsRejectAllStaticScanPointsEvenWhenComplianceAlsoChanges(bool changesCompliance)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var plan = b.Scan(SourceFunction.Voltage, 0.02);
        if (changesCompliance) plan = new(plan.Name, new SequenceStep(new("outer"), [new SetParameterStep(new("compliance"), new(b.Source.Id, Keithley2450Parameters.CurrentCompliance.Id), new ConstantValue(new(0.001, PhysicalUnit.Ampere))), plan.Root]), plan.Preparations);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task DynamicLevelOutsideTheActualManualRangeFailsBeforeWritingAndStillConfirmsOutputOff()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(SourceFunction.Voltage, 0.02);
        var plan = new ExperimentPlan("Dynamic invalid level", new SequenceStep(new("root"), [new AssignValueStep(new("value"), new("target"), new ConstantValue(new(0.1, PhysicalUnit.Volt))), new SourceOutputStep(new("on"), b.Source.Id, true), new SetParameterStep(new("write"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new VariableValue(new("target")))]), [prep]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.DoesNotContain(peer.Server.Commands, c => c.StartsWith(":SOUR:VOLT:LEV 0.1", StringComparison.Ordinal)); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task ManualParameterReadsAndExplicitAutomaticActionUseTheSameOwnedSession(SourceFunction function)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); await b.Registry.ConnectAsync(b.Source.Id, Token);
        var prep = b.Preparation(function, 0.2); await b.Manager.ConfigureAsync(prep.ToSetpoint() with { ManualRange = null }, prep.Sense, Token);
        var parameter = function == SourceFunction.Voltage ? Keithley2450Parameters.ManualVoltageRange : Keithley2450Parameters.ManualCurrentRange;
        await b.Registry.WriteAsync(b.Source.Id, parameter.Id, prep.ManualSourceRange!.Value, Token);
        Assert.Equal(prep.ManualSourceRange.Value, await b.Registry.ReadAsync(b.Source.Id, parameter.Id, Token)); Assert.Equal(0, (await b.Registry.ReadAsync(b.Source.Id, Keithley2450Parameters.SourceAutoRange.Id, Token)).Value);
        await b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.EnableSourceAutoRange.Id, new(1, PhysicalUnit.Dimensionless), Token);
        Assert.Equal(1, (await b.Registry.ReadAsync(b.Source.Id, Keithley2450Parameters.SourceAutoRange.Id, Token)).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.ReadAsync(b.Source.Id, parameter.Id, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.EnableSourceAutoRange.Id, new(0, PhysicalUnit.Dimensionless), Token));
        await b.Registry.DisconnectAsync(b.Source.Id, Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedRangeOrLiveRangeChangesCannotEnableOrReconfigureOutput(bool liveChange)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        if (!liveChange)
        {
            peer.IgnoreRangeWrite = true; await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Scan(SourceFunction.Voltage, 0.2), Token));
            Assert.Null(b.Runs.LatestDataSet); Assert.DoesNotContain(":OUTP ON", peer.Server.Commands); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun);
        }
        else
        {
            await b.Source.ConnectAsync(Token); await b.Source.EnableOutputAsync(Token); var before = peer.Server.Commands.Count;
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Source.ConfigureSourceAsync(b.Preparation(SourceFunction.Voltage, 0.2).ToSetpoint(), Token));
            Assert.All(peer.Server.Commands.Skip(before), c => Assert.EndsWith("?", c)); Assert.True(peer.Output); await b.Source.SafeStopAsync(Token);
        }
        await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task PausedRangeDriftPreventsOutputRestorationAndReleasesOnlyAfterStopConfirmation()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); using var events = b.Hub.Subscribe();
        var scan = b.Scan(SourceFunction.Voltage, 0.2); var plan = new ExperimentPlan(scan.Name, new SequenceStep(new("outer"), [scan.Root, new DelayStep(new("dwell"), TimeSpan.FromMinutes(1))]), scan.Preparations);
        var running = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            Assert.False(peer.Output); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.ManualVoltageRange.Id, new(20, PhysicalUnit.Volt), Token));
            var state = await b.Source.GetSnapshotAsync(Token); await b.Source.ConfigureSourceAsync(state.Source with { ManualRange = new(20, PhysicalUnit.Volt) }, Token);
            b.Runs.Resume(); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Contains("changed while paused", error.Message);
            Assert.Single(peer.Server.Commands, c => c == ":OUTP ON"); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        }
        finally { if (!running.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => running); } await b.Source.DisconnectAsync(Token); }
    }
    [Fact]
    public async Task ManualRangeChangedDuringAcquisitionInvalidatesTheFrameBeforeAnyRow()
    {
        await using var peer = new Keithley2450ScpiPeer(); var measured = false;
        peer.Override = (command, _) =>
        {
            if (command == ":MEAS? \"defbuffer1\", SOUR, READ, SOURSTAT, STAT") measured = true;
            return measured && command == ":SOUR:VOLT:RANG?" ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("20")) : null;
        };
        var b = new Bench(peer); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Scan(SourceFunction.Voltage, 0.2), Token));
        Assert.Contains("changed during acquisition", error.Message); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task ImportedApproximateRangeScansAndExplicitAutomaticResetExecuteOnTheRealProtocolPath(SourceFunction function)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(function, 0.2);
        var bindings = new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k2450"] = b.Source.Id }, [], [new(b.Source.Id, function, prep.Level, prep.Compliance, prep.Sense)]);
        var voltage = function == SourceFunction.Voltage; var p = new JsonObject { ["points"] = new JsonArray(-0.03, 0.01, 0.03), ["channel"] = "none", ["settle_s"] = 0, [voltage ? "source_range_v" : "source_range_a"] = "0.05", [voltage ? "compliance_a" : "compliance_v"] = voltage ? 105e-6 : 21 };
        if (!voltage) p["current_mode"] = "bias";
        var root = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = voltage ? "scan_voltage" : "scan_current",
            ["params"] = p,
            ["children"] = new JsonArray(new JsonObject { ["type"] = "measurement", ["name"] = voltage ? "meas_current" : "meas_voltage", ["params"] = new JsonObject { ["instrument"] = "k2450", ["sensing_mode"] = "4wire" } })
        };
        var workspace = new ExperimentPlanWorkspace(b.Documents, b.Runs, new PpmsExperimentPlanImporter()); var imported = workspace.Import(root.ToJsonString(), bindings);
        Assert.Equal(0, peer.Server.Connections); await b.Runs.StartPlanAsync(b.Documents.Parse(imported.Text), Token);
        var actual = await b.Source.GetSnapshotAsync(Token); Assert.Equal(new(voltage ? 0.2 : 0.1, voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere), actual.Source.ManualRange);
        Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.False(peer.Output);
        var reset = new JsonObject { ["type"] = "command", ["name"] = voltage ? "set_voltage" : "set_current", ["params"] = new JsonObject { ["instrument"] = "k2450", ["channel"] = "none", [voltage ? "target_v" : "target_a"] = 0.01, [voltage ? "compliance_a" : "compliance_v"] = voltage ? 105e-6 : 21, [voltage ? "source_range_v" : "source_range_a"] = "auto" } };
        await b.Runs.StartPlanAsync(workspace.Import(reset.ToJsonString(), bindings).Plan, Token); Assert.Null((await b.Source.GetSnapshotAsync(Token)).Source.ManualRange); Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("201")]
    [InlineData("NaN")]
    [InlineData("not a range")]
    public async Task InvalidImportedRangeRetainsPathDiagnosticsAndPerformsNoIo(string range)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(SourceFunction.Voltage, 0.2);
        var bindings = new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k2450"] = b.Source.Id }, [], [prep]);
        var root = new JsonObject { ["type"] = "scan", ["name"] = "scan_voltage", ["source_line"] = 9, ["params"] = new JsonObject { ["points"] = new JsonArray(0.01), ["channel"] = "none", ["source_range_v"] = range } };
        var error = Assert.Throws<FormatException>(() => new ExperimentPlanWorkspace(b.Documents, b.Runs, new PpmsExperimentPlanImporter()).Import(root.ToJsonString(), bindings));
        Assert.Contains("source line 9", error.Message); Assert.Contains("ppms.root", error.Message); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands); Assert.False(b.Runs.HasActiveRun);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private sealed class Bench
    {
        public TcpKeithley2450SourceMeter Source { get; }
        public InstrumentManager Manager { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public JsonExperimentPlanDocument Documents { get; } = new();
        public Bench(Keithley2450ScpiPeer peer, IRunResultListener? listener = null)
        { Source = new(new("tcp-smu"), "Range source", peer.Server.Configuration()); Manager = new(Source, Hub); Registry = new([Manager], Leases); Runs = new(Manager, Leases, Hub, listener is null ? null : [listener], deviceRegistry: Registry); }
        public SourcePreparation Preparation(SourceFunction function, double range) => new(Source.Id, function, new(0, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere), new(function == SourceFunction.Voltage ? 105e-6 : 21, function == SourceFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage, SensingMode.FourWire, 1, true), Keithley2450SourceRanges.Resolve(function, new(range, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere)));
        public ExperimentPlan Scan(SourceFunction function, double range)
        {
            var parameter = function == SourceFunction.Voltage ? Keithley2450Parameters.SourceVoltage : Keithley2450Parameters.SourceCurrent;
            return new("Manual range scan", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), new ScanStep(new("scan"), new(Source.Id, parameter.Id), new("requested"), new([new(-0.03, parameter.Unit), new(0.01, parameter.Unit), new(0.03, parameter.Unit)]), TimeSpan.Zero, new ReadSourceSenseStep(new("read"), Source.Id, function, new("voltage"), new("current")))]), [Preparation(function, range)]);
        }
    }
}
