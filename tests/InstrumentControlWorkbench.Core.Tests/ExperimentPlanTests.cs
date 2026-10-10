using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ExperimentPlanTests
{
    [Fact]
    public void ScanStrategiesPreserveEndpointsDirectionAndUnits()
    {
        var descending = ScanValues.Stepped(new(1, PhysicalUnit.Volt), new(0, PhysicalUnit.Volt), new(0.3, PhysicalUnit.Volt));
        Assert.Equal(new[] { 1.0, 0.7, 0.4, 0.1, 0.0 }, descending.Values.Select(v => Math.Round(v.Value, 10)));
        var linear = ScanValues.Linear(new(-1, PhysicalUnit.Tesla), new(1, PhysicalUnit.Tesla), 3);
        Assert.Equal(new[] { -1.0, 0.0, 1.0 }, linear.Values.Select(v => v.Value));
        var logarithmic = ScanValues.Logarithmic(new(1, PhysicalUnit.Ohm), new(100, PhysicalUnit.Ohm), 3);
        Assert.Equal(10, logarithmic.Values[1].Value, 10);
        Assert.Equal(100, logarithmic.Values[^1].Value);
        var segments = ScanValues.Segmented([linear, ScanValues.Linear(new(1, PhysicalUnit.Tesla), new(0, PhysicalUnit.Tesla), 2)]);
        Assert.Equal(new[] { -1.0, 0.0, 1.0, 0.0 }, segments.Values.Select(v => v.Value));
        Assert.All(segments.Values, v => Assert.Equal(PhysicalUnit.Tesla, v.Unit));
    }

    [Fact]
    public void InvalidOrOversizedScansAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new ScanValues([new(1, PhysicalUnit.Volt), new(2, PhysicalUnit.Ampere)]));
        Assert.Throws<ArgumentException>(() => ScanValues.Logarithmic(new(0, PhysicalUnit.Volt), new(1, PhysicalUnit.Volt), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanValues.Stepped(new(0, PhysicalUnit.Volt), new(1, PhysicalUnit.Volt), new(1e-9, PhysicalUnit.Volt)));
        Assert.Throws<ArgumentException>(() => ScanValues.Stepped(new(0, PhysicalUnit.Volt), new(1, PhysicalUnit.Volt), new(-0.1, PhysicalUnit.Volt)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanValues.Linear(new(0, PhysicalUnit.Volt), new(1, PhysicalUnit.Volt), 1));
    }

    [Fact]
    public void PlanCopiesCollectionsAndRejectsUnsupportedDurations()
    {
        var quantities = new List<Quantity> { new(1, PhysicalUnit.Volt) };
        var scan = new ScanValues(quantities);
        quantities[0] = new(2, PhysicalUnit.Volt);
        Assert.Equal(1, scan.Values.Single().Value);
        var children = new List<ExperimentStep> { new DelayStep(new("delay"), TimeSpan.Zero) };
        var metadata = new Dictionary<string, Quantity> { ["points"] = new(1, PhysicalUnit.Dimensionless) };
        var plan = new ExperimentPlan("copy", new SequenceStep(new("root"), children), metadata: metadata);
        children.Clear(); metadata.Clear();
        Assert.Single(((SequenceStep)plan.Root).Children);
        Assert.Single(plan.Metadata);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayStep(new("huge"), TimeSpan.FromDays(100)));
    }

    [Fact]
    public void HeterogeneousRowsRequireOnlyPresentColumnsAndTheirDependencies()
    {
        var axis = new ParameterDescriptor(new("voltage"), "Voltage", PhysicalUnit.Volt, ParameterAccess.Read);
        var current = new ParameterDescriptor(new("current"), "Current", PhysicalUnit.Ampere, ParameterAccess.Read);
        var temp = new ParameterDescriptor(new("temperature"), "Temperature", PhysicalUnit.Kelvin, ParameterAccess.Read);
        var schema = new DataSetSchema([new(axis, optional: true), new(current, [axis.Id], optional: true), new(temp, optional: true)]);
        schema.ValidateRow(new(DateTimeOffset.UnixEpoch, new Dictionary<ParameterId, Quantity> { [temp.Id] = new(300, PhysicalUnit.Kelvin) }));
        schema.ValidateRow(new(DateTimeOffset.UnixEpoch, new Dictionary<ParameterId, Quantity>
        { [axis.Id] = new(0.1, PhysicalUnit.Volt), [current.Id] = new(1e-3, PhysicalUnit.Ampere) }));
        Assert.Throws<ArgumentException>(() => schema.ValidateRow(new(DateTimeOffset.UnixEpoch,
            new Dictionary<ParameterId, Quantity> { [current.Id] = new(1e-3, PhysicalUnit.Ampere) })));
        Assert.Throws<ArgumentException>(() => schema.ValidateRow(new(DateTimeOffset.UnixEpoch,
            new Dictionary<ParameterId, Quantity> { [new("unknown")] = new(0, PhysicalUnit.Dimensionless) })));
        Assert.Throws<ArgumentException>(() => new DataSetSchema([new(temp)]).ValidateRow(new(DateTimeOffset.UnixEpoch,
            new Dictionary<ParameterId, Quantity>())));
    }
}
