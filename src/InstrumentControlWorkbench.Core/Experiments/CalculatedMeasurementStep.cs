using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record CalculatedColumn
{
    public CalculatedColumn(ParameterId column, PlanValue expression)
    {
        ArgumentNullException.ThrowIfNull(column); ArgumentNullException.ThrowIfNull(expression);
        NumericPlanValue.Variables(expression); Column = column; Expression = expression;
    }
    public ParameterId Column { get; }
    public PlanValue Expression { get; }
}
public sealed record CalculatedMeasurementStep : ExperimentStep
{
    public CalculatedMeasurementStep(StepId id, ExperimentStep body, IEnumerable<CalculatedColumn> columns, bool continueOnUnavailable = false) : base(id)
    {
        ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(columns); var items = columns.ToArray();
        if (items.Length is < 1 or > 64 || items.Any(column => column is null) || items.Select(column => column.Column).Distinct().Count() != items.Length)
            throw new ArgumentException("Calculations require one to 64 distinct numeric result columns.");
        if (body is MeasurementGroupStep group)
        {
            Acquisition = group; InputCount = group.InputCount + 1; InputColumns = Array.AsReadOnly(group.Members.SelectMany(member => member.OutputColumns).ToArray());
        }
        else
        {
            var member = new MeasurementGroupMember("calculated measurement", body); Acquisition = member.Acquisition; InputCount = member.InputCount; InputColumns = member.OutputColumns;
        }
        if (items.Any(column => InputColumns.Contains(column.Column))) throw new ArgumentException("Calculated results cannot replace acquisition inputs.");
        Body = body; Columns = Array.AsReadOnly(items); ContinueOnUnavailable = continueOnUnavailable;
    }
    public ExperimentStep Body { get; }
    public ExperimentStep Acquisition { get; }
    public int InputCount { get; }
    public IReadOnlyList<ParameterId> InputColumns { get; }
    public IReadOnlyList<CalculatedColumn> Columns { get; }
    public bool ContinueOnUnavailable { get; }
}
