using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

// A measurement group has an explicit boundary on each execution. It cannot
// pool readings from adjacent scan points or incomplete previous executions.
public sealed record SampleAnalysisStep : ExperimentStep
{
    public SampleAnalysisStep(StepId id, IEnumerable<ReadSampleResistanceStep> readings, IEnumerable<SampleDerivedColumn> columns) : base(id)
    {
        ArgumentNullException.ThrowIfNull(readings); ArgumentNullException.ThrowIfNull(columns);
        var r = readings.ToArray(); var c = columns.ToArray();
        if (r.Length is < 1 or > 8 || r.Any(v => v is null) || r.Select(v => v.Id).Distinct().Count() != r.Length)
            throw new ArgumentException("An analysis group needs one to eight distinct readings.", nameof(readings));
        if (c.Length != 4 || c.Any(v => v is null) || c.Select(v => v.Kind).Distinct().Count() != c.Length || c.Select(v => v.Column).Distinct().Count() != c.Length)
            throw new ArgumentException("An analysis group needs four distinct derived quantities and column IDs.", nameof(columns));
        Readings = Array.AsReadOnly(r); Columns = Array.AsReadOnly(c);
    }
    public IReadOnlyList<ReadSampleResistanceStep> Readings { get; }
    public IReadOnlyList<SampleDerivedColumn> Columns { get; }
    public SampleProfile ValidateRouting(RoutingConfiguration routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        var first = routing.Resolve(Readings[0].Binding);
        var sample = routing.Samples.Single(s => s.Id == first.Binding.Sample);
        var selections = SampleResistanceAnalysis.Selections(sample.Topology);
        if (!Readings.Select(r => r.Binding.Selection).SequenceEqual(selections) ||
            !Columns.Select(c => c.Kind).ToHashSet().SetEquals(SampleResistanceAnalysis.Kinds(sample.Topology)))
            throw new ArgumentException("Analysis requires the complete ordered Hall or van der Pauw measurement and all derived columns.");
        foreach (var reading in Readings)
        {
            var route = routing.Resolve(reading.Binding);
            if (reading.RoutingManagedExternally || route.SourceMeter != reading.Acquisition.Device || route.Binding.Sample != first.Binding.Sample || route.Binding.Wiring != first.Binding.Wiring ||
                reading.Acquisition.Device != Readings[0].Acquisition.Device || reading.Acquisition.Function != Readings[0].Acquisition.Function ||
                !reading.Acquisition.EnvironmentReads.SequenceEqual(Readings[0].Acquisition.EnvironmentReads) ||
                reading.Acquisition.VoltageColumn != Readings[0].Acquisition.VoltageColumn || reading.Acquisition.CurrentColumn != Readings[0].Acquisition.CurrentColumn ||
                reading.Acquisition.ResistanceColumn != Readings[0].Acquisition.ResistanceColumn)
                throw new ArgumentException("Analysis readings must manage their routes internally and share the configured source, sample, wiring, source function and raw columns.");
        }
        if (Columns.Any(c => c.Column == Readings[0].Acquisition.VoltageColumn || c.Column == Readings[0].Acquisition.CurrentColumn || c.Column == Readings[0].Acquisition.ResistanceColumn || Readings[0].Acquisition.EnvironmentReads.Any(e => e.Column == c.Column)))
            throw new ArgumentException("Derived columns cannot overwrite raw columns.");
        return sample;
    }
}
public sealed record SampleDerivedColumn
{
    public SampleDerivedColumn(SampleDerivedQuantity kind, ParameterId column)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(column); Kind = kind; Column = column;
    }
    public SampleDerivedQuantity Kind { get; }
    public ParameterId Column { get; }
}
