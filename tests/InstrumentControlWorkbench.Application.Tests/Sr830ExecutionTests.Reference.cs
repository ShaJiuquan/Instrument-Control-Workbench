using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Sr830ExecutionTests
{
    private static ReadSr830FrameStep ReferenceFrame(Bench b, Sr830InputMode input = Sr830InputMode.VoltageAB) => new(new("raw"), b.Meter.Descriptor.Id, input, new("raw"), true);
    [Fact]
    public void ReferenceEnabledFrameNeedsExplicitCapabilityBeforeConnection()
    {
        var b = new Bench(); Assert.Throws<NotSupportedException>(() => b.Runs.PreviewPlan(b.Plan(ReferenceFrame(b)))); Assert.Equal(0, b.Meter.Connections);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("frequency")]
    [InlineData("amplitude")]
    [InlineData("source")]
    public async Task ReferenceDriftDiscardsWholeFrameAndMeasuredVariables(string damage)
    {
        var b = new Bench(reference: true);
        if (damage == "missing") b.Meter.Reference = null;
        else b.Meter.OnRead = _ => { b.Meter.Reference = new(damage == "source" ? Sr830ReferenceSource.External : Sr830ReferenceSource.Internal, new(damage == "frequency" ? 17.3 : 13.7, PhysicalUnit.Hertz), new(damage == "amplitude" ? .006 : .004, PhysicalUnit.Volt), 1); return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(ReferenceFrame(b)), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("raw.x_v"))); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(Sr830InputMode.VoltageAB)]
    [InlineData(Sr830InputMode.Current1M)]
    [InlineData(Sr830InputMode.Current100M)]
    public async Task ReferredSignalAndReferenceSurviveNativeSaveFreshArchiveBlackboardAndCsv(Sr830InputMode input)
    {
        var folder = Path.Combine(Path.GetTempPath(), "sr830-reference-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(archive, reference: true);
            var analysis = Sr830InputReferredAnalysis.Create(new("refer"), ReferenceFrame(b, input), new("signal")); var plan = b.Plan(b.Write("input", Sr830Parameters.InputMode, (int)input), analysis);
            var codec = new JsonExperimentPlanDocument(); await codec.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); await b.Runs.StartPlanAsync(await codec.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var data = b.Runs.LatestDataSet!; Assert.Equal(2, data.Rows.Count); var result = data.Rows[1]; var current = input != Sr830InputMode.VoltageAB; var gain = input == Sr830InputMode.Current100M ? 1e8 : current ? 1e6 : 1;
            Assert.Equal(new(.001 / gain, current ? PhysicalUnit.Ampere : PhysicalUnit.Volt), result.Values[new("signal.input_x")]); Assert.Equal(new(.004, PhysicalUnit.Volt), result.Values[new("raw.sine_amplitude_v")]); Assert.Equal(13.7, result.Values[new("raw.reference_frequency_hz")].Value); Assert.NotNull(result.Calculation); Assert.Equal(ReadingQualityState.Unobserved, result.QualityState); Assert.Equal(1, b.Meter.Reads);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(data.Id, Token); Assert.Equal(result.Calculation!.Step, stored.DataSet.Rows[1].Calculation!.Step); Assert.Equal(result.Calculation.InputRow, stored.DataSet.Rows[1].Calculation!.InputRow); Assert.Equal(result.Calculation.Outcomes, stored.DataSet.Rows[1].Calculation!.Outcomes); Assert.NotNull(stored.DataSet.Blackboard);
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "signal.csv"), Token); Assert.Contains("raw.reference_frequency_hz", await File.ReadAllTextAsync(Path.Combine(folder, "signal.csv"))); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("partial")]
    [InlineData("harmonic")]
    [InlineData("product")]
    [InlineData("source")]
    public async Task ArchiveRejectsIncompleteOrInconsistentReferenceEvidence(string damage)
    {
        var b = new Bench(reference: true); var plan = b.Plan(ReferenceFrame(b)); await b.Runs.StartPlanAsync(plan, Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var values = new Dictionary<ParameterId, Quantity>(row.Values);
        if (damage == "partial") values.Remove(new("raw.harmonic"));
        else if (damage == "harmonic") values[new("raw.harmonic")] = new(1.5, PhysicalUnit.Dimensionless);
        else if (damage == "source") values[new("raw.reference_source")] = new(.5, PhysicalUnit.Dimensionless);
        else { values[new("raw.reference_frequency_hz")] = new(102000, PhysicalUnit.Hertz); values[new("raw.harmonic")] = new(2, PhysicalUnit.Dimensionless); }
        Assert.ThrowsAny<ArgumentException>(() => new RunDataRowValidator(plan, b.Runs.LatestDataSet.Schema).Validate(new(row.Timestamp, values, quality: row.Quality)));
    }
    [Theory]
    [InlineData("raw.reference_source", .5, PhysicalUnit.Dimensionless)]
    [InlineData("raw.harmonic", 1.5, PhysicalUnit.Dimensionless)]
    [InlineData("raw.input_mode", 2, PhysicalUnit.Dimensionless)]
    [InlineData("raw.sensitivity_v", .015, PhysicalUnit.Volt)]
    [InlineData("raw.theta_deg", 181, PhysicalUnit.Degree)]
    public async Task ArchivedFrameBlackboardRejectsInvalidObservedValues(string variable, double value, PhysicalUnit unit)
    {
        var b = new Bench(reference: true); var plan = b.Plan(ReferenceFrame(b)); await b.Runs.StartPlanAsync(plan, Token);
        var original = b.Runs.LatestBlackboardSnapshot!; var entries = new Dictionary<ParameterId, BlackboardEntry>(original.Entries); var column = new ParameterId(variable); var before = entries[column];
        entries[column] = new(new BlackboardQuantity(new(value, unit)), before.Source, before.UpdatedAt, before.Elapsed, before.Kind);
        Assert.Throws<ArgumentException>(() => new RunDataRowValidator(plan, b.Runs.LatestDataSet!.Schema).ValidateBlackboard(original.Id, new(original.Id, original.CapturedAt, original.Elapsed, entries)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseRechecksDeclaredReferenceReadbackBeforeResume(bool drift)
    {
        var b = new Bench(reference: true); using var events = b.Hub.Subscribe(); var run = b.Runs.StartPlanAsync(b.Plan(ReferenceFrame(b), new DelayStep(new("wait"), TimeSpan.FromSeconds(30))), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        if (drift) b.Meter.Reference = new(Sr830ReferenceSource.Internal, new(17.3, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt), 1);
        b.Runs.Resume(); if (drift) await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        else { await Until(events, WorkbenchNotificationKind.RunResumed); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.False(b.Runs.HasActiveRun); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }
    [Fact]
    public async Task NominalCurrentToObservedSineRatioUsesSiemensAndExistingCalculationEvidence()
    {
        var b = new Bench(reference: true); var frame = ReferenceFrame(b, Sr830InputMode.Current1M); var referred = Sr830InputReferredAnalysis.Create(new("refer"), frame, new("signal"));
        var ratio = new CalculatedColumn(new("instrument_ratio"), new ArithmeticValue(ArithmeticOperator.Divide, referred.Columns[0].Expression, new VariableValue(new("raw.sine_amplitude_v"))));
        var combined = new CalculatedMeasurementStep(new("analysis"), frame, referred.Columns.Append(ratio)); await b.Runs.StartPlanAsync(b.Plan(b.Write("input", Sr830Parameters.InputMode, 2), combined), Token);
        var result = b.Runs.LatestDataSet!.Rows[1]; Assert.Equal(new(2.5e-7, PhysicalUnit.Siemens), result.Values[new("instrument_ratio")]); Assert.All(result.Calculation!.Outcomes, outcome => Assert.Equal(CalculationIssue.None, outcome.Issue));
        var corrupted = new Dictionary<ParameterId, Quantity>(result.Values) { [new("instrument_ratio")] = new(1, PhysicalUnit.Siemens) };
        var validator = new RunDataRowValidator(b.Runs.LatestDataSet.Plan, b.Runs.LatestDataSet.Schema); validator.Validate(b.Runs.LatestDataSet.Rows[0]); Assert.Throws<ArgumentException>(() => validator.Validate(new(result.Timestamp, corrupted, quality: result.Quality, calculation: result.Calculation)));
    }
    [Fact]
    public void ReadOnlyReferenceParametersCannotBeUsedAsOscillatorWrites()
    {
        var b = new Bench(reference: true); Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(b.Plan(b.Write("amplitude", Sr830ReferenceParameters.SineAmplitude, .006), ReferenceFrame(b)))); Assert.Equal(0, b.Meter.Connections);
    }
}
