using System.Collections.Concurrent;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

internal sealed record DeviceResumeState(InstrumentId Device, bool OutputEnabled, ParameterId? Target, Quantity? TargetValue, SourceMeterSnapshot? SourceState = null, TemperatureControlConfiguration? TemperatureState = null, Keithley2182Settings? MeterState = null, Core.Instruments.Ports.K6221CurrentSourceSnapshot? CurrentSourceState = null, Quantity? TemperatureRate = null, Quantity? TemperatureSoftwareTarget = null, Quantity? TemperatureSoftwareRate = null, Sr830ResumeConfiguration? LockInState = null, bool Homing = false, Keithley6485Settings? PicoammeterState = null, Keithley6514Settings? ElectrometerState = null);

public sealed record DeviceCleanupFailure(InstrumentId InstrumentId, Exception Error);

// Explicit release requires successful stop confirmation for every touched resource.
// No automatic disposal can turn unknown hardware state into a released lease.
public sealed partial class DeviceRunResources
{
    private readonly IReadOnlyDictionary<InstrumentId, IDeviceSession> sessions;
    private readonly InstrumentLeaseService leases;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<InstrumentId, Task<Exception?>> pendingStops = [];
    private readonly HashSet<InstrumentId> touched = [];
    private readonly HashSet<InstrumentId> activeHomes = [];
    private readonly HashSet<InstrumentId> heldRoutes = [];
    private readonly ConcurrentDictionary<InstrumentId, Quantity> heldTemperatureTargets = new();
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
            // Never reconnect any part of the group while an isolation task still owns IO.
            foreach (var session in sessions.Values) await RequireAcStopFinishedAsync(session.Descriptor.Id, reconnect: true);
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
        => (await ReadObservationAsync(id, parameter, token)).Value;

