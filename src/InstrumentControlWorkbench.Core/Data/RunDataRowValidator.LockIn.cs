using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed partial class RunDataRowValidator
{
    private static bool ValidLockInValue(ReadSr830FrameStep step, ParameterId variable, BlackboardEntry entry)
    {
        var channel = step.Channels.SingleOrDefault(c => c.Column == variable);
        if (channel is null || entry.Value is not BlackboardQuantity number) return false;
        var parameter = Sr830Parameters.WithReference.Single(p => p.Id == channel.Address.Parameter);
        try
        {
            parameter.Validate(number.Value);
            if (parameter.Access.HasFlag(ParameterAccess.Write)) Sr830Parameters.ValidateWrite(parameter.Id, number.Value);
            if (parameter.Id == Sr830Parameters.InputMode.Id && number.Value.Value != (int)step.Input) return false;
            if (parameter.Id == Sr830Parameters.Sensitivity.Id && step.Input == Sr830InputMode.Current100M && number.Value.Value > .01) return false;
            if ((parameter.Id == Sr830ReferenceParameters.Source.Id || parameter.Id == Sr830ReferenceParameters.Harmonic.Id) && number.Value.Value != Math.Truncate(number.Value.Value)) return false;
            return true;
        }
        catch (ArgumentException) { return false; }
    }
    private void ValidateLockInFrame(MeasurementDataRow row)
    {
        foreach (var step in lockInFrames)
        {
            if (!step.Channels.Any(c => row.Values.ContainsKey(c.Column))) continue;
            if (step.Channels.Any(c => !row.Values.ContainsKey(c.Column))) throw new ArgumentException("Lock-in acquisition requires the complete raw frame and input configuration columns.");
            var observed = step.Channels.ToDictionary(c => c.Address.Parameter, c => row.Values[c.Column]);
            foreach (var value in observed.Where(v => v.Key == Sr830Parameters.InputMode.Id || v.Key == Sr830Parameters.FilterSlope.Id)) Sr830Parameters.ValidateWrite(value.Key, value.Value);
            var settings = new Sr830InputSettings((Sr830InputMode)(int)observed[Sr830Parameters.InputMode.Id].Value, observed[Sr830Parameters.Sensitivity.Id], observed[Sr830Parameters.TimeConstant.Id], (int)observed[Sr830Parameters.FilterSlope.Id].Value);
            if (step.IncludeReference)
            {
                var source = observed[Sr830ReferenceParameters.Source.Id]; var harmonic = observed[Sr830ReferenceParameters.Harmonic.Id];
                if (source.Unit != PhysicalUnit.Dimensionless || source.Value != Math.Truncate(source.Value) || harmonic.Unit != PhysicalUnit.Dimensionless || harmonic.Value != Math.Truncate(harmonic.Value)) throw new ArgumentException("Reference source/harmonic require integral observed codes.");
                _ = new Sr830ReferenceReadback((Sr830ReferenceSource)(int)source.Value, observed[Sr830ReferenceParameters.Frequency.Id], observed[Sr830ReferenceParameters.SineAmplitude.Id], (int)harmonic.Value);
            }
            if (settings.Input != step.Input) throw new ArgumentException("Archived lock-in input mode differs from its native step.");
        }
    }
}
