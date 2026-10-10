using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Devices;

internal sealed class InstrumentOperationGate(InstrumentId instrumentId)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RunId? runOwner;

    public async Task ClaimAsync(RunId owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await gate.WaitAsync(token);
        try
        {
            if (runOwner is not null) throw new InvalidOperationException($"{instrumentId} is owned by {runOwner}.");
            runOwner = owner;
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(RunId owner, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (runOwner != owner) throw new InvalidOperationException($"{owner} does not own {instrumentId}.");
            runOwner = null;
        }
        finally { gate.Release(); }
    }

    public async Task EnterAsync(CancellationToken token, RunId? owner)
    {
        await gate.WaitAsync(token);
        if (runOwner != owner)
        {
            gate.Release();
            throw new InvalidOperationException($"{instrumentId} is reserved by another operation.");
        }
    }

    public void Exit() => gate.Release();
}
