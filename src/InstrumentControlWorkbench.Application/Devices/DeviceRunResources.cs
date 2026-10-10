using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

internal sealed record DeviceResumeState(InstrumentId Device, bool OutputEnabled, ParameterId? Target, Quantity? TargetValue);

public sealed record DeviceCleanupFailure(InstrumentId InstrumentId, Exception Error);

// Explicit release requires successful stop confirmation for every touched resource.
// No automatic disposal can turn unknown hardware state into a released lease.
public sealed class DeviceRunResources
{
    private readonly IReadOnlyDictionary<InstrumentId, IDeviceSession> sessions;
    private readonly InstrumentLeaseService leases;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<InstrumentId, Task<Exception?>> pendingStops = [];
    private readonly HashSet<InstrumentId> touched = [];
    private readonly HashSet<InstrumentId> recoveryRequired = [];
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
                token.ThrowIfCancellationRequested();
                touched.Add(session.Descriptor.Id);
                await session.ConnectDeviceAsync(token, Owner);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<Quantity> ReadAsync(InstrumentId id, ParameterId parameter, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            await RequireStoppedTaskFinishedAsync(id);
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            return await Get(id).ReadParameterAsync(parameter, token, Owner);
        }
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
            token.ThrowIfCancellationRequested();
            touched.Add(id);
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
            token.ThrowIfCancellationRequested();
            touched.Add(id);
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
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            var status = await manager.GetDeviceStatusAsync(token, Owner);
            if (!status.Connected) throw new InvalidOperationException($"{id} is disconnected.");
            if (status.SafetyConfirmed) await manager.EnterRunAsync(token, Owner);
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
                if (!touched.Contains(id)) continue;
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
            foreach (var pair in pendingStops)
                if (pair.Value.IsCompleted && await pair.Value is null) recoveryRequired.Remove(pair.Key);
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
            foreach (var id in touched)
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

    public async Task<DeviceStatus> GetStatusAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            await RequireStoppedTaskFinishedAsync(id);
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            return await Get(id).GetDeviceStatusAsync(token, Owner);
        }
        finally { gate.Release(); }
    }

    public async Task DisableSourceOutputAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var session = Get(id) as InstrumentManager ?? throw new NotSupportedException($"{id} has no source-output capability.");
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            if (!pendingStops.ContainsKey(id)) pendingStops.Add(id, StopAndCaptureAsync(session));
            var error = await pendingStops[id].WaitAsync(timeout, token);
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            recoveryRequired.Remove(id);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<InstrumentParameterSnapshot>> CaptureParametersAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var result = new List<InstrumentParameterSnapshot>();
            foreach (var session in sessions.Values)
            {
                var id = session.Descriptor.Id;
                await RequireStoppedTaskFinishedAsync(id);
                token.ThrowIfCancellationRequested();
                touched.Add(id);
                if (session is InstrumentManager source)
                    result.Add(await source.CaptureParametersAsync(token, Owner));
                else
                {
                    var status = await session.GetDeviceStatusAsync(token, Owner);
                    result.Add(new(id, session.Descriptor.Model, status.Timestamp,
                        status.Values.Select(v => new ParameterSnapshot(session.Descriptor.GetParameter(v.Key), v.Value)),
                        new Dictionary<string, string>
                        {
                            ["driver_kind"] = session.Descriptor.DriverKind.ToString(),
                            ["stop_policy"] = session.Descriptor.StopPolicy.ToString(),
                        }));
                }
            }
            return result.AsReadOnly();
        }
        finally { gate.Release(); }
    }

    internal async Task<IReadOnlyList<DeviceResumeState>> CaptureResumeStateAsync(CancellationToken token)
    {
        var result = new List<DeviceResumeState>();
        foreach (var session in sessions.Values)
        {
            var status = await GetStatusAsync(session.Descriptor.Id, token);
            if (session is InstrumentManager)
                result.Add(new(session.Descriptor.Id, status.Connected && !status.SafetyConfirmed, null, null));
            else if (session is AxisControllerSession axis)
                result.Add(new(session.Descriptor.Id, false, axis.Definition.Target.Id, status.Values[axis.Definition.Target.Id]));
            else throw new NotSupportedException($"Pause has no resume policy for {session.Descriptor.Id}.");
        }
        return result.AsReadOnly();
    }

    internal async Task RestoreResumeStateAsync(IReadOnlyList<DeviceResumeState> states, CancellationToken token)
    {
        // Restore held axis targets first; source output stays off until these succeed.
        foreach (var state in states.Where(s => s.Target is not null))
            await WriteAsync(state.Device, state.Target!, state.TargetValue!.Value, token);
        foreach (var state in states.Where(s => s.OutputEnabled)) await EnableSourceOutputAsync(state.Device, token);
    }

    private async Task RequireStoppedTaskFinishedAsync(InstrumentId id)
    {
        Get(id);
        if (recoveryRequired.Contains(id) || pendingStops.TryGetValue(id, out var stop) && (!stop.IsCompleted || await stop is not null))
            throw new InvalidOperationException($"{id} requires stop recovery before observation or mutation.");
    }

    private async Task PrepareMutationAsync(InstrumentId id, bool reconnect = false)
    {
        if (recoveryRequired.Contains(id) && !reconnect)
            throw new InvalidOperationException($"{id} requires fresh stop confirmation after reconnect before it can be changed.");
        if (!pendingStops.TryGetValue(id, out var stop)) return;
        if (!stop.IsCompleted) throw new InvalidOperationException($"{id} has a live stop attempt.");
        if (await stop is not null)
        {
            if (!reconnect) throw new InvalidOperationException($"{id} requires stop recovery before it can be changed.");
            recoveryRequired.Add(id);
        }
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
