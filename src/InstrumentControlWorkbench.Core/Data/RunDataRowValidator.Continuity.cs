using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed partial class RunDataRowValidator
{
    private void ValidateContinuity(MeasurementDataRow row)
    {
        var observations = continuityReads.Values.Where(read => read.Columns.Any(row.Values.ContainsKey)).ToArray();
        if (observations.Length > 1) throw new ArgumentException("A continuity row identifies exactly one channel/pair observation.");
        foreach (var read in observations)
        {
            if (row.MeasurementGroup is not null || row.Calculation is not null || row.Analysis is not null || row.Routing is not null || row.Environment.Count != 0 || row.IndependentResistance.Count != 0 || row.AcquisitionFailures.Count != 0)
                throw new ArgumentException("Continuity diagnostics must preserve a standalone pair observation.");
            if (!row.Values.TryGetValue(read.StatusColumn, out var status) || !row.Values.TryGetValue(read.ReasonColumn, out var reason) || status.Unit != PhysicalUnit.Dimensionless || reason.Unit != PhysicalUnit.Dimensionless || status.Value != Math.Truncate(status.Value) || reason.Value != Math.Truncate(reason.Value)) throw new ArgumentException("Continuity needs explicit integral status and reason.");
            var s = (ElectrodeContinuityStatus)(int)status.Value; var why = (ElectrodeContinuityReason)(int)reason.Value;
            if (!Enum.IsDefined(s) || !Enum.IsDefined(why)) throw new ArgumentException("Unknown continuity outcome.");
            var haveV = row.Values.TryGetValue(read.VoltageColumn, out var voltage); var haveI = row.Values.TryGetValue(read.CurrentColumn, out var current); var haveR = row.Values.TryGetValue(read.ResistanceColumn, out var resistance);
            if (why is ElectrodeContinuityReason.NotConfigured or ElectrodeContinuityReason.Overrange)
            {
                if (haveV || haveI || haveR || row.Quality.Count != 0 || (why == ElectrodeContinuityReason.NotConfigured ? s != ElectrodeContinuityStatus.NotConfigured || read.RequiredRoute is not null : s != ElectrodeContinuityStatus.Open || read.RequiredRoute is null)) throw new ArgumentException("Unmeasured pair cannot claim electrical values or successful quality.");
            }
            else
            {
                if (read.RequiredRoute is null || !haveV || !haveI || row.Quality.Count != 2 || row.Quality.Any(q => q.Step != read.Id || q.Device != read.Source.Device || q.MeterConfiguration is not null || q.PicoammeterConfiguration is not null || q.ElectrometerConfiguration is not null) || row.Quality[0].CapturedAt != row.Quality[1].CapturedAt || row.Quality[0].Status != row.Quality[1].Status || row.Quality[0].AcquisitionConfiguration != row.Quality[1].AcquisitionConfiguration) throw new ArgumentException("Measured continuity requires its original paired quality evidence.");
                if (why == ElectrodeContinuityReason.ZeroCurrent)
                { if (current.Value != 0 || haveR || s != ElectrodeContinuityStatus.Open) throw new ArgumentException("Zero-current pair is open without fabricated resistance."); }
                else
                {
                    var expected = Math.Abs(voltage.Value / current.Value);
                    if (current.Value == 0 || !double.IsFinite(expected) || !haveR || resistance.Unit != PhysicalUnit.Ohm || resistance.Value != expected || s != (expected <= read.MaximumConnectedResistance.Value ? ElectrodeContinuityStatus.Connected : ElectrodeContinuityStatus.Open)) throw new ArgumentException("Continuity threshold or V/I outcome differs from its observation.");
                }
            }
        }
    }
}
