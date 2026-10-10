using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Experiments;

// The desktop IV recipe is an adapter to the same typed executor used by
// nested multi-device plans, rather than a second lifecycle implementation.
internal static class IvExperimentPlan
{
    public static ExperimentPlan Create(InstrumentId device, IvSweepRecipe recipe)
        => new(recipe.Name, new SequenceStep(new("iv"),
        [
            new SourceOutputStep(new("output_on"), device, true),
            new ScanStep(new("voltage_scan"), new(device, Keithley2450Parameters.SourceVoltage.Id),
                Keithley2450Parameters.SourceVoltage.Id,
                ScanValues.Linear(new(recipe.StartVoltage, PhysicalUnit.Volt), new(recipe.StopVoltage, PhysicalUnit.Volt), recipe.Points),
                TimeSpan.FromSeconds(recipe.DwellSeconds),
                new ReadSourceSenseStep(new("source_sense_read"), device, SourceFunction.Voltage, Keithley2450Parameters.MeasuredVoltage.Id, Keithley2450Parameters.MeasuredCurrent.Id)),
        ]),
        [new(device, SourceFunction.Voltage, new(recipe.StartVoltage, PhysicalUnit.Volt), new(recipe.ComplianceAmps, PhysicalUnit.Ampere),
            new(SenseFunction.Current, SensingMode.FourWire, 1, true))],
        new Dictionary<string, Quantity>
        {
            ["start_voltage"] = new(recipe.StartVoltage, PhysicalUnit.Volt),
            ["stop_voltage"] = new(recipe.StopVoltage, PhysicalUnit.Volt),
            ["points"] = new(recipe.Points, PhysicalUnit.Dimensionless),
            ["compliance"] = new(recipe.ComplianceAmps, PhysicalUnit.Ampere),
            ["dwell"] = new(recipe.DwellSeconds, PhysicalUnit.Second),
        });
}
