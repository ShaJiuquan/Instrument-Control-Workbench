using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

// Source readback and sense value belong to one acquisition, not a configuration
// snapshot combined with an independently triggered measurement.
public sealed record SourceSenseReading
{
    public SourceSenseReading(SourceFunction function, SenseFunction senseFunction, Quantity source, Quantity sense, DateTimeOffset timestamp)
    {
        if (!Enum.IsDefined(function) || !Enum.IsDefined(senseFunction)) throw new ArgumentOutOfRangeException(nameof(function));
        var sourceUnit = function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere;
        var senseUnit = senseFunction switch
        {
            SenseFunction.Voltage => PhysicalUnit.Volt,
            SenseFunction.Current => PhysicalUnit.Ampere,
            _ => PhysicalUnit.Ohm,
        };
        if (source.Unit != sourceUnit || sense.Unit != senseUnit) throw new ArgumentException("Source/sense reading units must match their functions.");
        Function = function; SenseFunction = senseFunction; Source = source; Sense = sense; Timestamp = timestamp;
    }
    public SourceFunction Function { get; }
    public SenseFunction SenseFunction { get; }
    public Quantity Source { get; }
    public Quantity Sense { get; }
    public DateTimeOffset Timestamp { get; }
}
