using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Core.Data;

public sealed partial class RunDataRowValidator
{
    private static bool SameAcEvidence(IReadOnlyList<Sr830AcEvidence> a, IReadOnlyList<Sr830AcEvidence> b)
        => a.Count == b.Count && a.Zip(b).All(pair => pair.First.ContentEquals(pair.Second));

    private void ValidateSr830Ac(MeasurementDataRow row)
    {
        var required = activeLockInFrames.Values.Where(step => step.Frame.Channels.Any(c => row.Values.ContainsKey(c.Column))).ToArray();
        if (row.Sr830Ac.Count != required.Length || row.Sr830Ac.Any(e => !required.Any(step => step.Id == e.Step)))
            throw new ArgumentException("Active SR830 columns require exactly their declared isolation evidence; extra evidence is forbidden.");
        foreach (var step in required)
        {
            var evidence = row.Sr830Ac.Single(e => e.Step == step.Id);
            if (evidence.Device != step.Device || evidence.Isolator != step.Isolator || !evidence.Connections.ToHashSet().SetEquals(step.Connections) || evidence.Reference != step.Excitation.ExpectedReference)
                throw new ArgumentException("Active SR830 evidence differs from the frozen generator, isolator, connections or excitation.");
            var reference = evidence.Reference.CaptureParameters();
            if (step.Frame.Channels.Any(c => !row.Values.ContainsKey(c.Column)) || step.Frame.Channels.Skip(8).Any(c => row.Values[c.Column] != reference[c.Address.Parameter]))
                throw new ArgumentException("Active SR830 row must preserve all twelve frame values and matching observed reference.");
            var raw = step.Frame.Channels.Take(4).Select(c => c.Column).ToHashSet();
            var quality = row.Quality.Where(q => q.Step == step.Id).ToArray();
            if (quality.Length != 4 || !quality.Select(q => q.Column).ToHashSet().SetEquals(raw) || quality.Any(q => q.Device != step.Device || q.CapturedAt != evidence.FrameCapturedAt || q.Status is not null || q.AcquisitionConfiguration is not null || q.MeterConfiguration is not null || q.PicoammeterConfiguration is not null || q.ElectrometerConfiguration is not null))
                throw new ArgumentException("Active SR830 requires four original raw-frame quality observations without invented hardware status.");
            if (row.Calculation is null && row.MeasurementGroup is null && row.Timestamp < evidence.AfterAcquisitionOpen.ObservedAt)
                throw new ArgumentException("Active SR830 row cannot precede its final isolation confirmation.");
        }
    }
}
