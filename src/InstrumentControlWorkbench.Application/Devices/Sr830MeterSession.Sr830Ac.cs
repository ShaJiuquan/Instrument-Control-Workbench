using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class Sr830MeterSession
{
    public bool SupportsExcitation => SupportsReference && port is ISr830ExcitationPort;

    // Resource orchestration must confirm the registered sample paths open before this call.
    internal async Task<Sr830ReferenceReadback> ConfigureExcitationAsync(Sr830ExcitationSettings settings, RunId owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!SupportsExcitation) throw new NotSupportedException("SR830 profile does not declare active excitation with reference readback.");
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token); RequireIdle(before);
            if (before.Reference is null) throw new InvalidOperationException("Active SR830 configuration requires fresh reference readback.");
            await ((ISr830ExcitationPort)port).ConfigureExcitationAsync(settings, token);
            token.ThrowIfCancellationRequested();
            var after = await port.GetSnapshotAsync(token); RequireIdle(after);
            if (after.Reference != settings.ExpectedReference || after.Settings != before.Settings)
                throw new InvalidOperationException("SR830 excitation or input settings did not confirm after configuration.");
            await PublishAsync(after, token);
            return after.Reference!;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
}
