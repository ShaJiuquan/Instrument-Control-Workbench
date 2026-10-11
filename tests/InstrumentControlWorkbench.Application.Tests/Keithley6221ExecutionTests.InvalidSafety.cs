using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortDuringFailedReadOrCleanupCannotCommitAnUnfinishedInvalidPoint(bool duringCleanup)
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ => { if (!duringCleanup) b.Runs.Abort(); throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Acquisition failed before abort"); };
        if (duringCleanup) b.Meter.OnAbort = _ => { b.Runs.Abort(); return Task.CompletedTask; };
        await b.Runs.StartPlanAsync(RecoveryPlan(b), Token);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Source.LastStopTokenLive); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerFaultOrAbortAfterInvalidRowPreservesItAndNeverProducesACompleteGroup(bool abort)
    {
        var listener = new SampleRowListener(); await using var b = new Bench(listener, 6);
        listener.Row = row => { Assert.Single(row.AcquisitionFailures); if (abort) { b.Runs.Abort(); return Task.CompletedTask; } throw new IOException("Invalid row listener refused"); };
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.InvalidResponse, "No voltage response");
        var plan = new PpmsExperimentPlanImporter().Parse(SampleJson("meas_r_vdp", new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(SampleTopology.VanDerPauw)));
        if (abort) await b.Runs.StartPlanAsync(plan, Token); else await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(plan, Token));
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Single(row.AcquisitionFailures); Assert.Null(row.Analysis); Assert.Null(row.MeasurementGroup);
        Assert.Equal(abort ? MeasurementRunState.Aborted : MeasurementRunState.Faulted, b.Runs.LatestDataSet.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task TimedOutInvalidPointAbortCannotContinueOrOverlapRecoveryBeforeTerminalStop()
    {
        await using var b = new Bench(); var resources = await b.Registry.AcquireRunAsync([b.Source.Descriptor.Id, b.Meter.Descriptor.Id], RunId.New(), Token, TimeSpan.FromMilliseconds(40));
        await resources.ConnectAllAsync(Token); await resources.ConfigureCurrentSourceAsync(b.Source.Descriptor.Id, b.Preparation.Settings, Token); await resources.EnableSourceOutputAsync(b.Source.Descriptor.Id, Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Original overrange");
        b.Meter.OnAbort = async _ => { try { await release.Task; } finally { exited.TrySetResult(); } };
        var r = b.Read; var read = new ReadCurrentResistanceStep(r.Id, r.Source, r.Meter, r.CurrentReference, r.VoltageColumn, r.CurrentColumn, r.ResistanceColumn, r.MinimumCurrent, r.ExpectedProgrammedCurrent, retry: new(true, TimeSpan.Zero, true));
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => resources.ReadCurrentResistanceAsync(read, b.Preparation.Settings.Current, Token)); Assert.Contains("Original overrange", error.ToString());
            Assert.Equal(1, b.Meter.ReadCalls); Assert.Equal(1, b.Meter.AbortCalls); Assert.NotEmpty(await resources.StopAllAsync(Token, retryFailed: true)); Assert.Equal(1, b.Meter.AbortCalls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token)); Assert.All(resources.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        }
        finally
        {
            b.Meter.OnAbort = null; release.TrySetResult(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotEmpty(await resources.StopAllAsync(Token));
            Assert.Empty(await resources.StopAllAsync(Token, retryFailed: true)); await resources.ReleaseAsync(Token);
        }
    }

    [Fact]
    public async Task SecondFailureCleanupRefusalRetainsBothAttemptErrorsWithoutAnInvalidRow()
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ =>
        {
            if (b.Meter.ReadCalls == 2) b.Source.RefuseStop = true;
            throw new InstrumentReadException(b.Meter.Descriptor.Id, b.Meter.ReadCalls == 1 ? InstrumentReadFailureKind.ReadTimeout : InstrumentReadFailureKind.Overrange, b.Meter.ReadCalls == 1 ? "First attempt failure" : "Final attempt failure");
        };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(RecoveryPlan(b), Token));
        Assert.Contains("First attempt failure", error.ToString()); Assert.Contains("Final attempt failure", error.ToString()); Assert.Contains("Output-off refused", error.ToString());
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun);
        b.Source.RefuseStop = false; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task FailedIterationRemovesPreviousElectricalValuesInsteadOfUsingAStaleResistance()
    {
        await using var b = new Bench(); b.Meter.OnRead = _ => b.Meter.ReadCalls == 2 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Second iteration unavailable")) : Task.CompletedTask;
        var plan = RecoveryPlan(b); plan = new(plan.Name, new RepeatStep(new("repeat"), 3, plan.Root), currentSourcePreparations: plan.CurrentSourcePreparations);
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count);
        Assert.Single(rows[0].IndependentResistance); Assert.Single(rows[1].AcquisitionFailures); Assert.Single(rows[1].Values); Assert.Single(rows[2].IndependentResistance);
        Assert.Equal(100, rows[2].IndependentResistance[0].Resistance.Value, 8); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet.State);
    }
}
