using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    internal async Task<SourceSenseReading> ReadPolarizationAsync(ReadPolarizationStep step, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); var manager = Get(step.Device) as InstrumentManager ?? throw new NotSupportedException("Polarization requires a source meter.");
            await PrepareMutationAsync(step.Device); token.ThrowIfCancellationRequested(); touched.Add(step.Device);
            var before = await manager.CaptureRunSourceStateAsync(Owner, token);
            var sense = step.Sense with { Acquisition = step.Sense.Acquisition ?? before.Sense.Acquisition };
            if (!before.Connected || !before.OutputEnabled || before.Source != step.Setpoint(step.ReadVoltage) || before.Sense != sense)
                throw new InvalidOperationException("Polarization read voltage/compliance/range/sense state changed before acquisition.");
            var frame = await manager.ReadSourceSenseAsync(SourceFunction.Voltage, token, Owner, step.Sense.Mode);
            var after = await manager.CaptureRunSourceStateAsync(Owner, token);
            if (!after.Connected || !after.OutputEnabled || after.Source != before.Source || after.Sense != before.Sense || after.Terminals != before.Terminals ||
                frame.Timestamp < before.LastHeartbeat || frame.Timestamp > after.LastHeartbeat)
                throw new InvalidOperationException("Polarization acquisition configuration or frame timestamp changed.");
            return frame;
        }
        finally { gate.Release(); }
    }
}
