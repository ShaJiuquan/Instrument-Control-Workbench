using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.Infrastructure.Data;

internal static partial class RunArchiveJson
{
    internal sealed record CalculationOutcomeDto(string Column, CalculationIssue Issue)
    {
        public CalculationOutcome ToDomain() => new(new(Column), Issue);
        public static CalculationOutcomeDto From(CalculationOutcome outcome) => new(outcome.Column.Value, outcome.Issue);
    }
    internal sealed record CalculationDto(string Step, int InputRow, int AlgorithmVersion, CalculationOutcomeDto[] Outcomes)
    {
        public CalculationSummary ToDomain() => new(new(Step), InputRow, Required(Outcomes).Select(outcome => Required(outcome).ToDomain()), AlgorithmVersion);
        public static CalculationDto From(CalculationSummary summary) => new(summary.Step.Value, summary.InputRow, summary.AlgorithmVersion, summary.Outcomes.Select(CalculationOutcomeDto.From).ToArray());
    }
}
