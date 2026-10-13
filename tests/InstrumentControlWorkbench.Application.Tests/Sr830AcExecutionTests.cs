using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Sr830AcExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredIsolationIncludesBaselinePathsAndFreshTerminalProofBeforeReleasing(bool baseline)
    {
        Bench? b = null; var atCommit = 0;
        b = new(baseline, listener: new Listener(async _ => { atCommit = b!.Matrix.Opens; Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); }));
        var step = b.Read(); await b.Runs.StartPlanAsync(b.Plan(step), Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(12, row.Values.Count - 1); Assert.Equal(.0023, row.Values[new("ac.r_v")].Value);
        Assert.Equal(Excitation.Frequency, row.Values[new("ac.reference_frequency_hz")]); Assert.Equal(Excitation.SineAmplitude, row.Values[new("ac.sine_amplitude_v")]); Assert.Equal(2, row.Values[new("ac.harmonic")].Value);
        Assert.Equal(1, b.Meter.Reads); Assert.Equal(1, b.Matrix.Closes); Assert.True(atCommit >= 2); Assert.True(b.Matrix.Opens > atCommit); Assert.True(b.Meter.Aborts > 0); b.AssertReleased();
        var configure = b.Events.IndexOf("configure-excitation"); var close = b.Events.IndexOf("close"); var frame = b.Events.IndexOf("frame");
        Assert.Contains("open", b.Events.Take(configure)); Assert.Contains("observe-open", b.Events.Take(configure)); Assert.True(configure < close && close < frame);
        Assert.Contains("observe-closed", b.Events.Skip(close + 1).Take(frame - close - 1)); Assert.Contains("open", b.Events.Skip(frame + 1));
        Assert.True((await b.Meter.GetSnapshotAsync(Token)).Reference!.SineAmplitude.Value > 0); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Theory]
    [InlineData("missing-capability")]
    [InlineData("passive-generator")]
    [InlineData("generator-mismatch")]
    [InlineData("partial-connections")]
    [InlineData("bounds")]
    public void InvalidCapabilityAndWiringRejectBeforeAnyConnection(string damage)
    {
        var b = new Bench(capability: damage != "missing-capability", activePort: damage != "passive-generator", capabilityGenerator: damage == "generator-mismatch" ? new InstrumentId("different-generator") : null);
        var paths = damage == "partial-connections" ? new[] { PathA } : damage == "bounds" ? new[] { new MatrixCrossPoint(1, 9, 1), PathB } : new[] { PathA, PathB };
        Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(b.Plan(b.Read(paths: paths)))); Assert.Equal(0, b.Meter.Connections); Assert.Equal(0, b.Matrix.Connections); Assert.Equal(0, b.Source.Connections); Assert.Empty(b.Events);
    }
    [Fact]
    public void GenericRouteCannotBypassRegisteredAcIsolationLifecycle()
    {
        var b = new Bench(); Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(b.Plan(new RouteMatrixStep(new("bypass"), b.Matrix.Definition.Device.Id, [PathA, PathB], TimeSpan.Zero)))); Assert.Empty(b.Events);
    }
    [Theory]
    [InlineData(false, "reference")]
    [InlineData(false, "input")]
    [InlineData(false, "route")]
    [InlineData(true, "reference")]
    [InlineData(true, "input")]
    [InlineData(true, "route")]
    public async Task DriftBeforeOrAfterFrameNeverCommitsMeasuredEvidence(bool afterFrame, string field)
    {
        var b = new Bench(); async Task Drift()
        {
            if (field == "reference") b.Meter.ReferenceOverride = new(Sr830ReferenceSource.External, Excitation.Frequency, Excitation.SineAmplitude, 2);
            if (field == "input") b.Meter.InputOverride = new(Sr830InputMode.VoltageA, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12);
            if (field == "route") await b.Matrix.ChangeExternallyAsync();
        }
        if (afterFrame) b.Meter.AfterRead = Drift; else b.Matrix.AfterClose = Drift;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("ac.x_v")));
        Assert.Equal(afterFrame ? 1 : 0, b.Meter.Reads); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task RefusedFinalIsolationDiscardsAcquiredFrameAndRetainsEveryClaimDespitePassiveAbortSuccess()
    {
        var b = new Bench(); b.Meter.AfterRead = () => { b.Matrix.RefuseOpen = true; return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Equal(1, b.Meter.ReturnedFrames); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Meter.Aborts > 0); b.AssertReserved();
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True((await b.Meter.GetSnapshotAsync(Token)).Reference!.SineAmplitude.Value > 0);
        b.Matrix.RefuseOpen = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task CapacityRejectsNextAttemptBeforeGeneratorConfigurationOrRelayClose()
    {
        var b = new Bench(retention: new(1)); await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(new RepeatStep(new("loop"), 2, b.Read())), Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Meter.Configurations); Assert.Equal(1, b.Matrix.Closes); Assert.Equal(1, b.Meter.Reads); b.AssertReleased();
    }
    [Fact]
    public async Task NativeFreshArchiveAndCsvRetainActualAcReferenceEvidenceWithoutHelperChild()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sr830-active-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(listener: archive); var codec = new JsonExperimentPlanDocument(); var plan = codec.Parse(codec.Serialize(b.Plan(b.Read())));
            Assert.Equal(2, codec.DescribeStructure(plan).Count); await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!;
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var restored = await fresh.LoadAsync(data.Id, Token); Assert.Equal(Assert.Single(data.Rows).Values, Assert.Single(restored.DataSet.Rows).Values); Assert.NotNull(restored.DataSet.Blackboard);
            var executed = Assert.IsType<ReadSr830AcFrameStep>(Assert.Single(Assert.IsType<SequenceStep>(restored.DataSet.Plan!.Root).Children)); Assert.Equal(Excitation, executed.Excitation); Assert.Equal(new[] { PathA, PathB }, executed.Connections);
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")); Assert.Contains("ac.sine_amplitude_v [Volt]", csv); Assert.Contains("ac.reference_frequency_hz [Hertz]", csv); Assert.Contains("read_sr830_ac_frame", csv);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Listener(Func<MeasurementDataRow, Task> onRow) : IRunResultListener
    {
        public Task OnStartedAsync(RunDataSetSnapshot data, CancellationToken token) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) => onRow(row);
        public Task OnFinalizedAsync(RunDataSetSnapshot data, CancellationToken token) => Task.CompletedTask;
    }
}
