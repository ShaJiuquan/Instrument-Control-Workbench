using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Core.Data;

public sealed partial class RunDataRowValidator
{
    private void ValidateCalculation(MeasurementDataRow row)
    {
        var summary = row.Calculation!;
        if (!calculations.TryGetValue(summary.Step, out var step) || summary.InputRow != count || previousRow is null || previousRow.Calculation is not null) throw new ArgumentException("Calculated row must reference its immediately preceding validated input.");
        var input = previousRow;
        var acquisition = step.Acquisition.Id;
        if (step.Acquisition is SampleAnalysisStep ? input.Analysis?.Step != acquisition : step.Acquisition is MeasurementGroupStep ? input.MeasurementGroup?.Step != acquisition : input.Quality.Count != 0 ? input.Quality.Any(quality => quality.Step != acquisition) : input.AcquisitionFailures.Count != 1 || input.AcquisitionFailures[0].Step != acquisition)
            throw new ArgumentException("Calculated input must belong to its declared acquisition.");
        if (row.Routing is { } routing) plan!.Routing!.ValidateContext(routing);
        var expected = CalculationSummary.BuildRow(step, count, input);
        if (!step.ContinueOnUnavailable && expected.Calculation!.Outcomes.Any(outcome => outcome.Issue != CalculationIssue.None)) throw new ArgumentException("Stop-on-error calculations cannot claim unavailable result rows.");
        if (!expected.Calculation!.Outcomes.SequenceEqual(summary.Outcomes) || !expected.Values.Keys.ToHashSet().SetEquals(row.Values.Keys) || expected.Values.Any(value => row.Values[value.Key] != value.Value) || row.Timestamp != input.Timestamp || ((row.Routing is null) != (input.Routing is null) || row.Routing is not null && row.Routing.Binding != input.Routing!.Binding) || !Equals(row.Analysis, input.Analysis) || ((row.MeasurementGroup is null) != (input.MeasurementGroup is null) || row.MeasurementGroup is not null && (row.MeasurementGroup.Step != input.MeasurementGroup!.Step || !row.MeasurementGroup.Inputs.SequenceEqual(input.MeasurementGroup.Inputs))) || !row.Environment.SequenceEqual(input.Environment) || !row.Quality.SequenceEqual(input.Quality) || !row.IndependentResistance.SequenceEqual(input.IndependentResistance) || !row.AcquisitionFailures.SequenceEqual(input.AcquisitionFailures))
            throw new ArgumentException("Calculation values, inputs, outcomes or provenance differ from the frozen plan and original row.");
    }
}
