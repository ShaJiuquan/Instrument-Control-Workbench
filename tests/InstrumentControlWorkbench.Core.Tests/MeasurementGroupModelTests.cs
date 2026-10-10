using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class MeasurementGroupModelTests
{
    private static ReadStep Read(string id, string column) => new(new(id), [new(new(column), new(new("source"), new("readback")))]);
    private static MeasurementGroupStep Group() => new(new("point"), [new("CH1", Read("a", "a")), new("CH1", Read("b", "b"))]);
    private static DataSetSchema Schema() => new([
        new(new(new("elapsed"), "Elapsed", PhysicalUnit.Second, ParameterAccess.Read)),
        new(new(new("a"), "A", PhysicalUnit.Volt, ParameterAccess.Read), [new("elapsed")], optional: true),
        new(new(new("b"), "B", PhysicalUnit.Ampere, ParameterAccess.Read), [new("elapsed")], optional: true),
    ]);
    private static MeasurementDataRow[] Inputs() => [
        new(DateTimeOffset.UnixEpoch.AddSeconds(1), new Dictionary<ParameterId, Quantity> { [new("a")] = new(0.1, PhysicalUnit.Volt), [new("elapsed")] = new(1, PhysicalUnit.Second) }),
        new(DateTimeOffset.UnixEpoch.AddSeconds(2), new Dictionary<ParameterId, Quantity> { [new("b")] = new(1e-5, PhysicalUnit.Ampere), [new("elapsed")] = new(2, PhysicalUnit.Second) }),
    ];
    [Fact]
    public void WidePointPreservesFirstTimeDistinctValuesAndRepeatedLabelIdentity()
    {
        var step = Group(); var raw = Inputs(); var point = MeasurementGroupSummary.BuildRow(step, 1, raw);
        Assert.Equal(raw[0].Timestamp, point.Timestamp); Assert.Equal(1, point.Values[new("elapsed")].Value);
        Assert.Equal(0.1, point.Values[new("a")].Value); Assert.Equal(1e-5, point.Values[new("b")].Value);
        Assert.Equal(new[] { "CH1", "CH1#2" }, point.MeasurementGroup!.Inputs.Select(i => i.Label));
        var data = new RunDataSet(RunId.New(), "Point", DateTimeOffset.UnixEpoch, Schema(), new Dictionary<string, Quantity>(), [], new("Point", step));
        foreach (var row in raw) data.Append(row); data.Append(point); Assert.Equal(3, data.Rows.Count);
    }
    [Theory]
    [InlineData("value")]
    [InlineData("time")]
    [InlineData("elapsed")]
    [InlineData("range")]
    [InlineData("label")]
    [InlineData("step")]
    [InlineData("missing")]
    public void ProvenanceRejectsTamperedOrIncompletePoint(string damage)
    {
        var step = Group(); var raw = Inputs(); var point = MeasurementGroupSummary.BuildRow(step, 1, raw);
        var values = new Dictionary<ParameterId, Quantity>(point.Values); var time = point.Timestamp; var summary = point.MeasurementGroup;
        switch (damage)
        {
            case "value": values[new("a")] = new(0.2, PhysicalUnit.Volt); break;
            case "time": time = raw[1].Timestamp; break;
            case "elapsed": values[new("elapsed")] = new(2, PhysicalUnit.Second); break;
            case "range": summary = new(step.Id, [new("CH1", 2, 1), new("CH1#2", 3, 1)]); break;
            case "label": summary = new(step.Id, [new("CH1#2", 1, 1), new("CH1", 2, 1)]); break;
            case "step": summary = new(new("other"), summary!.Inputs); break;
        }
        var validator = new RunDataRowValidator(new("Point", step), Schema()); validator.Append(raw[0]);
        if (damage != "missing") validator.Append(raw[1]);
        Assert.Throws<ArgumentException>(() => validator.Append(new(time, values, measurementGroup: summary)));
    }
    [Fact]
    public void MembersFreezeAndRejectNonMeasurementControlFlowAndSharedResultColumns()
    {
        var members = new List<MeasurementGroupMember> { new("CH1#2", Read("a", "a")), new("CH1", Read("b", "b")), new("CH1", Read("c", "c")) };
        var group = new MeasurementGroupStep(new("point"), members); members.Clear();
        Assert.Equal(new[] { "CH1#2", "CH1", "CH1#3" }, group.Labels);
        Assert.Throws<ArgumentException>(() => new MeasurementGroupMember("bad", new DelayStep(new("delay"), TimeSpan.Zero)));
        Assert.Throws<ArgumentException>(() => new MeasurementGroupMember("bad", new RepeatStep(new("repeat"), 2, Read("r", "a"))));
        Assert.Throws<ArgumentException>(() => new MeasurementGroupMember("bad", new SequenceStep(new("sequence"), [Read("r1", "a"), Read("r2", "b")])));
        Assert.Throws<ArgumentException>(() => new MeasurementGroupStep(new("p"), [new("A", Read("r1", "a")), new("B", Read("r2", "a"))]));
        Assert.Throws<ArgumentException>(() => new MeasurementGroupStep(new("p"), Enumerable.Range(0, 65).Select(i => new MeasurementGroupMember("A", Read("r" + i, "c" + i)))));
    }
    [Fact]
    public void SummaryCannotBeNestedOrAcceptedWithoutDeclaredPlan()
    {
        var group = Group(); var inputs = Inputs(); var point = MeasurementGroupSummary.BuildRow(group, 1, inputs);
        Assert.Throws<ArgumentException>(() => new MeasurementGroupMember("nested", group));
        Assert.Throws<ArgumentException>(() => new RunDataRowValidator(null, Schema()).Append(point));
        Assert.Throws<ArgumentException>(() => new MeasurementGroupSummary(group.Id, [new("A", 1, 1), new("B", 3, 1)]));
        Assert.Throws<ArgumentException>(() => new MeasurementGroupSummary(group.Id, [new("A", 1, 1), new("A", 2, 1)]));
    }
}
