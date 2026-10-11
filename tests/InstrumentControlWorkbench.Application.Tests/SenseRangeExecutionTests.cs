using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class SenseRangeExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task NativeFixedSenseRangeExecutesPairedScanThenFreshArchiveRetainsActualInitialRange(SourceFunction function)
    {
        var folder = Path.Combine(Path.GetTempPath(), "sense-range-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(peer, archive);
            var plan = b.Scan(function); var path = Path.Combine(folder, "plan.json"); await b.Documents.SaveAsync(path, plan, Token);
            var loaded = await b.Documents.LoadAsync(path, Token); Assert.Equal(plan.Preparations[0].Sense, loaded.Preparations[0].Sense);
            await b.Runs.StartPlanAsync(loaded, Token); var data = b.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count); Assert.False(peer.Output);
            var state = await b.Source.GetSnapshotAsync(Token); Assert.Equal(plan.Preparations[0].Sense.ManualRange, state.Sense.ManualRange);
            Assert.All(data.Rows, row => { Assert.Equal(peer.ActualSource, row.Values[new(function == SourceFunction.Voltage ? "voltage" : "current")].Value); Assert.Equal(peer.ActualSense, row.Values[new(function == SourceFunction.Voltage ? "current" : "voltage")].Value); });
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(data.Id, Token); var device = Assert.Single(saved.DataSet.Instruments);
            Assert.Equal("Manual", device.Configuration["sense_range_mode"]);
            var rangeId = Keithley2450Parameters.ManualSenseRange(state.Sense.Function).Id;
            Assert.Equal(state.Sense.ManualRange!.Value, Assert.Single(device.Parameters, p => p.Descriptor.Id == rangeId).Value);
            Assert.Equal(loaded.Preparations[0].Sense, saved.DataSet.Plan!.Preparations[0].Sense);
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); Assert.Contains(rangeId.Value, await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
            await b.Source.DisconnectAsync(Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public void OldAutomaticAndRetainedRangeNativeDocumentsRemainUnambiguous()
    {
        var documents = new JsonExperimentPlanDocument();
        foreach (var auto in new[] { true, false })
        {
            var plan = new ExperimentPlan("Legacy range", new DelayStep(new("wait"), TimeSpan.Zero), [new(new("smu"), SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, auto))]);
            var text = documents.Serialize(plan); Assert.DoesNotContain("manualRange", text); var restored = documents.Parse(text);
            Assert.Equal(auto, restored.Preparations[0].Sense.AutoRange); Assert.Null(restored.Preparations[0].Sense.ManualRange);
        }
    }
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task SessionReadsWritesAndExplicitAutoActionUseTheActualSenseFunction(SourceFunction function)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(function);
        await b.Registry.ConnectAsync(b.Source.Id, Token); await b.Manager.ConfigureAsync(prep.ToSetpoint(), prep.Sense, Token);
        var parameter = Keithley2450Parameters.ManualSenseRange(prep.Sense.Function);
        Assert.Equal(prep.Sense.ManualRange!.Value, await b.Registry.ReadAsync(b.Source.Id, parameter.Id, Token));
        Assert.Equal(0, (await b.Registry.ReadAsync(b.Source.Id, Keithley2450Parameters.SenseAutoRange.Id, Token)).Value);
        await b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.EnableSenseAutoRange.Id, new(1, PhysicalUnit.Dimensionless), Token);
        Assert.Equal(1, (await b.Registry.ReadAsync(b.Source.Id, Keithley2450Parameters.SenseAutoRange.Id, Token)).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.ReadAsync(b.Source.Id, parameter.Id, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.EnableSenseAutoRange.Id, new(0, PhysicalUnit.Dimensionless), Token));
        await b.Registry.WriteAsync(b.Source.Id, parameter.Id, prep.Sense.ManualRange.Value, Token);
        Assert.Equal(prep.Sense.ManualRange.Value, await b.Registry.ReadAsync(b.Source.Id, parameter.Id, Token));
        var inactive = function == SourceFunction.Voltage ? Keithley2450Parameters.ManualSenseVoltageRange : Keithley2450Parameters.ManualSenseCurrentRange;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, inactive.Id, new(inactive.Minimum!.Value.Value, inactive.Unit), Token));
        await b.Source.DisconnectAsync(Token); Assert.Empty((await b.Manager.GetDeviceStatusAsync(Token)).Values); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Manager.CaptureParametersAsync(Token));
    }
    [Theory]
    [InlineData(0.000001)]
    [InlineData(0.002)]
    public async Task StaticComplianceBoundsRejectBeforeConnectionEvenWhenTheLevelAlsoChanges(double compliance)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var scan = b.Scan(SourceFunction.Voltage);
        var plan = new ExperimentPlan(scan.Name, new SequenceStep(new("outer"), [Write("limit", b.Source.Id, Keithley2450Parameters.CurrentCompliance, new ConstantValue(new(compliance, PhysicalUnit.Ampere))), scan.Root]), scan.Preparations);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task DynamicComplianceCannotChangeTheSourceOutsideItsActualFixedSenseRange()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        var plan = new ExperimentPlan("Dynamic range bounds", new SequenceStep(new("root"), [new AssignValueStep(new("value"), new("limit"), new ConstantValue(new(0.002, PhysicalUnit.Ampere))), new SourceOutputStep(new("on"), b.Source.Id, true), Write("write", b.Source.Id, Keithley2450Parameters.CurrentCompliance, new VariableValue(new("limit")))]), [b.Preparation(SourceFunction.Voltage)]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.DoesNotContain(peer.Server.Commands, c => c.StartsWith(":SOUR:VOLT:ILIM 0.002", StringComparison.Ordinal)); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedRangeOrImplicitSourceLimitChangeFailsBeforeOutput(bool changesLimit)
    {
        await using var peer = new Keithley2450ScpiPeer(); peer.IgnoreSenseRangeWrite = !changesLimit; peer.SenseWriteChangesCompliance = changesLimit ? 20e-6 : null; var b = new Bench(peer);
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Scan(SourceFunction.Voltage), Token));
        Assert.Contains("sense configuration", error.Message); Assert.DoesNotContain(":OUTP ON", peer.Server.Commands); Assert.False(peer.Output); Assert.Null(b.Runs.LatestDataSet); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task OwnedLiveSenseRangeChangesPreserveThePreparedSourceAndAcquireARealPair()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(SourceFunction.Voltage);
        var plan = new ExperimentPlan("Live sense span", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), Write("range", b.Source.Id, Keithley2450Parameters.ManualSenseCurrentRange, new ConstantValue(new(0.01, PhysicalUnit.Ampere))), b.Pair(SourceFunction.Voltage)]), [prep]);
        await b.Runs.StartPlanAsync(plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output);
        var state = await b.Source.GetSnapshotAsync(Token); Assert.Equal(prep.ToSetpoint(), state.Source); Assert.Equal(new(0.01, PhysicalUnit.Ampere), state.Sense.ManualRange); Assert.Single(peer.Server.Commands, c => c == ":OUTP ON"); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SenseRangeDriftDuringSingleOrPairedAcquisitionDiscardsTheReading(bool paired)
    {
        await using var peer = new Keithley2450ScpiPeer(); var measured = false;
        peer.Override = (command, _) => { if (command.StartsWith(":MEAS?", StringComparison.Ordinal)) measured = true; return measured && command == ":SENS:CURR:RANG?" ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("0.01")) : null; };
        var b = new Bench(peer); ExperimentStep read = paired ? b.Pair(SourceFunction.Voltage) : new ReadStep(new("read"), [new(new("current"), new(b.Source.Id, Keithley2450Parameters.MeasuredCurrent.Id))]);
        var plan = new ExperimentPlan("Range drift", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), read]), [b.Preparation(SourceFunction.Voltage)]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Contains("changed during acquisition", error.Message);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task PausedManualSenseDriftDeniesOperatorWritesAndOutputRestoration()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); using var events = b.Hub.Subscribe(); var scan = b.Scan(SourceFunction.Voltage);
        var plan = new ExperimentPlan(scan.Name, new SequenceStep(new("outer"), [scan.Root, new DelayStep(new("dwell"), TimeSpan.FromMinutes(1))]), scan.Preparations); var running = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.ManualSenseCurrentRange.Id, new(0.01, PhysicalUnit.Ampere), Token));
            var state = await b.Source.GetSnapshotAsync(Token); await b.Source.ConfigureSenseAsync(state.Sense with { ManualRange = new(0.01, PhysicalUnit.Ampere) }, Token);
            b.Runs.Resume(); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Contains("changed while paused", error.Message);
            Assert.Single(peer.Server.Commands, c => c == ":OUTP ON"); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        }
        finally { if (!running.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => running); } await b.Source.DisconnectAsync(Token); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PpmsOwnedOrScanManagedResistanceSelectsNumericSenseRangeWithoutChangingTheActiveExcitation(bool managed)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(SourceFunction.Voltage);
        var node = Measurement("meas_r", new() { ["mode"] = "volt_dc", ["voltage_v"] = 0.02, ["compliance_a"] = 0.0001, ["current_range_a"] = "0.0005", ["channel"] = "none", ["sensing_mode"] = "4wire", ["settle_s"] = 0 });
        if (managed) node = new JsonObject { ["type"] = "scan", ["name"] = "scan_voltage", ["params"] = new JsonObject { ["points"] = new JsonArray(0.01, 0.03), ["channel"] = "none", ["compliance_a"] = 0.0001 }, ["children"] = new JsonArray(node) };
        var imported = b.Workspace.Import(node.ToJsonString(), b.Bindings(prep)); Assert.Equal(0, peer.Server.Connections);
        await b.Runs.StartPlanAsync(b.Documents.Parse(imported.Text), Token); Assert.Equal(managed ? 2 : 1, b.Runs.LatestDataSet!.Rows.Count); var state = await b.Source.GetSnapshotAsync(Token);
        Assert.Equal(new(0.001, PhysicalUnit.Ampere), state.Sense.ManualRange); Assert.Equal(managed ? 0.03 : 0.02, state.Source.Level); Assert.Equal(0.0001, state.Source.Compliance); Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, "measure_current", "meter_range_a", "0.0005", 0.001)]
    [InlineData(SourceFunction.Current, "measure_voltage", "meter_range_v", "3", 20)]
    public async Task StandalonePpmsMeterRangesConfigureComplementarySensingAndPreserveActualData(SourceFunction function, string name, string key, string selector, double expected)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(function);
        var node = new JsonObject { ["type"] = "sequence", ["children"] = new JsonArray(new JsonObject { ["type"] = "command", ["name"] = "k2450.set_output", ["params"] = new JsonObject { ["enabled"] = true } }, Measurement(name, new() { ["instrument"] = "k2450", ["sensing_mode"] = "4wire", [key] = selector })) };
        var document = b.Workspace.Import(node.ToJsonString(), b.Bindings(prep)); await b.Runs.StartPlanAsync(document.Plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
        var state = await b.Source.GetSnapshotAsync(Token); Assert.Equal(expected, state.Sense.ManualRange!.Value.Value); Assert.Equal(peer.ActualSense, b.Runs.LatestDataSet.Rows[0].Values[new("ppms.root.1.value")].Value); Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("NaN")]
    [InlineData("wrong")]
    public async Task InvalidPpmsSenseSelectorsRetainLineDiagnosticsBeforeAnyIo(string selector)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var node = Measurement("meas_r", new() { ["mode"] = "volt_dc", ["meter_range_a"] = selector }); node["source_line"] = 9;
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings(b.Preparation(SourceFunction.Voltage)))); Assert.Contains("source line 9", error.Message); Assert.Contains("ppms.root", error.Message); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, SenseFunction.Current, 0.0001, PhysicalUnit.Ampere)]
    [InlineData(SourceFunction.Current, SenseFunction.Voltage, 20, PhysicalUnit.Volt)]
    public async Task LegacyFixedModeResolvesTheRequestedFunctionsExistingRangeFromHardware(SourceFunction source, SenseFunction sense, double range, PhysicalUnit unit)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); await b.Source.ConnectAsync(Token);
        await b.Source.ConfigureSourceAsync(new(source, 0, source == SourceFunction.Voltage ? 105e-6 : 21), Token);
        await b.Source.ConfigureSenseAsync(new(sense, SensingMode.TwoWire, 0.5, false), Token);
        var state = await b.Source.GetSnapshotAsync(Token); Assert.False(state.Sense.AutoRange); Assert.Equal(new(range, unit), state.Sense.ManualRange);
        var parameters = await b.Manager.CaptureParametersAsync(Token); Assert.Equal(new(range, unit), Assert.Single(parameters.Parameters, p => p.Descriptor.Id == Keithley2450Parameters.ManualSenseRange(sense).Id).Value);
        await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task ConsecutiveRunsCanReprepareAcrossDifferentSourceAndFixedSenseFunctionsWithoutStaleRangeConstraints()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        foreach (var function in new[] { SourceFunction.Voltage, SourceFunction.Current, SourceFunction.Voltage })
        {
            var plan = b.Scan(function); await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count);
            var state = await b.Source.GetSnapshotAsync(Token); Assert.Equal(plan.Preparations[0].ToSetpoint() with { Level = 0.03 }, state.Source); Assert.Equal(plan.Preparations[0].Sense, state.Sense); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun);
        }
        await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidNativeSenseRangesRejectAtPreflightWithoutConnecting(bool nonNominal)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(SourceFunction.Voltage);
        var parameter = nonNominal ? Keithley2450Parameters.ManualSenseCurrentRange : Keithley2450Parameters.ManualSenseVoltageRange;
        var value = new Quantity(nonNominal ? 0.0005 : 20, parameter.Unit);
        var plan = new ExperimentPlan("Invalid sense range", Write("range", b.Source.Id, parameter, new ConstantValue(value)), [prep]);
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)); Assert.IsAssignableFrom<ArgumentException>(error); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task PublishedCombinedRangeFixtureLoadsAndRunsAgainstOnlyItsConfiguredSimulation()
    {
        var started = await new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new())
            .StartAsync(Path.Combine(AppContext.BaseDirectory, "devices", "workbench-devices.json"), null, Token);
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "source-sense-range-scan.plan.json"), Token);
        await started.Runs.StartPlanAsync(plan, Token); Assert.Equal(3, started.Runs.LatestDataSet!.Rows.Count);
        var device = Assert.Single(started.Runs.LatestDataSet.Instruments); Assert.Equal("Manual", device.Configuration["sense_range_mode"]); Assert.Equal("Manual", device.Configuration["source_range_mode"]);
        Assert.Equal(new(0.001, PhysicalUnit.Ampere), await started.Devices.Registry.ReadAsync(device.InstrumentId, Keithley2450Parameters.ManualSenseCurrentRange.Id, Token));
        foreach (var other in started.Devices.Registry.Devices.Where(d => d.Id != device.InstrumentId)) Assert.False((await started.Devices.Registry.GetStatusAsync(other.Id, Token)).Connected);
        Assert.False(started.Runs.HasActiveRun);
    }
    [Fact]
    public async Task PhysicalOverflowReplyNeverBecomesAFiniteManualRangeRow()
    {
        await using var peer = new Keithley2450ScpiPeer { ActualSense = 9.9e37 }; var b = new Bench(peer);
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Scan(SourceFunction.Voltage), Token)); Assert.Contains("overrange", error.Message);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveSenseSideEffectFaultUsesIndependentStopAndRetainsFailedCleanupUntilRecovery(bool refuseOff)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        peer.Override = (command, _) => { if (command == ":OUTP ON") { peer.SenseWriteChangesCompliance = 20e-6; peer.RefuseOff = refuseOff; } return null; };
        var plan = new ExperimentPlan("Live range side effect", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), Write("range", b.Source.Id, Keithley2450Parameters.ManualSenseCurrentRange, new ConstantValue(new(0.01, PhysicalUnit.Ampere))), b.Pair(SourceFunction.Voltage)]), [b.Preparation(SourceFunction.Voltage)]);
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)); Assert.NotNull(error); Assert.Contains("sense configuration", error.ToString()); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        if (refuseOff)
        {
            Assert.True(peer.Output); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow.AddYears(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.EnableSenseAutoRange.Id, new(1, PhysicalUnit.Dimensionless), Token));
            peer.RefuseOff = false; await b.Runs.RecoverSafetyAsync(Token);
        }
        Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task AnUnchangedFixedSenseRangeSurvivesPauseResumeAndAbortWithVerifiedOutputRestoration()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); using var events = b.Hub.Subscribe(); var prep = b.Preparation(SourceFunction.Voltage);
        var plan = new ExperimentPlan("Fixed sense pause", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), b.Pair(SourceFunction.Voltage), new DelayStep(new("wait"), TimeSpan.FromMinutes(1))]), [prep]); var running = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); Assert.False(peer.Output);
            b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); Assert.True(peer.Output); Assert.Equal(prep.Sense, (await b.Source.GetSnapshotAsync(Token)).Sense);
            b.Runs.Abort(); await running; Assert.False(peer.Output); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!running.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => running); } await b.Source.DisconnectAsync(Token); }
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, 1)]
    [InlineData(SourceFunction.Current, 0.02)]
    public async Task LiteralSenseSpansCannotForceComplianceAdjustmentEvenWhenTheSourceLevelAlsoChanges(SourceFunction function, double span)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var scan = b.Scan(function); var parameter = Keithley2450Parameters.ManualSenseRange(scan.Preparations[0].Sense.Function);
        var plan = new ExperimentPlan("Bad span with scan", new SequenceStep(new("outer"), [Write("range", b.Source.Id, parameter, new ConstantValue(new(span, parameter.Unit))), scan.Root]), scan.Preparations);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands); Assert.False(b.Runs.HasActiveRun);
    }
    private static SetParameterStep Write(string id, InstrumentId device, ParameterDescriptor parameter, PlanValue value) => new(new(id), new(device, parameter.Id), value);
    private static JsonObject Measurement(string name, JsonObject parameters) => new() { ["type"] = "measurement", ["name"] = name, ["params"] = parameters };
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private sealed class Bench
    {
        public TcpKeithley2450SourceMeter Source { get; }
        public InstrumentManager Manager { get; }
        public DeviceRegistry Registry { get; }
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public MeasurementRunService Runs { get; }
        public JsonExperimentPlanDocument Documents { get; } = new();
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench(Keithley2450ScpiPeer peer, IRunResultListener? listener = null)
        {
            Source = new(new("tcp-smu"), "Sense range source", peer.Server.Configuration()); Manager = new(Source, Hub); Registry = new([Manager], Leases); Runs = new(Manager, Leases, Hub, listener is null ? null : [listener], deviceRegistry: Registry); Workspace = new(Documents, Runs, new PpmsExperimentPlanImporter());
        }
        public SourcePreparation Preparation(SourceFunction function) => new(Source.Id, function, new(0, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere), new(function == SourceFunction.Voltage ? 105e-6 : 21, function == SourceFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage, SensingMode.FourWire, 1, false, new(function == SourceFunction.Voltage ? 0.001 : 20, function == SourceFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt)));
        public ReadSourceSenseStep Pair(SourceFunction function) => new(new("read"), Source.Id, function, new("voltage"), new("current"));
        public ExperimentPlan Scan(SourceFunction function)
        {
            var parameter = function == SourceFunction.Voltage ? Keithley2450Parameters.SourceVoltage : Keithley2450Parameters.SourceCurrent;
            return new("Fixed sense range scan", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), new ScanStep(new("scan"), new(Source.Id, parameter.Id), new("requested"), new([new(-0.03, parameter.Unit), new(0.01, parameter.Unit), new(0.03, parameter.Unit)]), TimeSpan.Zero, Pair(function))]), [Preparation(function)]);
        }
        public ExperimentImportBindings Bindings(SourcePreparation preparation) => new(new Dictionary<string, InstrumentId> { ["k2450"] = Source.Id }, [], [preparation]);
    }
}
