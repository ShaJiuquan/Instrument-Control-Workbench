using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    internal async Task<SourceSenseReading?> ReadContinuityAsync(ReadElectrodeContinuityStep step, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); var manager = Get(step.Source.Device) as InstrumentManager ?? throw new NotSupportedException("Continuity requires a source meter.");
            await PrepareMutationAsync(step.Source.Device); token.ThrowIfCancellationRequested(); touched.Add(step.Source.Device);
            var before = await manager.CaptureRunSourceStateAsync(Owner, token);
            var sense = step.Source.Sense with { Acquisition = step.Source.Sense.Acquisition ?? before.Sense.Acquisition };
            if (!before.Connected || !before.OutputEnabled || before.Source != step.Source.ToSetpoint() || before.Sense != sense)
                throw new InvalidOperationException("Continuity excitation/sense configuration changed before acquisition.");
            SourceSenseReading? frame;
            try { frame = await manager.ReadSourceSenseAsync(SourceFunction.Voltage, token, Owner, SensingMode.TwoWire); }
            catch (InstrumentReadException error) when (error.Device == step.Source.Device && error.Kind == InstrumentReadFailureKind.Overrange)
            { frame = null; }
            var after = await manager.CaptureRunSourceStateAsync(Owner, token);
            if (!after.Connected || !after.OutputEnabled || after.Source != before.Source || after.Sense != before.Sense || after.Terminals != before.Terminals ||
                frame is not null && (frame.Timestamp < before.LastHeartbeat || frame.Timestamp > after.LastHeartbeat))
                throw new InvalidOperationException("Continuity configuration or acquisition timestamp changed.");
            return frame;
        }
        finally { gate.Release(); }
    }
}
