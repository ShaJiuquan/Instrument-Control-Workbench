using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

public sealed class MockAxisController : IAxisControllerPort
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private bool connected;
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

    public Task SetRateAsync(Quantity value, CancellationToken token)
    {
        Definition.Rate.ValidateWrite(value);
        token.ThrowIfCancellationRequested();
        lock (gate) { RequireConnected(); Advance(); rate = value.Value; }
        return Task.CompletedTask;
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
        var perSecond = Definition.Rate.Unit == PhysicalUnit.KelvinPerMinute ? rate / 60 : rate;
        var distance = perSecond * delta;
        readback = target > readback ? Math.Min(target, readback + distance) : Math.Max(target, readback - distance);
    }

    private AxisControllerSnapshot Snapshot() => new(connected, new(target, Definition.Target.Unit),
        new(readback, Definition.Readback.Unit), new(rate, Definition.Rate.Unit), connected && target != readback, clock.GetUtcNow());
    private void RequireConnected()
    {
        if (!connected) throw new InvalidOperationException($"{Definition.Device.Id} is disconnected.");
    }
}
