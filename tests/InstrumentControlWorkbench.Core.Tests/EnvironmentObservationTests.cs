using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class EnvironmentObservationTests
{
    [Fact]
    public void ValuesAndFailureEvidenceRequireDistinctTypedSourcesAndImmutablePolicies()
    {
        var address = new ParameterAddress(new("temperature"), new("temperature.readback"));
        var reads = new List<EnvironmentRead> { new(EnvironmentRole.Temperature, new("T"), address) };
        var read = new ReadResistanceStep(new("r"), new("source"), Core.Instruments.SourceFunction.Voltage, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere), reads);
        reads.Clear(); Assert.Single(read.EnvironmentReads);
        Assert.Throws<ArgumentException>(() => new ReadStep(new("read"), [new(new("T"), address)], read.EnvironmentReads));
        Assert.Throws<ArgumentException>(() => new ReadSourceSenseStep(new("read"), new("source"), Core.Instruments.SourceFunction.Voltage, new("V"), new("I"), [new(EnvironmentRole.Field, new("a")), new(EnvironmentRole.Field, new("b"))]));
        Assert.Throws<ArgumentException>(() => new EnvironmentObservation(EnvironmentRole.Temperature, new("T"), address, DateTimeOffset.UtcNow, new(4.2, PhysicalUnit.Tesla)));
        Assert.Throws<ArgumentException>(() => new EnvironmentObservation(EnvironmentRole.Temperature, new("T"), null, DateTimeOffset.UtcNow, new(4.2, PhysicalUnit.Kelvin)));
        var observations = new List<EnvironmentObservation> { new(EnvironmentRole.Temperature, new("T"), address, DateTimeOffset.UtcNow, null, "read failed") };
        var row = new MeasurementDataRow(DateTimeOffset.UtcNow, new Dictionary<ParameterId, Quantity>(), environment: observations); observations.Clear(); Assert.Single(row.Environment);
    }
    [Theory]
    [InlineData("none")]
    [InlineData("both")]
    [InlineData("blank")]
    [InlineData("time")]
    [InlineData("long")]
    public void ObservationRejectsAmbiguousOrUnboundedUnavailableData(string damage)
    {
        Quantity? value = damage == "both" ? new(4.2, PhysicalUnit.Kelvin) : null;
        var error = damage switch { "none" => null, "blank" => " ", "long" => new string('x', 8193), _ => "failed" };
        var time = damage == "time" ? default : DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new EnvironmentObservation(EnvironmentRole.Temperature, new("T"), new(new("controller"), new("readback")), time, value, error));
    }
}