    public async Task<ParameterObservation> ReadObservationAsync(InstrumentId id, ParameterId parameter, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            // A voltage acquisition can restart the meter trigger model. Its
            // earlier idle confirmation cannot authorize release after this IO.
            if (Get(id) is Keithley2182MeterSession && parameter == Keithley2182Parameters.Voltage.Id) await PrepareMutationAsync(id);
            else if (Get(id) is Keithley6514MeterSession && Keithley6514Parameters.ReadingFunction(parameter) is not null) await PrepareMutationAsync(id);
            else if (Get(id) is Keithley6485MeterSession && parameter == Keithley6485Parameters.Current.Id) await PrepareMutationAsync(id);
            else await RequireStoppedTaskFinishedAsync(id);
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            return await Get(id).ReadObservationAsync(parameter, token, Owner);
        }
        finally { gate.Release(); }
    }

    public async Task<Core.Instruments.Ports.Sr830FrameObservation> ReadSr830FrameAsync(InstrumentId id, Sr830InputMode input, CancellationToken token, bool includeReference = false)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var session = Get(id) as Sr830MeterSession ?? throw new NotSupportedException($"{id} is not a passive SR830 meter.");
            await PrepareMutationAsync(id); token.ThrowIfCancellationRequested(); touched.Add(id);
            return await session.ReadFrameAsync(input, token, Owner, includeReference);
        }
        finally { gate.Release(); }
    }

    public async Task<SourceSenseReading> ReadSourceSenseAsync(InstrumentId id, SourceFunction function, CancellationToken token, SensingMode? requiredSensingMode = null)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var manager = Get(id) as InstrumentManager ?? throw new NotSupportedException($"{id} is not a source meter.");
            // Paired acquisition can configure sensing and observes enabled output;
            // a previous stop confirmation cannot be reused after this operation.
            await PrepareMutationAsync(id);
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            return await manager.ReadSourceSenseAsync(function, token, Owner, requiredSensingMode);
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
            Keithley2450Limits.ValidateConfiguration(source, sense);
            await PrepareMutationAsync(id);
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            await manager.ConfigureAsync(source, sense, token, Owner);
        }
        finally { gate.Release(); }
    }

    internal async Task<SoftwareTemperatureRampState> BeginSoftwareTemperatureRampAsync(InstrumentId id, Quantity target, bool enableControl, Quantity? softwareRate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            await PrepareMutationAsync(id); touched.Add(id);
            return await axis.BeginSoftwareTemperatureRampAsync(target, enableControl, softwareRate, Owner, token);
        }
        finally { gate.Release(); }
    }
    internal async Task SetHardwareTemperatureTargetAsync(InstrumentId id, Quantity target, Quantity rate, bool enableControl, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            await PrepareMutationAsync(id); token.ThrowIfCancellationRequested(); touched.Add(id);
            await axis.SetHardwareTemperatureTargetAsync(target, rate, enableControl, Owner, token);
        }
        finally { gate.Release(); }
    }
    internal async Task ConfigureTemperatureStabilityAsync(InstrumentId id, TemperatureStabilityChange change, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await RequireStoppedTaskFinishedAsync(id);
            var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            await axis.ConfigureTemperatureStabilityAsync(change, token, Owner);
        }
        finally { gate.Release(); }
    }
    internal async Task ResetTemperatureEvidenceAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await RequireStoppedTaskFinishedAsync(id);
            var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            await axis.ResetTemperatureEvidenceAsync(Owner, token);
        }
        finally { gate.Release(); }
    }
    internal async Task StartRotationHomeAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException("Device is not an axis controller.");
            await PrepareMutationAsync(id); token.ThrowIfCancellationRequested(); touched.Add(id); activeHomes.Add(id);
            await axis.StartHomingAsync(Owner, token);
        }
        finally { gate.Release(); }
    }
    internal async Task CompleteRotationHomeAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); token.ThrowIfCancellationRequested(); activeHomes.Remove(id); }
        finally { gate.Release(); }
    }
    internal async Task<AxisControllerSnapshot> ReadAxisSnapshotAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await RequireStoppedTaskFinishedAsync(id); token.ThrowIfCancellationRequested(); touched.Add(id);
            var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            return await axis.ReadAxisSnapshotAsync(Owner, token);
        }
        finally { gate.Release(); }
    }
    internal async Task<TemperatureStabilityObservation> ReadTemperatureStabilityAsync(InstrumentId id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await RequireStoppedTaskFinishedAsync(id); token.ThrowIfCancellationRequested(); touched.Add(id);
            var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            return await axis.ReadTemperatureStabilityAsync(token, Owner);
        }
        finally { gate.Release(); }
    }
    internal async Task ConfigureTemperatureAsync(InstrumentId id, TemperatureControlAction action, Quantity? rate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            await PrepareMutationAsync(id); token.ThrowIfCancellationRequested(); touched.Add(id);
            await axis.ConfigureTemperatureAsync(action, rate, token, Owner);
        }
        finally { gate.Release(); }
    }
    internal async Task WriteSoftwareTemperatureTargetAsync(InstrumentId id, Quantity target, SoftwareTemperatureRampState state, bool final, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var axis = Get(id) as AxisControllerSession ?? throw new NotSupportedException($"{id} is not an axis controller.");
            await PrepareMutationAsync(id); touched.Add(id);
            await axis.WriteSoftwareTemperatureTargetAsync(target, state, final, Owner, token);
        }
        finally { gate.Release(); }
    }

    public async Task EnableSourceOutputAsync(InstrumentId id, CancellationToken token, SensingMode? requiredSensingMode = null, SourceMeterSnapshot? expectedResumeState = null, Core.Instruments.Ports.K6221CurrentSourceSnapshot? expectedCurrentState = null)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var session = Get(id);
            if (session is not (InstrumentManager or Keithley6221CurrentSourceSession)) throw new NotSupportedException($"{id} has no source-output capability.");
            await PrepareMutationAsync(id);
            foreach (var matrix in sessions.Values.OfType<MatrixSwitchSession>().Where(m => m.Definition.GuardedSources.Contains(id)))
            {
                if (matrix.Definition.AcIsolation is not null) throw new InvalidOperationException("Dedicated AC isolation cannot guard generic DC output enable; combined DC and AC operation is unsupported.");
                await PrepareMutationAsync(matrix.Descriptor.Id);
                touched.Add(matrix.Descriptor.Id);
                await matrix.ConfirmHeldRouteAsync(Owner, token);
            }
            token.ThrowIfCancellationRequested();
            touched.Add(id);
            if (session is Keithley6221CurrentSourceSession current)
            {
                if (requiredSensingMode is not null || expectedResumeState is not null) throw new ArgumentException("Pure current source has no sensing mode or SMU resume state.");
                await current.EnableOutputAsync(Owner, token, expectedCurrentState); return;
            }
            var manager = (InstrumentManager)session;
            if (expectedCurrentState is not null) throw new ArgumentException("SMU resume cannot use a pure current-source snapshot.");
            var status = await manager.GetDeviceStatusAsync(token, Owner);
            if (!status.Connected) throw new InvalidOperationException($"{id} is disconnected.");
            if (status.SafetyConfirmed || expectedResumeState is not null) await manager.EnterRunAsync(token, Owner, requiredSensingMode, expectedResumeState);
            else if (requiredSensingMode is { } mode && (await manager.CaptureRunSourceStateAsync(Owner, token)).Sense.Mode != mode)
                throw new InvalidOperationException("Enabled source sensing mode differs from the required preparation.");
        }
        finally { gate.Release(); }
    }

    public async Task VerifyMatrixRouteAsync(InstrumentId id, IReadOnlyList<MatrixCrossPoint> desired, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (desired.Count > 4096) throw new ArgumentOutOfRangeException(nameof(desired), "Matrix connection count exceeds the software budget.");
        token.ThrowIfCancellationRequested();
        var points = desired.ToArray();
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var matrix = Get(id) as MatrixSwitchSession ?? throw new NotSupportedException($"{id} has no matrix capability.");
            if (matrix.Definition.AcIsolation is not null) throw new InvalidOperationException("Registered AC isolation forbids generic matrix routing.");
            matrix.Definition.Validate(points);
            if (points.Length > 4096 || points.Distinct().Count() != points.Length) throw new ArgumentException("Expected connections must be unique and bounded.", nameof(desired));
            await PrepareMutationAsync(id); token.ThrowIfCancellationRequested(); touched.Add(id);
            await matrix.ConfirmHeldRouteAsync(Owner, token, points);
        }
        finally { gate.Release(); }
    }

    public async Task RouteMatrixAsync(InstrumentId id, IReadOnlyList<MatrixCrossPoint> desired, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (desired.Count > 4096) throw new ArgumentOutOfRangeException(nameof(desired), "Matrix connection count exceeds the software budget.");
        token.ThrowIfCancellationRequested();
        var points = desired.ToArray();
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var matrix = Get(id) as MatrixSwitchSession ?? throw new NotSupportedException($"{id} has no matrix capability.");
            if (matrix.Definition.AcIsolation is not null) throw new InvalidOperationException("Registered AC isolation forbids generic matrix routing.");
            matrix.Definition.Validate(points);
            if (points.Length > 4096 || points.Distinct().Count() != points.Length) throw new ArgumentException("Desired matrix connections must be unique and within the software budget.", nameof(desired));
            var guards = GetGuards(matrix);
            await PrepareMutationAsync(id);
            foreach (var guard in guards)
            {
                await PrepareMutationAsync(guard.Descriptor.Id);
                token.ThrowIfCancellationRequested();
                touched.Add(guard.Descriptor.Id);
                pendingStops.Add(guard.Descriptor.Id, StopAndCaptureAsync(guard));
            }
            var failures = await AwaitStopsAsync(guards.Select(g => g.Descriptor.Id), token);
            if (failures.Count != 0) throw new AggregateException("Excitation-off confirmation failed; no matrix relays were changed.", failures.Select(f => f.Error));
            token.ThrowIfCancellationRequested(); touched.Add(id);
            await matrix.ReconcileAsync(points, Owner, token);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<DeviceCleanupFailure>> StopAllAsync(CancellationToken token, bool retryFailed = false, bool preserveRouting = false)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            foreach (var pair in pendingStops.ToArray())
            {
                if (!pair.Value.IsCompleted) continue;
                if ((heldRoutes.Contains(pair.Key) || heldTemperatureScans.Contains(pair.Key)) && !preserveRouting || retryFailed && await pair.Value is not null)
                { pendingStops.Remove(pair.Key); heldRoutes.Remove(pair.Key); heldTemperatureScans.Remove(pair.Key); }
            }
            var matrices = sessions.Values.OfType<MatrixSwitchSession>().Where(m => touched.Contains(m.Descriptor.Id)).ToArray();
            // A new relay cleanup needs fresh excitation-off proof. Never replace a
            // live or failed source stop; recovery alone may retry failed attempts.
            foreach (var matrix in matrices.Where(m => m.HasRoutingMutations && !pendingStops.ContainsKey(m.Descriptor.Id)))
                foreach (var guard in GetGuards(matrix))
                {
                    touched.Add(guard.Descriptor.Id);
                    if (pendingStops.TryGetValue(guard.Descriptor.Id, out var old) && old.IsCompleted && await old is null)
                        pendingStops.Remove(guard.Descriptor.Id);
                }
            foreach (var session in sessions.Values.Where(s => s is not MatrixSwitchSession && touched.Contains(s.Descriptor.Id)))
                if (!pendingStops.ContainsKey(session.Descriptor.Id)) pendingStops.Add(session.Descriptor.Id, StopAndCaptureAsync(session, preserveRouting));
            // All non-matrix stops begin together; only confirmed excitation-off
            // permits a later matrix relay write. Axis failure retains ownership
            // but does not prevent relay cleanup when excitation is confirmed off.
            // AC emergency opening is independent of passive abort and DC-guard failures.
            var acFailures = await StopAcIsolationsAsync(retryFailed, preserveRouting, token);
            var failures = new List<DeviceCleanupFailure>(acFailures);
            failures.AddRange(await AwaitStopsAsync(pendingStops.Keys.Where(id => Get(id) is not MatrixSwitchSession).ToArray(), token));
            if (preserveRouting) foreach (var id in temperatureScanRequiresRampOff.Where(id => pendingStops.TryGetValue(id, out var stop) && stop.IsCompleted))
                    if (await pendingStops[id] is null) heldTemperatureScans.Add(id);
            foreach (var matrix in matrices)
            {
                var id = matrix.Descriptor.Id;
                if (acFailures.Any(f => f.InstrumentId == id))
                {
                    recoveryRequired.Add(id);
                    continue; // Failed or live isolation must not overlap generic matrix cleanup.
                }
                if (pendingStops.ContainsKey(id)) continue;
                if (matrix.HasRoutingMutations && GetGuards(matrix).Any(g => failures.Any(f => f.InstrumentId == g.Descriptor.Id)))
                {
                    recoveryRequired.Add(id);
                    failures.Add(new(id, new InvalidOperationException($"{id}: matrix cleanup deferred until excitation-off is confirmed; ownership is retained.")));
                    continue;
                }
                if (preserveRouting) heldRoutes.Add(id);
                pendingStops.Add(id, StopAndCaptureAsync(matrix, preserveRouting));
            }
            failures.AddRange(await AwaitStopsAsync(pendingStops.Keys.Where(id => Get(id) is MatrixSwitchSession).ToArray(), token));
            foreach (var pair in pendingStops)
                if (pair.Value.IsCompleted && await pair.Value is null && !acFailures.Any(f => f.InstrumentId == pair.Key)) recoveryRequired.Remove(pair.Key);
            return failures.AsReadOnly();
        }
        finally { gate.Release(); }
    }

    private IDeviceSession[] GetGuards(MatrixSwitchSession matrix)
    {
        var guards = matrix.Definition.GuardedSources.Select(Get).ToArray();
        if (guards.Any(g => g.Descriptor.StopPolicy != DeviceStopPolicy.OutputOff)) throw new InvalidOperationException("Matrix excitation guards must confirm output-off.");
        return guards;
    }

    private async Task<IReadOnlyList<DeviceCleanupFailure>> AwaitStopsAsync(IEnumerable<InstrumentId> ids, CancellationToken token)
    {
        var failures = await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                var error = await pendingStops[id].WaitAsync(timeout, token);
                return error is null ? null : new DeviceCleanupFailure(id, error);
            }
            catch (TimeoutException ex)
            {
                return new DeviceCleanupFailure(id, new TimeoutException($"{id}: stop confirmation timed out; ownership is retained.", ex));
            }
        }));
        return Array.AsReadOnly(failures.OfType<DeviceCleanupFailure>().ToArray());
    }

    public async Task ReleaseAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            if (!await HasTerminalAcProofAsync()) throw new InvalidOperationException("Fresh terminal AC isolation proof is required; all resources remain reserved.");
            foreach (var id in touched)
                if (heldRoutes.Contains(id) || recoveryRequired.Contains(id) || !pendingStops.TryGetValue(id, out var stop) || !stop.IsCompleted || await stop is not null)
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
            var session = Get(id);
            if (session is not (InstrumentManager or Keithley6221CurrentSourceSession)) throw new NotSupportedException($"{id} has no source-output capability.");
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
                else if (session is MatrixSwitchSession matrix)
                    result.Add(await matrix.CaptureParametersAsync(Owner, token));
                else
                {
                    var status = await session.GetDeviceStatusAsync(token, Owner);
                    if (session is Keithley2182MeterSession or Keithley6221CurrentSourceSession && !status.Connected) throw new InvalidOperationException($"{id}: disconnected device cannot provide run settings.");
                    var configuration = new Dictionary<string, string>
                    {
                        ["display_name"] = session.Descriptor.DisplayName,
                        ["driver_kind"] = session.Descriptor.DriverKind.ToString(),
                        ["stop_policy"] = session.Descriptor.StopPolicy.ToString(),
                    };
                    if (session is AxisControllerSession axis && axis.Definition.SupportsSoftwareTemperatureRamp)
                    {
                        var temperature = await axis.CaptureTemperatureConfigurationAsync(Owner, token);
                        configuration["hardware_ramp_enabled"] = temperature.HardwareRampEnabled.ToString();
                        configuration["temperature_control_enabled"] = temperature.ControlEnabled.ToString();
                        var stability = await axis.ConfigureTemperatureStabilityAsync(new(), token, Owner);
                        configuration["temperature_tolerance_k"] = stability.Tolerance.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        configuration["temperature_settling_threshold_k"] = stability.SettlingThreshold.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        configuration["temperature_stable_duration_s"] = stability.StableFor.TotalSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    result.Add(new(id, session.Descriptor.Model, status.Timestamp,
                        status.Values.Select(v => new ParameterSnapshot(session.Descriptor.GetParameter(v.Key), v.Value)), configuration));
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
            if (session is InstrumentManager manager)
            {
                var snapshot = await manager.CaptureRunSourceStateAsync(Owner, token);
                result.Add(new(session.Descriptor.Id, snapshot.Connected && snapshot.OutputEnabled, null, null, snapshot));
            }
            else if (session is Keithley2182MeterSession meter)
                result.Add(new(session.Descriptor.Id, false, null, null, MeterState: await meter.CaptureResumeSettingsAsync(Owner, token)));
            else if (session is Keithley6514MeterSession electrometer)
                result.Add(new(session.Descriptor.Id, false, null, null, ElectrometerState: await electrometer.CaptureResumeSettingsAsync(Owner, token)));
            else if (session is Keithley6485MeterSession picoammeter)
                result.Add(new(session.Descriptor.Id, false, null, null, PicoammeterState: await picoammeter.CaptureResumeSettingsAsync(Owner, token)));
            else if (session is Sr830MeterSession lockIn)
                result.Add(new(session.Descriptor.Id, false, null, null, LockInState: await lockIn.CaptureResumeSettingsAsync(Owner, token)));
            else if (session is Keithley6221CurrentSourceSession current)
            {
                var snapshot = await current.CaptureSourceStateAsync(Owner, token);
                result.Add(new(session.Descriptor.Id, snapshot.OutputEnabled, null, null, CurrentSourceState: snapshot));
            }
            else if (session is AxisControllerSession axis)
                result.Add(new(session.Descriptor.Id, false, axis.Definition.Target.Id, status.Values[axis.Definition.Target.Id], TemperatureState: axis.Definition.SupportsSoftwareTemperatureRamp ? await axis.CaptureTemperatureConfigurationAsync(Owner, token) : null,
                    TemperatureRate: axis.Definition.SupportsSoftwareTemperatureRamp ? status.Values[axis.Definition.Rate.Id] : null,
                    TemperatureSoftwareTarget: status.Temperature?.SoftwareTarget, TemperatureSoftwareRate: status.Temperature?.SoftwareRate, Homing: activeHomes.Contains(session.Descriptor.Id)));
            else if (session is not MatrixSwitchSession) throw new NotSupportedException($"Pause has no resume policy for {session.Descriptor.Id}.");
        }
        return result.AsReadOnly();
    }

    internal async Task RestoreResumeStateAsync(IReadOnlyList<DeviceResumeState> states, CancellationToken token)
    {
        // Keep the actual confirmed hold target; it can legitimately differ from the pre-pause target.
        var confirmedHeldTargets = new Dictionary<InstrumentId, Quantity>();
        // Verify every held route even when no source was enabled before pause.
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            foreach (var state in states.Where(s => s.SourceState is not null))
            {
                var current = await ((InstrumentManager)Get(state.Device)).CaptureRunSourceStateAsync(Owner, token);
                var expected = state.SourceState!;
                if (!current.Connected || current.OutputEnabled || current.Source != expected.Source || current.Sense != expected.Sense || current.Terminals != expected.Terminals)
                    throw new InvalidOperationException($"{state.Device}: source state or configuration changed while paused; resume refused.");
            }
            foreach (var state in states.Where(s => s.LockInState is not null))
            {
                await PrepareMutationAsync(state.Device); touched.Add(state.Device);
                if (await ((Sr830MeterSession)Get(state.Device)).CaptureResumeSettingsAsync(Owner, token) != state.LockInState)
                    throw new InvalidOperationException($"{state.Device}: lock-in input configuration changed while paused; resume refused.");
            }
            foreach (var state in states.Where(s => s.ElectrometerState is not null))
            {
                await PrepareMutationAsync(state.Device); touched.Add(state.Device);
                if (await ((Keithley6514MeterSession)Get(state.Device)).CaptureResumeSettingsAsync(Owner, token) != state.ElectrometerState)
                    throw new InvalidOperationException($"{state.Device}: electrometer configuration changed while paused; resume refused.");
            }
            foreach (var state in states.Where(s => s.PicoammeterState is not null))
            {
                await PrepareMutationAsync(state.Device); touched.Add(state.Device);
                if (await ((Keithley6485MeterSession)Get(state.Device)).CaptureResumeSettingsAsync(Owner, token) != state.PicoammeterState)
                    throw new InvalidOperationException($"{state.Device}: picoammeter configuration changed while paused; resume refused.");
            }
            foreach (var state in states.Where(s => s.MeterState is not null))
            {
                await PrepareMutationAsync(state.Device); touched.Add(state.Device);
                if (await ((Keithley2182MeterSession)Get(state.Device)).CaptureResumeSettingsAsync(Owner, token) != state.MeterState)
                    throw new InvalidOperationException($"{state.Device}: voltage meter configuration changed while paused; resume refused.");
            }
            foreach (var state in states.Where(s => s.CurrentSourceState is not null))
            {
                await PrepareMutationAsync(state.Device); touched.Add(state.Device);
                var current = await ((Keithley6221CurrentSourceSession)Get(state.Device)).CaptureSourceStateAsync(Owner, token);
                if (current.OutputEnabled || current.Settings != state.CurrentSourceState!.Settings) throw new InvalidOperationException($"{state.Device}: current source configuration changed while paused; resume refused.");
            }
            foreach (var state in states.Where(s => s.TemperatureState is not null))
            {
                var hasHeldTarget = heldTemperatureTargets.TryGetValue(state.Device, out var heldTarget);
                // Observation consumes the old pause stop proof before any check can fail.
                await PrepareMutationAsync(state.Device);
                var axis = (AxisControllerSession)Get(state.Device);
                if (await axis.CaptureTemperatureConfigurationAsync(Owner, token) != state.TemperatureState)
                    throw new InvalidOperationException($"{state.Device}: temperature control changed while paused; resume refused.");
                var status = await axis.GetDeviceStatusAsync(token, Owner);
                if (!status.Connected || status.Values[axis.Definition.Rate.Id] != state.TemperatureRate)
                    throw new InvalidOperationException($"{state.Device}: temperature rate changed while paused; resume refused.");
                if (!hasHeldTarget || status.Values[axis.Definition.Target.Id] != heldTarget)
                {
                    temperatureScanRequiresRampOff.Add(state.Device);
                    throw new InvalidOperationException($"{state.Device}: temperature target changed while paused or its hold proof is unavailable; resume refused.");
                }
                confirmedHeldTargets.Add(state.Device, heldTarget);
            }
            foreach (var matrix in sessions.Values.OfType<MatrixSwitchSession>())
            {
                await PrepareMutationAsync(matrix.Descriptor.Id);
                touched.Add(matrix.Descriptor.Id);
                await matrix.ConfirmHeldRouteAsync(Owner, token);
            }
        }
        finally { gate.Release(); }
        // Restore held axis targets first; source output stays off until these succeed.
        foreach (var state in states.Where(s => s.Target is not null))
        {
            if (state.TemperatureState is { } configuration)
            {
                await gate.WaitAsync(token);
                try
                {
                    await PrepareMutationAsync(state.Device); touched.Add(state.Device);
                    await ((AxisControllerSession)Get(state.Device)).RestoreTemperatureTargetAsync(state.TargetValue!.Value, confirmedHeldTargets[state.Device], configuration, state.TemperatureRate!.Value, state.TemperatureSoftwareTarget, state.TemperatureSoftwareRate, Owner, token);
                }
                catch { temperatureScanRequiresRampOff.Add(state.Device); throw; }
                finally { gate.Release(); }
            }
            else if (state.Homing) await StartRotationHomeAsync(state.Device, token);
            else await WriteAsync(state.Device, state.Target!, state.TargetValue!.Value, token);
        }
        foreach (var state in states.Where(s => s.OutputEnabled)) await EnableSourceOutputAsync(state.Device, token, expectedResumeState: state.SourceState, expectedCurrentState: state.CurrentSourceState);
    }

    private async Task RequireStoppedTaskFinishedAsync(InstrumentId id)
    {
        Get(id);
        await RequireAcStopFinishedAsync(id);
        if (recoveryRequired.Contains(id) || pendingStops.TryGetValue(id, out var stop) && (!stop.IsCompleted || await stop is not null))
            throw new InvalidOperationException($"{id} requires stop recovery before observation or mutation.");
    }

    private async Task PrepareMutationAsync(InstrumentId id, bool reconnect = false)
    {
        await RequireAcStopFinishedAsync(id, reconnect);
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
        heldRoutes.Remove(id); heldTemperatureScans.Remove(id); heldTemperatureTargets.TryRemove(id, out _);
    }

    private async Task<Exception?> StopAndCaptureAsync(IDeviceSession session, bool preserveRouting = false)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            if (session is MatrixSwitchSession matrix && preserveRouting)
            {
                await matrix.ConfirmHeldRouteAsync(Owner, deadline.Token);
                return null;
            }
            var status = session is AxisControllerSession cooling && coolingRequiresOff.Contains(session.Descriptor.Id) ? await cooling.StopCoolingAsync(Owner, deadline.Token) : !preserveRouting && session is AxisControllerSession ramp && temperatureScanRequiresRampOff.Contains(session.Descriptor.Id) ? await ramp.StopTemperatureScanAsync(Owner, deadline.Token) : session is MatrixSwitchSession routed ? await routed.StopAfterSourcesAsync(deadline.Token, Owner)
                : await session.StopDeviceAsync(deadline.Token, Owner);
            if (!status.SafetyConfirmed) throw new InvalidOperationException($"{session.Descriptor.Id} did not confirm {session.Descriptor.StopPolicy}.");
            if (preserveRouting && session is AxisControllerSession heldAxis && heldAxis.Definition.SupportsSoftwareTemperatureRamp)
                heldTemperatureTargets[session.Descriptor.Id] = status.Values[heldAxis.Definition.Target.Id];
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
