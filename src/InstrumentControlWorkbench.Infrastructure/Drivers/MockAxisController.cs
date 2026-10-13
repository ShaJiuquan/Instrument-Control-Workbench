using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

public sealed class MockAxisController : ITemperatureConfigurationPort, IRotationHomingPort
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private bool connected;
    private TemperatureControlConfiguration temperatureControl = new(true, true);
    private double target;
    private double readback;
    private double rate;
    private long updatedAt;

    public MockAxisController(AxisControllerDefinition definition, Quantity initial, Quantity initialRate, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Device.DriverKind != DriverKind.Simulation)
            throw new ArgumentException("A mock must be identified as Simulation.", nameof(definition));
        definition.Target.Validate(initial);
        definition.Readback.Validate(initial);
        definition.Rate.ValidateWrite(initialRate);
        Definition = definition;
        this.clock = clock ?? TimeProvider.System;
        target = readback = initial.Value;
        rate = initialRate.Value;
        updatedAt = this.clock.GetTimestamp();
    }

    public AxisControllerDefinition Definition { get; }
    public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            connected = true;
            updatedAt = clock.GetTimestamp();
            return Task.FromResult(Snapshot());
        }
    }

    public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) { Advance(); return Task.FromResult(Snapshot()); }
    }

    public Task SetTargetAsync(Quantity value, CancellationToken token)
    {
        Definition.Target.ValidateWrite(value);
        token.ThrowIfCancellationRequested();
        lock (gate) { RequireConnected(); Advance(); target = value.Value; }
        return Task.CompletedTask;
    }

    public Task StartHomingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Definition.SupportsHoming) throw new NotSupportedException("This simulation profile has no dedicated rotational homing.");
        lock (gate)
        {
            RequireConnected();
            // Captured Rust mock likewise completes its dedicated home command
            // immediately. This is not a physical travel/sensor simulation.
            target = readback = 0; updatedAt = clock.GetTimestamp();
        }
        return Task.CompletedTask;
    }

    public Task SetRateAsync(Quantity value, CancellationToken token)
    {
        Definition.Rate.ValidateWrite(value);
        token.ThrowIfCancellationRequested();
        lock (gate) { RequireConnected(); Advance(); rate = value.Value; }
        return Task.CompletedTask;
    }

    public Task PrepareSoftwareRampAsync(Quantity initialTarget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Definition.Target.ValidateWrite(initialTarget);
        lock (gate) { RequireTemperatureControl(); RequireConnected(); Advance(); target = initialTarget.Value; temperatureControl = new(false, true); }
        return Task.CompletedTask;
    }
    public Task SetHardwareRampAsync(Quantity value, CancellationToken token)
    {
        Definition.Rate.ValidateWrite(value); token.ThrowIfCancellationRequested();
        lock (gate) { RequireTemperatureControl(); RequireConnected(); Advance(); rate = value.Value; temperatureControl = new(true, true); }
        return Task.CompletedTask;
    }
    public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) { RequireTemperatureControl(); RequireConnected(); return Task.FromResult(temperatureControl); }
    }
    public Task ConfigureControlAsync(TemperatureControlConfiguration configuration, Quantity? requestedRate, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (requestedRate is { } value) Definition.Rate.ValidateWrite(value);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            RequireTemperatureControl(); RequireConnected(); Advance();
            if (requestedRate is { } newRate) rate = newRate.Value;
            temperatureControl = configuration;
        }
        return Task.CompletedTask;
    }
    private void RequireTemperatureControl()
    {
        if (!Definition.SupportsSoftwareTemperatureRamp) throw new NotSupportedException("This axis has no temperature-control profile.");
    }

    public Task HoldAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) { Advance(); target = readback; }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            Advance();
            if (target != readback) throw new InvalidOperationException("Hold motion before disconnecting the controller.");
            connected = false;
        }
        return Task.CompletedTask;
    }

    private void Advance()
    {
        var now = clock.GetTimestamp();
        var delta = clock.GetElapsedTime(updatedAt, now).TotalSeconds;
        updatedAt = now;
        if (!connected) return;
        if (Definition.SupportsSoftwareTemperatureRamp && !temperatureControl.ControlEnabled) return;
        var perSecond = Definition.Rate.Unit == PhysicalUnit.KelvinPerMinute ? rate / 60 : rate;
        var distance = perSecond * delta;
        readback = target > readback ? Math.Min(target, readback + distance) : Math.Max(target, readback - distance);
    }

    private AxisControllerSnapshot Snapshot() => new(connected, new(target, Definition.Target.Unit),
        new(readback, Definition.Readback.Unit), new(rate, Definition.Rate.Unit), connected && target != readback &&
        (!Definition.SupportsSoftwareTemperatureRamp || temperatureControl.ControlEnabled), clock.GetUtcNow());
    private void RequireConnected()
    {
        if (!connected) throw new InvalidOperationException($"{Definition.Device.Id} is disconnected.");
    }
}
