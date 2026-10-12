using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static CalculatedMeasurementStep CalculatedPair(Bench b, bool continueOnUnavailable = false) => new(new("calculate"), new ReadSourceSenseStep(new("paired"), b.Source.Id, SourceFunction.Voltage, new("V"), new("I")),
        [new(new("derived_R"), new ArithmeticValue(ArithmeticOperator.Divide, new VariableValue(new("V")), new VariableValue(new("I")))), new(new("P"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("V")), new VariableValue(new("I")))), new(new("scaled_P"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("P")), new ConstantValue(new(2, PhysicalUnit.Dimensionless))))], continueOnUnavailable);
    [Fact]
    public async Task CalculatedScanPreservesEachInputReplaysArchiveAndExportsEachResultColumn()
    {
        var folder = Path.Combine(Path.GetTempPath(), "calculated-rows-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive); var step = CalculatedPair(b); var scan = new ScanStep(new("scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("requested"), new ScanValues([new(.01, PhysicalUnit.Volt), new(.02, PhysicalUnit.Volt), new(.03, PhysicalUnit.Volt)]), TimeSpan.Zero, step);
                var original = b.Powered(scan); var path = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(path, new JsonExperimentPlanDocument().Serialize(original), Token); var loaded = await b.Workspace.LoadAsync(path, Token); await b.Runs.StartPlanAsync(loaded.Plan, Token); id = b.Runs.LatestDataSet!.Id; CheckCalculatedRows(b.Runs.LatestDataSet.Rows);
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            }
            await using var fresh = new FileRunArchive(folder); var reopened = await fresh.LoadAsync(id, Token); CheckCalculatedRows(reopened.DataSet.Rows);
            var export = Path.Combine(Path.GetTempPath(), "calculated-" + id.Value.ToString("N") + ".csv"); try { await fresh.ExportCsvAsync(id, export, Token); var csv = await File.ReadAllTextAsync(export); Assert.Contains("P [Watt]", csv); Assert.Contains("calculation_json", csv); Assert.Contains("scaled_P [Watt]", csv); } finally { File.Delete(export); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static void CheckCalculatedRows(IReadOnlyList<MeasurementDataRow> rows)
    {
        Assert.Equal(6, rows.Count);
        for (var i = 0; i < 3; i++)
        {
            var raw = rows[i * 2]; var result = rows[i * 2 + 1]; Assert.Null(raw.Calculation); Assert.NotNull(result.Calculation); Assert.Equal(i * 2 + 1, result.Calculation.InputRow); Assert.Equal(raw.Timestamp, result.Timestamp); Assert.Equal(raw.Quality, result.Quality); Assert.All(result.Calculation.Outcomes, outcome => Assert.Equal(CalculationIssue.None, outcome.Issue));
            Assert.Equal(new Quantity(raw.Values[new("V")].Value / raw.Values[new("I")].Value, PhysicalUnit.Ohm), result.Values[new("derived_R")]); Assert.Equal(new Quantity(raw.Values[new("V")].Value * raw.Values[new("I")].Value, PhysicalUnit.Watt), result.Values[new("P")]); Assert.Equal(result.Values[new("P")].Value * 2, result.Values[new("scaled_P")].Value); Assert.False(raw.Values.ContainsKey(new("P")));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DivisionAtZeroRetainsRawDataAndExplicitPolicyControlsScanContinuation(bool continued)
    {
        var b = new Bench(); var step = CalculatedPair(b, continued); var original = b.Powered(new ScanStep(new("scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("requested"), new ScanValues([new(0, PhysicalUnit.Volt), new(.01, PhysicalUnit.Volt)]), TimeSpan.Zero, step));
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(original, Token));
        if (continued) { Assert.Null(error); Assert.Equal(4, b.Runs.LatestDataSet!.Rows.Count); var unavailable = b.Runs.LatestDataSet.Rows[1]; Assert.False(unavailable.Values.ContainsKey(new("derived_R"))); Assert.Equal(CalculationIssue.DivideByZero, unavailable.Calculation!.Outcomes[0].Issue); Assert.True(b.Runs.LatestDataSet.Rows[3].Values.ContainsKey(new("derived_R"))); }
        else { Assert.NotNull(error); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.LatestBlackboard!.ContainsKey(new("derived_R"))); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData("unknownInput")]
    [InlineData("staleVariable")]
    [InlineData("forward")]
    [InlineData("collision")]
    public async Task CalculationCompilerRejectsNonRowInputsAndColumnCollisionsBeforeIO(string damage)
    {
        var b = new Bench(); var original = CalculatedPair(b); var results = original.Columns.ToArray();
        if (damage == "collision") results[0] = new(new("requested"), new VariableValue(new("V")));
        else results[0] = new(results[0].Column, new VariableValue(new(damage == "forward" ? "P" : damage == "staleVariable" ? "old" : "missing")));
        var step = new CalculatedMeasurementStep(original.Id, original.Body, results); var scan = new ForEachStep(new("loop"), new("requested"), new ScanValues([new(.01, PhysicalUnit.Volt)]), step);
        var plan = b.Powered(new SequenceStep(new("body"), [new AssignValueStep(new("seed"), new("old"), new ConstantValue(new(1, PhysicalUnit.Volt))), scan]));
        Assert.NotNull(Record.Exception(() => b.Runs.PreviewPlan(plan))); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData("value")]
    [InlineData("input")]
    [InlineData("outcome")]
    [InlineData("timestamp")]
    [InlineData("quality")]
    [InlineData("missingMarker")]
    public async Task CalculatedRowValidatorRejectsForgedValuesAndOriginalInputEvidence(string damage)
    {
        var b = new Bench(); var plan = b.Powered(CalculatedPair(b)); await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!; var raw = data.Rows[0]; var result = data.Rows[1]; var values = new Dictionary<ParameterId, Quantity>(result.Values); var summary = result.Calculation!;
        if (damage == "value") values[new("P")] = new(100, PhysicalUnit.Watt);
        if (damage == "input") summary = new(summary.Step, summary.InputRow + 1, summary.Outcomes);
        if (damage == "outcome") summary = new(summary.Step, summary.InputRow, summary.Outcomes.Select(outcome => new CalculationOutcome(outcome.Column, CalculationIssue.MissingInput)));
        var forged = new MeasurementDataRow(damage == "timestamp" ? result.Timestamp.AddTicks(1) : result.Timestamp, values, quality: damage == "quality" ? [] : result.Quality, calculation: damage == "missingMarker" ? null : summary);
        var validator = new RunDataRowValidator(plan, data.Schema); validator.Append(raw); Assert.Throws<ArgumentException>(() => validator.Append(forged));
    }
    [Fact]
    public async Task CalculatedJointMeasurementReplaysOriginalWidePointFromArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "calculated-point-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive); var group = new MeasurementGroupStep(new("point"), [new("current", b.ReadSource("current")), new("field", b.ReadField("field"))]);
                var calculation = new CalculatedMeasurementStep(new("calculate"), group, [new(new("scaled_current"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("current.value")), new ConstantValue(new(2, PhysicalUnit.Dimensionless))))]); await b.Runs.StartPlanAsync(b.Powered(calculation), Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(4, rows.Count); Assert.NotNull(rows[^1].Calculation); Assert.NotNull(rows[^1].MeasurementGroup); Assert.Equal(rows[^2].MeasurementGroup!.Inputs, rows[^1].MeasurementGroup!.Inputs); id = b.Runs.LatestDataSet.Id;
            }
            await using var fresh = new FileRunArchive(folder); var data = await fresh.LoadAsync(id, Token); Assert.Equal(4, data.DataSet.Rows.Count); Assert.Equal(3, data.DataSet.Rows[^1].Calculation!.InputRow);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task FailedNextPointClearsPreviousCalculatedBlackboardValues()
    {
        var b = new Bench(); var scan = new ScanStep(new("scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("requested"), new ScanValues([new(.01, PhysicalUnit.Volt), new(0, PhysicalUnit.Volt)]), TimeSpan.Zero, CalculatedPair(b));
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(scan), Token))); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.True(b.Runs.LatestDataSet.Rows[1].Values.ContainsKey(new("P"))); Assert.False(b.Runs.LatestBlackboard!.ContainsKey(new("P"))); Assert.False(b.Runs.LatestBlackboard.ContainsKey(new("derived_R"))); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingEnvironmentInputNeverBecomesAComputedZero(bool continued)
    {
        var b = new Bench(); var body = new ReadSourceSenseStep(new("read"), b.Source.Id, SourceFunction.Voltage, new("V"), new("I"), [new EnvironmentRead(EnvironmentRole.Temperature, new("ambient"))]);
        var calculated = new CalculatedMeasurementStep(new("calculate"), body, [new(new("scaled_temperature"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("ambient")), new ConstantValue(new(2, PhysicalUnit.Dimensionless))))], continued);
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(calculated), Token)); if (continued) { Assert.Null(error); var row = b.Runs.LatestDataSet!.Rows[^1]; Assert.Equal(CalculationIssue.MissingInput, Assert.Single(row.Calculation!.Outcomes).Issue); Assert.False(row.Values.ContainsKey(new("scaled_temperature"))); Assert.Equal(b.Runs.LatestDataSet.Rows[0].Environment, row.Environment); } else { Assert.NotNull(error); Assert.Single(b.Runs.LatestDataSet!.Rows); }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("abortRaw")]
    [InlineData("failRaw")]
    [InlineData("failCalculation")]
    public async Task CancellationAndListenerFaultDoNotEmitAStaleOrDuplicateCalculatedRow(string mode)
    {
        var listener = new CalculationDeliveryListener(); var b = new Bench(listener);
        listener.Deliver = row => { if (mode == "abortRaw" && row.Calculation is null) b.Runs.Abort(); if (mode == "failRaw" && row.Calculation is null || mode == "failCalculation" && row.Calculation is not null) throw new IOException("Result delivery refused."); };
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(CalculatedPair(b)), Token)); if (mode == "abortRaw") Assert.Null(error); else Assert.NotNull(error); Assert.Equal(mode == "failCalculation" ? 2 : 1, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(mode == "abortRaw" ? MeasurementRunState.Aborted : MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
    }
    private sealed class CalculationDeliveryListener : IRunResultListener
    {
        public Action<MeasurementDataRow>? Deliver { get; set; }
        public Task OnStartedAsync(RunDataSetSnapshot data, CancellationToken token) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) { Deliver?.Invoke(row); return Task.CompletedTask; }
        public Task OnFinalizedAsync(RunDataSetSnapshot data, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task RepositoryCalculatedExampleRunsSixRowsWithPerPointResistanceAndPower()
    {
        var b = new Bench(); var loaded = await b.Workspace.LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "calculated-source-scan.plan.json"), Token); await b.Runs.StartPlanAsync(loaded.Plan, Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(6, rows.Count); Assert.Equal(3, rows.Count(row => row.Calculation is not null)); Assert.All(rows.Where(row => row.Calculation is not null), row => { Assert.Equal(PhysicalUnit.Watt, row.Values[new("calculated_P")].Unit); Assert.Equal(PhysicalUnit.Ohm, row.Values[new("calculated_R")].Unit); }); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("required")]
    [InlineData("unit")]
    [InlineData("dependencies")]
    public void CalculatedSchemaCannotPretendResultsArePhysicalReadsOrDropDependencies(string damage)
    {
        var b = new Bench(); var plan = b.Powered(CalculatedPair(b)); var schema = b.Runs.PreviewPlan(plan).Schema;
        var forged = new DataSetSchema(schema.Columns.Select(column => column.Parameter.Id.Value != "P" ? column : new DataColumn(damage == "unit" ? new ParameterDescriptor(column.Parameter.Id, column.Parameter.Label, PhysicalUnit.Ohm, ParameterAccess.Read) : column.Parameter, damage == "dependencies" ? [] : column.DependsOn, damage != "required", damage == "source" ? new ParameterAddress(b.Source.Id, Keithley2450Parameters.MeasuredVoltage.Id) : null)));
        Assert.Throws<ArgumentException>(() => new RunDataRowValidator(plan, forged));
    }

}
