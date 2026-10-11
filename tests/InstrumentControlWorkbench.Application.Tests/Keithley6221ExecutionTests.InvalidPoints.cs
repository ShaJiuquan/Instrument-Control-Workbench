using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Theory]
    [InlineData(InstrumentReadFailureKind.ReadTimeout, null, 2, "timeout")]
    [InlineData(InstrumentReadFailureKind.BufferTimeout, null, 2, "timeout")]
    [InlineData(InstrumentReadFailureKind.CommandRejected, -221, 2, "settings_conflict")]
    [InlineData(InstrumentReadFailureKind.CommandRejected, -241, 1, "measurement_error")]
    [InlineData(InstrumentReadFailureKind.CommandRejected, 419, 1, "trigger_link_error")]
    [InlineData(InstrumentReadFailureKind.CommandRejected, 301, 1, "overrange")]
    [InlineData(InstrumentReadFailureKind.Overrange, null, 1, "overrange")]
    [InlineData(InstrumentReadFailureKind.InvalidResponse, null, 1, "measurement_error")]
    [InlineData(InstrumentReadFailureKind.VisaIoRead, null, 1, "measurement_error")]
    public async Task RecoverableTerminalReadsContinueWithAbsentElectricalValuesAndRealFailureProof(InstrumentReadFailureKind kind, int? code, int attempts, string status)
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, kind, "No electrical measurement", code);
        await b.Runs.StartPlanAsync(RecoveryPlan(b), Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var failure = Assert.Single(row.AcquisitionFailures);
        Assert.Equal(attempts, failure.Attempts); Assert.Equal(attempts, b.Meter.ReadCalls); Assert.Equal(status, failure.Status);
        Assert.Equal(kind, failure.Kind); Assert.Equal(code, failure.CommandCode); Assert.Equal(CurrentSourceModel.Keithley6221, failure.SourceConfiguration.Model);
        Assert.Equal(ReadingQualityState.ContainsFailedAcquisitions, row.QualityState); Assert.Empty(row.Quality); Assert.Empty(row.IndependentResistance);
        Assert.Single(row.Values); Assert.True(row.Values.ContainsKey(new("elapsed")));
        Assert.True(failure.FailedAt <= failure.SourceConfirmedAfter && failure.SourceConfirmedAfter <= failure.IdleConfirmedAt && failure.IdleConfirmedAt <= row.Timestamp);
        Assert.NotNull(failure.SourceOffConfirmedAt); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ManagedTwoPointScanKeepsParentExcitationAndCurrentIdentityAcrossInvalidPoints()
    {
        var listener = new SampleRowListener(); await using var b = new Bench(listener);
        listener.Row = async row => { Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Single(row.AcquisitionFailures); };
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Both attempts failed");
        var node = new JsonObject { ["type"] = "scan", ["name"] = "scan_current", ["params"] = new JsonObject { ["points"] = new JsonArray(1e-5, 2e-5), ["channel"] = "ch1", ["settle_s"] = 0 }, ["children"] = new JsonArray(JsonNode.Parse(ResistanceJson(new() { ["settle_s"] = 20 }))) };
        await b.Runs.StartPlanAsync(new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog())), Token);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(2, rows.Count); Assert.Equal(4, b.Meter.ReadCalls); Assert.Equal(1, b.Source.Enables);
        Assert.Equal(new[] { 1e-5, 2e-5 }, rows.Select(r => r.AcquisitionFailures.Single().SourceConfiguration.Level));
        Assert.All(rows, r => { Assert.Null(r.AcquisitionFailures.Single().SourceOffConfirmedAt); Assert.Equal(3, r.Environment.Count); });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(SampleTopology.TwoWire, "meas_r_2w", 0, 1)]
    [InlineData(SampleTopology.FourWire, "meas_r_4w", 0, 1)]
    [InlineData(SampleTopology.HallBar, "meas_r_hallbar", 0, 2)]
    [InlineData(SampleTopology.HallBar, "meas_r_hallbar", 1, 2)]
    [InlineData(SampleTopology.VanDerPauw, "meas_r_vdp", 0, 8)]
    [InlineData(SampleTopology.VanDerPauw, "meas_r_vdp", 4, 8)]
    public async Task FailedSampleInputKeepsItsSelectionAndOnlyCalculatesAvailableResults(SampleTopology topology, string name, int failedInput, int inputs)
    {
        await using var b = new Bench(columns: 6);
        b.Meter.OnRead = _ => b.Meter.ReadCalls == failedInput + 1 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Input unavailable")) : Task.CompletedTask;
        var plan = new PpmsExperimentPlanImporter().Parse(SampleJson(name, new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(topology)));
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(inputs == 1 ? 1 : inputs + 1, rows.Count); Assert.Equal(inputs, b.Meter.ReadCalls);
        var failed = rows[failedInput]; Assert.Single(failed.AcquisitionFailures); Assert.Empty(failed.IndependentResistance); Assert.Empty(failed.Quality); Assert.NotNull(failed.Routing); Assert.Equal(3, failed.Environment.Count);
        Assert.DoesNotContain(failed.AcquisitionFailures[0].ResistanceColumn, failed.Values.Keys);
        if (inputs > 1)
        {
            var summary = rows[^1]; Assert.Equal(SampleAnalysisIssues.FailedAcquisition, summary.Analysis!.Issues);
            Assert.Single(summary.AcquisitionFailures); Assert.Equal(inputs - 1, summary.IndependentResistance.Count);
            if (topology == SampleTopology.HallBar)
            {
                Assert.Equal(failedInput != 0, summary.Values.Keys.Any(c => c.Value.EndsWith("LongitudinalResistance", StringComparison.Ordinal)));
                Assert.Equal(failedInput != 1, summary.Values.Keys.Any(c => c.Value.EndsWith("TransverseResistance", StringComparison.Ordinal)));
                Assert.Equal(failedInput == 1, summary.Values.Keys.Any(c => c.Value.EndsWith("SheetResistance", StringComparison.Ordinal)));
                Assert.Equal(failedInput == 1, summary.Values.Keys.Any(c => c.Value.EndsWith("Resistivity", StringComparison.Ordinal)));
            }
            else
            {
                Assert.Equal(failedInput >= 4, summary.Values.Keys.Any(c => c.Value.EndsWith("HorizontalMeanResistance", StringComparison.Ordinal)));
                Assert.Equal(failedInput < 4, summary.Values.Keys.Any(c => c.Value.EndsWith("VerticalMeanResistance", StringComparison.Ordinal)));
                Assert.DoesNotContain(summary.Values.Keys, c => c.Value.EndsWith("SheetResistance", StringComparison.Ordinal) || c.Value.EndsWith("Resistivity", StringComparison.Ordinal));
            }
        }
        Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet.State); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(SampleTopology.HallBar, "meas_r_hallbar", 2)]
    [InlineData(SampleTopology.VanDerPauw, "meas_r_vdp", 8)]
    public async Task EntireFailedGroupRetainsEveryInputWithoutFabricatingAnyDerivedValue(SampleTopology topology, string name, int inputs)
    {
        await using var b = new Bench(columns: 6); b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.InvalidResponse, "No response");
        var plan = new PpmsExperimentPlanImporter().Parse(SampleJson(name, new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(topology)));
        await b.Runs.StartPlanAsync(plan, Token); var summary = b.Runs.LatestDataSet!.Rows[^1];
        Assert.Equal(inputs, b.Meter.ReadCalls); Assert.Equal(inputs, summary.AcquisitionFailures.Count); Assert.Single(summary.Values);
        Assert.Empty(summary.IndependentResistance); Assert.Empty(summary.Quality); Assert.Equal(SampleAnalysisIssues.FailedAcquisition, summary.Analysis!.Issues);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("meter")]
    [InlineData("route")]
    [InlineData("wrongDevice")]
    [InlineData("generic")]
    [InlineData("unlistedCode")]
    public async Task InvalidPointRecoveryNeverSuppressesDriftOrUnqualifiedErrors(string change)
    {
        await using var b = new Bench();
        b.Meter.OnRead = async _ =>
        {
            if (change == "source") await b.Source.ConfigureAsync(new(new(2e-5, PhysicalUnit.Ampere), b.Preparation.Settings.Compliance, b.Preparation.Settings.ManualRange), Token);
            if (change == "meter") await b.Meter.ConfigureAsync((await b.Meter.GetSnapshotAsync(Token)).Settings!.WithParameter(Keithley2182Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless)), Token);
            if (change == "route") await b.Matrix.OpenAsync([b.Route.Closed[0]], Token);
            if (change == "generic") throw new TimeoutException("Untyped error");
            throw new InstrumentReadException(change == "wrongDevice" ? b.Source.Descriptor.Id : b.Meter.Descriptor.Id, change == "unlistedCode" ? InstrumentReadFailureKind.CommandRejected : InstrumentReadFailureKind.Overrange, "Not safely recoverable", change == "unlistedCode" ? 999 : null);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(RecoveryPlan(b), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDuringInvalidPointCleanupRetainsClaimsAndOriginalError(bool meter)
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ => { if (meter) b.Meter.OnAbort = _ => throw new IOException("Idle refused"); else b.Source.RefuseStop = true; throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Original acquisition failure"); };
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(RecoveryPlan(b), Token));
        Assert.Contains("Original acquisition failure", failure.ToString()); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        b.Source.RefuseStop = false; b.Meter.OnAbort = null; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public void RecoverableReadInvalidatesEvenAnEarlierAssignmentOfTheSameVariable()
    {
        var b = new Bench(); var body = Assert.IsType<SequenceStep>(RecoveryPlan(b).Root);
        var plan = new ExperimentPlan("No stale fallback", new SequenceStep(new("outer"), [new AssignValueStep(new("seed"), b.Read.VoltageColumn, new ConstantValue(new(1, PhysicalUnit.Volt))), body, new SetParameterStep(new("consume"), new(b.Source.Descriptor.Id, Keithley6221Parameters.Compliance.Id), new VariableValue(b.Read.VoltageColumn))]), currentSourcePreparations: [b.Preparation]);
        var error = Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan)); Assert.Contains("not defined on every path", error.Message); Assert.Equal(0, b.Source.Connections);
    }

    private static ExperimentPlan RecoveryPlan(Bench b, bool owns = true)
    {
        var r = b.Read; var read = new ReadCurrentResistanceStep(r.Id, r.Source, r.Meter, r.CurrentReference, r.VoltageColumn, r.CurrentColumn, r.ResistanceColumn, r.MinimumCurrent, r.ExpectedProgrammedCurrent, b.Route, retry: new(owns, TimeSpan.Zero, true));
        return new("Explicit invalid-point recovery", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), b.Route.Device, b.Route.Closed, TimeSpan.Zero), new SourceOutputStep(new("on"), r.Source, true), read]), currentSourcePreparations: [b.Preparation]);
    }
}
