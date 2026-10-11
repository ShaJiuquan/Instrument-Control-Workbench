using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record MeasurementGroupInput
{
    public MeasurementGroupInput(string label, int firstInputRow, int inputCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > 1040 || firstInputRow < 1 || inputCount < 1 || inputCount > MeasurementGroupStep.MaximumInputs) throw new ArgumentOutOfRangeException(nameof(firstInputRow));
        Label = label; FirstInputRow = firstInputRow; InputCount = inputCount;
    }
    public string Label { get; }
    public int FirstInputRow { get; }
    public int InputCount { get; }
}

public sealed record MeasurementGroupSummary
{
    public MeasurementGroupSummary(StepId step, IEnumerable<MeasurementGroupInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(step); ArgumentNullException.ThrowIfNull(inputs); var items = inputs.ToArray();
        if (items.Length is < 2 or > 64 || items.Any(i => i is null) || items.Select(i => i.Label).Distinct(StringComparer.Ordinal).Count() != items.Length ||
            items.Sum(i => i.InputCount) > MeasurementGroupStep.MaximumInputs)
            throw new ArgumentException("Measurement summary needs bounded distinct input groups.");
        for (var i = 1; i < items.Length; i++)
            if (items[i].FirstInputRow != checked(items[i - 1].FirstInputRow + items[i - 1].InputCount)) throw new ArgumentException("Measurement inputs must be contiguous and ordered.");
        Step = step; Inputs = Array.AsReadOnly(items);
    }
    public StepId Step { get; }
    public IReadOnlyList<MeasurementGroupInput> Inputs { get; }

    // Wide values come from each logical result; first raw acquisition owns point time.
    public static MeasurementDataRow BuildRow(MeasurementGroupStep step, int firstInputRow, IReadOnlyList<MeasurementDataRow> rows)
    {
        ArgumentNullException.ThrowIfNull(step); ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count != step.InputCount) throw new ArgumentException("Measurement group input is incomplete.");
        var elapsed = new ParameterId("elapsed"); var values = new Dictionary<ParameterId, Quantity>(); var inputs = new List<MeasurementGroupInput>();
        var offset = 0; var environment = new List<EnvironmentObservation>(); var quality = new List<AcquisitionQuality>(); var resistance = new List<IndependentResistanceEvidence>();
        for (var i = 0; i < step.Members.Count; i++)
        {
            var member = step.Members[i]; inputs.Add(new(step.Labels[i], checked(firstInputRow + offset), member.InputCount));
            quality.AddRange(rows[offset + member.InputCount - 1].Quality);
            resistance.AddRange(rows[offset + member.InputCount - 1].IndependentResistance);
            environment.AddRange(rows[offset + member.InputCount - 1].Environment);
            foreach (var pair in rows[offset + member.InputCount - 1].Values)
                if (pair.Key != elapsed && !values.TryAdd(pair.Key, pair.Value) && values[pair.Key] != pair.Value)
                    throw new ArgumentException("Grouped acquisitions disagree on a shared point column.");
            offset += member.InputCount;
        }
        if (quality.Count != 0)
        {
            var end = 0;
            foreach (var member in step.Members)
            {
                end += member.InputCount;
                if (rows[end - 1].Quality.Count == 0) throw new ArgumentException("A wide point cannot claim complete quality while a member has no evidence.");
            }
        }
        values[elapsed] = rows[0].Values[elapsed];
        return new(rows[0].Timestamp, values, measurementGroup: new(step.Id, inputs), environment: environment, quality: quality, independentResistance: resistance);
    }
}
