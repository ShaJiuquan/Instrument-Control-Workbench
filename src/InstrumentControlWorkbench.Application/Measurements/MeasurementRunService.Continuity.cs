using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task AcquireContinuityRowAsync(ReadElectrodeContinuityStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        var status = ElectrodeContinuityStatus.NotConfigured; var reason = ElectrodeContinuityReason.NotConfigured;
        Core.Instruments.SourceSenseReading? frame = null; Quantity? resistance = null;
        if (step.RequiredRoute is { } route)
        {
            await run.Resources!.DisableSourceOutputAsync(step.Source.Device, run.Token);
            await run.Resources.VerifyMatrixRouteAsync(route.Device, route.Closed, run.Token);
            await run.Resources.ConfigureSourceMeterAsync(step.Source.Device, step.Source.ToSetpoint(), step.Source.Sense, run.Token);
            await run.Resources.EnableSourceOutputAsync(step.Source.Device, run.Token, Core.Instruments.SensingMode.TwoWire);
            await DwellAsync(run, step.Settle);
            await run.Resources.VerifyMatrixRouteAsync(route.Device, route.Closed, run.Token);
            frame = await run.Resources.ReadContinuityAsync(step, run.Token);
            await run.Resources.VerifyMatrixRouteAsync(route.Device, route.Closed, run.Token);
            await run.Resources.DisableSourceOutputAsync(step.Source.Device, CancellationToken.None);
            run.Token.ThrowIfCancellationRequested();
            if (frame is null) { status = ElectrodeContinuityStatus.Open; reason = ElectrodeContinuityReason.Overrange; }
            else if (frame.Sense.Value == 0) { status = ElectrodeContinuityStatus.Open; reason = ElectrodeContinuityReason.ZeroCurrent; }
            else
            {
                var ohms = Math.Abs(frame.Source.Value / frame.Sense.Value);
                if (!double.IsFinite(ohms)) throw new InvalidOperationException("Continuity resistance overflow; no result retained.");
                resistance = new(ohms, PhysicalUnit.Ohm); reason = ElectrodeContinuityReason.Resistance;
                status = ohms <= step.MaximumConnectedResistance.Value ? ElectrodeContinuityStatus.Connected : ElectrodeContinuityStatus.Open;
            }
        }
        var values = new Dictionary<ParameterId, Quantity>(run.Axes) { [step.StatusColumn] = new((int)status, PhysicalUnit.Dimensionless), [step.ReasonColumn] = new((int)reason, PhysicalUnit.Dimensionless), [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second) };
        foreach (var column in step.Columns) run.Blackboard.Remove(column);
        if (frame is not null) { values[step.VoltageColumn] = frame.Source; values[step.CurrentColumn] = frame.Sense; }
        if (resistance is { } result) values[step.ResistanceColumn] = result;
        foreach (var column in step.Columns) if (values.TryGetValue(column, out var value)) run.SetVariable(column, value, step.Id);
        var quality = frame is null ? [] : new[] { step.VoltageColumn, step.CurrentColumn }.Select(column => new AcquisitionQuality(step.Id, step.Source.Device, column, frame.Timestamp, frame.Status, frame.AcquisitionConfiguration)).ToArray();
        await CommitRowAsync(new(DateTimeOffset.UtcNow, values, quality: quality), step.Id, run, started);
    }
}
