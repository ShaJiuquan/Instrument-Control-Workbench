using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task CalculationDraftInputsUseCurrentRowAxesElapsedAndEarlierResultsWithoutHardware()
    {
        var b = new Bench(); var calculation = CalculatedPair(b); var plan = b.Powered(new SequenceStep(new("sequence"), [new AssignValueStep(new("old"), new("stale"), new ConstantValue(new(2, PhysicalUnit.Volt))),
            new ScanStep(new("scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("bias"), new ScanValues([new(.01, PhysicalUnit.Volt)]), TimeSpan.Zero, calculation)]));
        var draft = new PlanCalculationDraft(calculation.Id, calculation.Body, [
            new(new("power_draft"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("V")), new VariableValue(new("I")))),
            new(new("voltage_draft"), new ArithmeticValue(ArithmeticOperator.Divide, new VariableValue(new("power_draft")), new VariableValue(new("I"))))]);
        var preview = b.Runs.DescribeCalculationDraft(plan, calculation.Id, PlanStructureEditKind.Replace, draft);
        Assert.Equal(new[] { "I", "V", "bias", "elapsed" }, preview.RowInputs.Select(input => input.Id.Value)); Assert.DoesNotContain(preview.RowInputs, input => input.Id.Value == "stale");
        Assert.Equal(PhysicalUnit.Watt, preview.Columns[0].Unit); Assert.Equal(PhysicalUnit.Volt, preview.Columns[1].Unit); Assert.All(preview.Columns, column => Assert.Null(column.Error));
        Assert.DoesNotContain(preview.Columns[0].Inputs, input => input.Id.Value == "power_draft"); Assert.Contains(preview.Columns[1].Inputs, input => input.Id.Value == "power_draft");
        Assert.True(preview.RowInputs.Single(input => input.Id.Value == "bias").IsActiveAxis); Assert.False(preview.RowInputs.Single(input => input.Id.Value == "elapsed").MayBeAbsent); Assert.False(preview.RowInputs.Single(input => input.Id.Value == "V").MayBeAbsent);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
        Assert.Throws<NotSupportedException>(() => ((IList<PlanVariableAvailability>)preview.RowInputs).Clear());
    }

    [Theory]
    [InlineData("forward")]
    [InlineData("self")]
    [InlineData("stale")]
    [InlineData("incomplete")]
    [InlineData("collision")]
    public void InvalidDraftResultIsReportedAndNeverOfferedAsAnEarlierValidResult(string mode)
    {
        var b = new Bench(); var calculation = CalculatedPair(b); var plan = b.Powered(new SequenceStep(new("sequence"), [new AssignValueStep(new("old"), new("stale"), new ConstantValue(new(2, PhysicalUnit.Volt))), calculation]));
        var bad = mode == "incomplete" ? null : new CalculatedColumn(new(mode == "collision" ? "V" : "bad"), new VariableValue(new(mode == "forward" ? "later" : mode == "self" ? "bad" : mode == "stale" ? "stale" : "V")));
        var preview = b.Runs.DescribeCalculationDraft(plan, calculation.Id, PlanStructureEditKind.Replace, new(calculation.Id, calculation.Body, [bad, new(new("later"), new VariableValue(new("I")))]));
        Assert.NotNull(preview.Columns[0].Error); Assert.Null(preview.Columns[0].Unit); Assert.DoesNotContain(preview.Columns[1].Inputs, input => input.Id.Value == "bad"); Assert.Equal(PhysicalUnit.Ampere, preview.Columns[1].Unit);
    }

    [Fact]
    public void RenamedDraftReadColumnsAndOptionalEnvironmentReplaceTheOldInputContext()
    {
        var b = new Bench(); var calculation = CalculatedPair(b); var body = new ReadSourceSenseStep(new("draft-read"), b.Source.Id, SourceFunction.Voltage, new("newV"), new("newI"), [new EnvironmentRead(EnvironmentRole.Temperature, new("ambient"))]);
        var preview = b.Runs.DescribeCalculationDraft(b.Powered(calculation), calculation.Id, PlanStructureEditKind.Replace, new(calculation.Id, body, [new(new("temperature_draft"), new VariableValue(new("ambient")))]));
        Assert.Contains(preview.RowInputs, input => input.Id.Value == "newV"); Assert.DoesNotContain(preview.RowInputs, input => input.Id.Value is "V" or "I");
        Assert.True(preview.RowInputs.Single(input => input.Id.Value == "ambient").MayBeAbsent); Assert.Equal(PhysicalUnit.Kelvin, preview.Columns[0].Unit); Assert.Null(preview.Columns[0].Error);
    }

    [Fact]
    public void JointDraftUsesAllPointMembersAndReportsEachTypedColumn()
    {
        var b = new Bench(); var original = CalculatedPair(b); var group = new MeasurementGroupStep(new("draft-point"), [new("source", b.ReadSource("current")), new("field", b.ReadField("field"))]);
        var preview = b.Runs.DescribeCalculationDraft(b.Powered(original), original.Id, PlanStructureEditKind.Replace, new(original.Id, group, [new(new("field_draft"), new VariableValue(new("field.value")))]));
        Assert.Equal(new[] { "current.value", "elapsed", "field.value" }, preview.RowInputs.Select(input => input.Id.Value)); Assert.Equal(PhysicalUnit.Tesla, preview.Columns[0].Unit);
    }

    [Fact]
    public void AppendInspectionAndPartialResultsDoNotAdmitOrMutateTheHostPlan()
    {
        var b = new Bench(); var plan = b.Powered(new SequenceStep(new("container"), [new DelayStep(new("wait"), TimeSpan.Zero)]));
        var preview = b.Runs.DescribeCalculationDraft(plan, new("container"), PlanStructureEditKind.Append, new(new("new-calculation"), b.ReadSource("draft-read"), [null]));
        Assert.NotEmpty(preview.RowInputs); Assert.NotNull(preview.Columns[0].Error); Assert.Null(b.Runs.LatestDataSet); Assert.False(b.Runs.HasActiveRun);
        Assert.Single(((SequenceStep)((SequenceStep)plan.Root).Children[1]).Children);
        Assert.Throws<ArgumentException>(() => b.Runs.DescribeCalculationDraft(plan, new("wait"), PlanStructureEditKind.Append, new(new("new"), b.ReadSource("read"), [null])));
    }

    [Fact]
    public void SubtreeAppendProbeValidatesLocalSetupBindingsWithoutOfferingThemAsRowInputs()
    {
        var b = new Bench(); var scope = new SubtreeStep(new("scope"), [new DelayStep(new("existing"), TimeSpan.Zero)], [new(new("outer_bias"), new("local_bias"))]);
        var plan = b.Powered(new SequenceStep(new("sequence"), [new AssignValueStep(new("outer"), new("outer_bias"), new ConstantValue(new(.01, PhysicalUnit.Volt))), scope]));
        var body = new SequenceStep(new("draft-body"), [new SetParameterStep(new("local-set"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new VariableValue(new("local_bias"))), b.ReadSource("local-read")]);
        var preview = b.Runs.DescribeCalculationDraft(plan, scope.Id, PlanStructureEditKind.Append, new(new("new-calculation"), body, [new(new("copy"), new VariableValue(new("local-read.value")))]));
        Assert.Equal(PhysicalUnit.Ampere, preview.Columns[0].Unit); Assert.Null(preview.Columns[0].Error); Assert.DoesNotContain(preview.RowInputs, input => input.Id.Value is "local_bias" or "outer_bias"); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public void DraftInspectionRejectsInvalidHostAndInvalidBodyAndReplacementIdentity()
    {
        var b = new Bench(); var original = CalculatedPair(b); var draft = new PlanCalculationDraft(original.Id, original.Body, [null]);
        var invalid = b.Powered(new SequenceStep(new("invalid"), [original, new AssignValueStep(new("unknown"), new("result"), new VariableValue(new("missing")))]));
        Assert.Throws<ArgumentException>(() => b.Runs.DescribeCalculationDraft(invalid, original.Id, PlanStructureEditKind.Replace, draft));
        Assert.Throws<ArgumentException>(() => b.Runs.DescribeCalculationDraft(b.Powered(original), original.Id, PlanStructureEditKind.Replace, new(new("wrong"), original.Body, [null])));
        Assert.Throws<ArgumentException>(() => b.Runs.DescribeCalculationDraft(b.Powered(original), original.Id, PlanStructureEditKind.Replace, new(original.Id, new DelayStep(new("no-acquisition"), TimeSpan.Zero), [null])));
    }
}
