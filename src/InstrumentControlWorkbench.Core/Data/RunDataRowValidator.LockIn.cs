using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed partial class RunDataRowValidator
{
    private void ValidateLockInFrame(MeasurementDataRow row)
    {
        foreach (var step in lockInFrames)
        {
            if (!step.Channels.Any(c => row.Values.ContainsKey(c.Column))) continue;
            if (step.Channels.Any(c => !row.Values.ContainsKey(c.Column))) throw new ArgumentException("Lock-in acquisition requires the complete raw frame and input configuration columns.");
            var observed = step.Channels.ToDictionary(c => c.Address.Parameter, c => row.Values[c.Column]);
            foreach (var value in observed.Where(v => v.Key == Sr830Parameters.InputMode.Id || v.Key == Sr830Parameters.FilterSlope.Id)) Sr830Parameters.ValidateWrite(value.Key, value.Value);
            var settings = new Sr830InputSettings((Sr830InputMode)(int)observed[Sr830Parameters.InputMode.Id].Value, observed[Sr830Parameters.Sensitivity.Id], observed[Sr830Parameters.TimeConstant.Id], (int)observed[Sr830Parameters.FilterSlope.Id].Value);
            if (settings.Input != step.Input) throw new ArgumentException("Archived lock-in input mode differs from its native step.");
        }
    }
}
