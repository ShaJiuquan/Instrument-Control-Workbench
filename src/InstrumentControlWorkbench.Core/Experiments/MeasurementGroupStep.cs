using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// One logical acquisition per member; setup/cleanup remains in the ordinary engine.
public sealed record MeasurementGroupMember
{
    public MeasurementGroupMember(string label, ExperimentStep body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label); ArgumentNullException.ThrowIfNull(body);
        if (label.Length > 1024 || label.Any(char.IsControl)) throw new ArgumentException("Measurement labels must be bounded display identifiers.");
        var pending = new Stack<(ExperimentStep Step, int Depth)>(); pending.Push((body, 0));
        ExperimentStep? acquisition = null; var count = 0;
        while (pending.TryPop(out var item))
        {
            if (++count > 10_000 || item.Depth > 128) throw new ArgumentException("Measurement member exceeds plan bounds.");
            switch (item.Step)
            {
                case SequenceStep sequence: foreach (var child in sequence.Children) pending.Push((child, item.Depth + 1)); break;
                case ReadStep or ReadSr830FrameStep or ReadPolarizationStep or ReadSourceSenseStep or ReadCurrentResistanceStep or ReadResistanceStep or ReadSampleResistanceStep or SampleAnalysisStep:
                    if (acquisition is not null) throw new ArgumentException("Each member must contain exactly one logical acquisition.");
                    acquisition = item.Step; break;
                case SetParameterStep or AssignValueStep or RouteMatrixStep or VerifyMatrixRouteStep or SourceOutputStep or DelayStep or WaitStableStep: break;
                default: throw new ArgumentException("Measurement members cannot contain scans, branches, repetitions or nested groups.");
            }
        }
        Acquisition = acquisition ?? throw new ArgumentException("Measurement member has no acquisition.");
        Label = label; Body = body; InputCount = Acquisition is SampleAnalysisStep analysis ? analysis.Readings.Count + 1 : 1;
        AcquisitionColumns = Array.AsReadOnly((Acquisition switch
        {
            ReadStep read => read.Channels.Select(c => c.Column),
            ReadSr830FrameStep lockIn => lockIn.Channels.Select(c => c.Column),
            ReadPolarizationStep pulse => new[] { pulse.VoltageColumn, pulse.CurrentColumn, pulse.AttemptsColumn },
            ReadSourceSenseStep pair => new[] { pair.VoltageColumn, pair.CurrentColumn },
            ReadCurrentResistanceStep independent => new[] { independent.VoltageColumn, independent.CurrentColumn, independent.ResistanceColumn },
            ReadResistanceStep resistance => new[] { resistance.VoltageColumn, resistance.CurrentColumn, resistance.ResistanceColumn },
            ReadSampleResistanceStep sample => new[] { sample.Acquisition.VoltageColumn, sample.Acquisition.CurrentColumn, sample.Acquisition.ResistanceColumn },
            SampleAnalysisStep sample => sample.Columns.Select(c => c.Column),
            _ => throw new NotSupportedException(),
        }).ToArray());
        EnvironmentReads = Acquisition switch
        {
            ReadStep read => read.EnvironmentReads,
            ReadSr830FrameStep => Array.Empty<EnvironmentRead>(),
            ReadPolarizationStep read => read.EnvironmentReads,
            ReadSourceSenseStep read => read.EnvironmentReads,
            ReadResistanceStep read => read.EnvironmentReads,
            ReadCurrentResistanceStep read => read.EnvironmentReads,
            ReadSampleResistanceStep read => read.Acquisition.EnvironmentReads,
            SampleAnalysisStep read => read.Readings[0].Acquisition.EnvironmentReads,
            _ => throw new NotSupportedException(),
        };
        OutputColumns = Array.AsReadOnly(AcquisitionColumns.Concat(EnvironmentReads.Select(e => e.Column)).ToArray());
    }
    public string Label { get; }
    public ExperimentStep Body { get; }
    public ExperimentStep Acquisition { get; }
    public int InputCount { get; }
    public IReadOnlyList<ParameterId> OutputColumns { get; }
    public IReadOnlyList<ParameterId> AcquisitionColumns { get; }
    public IReadOnlyList<EnvironmentRead> EnvironmentReads { get; }
}

public sealed record MeasurementGroupStep : ExperimentStep
{
    public const int MaximumInputs = 1024;
    public MeasurementGroupStep(StepId id, IEnumerable<MeasurementGroupMember> members) : base(id)
    {
        ArgumentNullException.ThrowIfNull(members); var items = members.ToArray();
        if (items.Length is < 2 or > 64 || items.Any(m => m is null) || items.Sum(m => m.InputCount) > MaximumInputs)
            throw new ArgumentException("Measurement groups need two to 64 members within the 1024-input bound.");
        var outputs = items.SelectMany(m => m.OutputColumns).ToArray();
        if (outputs.Distinct().Count() != outputs.Length) throw new ArgumentException("Measurement members need distinct result column IDs; display labels may repeat.");
        var labels = new HashSet<string>(StringComparer.Ordinal); var unique = new List<string>();
        foreach (var member in items)
        {
            var label = member.Label; var suffix = 2;
            while (!labels.Add(label)) label = member.Label + "#" + suffix++;
            unique.Add(label);
        }
        Members = Array.AsReadOnly(items); Labels = Array.AsReadOnly(unique.ToArray()); InputCount = items.Sum(m => m.InputCount);
    }
    public IReadOnlyList<MeasurementGroupMember> Members { get; }
    public IReadOnlyList<string> Labels { get; }
    public int InputCount { get; }
}
