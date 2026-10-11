using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Theory]
    [InlineData(true, InstrumentReadFailureKind.ReadTimeout, null)]
    [InlineData(true, InstrumentReadFailureKind.BufferTimeout, null)]
    [InlineData(true, InstrumentReadFailureKind.CommandRejected, -221)]
    [InlineData(false, InstrumentReadFailureKind.ReadTimeout, null)]
    public async Task RetryConfirmsIdleAndPreservesOwnedOrManagedExcitation(bool owned, InstrumentReadFailureKind kind, int? code)
    {
        await using var b = new Bench(); var abortedOutput = new List<bool>();
        b.Meter.OnRead = async _ =>
        {
            Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            if (b.Meter.ReadCalls == 1) throw new InstrumentReadException(b.Meter.Descriptor.Id, kind, "Transient acquisition failure", code);
        };
        b.Meter.OnAbort = async _ => abortedOutput.Add((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await b.Runs.StartPlanAsync(RetryPlan(b, owned), Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var evidence = Assert.Single(row.IndependentResistance);
        var retry = Assert.IsType<CurrentResistanceRetryEvidence>(evidence.Retry);
        Assert.Equal(2, evidence.Attempts); Assert.Equal(kind, retry.Kind); Assert.Equal(code, retry.CommandCode);
        Assert.Equal(owned, retry.SourceOffConfirmedAt is not null); Assert.Equal(!owned, abortedOutput[0]);
        Assert.True(retry.AttemptStartedAt <= retry.FailedAt && retry.FailedAt <= retry.IdleConfirmedAt && retry.IdleConfirmedAt <= evidence.SourceConfirmedBefore);
        Assert.Equal(100, evidence.Resistance.Value, 8); Assert.Equal(2, b.Meter.ReadCalls); Assert.Equal(owned ? 2 : 1, b.Source.Enables);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.False(b.Runs.HasActiveRun); Assert.All(row.Quality, q => Assert.Null(q.Status));
    }

    [Theory]
    [InlineData("overrange")]
    [InlineData("invalidResponse")]
    [InlineData("visaRead")]
    [InlineData("otherCode")]
    [InlineData("wrongDevice")]
    [InlineData("genericTimeout")]
    [InlineData("noPolicy")]
    public async Task RetryNeverGuessesEligibilityFromAnUntypedOrIneligibleFailure(string error)
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ => throw error switch
        {
            "overrange" => new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Overrange"),
            "invalidResponse" => new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.InvalidResponse, "Invalid response"),
            "visaRead" => new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.VisaIoRead, "VISA read"),
            "otherCode" => new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.CommandRejected, "Rejected", 419),
            "wrongDevice" => new InstrumentReadException(b.Source.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Wrong device"),
            "genericTimeout" => new TimeoutException("Untyped timeout"),
            _ => new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "No retry policy"),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(error == "noPolicy" ? b.Plan(true) : RetryPlan(b, true), Token));
        Assert.Equal(1, b.Meter.ReadCalls); Assert.Equal(1, b.Source.Enables); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task TwoFailedAttemptsPreserveBothErrorsAndNeverCreateZeroOrThirdAttempt()
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Attempt " + b.Meter.ReadCalls);
        var error = await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartPlanAsync(RetryPlan(b, true), Token));
        var failures = error.Flatten().InnerExceptions;
        Assert.Contains(failures, e => e.Message == "Attempt 1"); Assert.Contains(failures, e => e.Message == "Attempt 2");
        Assert.Equal(2, b.Meter.ReadCalls); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("meter")]
    [InlineData("route")]
    public async Task DriftBetweenFailedAndRetriedReadRefusesSecondTrigger(string drift)
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "First failure");
        b.Meter.OnAbort = async _ =>
        {
            if (drift == "source") await b.Source.ConfigureAsync(new(new(2e-5, PhysicalUnit.Ampere), b.Preparation.Settings.Compliance, b.Preparation.Settings.ManualRange), Token);
            if (drift == "meter") await b.Meter.ConfigureAsync((await b.Meter.GetSnapshotAsync(Token)).Settings!.WithParameter(Keithley2182Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless)), Token);
            if (drift == "route") await b.Matrix.OpenAsync([b.Route.Closed[0]], Token);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(RetryPlan(b, true), Token));
        Assert.Equal(1, b.Meter.ReadCalls); Assert.Equal(1, b.Source.Enables); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task RetryCleanupFailureRetainsClaimsAndOriginalErrorWithoutOutputRestore()
    {
        await using var b = new Bench();
        b.Meter.OnRead = _ => { b.Source.RefuseStop = true; throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Original read failure"); };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(RetryPlan(b, true), Token));
        Assert.Contains("Original read failure", error.ToString()); Assert.Contains("Output-off refused", error.ToString());
        Assert.Equal(1, b.Meter.ReadCalls); Assert.Equal(1, b.Source.Enables); Assert.True(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        b.Source.RefuseStop = false; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Fact]
    public async Task CancelDuringRetryCleanupStillConfirmsIdleAndOffWithoutSecondRead()
    {
        await using var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Transient read failure");
        b.Meter.OnAbort = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var run = b.Runs.StartPlanAsync(RetryPlan(b, true), Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); }
        finally { release.TrySetResult(); }
        await run; Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows);
        Assert.Equal(1, b.Meter.ReadCalls); Assert.Equal(1, b.Source.Enables); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Theory]
    [InlineData(SampleTopology.TwoWire, "meas_r_2w", 1)]
    [InlineData(SampleTopology.FourWire, "meas_r_4w", 1)]
    [InlineData(SampleTopology.HallBar, "meas_r_hallbar", 2)]
    [InlineData(SampleTopology.VanDerPauw, "meas_r_vdp", 8)]
    public async Task ImportedSampleRetriesEachSelectionAndRetainsEveryFailureInAnalysis(SampleTopology topology, string name, int count)
    {
        await using var b = new Bench(columns: 6);
        b.Meter.OnRead = _ => b.Meter.ReadCalls % 2 == 1 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.BufferTimeout, "Transient sample failure")) : Task.CompletedTask;
        var plan = new PpmsExperimentPlanImporter().Parse(SampleJson(name, new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(topology)));
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(count * 2, b.Meter.ReadCalls); Assert.Equal(count * 2, b.Source.Enables);
        Assert.Equal(count > 1 ? count + 1 : count, b.Runs.LatestDataSet!.Rows.Count);
        Assert.All(b.Runs.LatestDataSet.Rows.SelectMany(r => r.IndependentResistance), r => { Assert.Equal(2, r.Attempts); Assert.NotNull(r.Retry); });
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ImportedManagedCurrentScanRetryLeavesParentSourceEnabled()
    {
        await using var b = new Bench(); b.Meter.OnRead = _ => b.Meter.ReadCalls == 1 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Managed read failure")) : Task.CompletedTask;
        var node = new JsonObject { ["type"] = "scan", ["name"] = "scan_current", ["params"] = new JsonObject { ["points"] = new JsonArray(1e-5), ["channel"] = "ch1", ["settle_s"] = 0 }, ["children"] = new JsonArray(JsonNode.Parse(ResistanceJson(new() { ["settle_s"] = 15 }))) };
        await b.Runs.StartPlanAsync(new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog())), Token);
        Assert.Equal(1, b.Source.Enables); Assert.Equal(2, b.Meter.ReadCalls);
        var e = Assert.Single(b.Runs.LatestDataSet!.Rows[0].IndependentResistance); Assert.Null(e.Retry!.SourceOffConfirmedAt);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("drift")]
    [InlineData("abort")]
    public async Task RetryDwellUsesOrdinaryPauseResumeAndAbortWithoutDeadlock(string action)
    {
        await using var b = new Bench(); using var events = b.Hub.Subscribe();
        b.Meter.OnRead = _ => b.Meter.ReadCalls == 1 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "First read failure")) : Task.CompletedTask;
        b.Meter.OnAbort = _ => { if (b.Meter.AbortCalls == 1) b.Runs.Pause(); return Task.CompletedTask; };
        var run = b.Runs.StartPlanAsync(RetryPlan(b, true, TimeSpan.FromMilliseconds(200)), Token);
        await Until(events, Core.Notifications.WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Equal(1, b.Meter.ReadCalls);
        var enables = b.Source.Enables;
        if (action == "drift") await b.Source.ConfigureAsync(new(new(2e-5, PhysicalUnit.Ampere), b.Preparation.Settings.Compliance, b.Preparation.Settings.ManualRange), Token);
        if (action == "abort") b.Runs.Abort(); else b.Runs.Resume();
        if (action == "drift") { await Assert.ThrowsAsync<InvalidOperationException>(() => run); Assert.Equal(enables, b.Source.Enables); }
        else await run;
        Assert.Equal(action == "resume" ? 2 : 1, b.Meter.ReadCalls);
        Assert.Equal(action == "resume" ? 1 : 0, b.Runs.LatestDataSet!.Rows.Count);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("managedSettle")]
    [InlineData("negative")]
    [InlineData("wrongType")]
    [InlineData("unknownField")]
    public void InvalidNativeRetryPolicyRejectsBeforeAnyConnection(string damage)
    {
        var b = new Bench(); var doc = new JsonExperimentPlanDocument(); var json = JsonNode.Parse(doc.Serialize(RetryPlan(b, true)))!.AsObject();
        var retry = json["root"]!["children"]![2]!["retry"]!.AsObject();
        switch (damage)
        {
            case "managedSettle": retry["ownsSourceOutput"] = false; retry["settleSeconds"] = 1; break;
            case "negative": retry["settleSeconds"] = -1; break;
            case "wrongType": retry["ownsSourceOutput"] = "true"; break;
            case "unknownField": retry["retryAllFailures"] = true; break;
        }
        Assert.ThrowsAny<Exception>(() => doc.Parse(json.ToJsonString())); Assert.Equal(0, b.Source.Connections);
    }

    private static ExperimentPlan RetryPlan(Bench b, bool ownsSourceOutput, TimeSpan settle = default)
    {
        var read = b.Read;
        var retried = new ReadCurrentResistanceStep(read.Id, read.Source, read.Meter, read.CurrentReference, read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn, read.MinimumCurrent, read.ExpectedProgrammedCurrent, b.Route, retry: new(ownsSourceOutput, settle));
        return new("Bounded current resistance retry", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), b.Route.Device, b.Route.Closed, TimeSpan.Zero), new SourceOutputStep(new("on"), read.Source, true), retried]), currentSourcePreparations: [b.Preparation]);
    }
}
