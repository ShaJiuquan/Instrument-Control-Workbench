using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed record DeviceCleanupFailure(InstrumentId InstrumentId, Exception Error);

// Explicit release requires successful stop confirmation for every resource.
// No automatic disposal can turn unknown hardware state into a released lease.
public sealed class DeviceRunResources
{
    private readonly IReadOnlyDictionary<InstrumentId, IDeviceSession> sessions;
    private readonly InstrumentLeaseService leases;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<InstrumentId, Task<Exception?>> pendingStops = [];
    private bool released;

    internal DeviceRunResources(IEnumerable<IDeviceSession> sessions, RunId owner, InstrumentLeaseService leases, TimeSpan timeout)
    {
        this.sessions = sessions.ToDictionary(d => d.Descriptor.Id);
        Owner = owner;
        this.leases = leases;
        this.timeout = timeout;
        Devices = Array.AsReadOnly(this.sessions.Values.Select(d => d.Descriptor).ToArray());
    }

    public RunId Owner { get; }
    public IReadOnlyList<DeviceDescriptor> Devices { get; }

    public async Task ConnectAllAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            foreach (var session in sessions.Values)
            {
                await PrepareMutationAsync(session.Descriptor.Id, reconnect: true);
                await session.ConnectDeviceAsync(token, Owner);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<Quantity> ReadAsync(InstrumentId id, ParameterId parameter, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); return await Get(id).ReadParameterAsync(parameter, token, Owner); }
        finally { gate.Release(); }
    }

    public async Task WriteAsync(InstrumentId id, ParameterId parameter, Quantity value, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var session = Get(id);
            session.Descriptor.GetParameter(parameter).ValidateWrite(value);
            await PrepareMutationAsync(id);
            await session.WriteParameterAsync(parameter, value, token, Owner);
        }
        finally { gate.Release(); }
    }

    public async Task ConfigureSourceMeterAsync(InstrumentId id, SourceSetpoint source, SenseConfiguration sense, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var manager = Get(id) as InstrumentManager ?? throw new NotSupportedException($"{id} is not a source meter.");
            Keithley2450Limits.Validate(source);
            Keithley2450Limits.Validate(sense);
            await PrepareMutationAsync(id);
            await manager.ConfigureAsync(source, sense, token, Owner);
        }
        finally { gate.Release(); }
    }

    public async Task EnableSourceOutputAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var manager = Get(id) as InstrumentManager ?? throw new NotSupportedException($"{id} has no source-output capability.");
            await PrepareMutationAsync(id);
            await manager.EnterRunAsync(token, Owner);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<DeviceCleanupFailure>> StopAllAsync(CancellationToken token, bool retryFailed = false)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            foreach (var session in sessions.Values)
            {
                var id = session.Descriptor.Id;
                if (retryFailed && pendingStops.TryGetValue(id, out var previous) && previous.IsCompleted && await previous is not null)
                    pendingStops.Remove(id);
                if (!pendingStops.ContainsKey(id)) pendingStops.Add(id, StopAndCaptureAsync(session));
            }
            // All stop attempts have started before waiting for any one adapter.
            var failures = await Task.WhenAll(pendingStops.Select(async pair =>
            {
                try
                {
                    var error = await pair.Value.WaitAsync(timeout, token);
                    return error is null ? null : new DeviceCleanupFailure(pair.Key, error);
                }
                catch (TimeoutException ex)
                {
                    return new DeviceCleanupFailure(pair.Key, new TimeoutException($"{pair.Key}: stop confirmation timed out; ownership is retained.", ex));
                }
            }));
            return Array.AsReadOnly(failures.OfType<DeviceCleanupFailure>().ToArray());
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            foreach (var id in sessions.Keys)
                if (!pendingStops.TryGetValue(id, out var stop) || !stop.IsCompleted || await stop is not null)
                    throw new InvalidOperationException($"{id} has not confirmed its stop policy; all resources remain reserved.");
            // Once release starts, finish releasing the confirmed-safe group even
            // if the caller cancels; no half-released resource group is exposed.
            foreach (var session in sessions.Values) await session.ReleaseRunAsync(Owner, CancellationToken.None);
            foreach (var id in sessions.Keys) leases.Release(id, Owner.ToString());
            released = true;
        }
        finally { gate.Release(); }
    }

    private async Task PrepareMutationAsync(InstrumentId id, bool reconnect = false)
    {
        if (!pendingStops.TryGetValue(id, out var stop)) return;
        if (!stop.IsCompleted || await stop is not null && !reconnect)
            throw new InvalidOperationException($"{id} requires stop recovery before it can be changed.");
        // Connecting establishes transport only. A completed failed stop may
        // need reconnection before another confirmation can succeed; it never
        // authorizes output enable or overlaps a still-running stop attempt.
        pendingStops.Remove(id);
    }

    private async Task<Exception?> StopAndCaptureAsync(IDeviceSession session)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            var status = await session.StopDeviceAsync(deadline.Token, Owner);
            if (!status.SafetyConfirmed) throw new InvalidOperationException($"{session.Descriptor.Id} did not confirm {session.Descriptor.StopPolicy}.");
            return null;
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            return new TimeoutException($"{session.Descriptor.Id}: stop deadline exceeded; ownership is retained.", ex);
        }
        catch (Exception ex) { return ex; }
    }

    private IDeviceSession Get(InstrumentId id) => sessions.TryGetValue(id, out var session) ? session
        : throw new InvalidOperationException($"This run does not own {id}.");
    private void RequireActive()
    {
        if (released) throw new InvalidOperationException("The run's resources have been released.");
    }
}
