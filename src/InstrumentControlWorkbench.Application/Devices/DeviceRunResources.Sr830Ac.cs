using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    private enum AcOpenKind { Sample, Pause, Terminal }
    private sealed record AcOpenResult(MatrixSwitchSnapshot? Snapshot, Exception? Error);
    private readonly Dictionary<InstrumentId, ReadSr830AcFrameStep> acIsolations = new();
    private readonly Dictionary<InstrumentId, Task<AcOpenResult>> acOpenStops = new();
    private readonly Dictionary<InstrumentId, AcOpenKind> acOpenKinds = new();

    // Registration performs no IO and must precede connection, preparation or acquisition.
    internal async Task RegisterSr830AcIsolationsAsync(IEnumerable<ReadSr830AcFrameStep> steps)
    {
        await gate.WaitAsync();
        try
        {
            RequireActive();
            foreach (var step in steps)
            {
                var matrix = Get(step.Isolator) as MatrixSwitchSession ?? throw new ArgumentException("AC isolation requires a matrix session.");
                var capability = matrix.Definition.AcIsolation;
                if (capability is null || capability.Generator != step.Device || !capability.Connections.ToHashSet().SetEquals(step.Connections) || Get(step.Device) is not Sr830MeterSession { SupportsExcitation: true })
                    throw new ArgumentException("Active AC requires registered generator and exact qualified isolation mapping.");
                acIsolations.TryAdd(step.Isolator, step); touched.Add(step.Isolator); touched.Add(step.Device);
                foreach (var guard in GetGuards(matrix)) touched.Add(guard.Descriptor.Id);
            }
        }
        finally { gate.Release(); }
    }

    private async Task RequireAcStopFinishedAsync(InstrumentId id, bool reconnect = false)
    {
        foreach (var step in acIsolations.Values.Where(s => s.Isolator == id || s.Device == id))
            if (acOpenStops.TryGetValue(step.Isolator, out var stop))
            {
                if (!stop.IsCompleted) throw new InvalidOperationException("AC isolation stop is still in flight; observation, reconnect and mutation are denied.");
                if (!reconnect && (await stop).Error is { } error) throw new InvalidOperationException("AC isolation failed; explicit safety recovery is required.", error);
            }
    }

    private async Task PrepareAcMutationAsync(ReadSr830AcFrameStep step)
    {
        if (!acIsolations.TryGetValue(step.Isolator, out var registered) || registered.Device != step.Device || !registered.Connections.ToHashSet().SetEquals(step.Connections))
            throw new InvalidOperationException("AC isolation duty was not registered before IO.");
        await RequireAcStopFinishedAsync(step.Isolator);
        if (acOpenStops.Remove(step.Isolator)) acOpenKinds.Remove(step.Isolator);
        await PrepareMutationAsync(step.Device); await PrepareMutationAsync(step.Isolator);
        touched.Add(step.Device); touched.Add(step.Isolator);
    }

    private async Task StopAcGuardsAsync(MatrixSwitchSession matrix, CancellationToken token)
    {
        var guards = GetGuards(matrix);
        foreach (var guard in guards)
        {
            touched.Add(guard.Descriptor.Id);
            if (pendingStops.TryGetValue(guard.Descriptor.Id, out var old) && old.IsCompleted && await old is null) pendingStops.Remove(guard.Descriptor.Id);
            if (!pendingStops.ContainsKey(guard.Descriptor.Id)) pendingStops.Add(guard.Descriptor.Id, StopAndCaptureAsync(guard));
        }
        var failures = await AwaitStopsAsync(guards.Select(g => g.Descriptor.Id), token);
        if (failures.Count != 0) throw new AggregateException("AC matrix DC guards did not confirm output off.", failures.Select(f => f.Error));
    }

    private async Task<AcOpenResult> OpenAcAndCaptureAsync(ReadSr830AcFrameStep step)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            var snapshot = await ((MatrixSwitchSession)Get(step.Isolator)).SetAcConnectionsAsync(step.Device, step.Connections, false, Owner, deadline.Token);
            return new(snapshot, null);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        { return new(null, new TimeoutException($"{step.Isolator}: AC isolation stop deadline exceeded; ownership is retained.", error)); }
        catch (Exception error) { return new(null, error); }
    }

    private Task<AcOpenResult> BeginAcOpen(ReadSr830AcFrameStep step, AcOpenKind kind)
    {
        if (acOpenStops.TryGetValue(step.Isolator, out var task)) return task;
        acOpenKinds[step.Isolator] = kind;
        task = OpenAcAndCaptureAsync(step); acOpenStops.Add(step.Isolator, task);
        return task;
    }

    private async Task<MatrixSwitchSnapshot> ConfirmAcOpenCoreAsync(ReadSr830AcFrameStep step, AcOpenKind kind, CancellationToken token)
    {
        var result = await BeginAcOpen(step, kind).WaitAsync(timeout, token);
        if (result.Error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error).Throw();
        // A late successful sample/pause task is drained, but cannot become terminal proof.
        // Report cleanup failure so finalization enters explicit recovery instead of failing at release.
        if (acOpenKinds[step.Isolator] != kind)
            throw new InvalidOperationException($"{step.Isolator}: the prior AC isolation task finished, but fresh {kind} isolation proof is still required; ownership is retained.");
        return result.Snapshot ?? throw new InvalidOperationException("AC isolation did not provide fresh route evidence.");
    }

    internal async Task<MatrixSwitchSnapshot> PrepareSr830AcAsync(ReadSr830AcFrameStep step, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await PrepareAcMutationAsync(step);
            await StopAcGuardsAsync((MatrixSwitchSession)Get(step.Isolator), token);
            var open = await ConfirmAcOpenCoreAsync(step, AcOpenKind.Sample, token);
            await ((Sr830MeterSession)Get(step.Device)).ConfigureExcitationAsync(step.Excitation, Owner, token);
            await ConfirmAcSettingsAsync(step, token); return open;
        }
        finally { gate.Release(); }
    }

    private async Task ConfirmAcSettingsAsync(ReadSr830AcFrameStep step, CancellationToken token)
    {
        var state = await ((Sr830MeterSession)Get(step.Device)).CaptureResumeSettingsAsync(Owner, token);
        if (state.Reference != step.Excitation.ExpectedReference || state.Input.Input != step.Input)
            throw new InvalidOperationException("AC excitation or input differs from the frozen acquisition.");
    }

    internal async Task ConnectSr830AcAsync(ReadSr830AcFrameStep step, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await RequireAcStopFinishedAsync(step.Isolator); await ConfirmAcSettingsAsync(step, token);
            var matrix = (MatrixSwitchSession)Get(step.Isolator);
            await matrix.ConfirmAcConnectionsAsync(step.Device, step.Connections, false, Owner, token);
            await PrepareAcMutationAsync(step); // Connection consumes every prior open proof.
            await matrix.SetAcConnectionsAsync(step.Device, step.Connections, true, Owner, token);
            await ConfirmAcSettingsAsync(step, token);
        }
        finally { gate.Release(); }
    }

    internal async Task<(Sr830FrameObservation Frame, MatrixSwitchSnapshot Before, MatrixSwitchSnapshot After)> ReadSr830AcFrameAsync(ReadSr830AcFrameStep step, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await RequireAcStopFinishedAsync(step.Isolator); await ConfirmAcSettingsAsync(step, token);
            var matrix = (MatrixSwitchSession)Get(step.Isolator);
            var before = await matrix.ConfirmAcConnectionsAsync(step.Device, step.Connections, true, Owner, token);
            var frame = await ((Sr830MeterSession)Get(step.Device)).ReadFrameAsync(step.Input, token, Owner, includeReference: true);
            if (frame.Reference != step.Excitation.ExpectedReference) throw new InvalidOperationException("AC excitation drifted; discard frame.");
            var after = await matrix.ConfirmAcConnectionsAsync(step.Device, step.Connections, true, Owner, token);
            await ConfirmAcSettingsAsync(step, token);
            return (frame, before, after);
        }
        finally { gate.Release(); }
    }

    internal async Task<MatrixSwitchSnapshot> ConfirmSr830AcOpenAsync(ReadSr830AcFrameStep step, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); return await ConfirmAcOpenCoreAsync(step, AcOpenKind.Sample, token); }
        finally { gate.Release(); }
    }

    // Caller owns gate. Each retained task owns its result; late completion cannot race a shared snapshot map.
    private async Task<IReadOnlyList<DeviceCleanupFailure>> StopAcIsolationsAsync(bool retryFailed, bool pause, CancellationToken token)
    {
        var kind = pause ? AcOpenKind.Pause : AcOpenKind.Terminal;
        foreach (var step in acIsolations.Values)
        {
            if (acOpenStops.TryGetValue(step.Isolator, out var old) && old.IsCompleted)
            {
                var result = await old;
                // Recovery needs a new terminal observation even when AC succeeded but another device failed.
                if (retryFailed || result.Error is null && acOpenKinds[step.Isolator] != kind)
                { acOpenStops.Remove(step.Isolator); acOpenKinds.Remove(step.Isolator); }
            }
            _ = BeginAcOpen(step, kind);
        }
        var failures = await Task.WhenAll(acIsolations.Values.Select(async step =>
        {
            try
            {
                _ = await ConfirmAcOpenCoreAsync(step, kind, token);
                return (DeviceCleanupFailure?)null;
            }
            catch (Exception error) { return new DeviceCleanupFailure(step.Isolator, error); }
        }));
        return Array.AsReadOnly(failures.OfType<DeviceCleanupFailure>().ToArray());
    }

    private async Task<bool> HasTerminalAcProofAsync()
    {
        foreach (var id in acIsolations.Keys)
            if (!acOpenStops.TryGetValue(id, out var task) || !task.IsCompleted || acOpenKinds[id] != AcOpenKind.Terminal || (await task).Error is not null)
                return false;
        return true;
    }
}
