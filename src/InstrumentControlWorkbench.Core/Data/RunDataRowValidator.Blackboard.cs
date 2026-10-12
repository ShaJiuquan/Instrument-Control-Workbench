using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed partial class RunDataRowValidator
{
    public void ValidateBlackboard(RunId id, RunBlackboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id != id) throw new ArgumentException("Blackboard snapshot belongs to a different run.");
        var units = schema.Columns.ToDictionary(c => c.Parameter.Id, c => c.Parameter.Unit);
        foreach (var (variable, entry) in snapshot.Entries)
        {
            if (!steps.TryGetValue(entry.Source, out var source)) throw new ArgumentException("Blackboard entry requires its frozen workflow source step.");
            bool valid;
            if (entry.Kind != BlackboardWriteKind.Step)
            {
                valid = source is SubtreeStep scope && (entry.Kind == BlackboardWriteKind.SubtreeInput ? scope.Inputs : scope.Outputs).Any(m => m.Destination == variable);
            }
            else valid = source switch
            {
                AssignValueStep assign => assign.Variable == variable && (assign.Value switch
                {
                    ConstantValue constant => entry.Value is BlackboardQuantity quantity && quantity.Value == constant.Value,
                    ArithmeticValue arithmetic => entry.Value is BlackboardQuantity result && result.Value.Unit == NumericPlanValue.InferUnit(arithmetic, input => snapshot.Entries.TryGetValue(input, out var value) && value.Value is BlackboardQuantity number ? number.Value.Unit : throw new ArgumentException("Arithmetic snapshot needs numeric input types.")) && (!NumericPlanValue.TryConstant(arithmetic, out var literal) || result.Value == literal),
                    BooleanValue boolean => entry.Value is BlackboardBoolean value && value.Value == boolean.Value,
                    TextValue text => entry.Value is BlackboardText value && value.Value == text.Value,
                    QuantityFromNumberValue qualified => IsQuantity(entry, qualified.Unit),
                    VariableValue => true,
                    _ => false,
                }),
                MutateValueStep mutation => mutation.Variable == variable && (mutation.Action == VariableMutation.Multiply ? entry.Value is BlackboardQuantity :
                    IsQuantity(entry, mutation.Value.Unit) && (mutation.Action != VariableMutation.Set || ((BlackboardQuantity)entry.Value).Value == mutation.Value)),
                ReadStep read => acquisitions[read.Id].ContainsKey(variable) && units.TryGetValue(variable, out var unit) && IsQuantity(entry, unit),
                ReadSourceSenseStep read => variable == read.VoltageColumn ? IsQuantity(entry, PhysicalUnit.Volt) : variable == read.CurrentColumn && IsQuantity(entry, PhysicalUnit.Ampere),
                ReadResistanceStep read => ValidResistance(read, variable, entry),
                ReadCurrentResistanceStep read => ValidResistance(read, variable, entry),
                ReadSampleResistanceStep read => ValidResistance(read.Acquisition, variable, entry),
                SampleAnalysisStep analysis => analysis.Columns.Any(c => c.Column == variable && IsQuantity(entry, SampleResistanceAnalysis.Unit(c.Kind))),
                ScanStep scan => variable == scan.Column && entry.Value is BlackboardQuantity point && scan.Values.Values.Contains(point.Value),
                ForEachStep loop => variable == loop.Variable ? entry.Value is BlackboardQuantity point && loop.Values.Values.Contains(point.Value) :
                    variable.Value == loop.Variable.Value + ".index" ? IsCount(entry, (int)loop.IndexBase, loop.Values.Values.Count - 1 + (int)loop.IndexBase) :
                    variable.Value == loop.Variable.Value + ".total" && IsCount(entry, loop.Values.Values.Count, loop.Values.Values.Count),
                WhileStep loop => variable == loop.IterationColumn && IsCount(entry, 1, plan!.MaximumStepExecutions),
                RetryStep retry => variable == retry.AttemptColumn ? IsCount(entry, 1, retry.MaxAttempts) : variable == retry.TotalColumn && IsCount(entry, retry.MaxAttempts, retry.MaxAttempts),
                FallbackStep fallback => variable == fallback.BranchColumn ? IsCount(entry, 1, fallback.Children.Count) : variable == fallback.TotalColumn && IsCount(entry, fallback.Children.Count, fallback.Children.Count),
                _ => false,
            };
            if (!valid) throw new ArgumentException($"Blackboard value {variable} has incompatible source, type, unit, or transfer provenance.");
        }
    }
    private static bool IsQuantity(BlackboardEntry entry, PhysicalUnit unit) => entry.Value is BlackboardQuantity quantity && quantity.Value.Unit == unit;
    private static bool IsCount(BlackboardEntry entry, long minimum, long maximum) => entry.Value is BlackboardQuantity { Value.Unit: PhysicalUnit.Dimensionless } number &&
        number.Value.Value == Math.Truncate(number.Value.Value) && number.Value.Value >= minimum && number.Value.Value <= maximum;
    private static bool ValidResistance(ResistanceAcquisitionStep read, ParameterId variable, BlackboardEntry entry)
        => variable == read.VoltageColumn ? IsQuantity(entry, PhysicalUnit.Volt) : variable == read.CurrentColumn ? IsQuantity(entry, PhysicalUnit.Ampere) : variable == read.ResistanceColumn && IsQuantity(entry, PhysicalUnit.Ohm);
}
