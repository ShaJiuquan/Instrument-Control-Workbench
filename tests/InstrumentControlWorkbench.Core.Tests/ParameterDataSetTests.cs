using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ParameterDataSetTests
{
    private static readonly ParameterDescriptor Voltage = Keithley2450Parameters.SourceVoltage;
    private static readonly ParameterDescriptor Current = Keithley2450Parameters.MeasuredCurrent;

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFinitePhysicalValuesAreRejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(value, PhysicalUnit.Volt));
    }

    [Fact]
    public void ParameterWritesRejectWrongUnitsLimitsAndReadOnlyAccess()
    {
        Assert.Throws<ArgumentException>(() => Voltage.ValidateWrite(new(1, PhysicalUnit.Ampere)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Voltage.ValidateWrite(new(211, PhysicalUnit.Volt)));
        Assert.Throws<InvalidOperationException>(() => Current.ValidateWrite(new(1e-3, PhysicalUnit.Ampere)));
        Assert.Throws<ArgumentException>(() => new ParameterDescriptor(new("bad"), "Bad", PhysicalUnit.Volt,
            ParameterAccess.Read, new(0, PhysicalUnit.Ampere)));
    }

    [Fact]
    public void SchemaRejectsAmbiguousMissingAndCyclicDependencies()
    {
        Assert.Throws<ArgumentException>(() => new DataSetSchema([new(Voltage), new(Voltage)]));
        Assert.Throws<ArgumentException>(() => new DataSetSchema([new(Current, [new("missing")])]));
        Assert.Throws<ArgumentException>(() => new DataSetSchema([
            new(Voltage, [Current.Id]), new(Current, [Voltage.Id])]));
        Assert.Throws<ArgumentException>(() => new DataSetSchema([new(Current, [Current.Id])]));
    }

    [Fact]
    public void SchemaRejectsMissingExtraAndMismatchedMeasurementValues()
    {
        var schema = new DataSetSchema([new(Voltage), new(Current, [Voltage.Id])]);
        Assert.Throws<ArgumentException>(() => schema.ValidateRow(new(DateTimeOffset.UnixEpoch,
            new Dictionary<ParameterId, Quantity> { [Voltage.Id] = new(1, PhysicalUnit.Volt) })));
        Assert.Throws<ArgumentException>(() => schema.ValidateRow(new(DateTimeOffset.UnixEpoch,
            new Dictionary<ParameterId, Quantity>
            {
                [Voltage.Id] = new(1, PhysicalUnit.Volt), [new("extra")] = new(1, PhysicalUnit.Ampere),
            })));
        Assert.Throws<ArgumentException>(() => schema.ValidateRow(new(DateTimeOffset.UnixEpoch,
            new Dictionary<ParameterId, Quantity>
            {
                [Voltage.Id] = new(1, PhysicalUnit.Ampere), [Current.Id] = new(1, PhysicalUnit.Ampere),
            })));
    }

    [Fact]
    public void CapturedSettingsAndRowsCannotBeChangedThroughOriginalCollections()
    {
        var settings = new Dictionary<string, string> { ["terminals"] = "Rear" };
        var parameters = new List<ParameterSnapshot> { new(Voltage, new(1, PhysicalUnit.Volt)) };
        var snapshot = new InstrumentParameterSnapshot(new("k2450"), "mock", DateTimeOffset.UnixEpoch, parameters, settings);
        var values = new Dictionary<ParameterId, Quantity> { [Voltage.Id] = new(1, PhysicalUnit.Volt) };
        var row = new MeasurementDataRow(DateTimeOffset.UnixEpoch, values);
        var recipe = new Dictionary<string, Quantity> { ["start_voltage"] = new(1, PhysicalUnit.Volt) };
        var data = new RunDataSet(RunId.New(), "immutable", DateTimeOffset.UnixEpoch, new([new(Voltage)]), recipe, [snapshot]);
        data.Append(row);

        settings["terminals"] = "Front";
        parameters.Clear();
        values[Voltage.Id] = new(2, PhysicalUnit.Volt);
        recipe["start_voltage"] = new(3, PhysicalUnit.Volt);

        Assert.Equal("Rear", snapshot.Configuration["terminals"]);
        Assert.Single(snapshot.Parameters);
        Assert.Equal(1, data.Rows[0].Values[Voltage.Id].Value);
        Assert.Equal(1, data.Recipe["start_voltage"].Value);
    }

    [Theory]
    [InlineData(MeasurementRunState.Completed)]
    [InlineData(MeasurementRunState.Aborted)]
    [InlineData(MeasurementRunState.Faulted)]
    public void FinalizedDataRetainsPartialRowsAndRejectsFurtherWrites(MeasurementRunState result)
    {
        var data = new RunDataSet(RunId.New(), "partial", DateTimeOffset.UnixEpoch, new([new(Voltage)]),
            new Dictionary<string, Quantity>(), []);
        var row = new MeasurementDataRow(DateTimeOffset.UnixEpoch,
            new Dictionary<ParameterId, Quantity> { [Voltage.Id] = new(1, PhysicalUnit.Volt) });
        data.Append(row);
        data.Complete(result, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Single(data.Rows);
        Assert.Equal(result, data.State);
        Assert.Throws<InvalidOperationException>(() => data.Append(row));
        Assert.Throws<InvalidOperationException>(() => data.Complete(result, DateTimeOffset.UnixEpoch.AddSeconds(2)));
    }
}
