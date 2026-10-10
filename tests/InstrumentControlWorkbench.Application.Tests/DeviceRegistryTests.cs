using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class DeviceRegistryTests
{
    [Fact]
    public async Task OversizedSafetyTimeoutIsRejectedBeforeOwningOrConnectingDevices()
    {
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([manager], leases);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeasurementRunService(manager, leases, hub, safetyTimeout: TimeSpan.MaxValue));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.AcquireRunAsync([driver.Id], RunId.New(),
            CancellationToken.None, TimeSpan.MaxValue));
        Assert.False(leases.IsLeased(driver.Id, DateTimeOffset.UtcNow));
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task SourceOutputCannotBeEnabledWithoutRunOwnership()
    {
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, new NotificationHub());
        await manager.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnterRunAsync(CancellationToken.None));
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }

    [Fact]
    public async Task StartupWithLiveOutputCannotSilentlyReconfigureItsSourceMode()
    {
        var driver = new MockKeithley2450SourceMeter();
        await driver.ConnectAsync(CancellationToken.None);
        await driver.EnableOutputAsync(CancellationToken.None);
        var manager = new InstrumentManager(driver, new NotificationHub());
        await manager.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(new(SourceFunction.Current, 1e-3, 10),
            new(SenseFunction.Voltage, SensingMode.FourWire, 1, true), CancellationToken.None));
        var unchanged = await driver.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(SourceFunction.Voltage, unchanged.Source.Function);
        Assert.True(unchanged.OutputEnabled);
        await manager.SafeStopAsync("startup recovery", CancellationToken.None);
        await manager.ConfigureAsync(new(SourceFunction.Current, 1e-3, 10),
            new(SenseFunction.Voltage, SensingMode.FourWire, 1, true), CancellationToken.None);
        Assert.Equal(SourceFunction.Current, (await driver.GetSnapshotAsync(CancellationToken.None)).Source.Function);
    }

    [Fact]
    public async Task OneRunCanOwnSourceTemperatureFieldAndAngleAndCleanUpByTheirPolicies()
    {
        var clock = new ManualTimeProvider();
        var hub = new NotificationHub();
        var source = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(source, hub);
        var temperature = MockEnvironmentCatalog.Temperature(clock);
        var field = MockEnvironmentCatalog.Field(clock);
        var angle = MockEnvironmentCatalog.Angle(clock);
        var leases = new InstrumentLeaseService(TimeSpan.FromMilliseconds(1));
        var registry = new DeviceRegistry([manager, new AxisControllerSession(temperature, hub),
            new AxisControllerSession(field, hub), new AxisControllerSession(angle, hub)], leases);
        Assert.All(registry.Devices, d => Assert.Equal(DriverKind.Simulation, d.DriverKind));
        var ids = registry.Devices.Select(d => d.Id).ToArray();
        var resources = await registry.AcquireRunAsync(ids, RunId.New(), CancellationToken.None);
        await resources.ConnectAllAsync(CancellationToken.None);
        await resources.ConfigureSourceMeterAsync(source.Id, new(SourceFunction.Voltage, 0.1, 105e-6),
            new(SenseFunction.Current, SensingMode.FourWire, 1, true), CancellationToken.None);
        await resources.EnableSourceOutputAsync(source.Id, CancellationToken.None);
        await resources.WriteAsync(temperature.Definition.Device.Id, temperature.Definition.Target.Id, new(301, PhysicalUnit.Kelvin), CancellationToken.None);
        await resources.WriteAsync(field.Definition.Device.Id, field.Definition.Target.Id, new(1, PhysicalUnit.Tesla), CancellationToken.None);
        await resources.WriteAsync(angle.Definition.Device.Id, angle.Definition.Target.Id, new(30, PhysicalUnit.Degree), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        var actualField = await resources.ReadAsync(field.Definition.Device.Id, field.Definition.Readback.Id, CancellationToken.None);
        Assert.Equal(0.2, actualField.Value, 10);
        foreach (var id in ids)
        {
            Assert.True(leases.IsLeased(id, DateTimeOffset.UtcNow.AddYears(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetStatusAsync(id, CancellationToken.None));
        }
        Assert.Empty(await resources.StopAllAsync(CancellationToken.None));
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        var held = await field.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(actualField, held.Target);
        Assert.False(held.Moving);
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(held.Readback, (await field.GetSnapshotAsync(CancellationToken.None)).Readback);
        await resources.ReleaseAsync(CancellationToken.None);
        Assert.All(ids, id => Assert.False(leases.IsLeased(id, DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReadAsync(source.Id, Keithley2450Parameters.MeasuredCurrent.Id, CancellationToken.None));
        var events = Drain(hub);
        Assert.Contains(events, n => n.Kind == WorkbenchNotificationKind.DeviceStateChanged && n.Device?.Descriptor.Id == field.Definition.Device.Id);
        Assert.Contains(events, n => n.Kind == WorkbenchNotificationKind.SafetyStateChanged && n.InstrumentId == source.Id);
    }

    [Fact]
    public async Task LegacyIvAndRegistryUseTheSameLeaseAndOwnershipBoundaries()
    {
        var hub = new NotificationHub();
        var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([manager], leases);
        var runs = new MeasurementRunService(manager, leases, hub);
        var running = runs.StartIvSweepAsync(new("owned IV", 0, 0.1, 3, 105e-6, 10), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AcquireRunAsync([manager.InstrumentId], RunId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.WriteAsync(manager.InstrumentId,
            Keithley2450Parameters.SourceVoltage.Id, new(0.5, PhysicalUnit.Volt), CancellationToken.None));
        runs.Abort();
        await running;
        var resources = await registry.AcquireRunAsync([manager.InstrumentId], RunId.New(), CancellationToken.None);
        await resources.ConnectAllAsync(CancellationToken.None);
        Assert.Empty(await resources.StopAllAsync(CancellationToken.None));
        await resources.ReleaseAsync(CancellationToken.None);
        await runs.StartIvSweepAsync(new("retry", 0, 0.1, 3, 105e-6, 0), CancellationToken.None);
    }

    [Fact]
    public async Task LeaseConflictIsAtomicAndDoesNotTouchOtherDevices()
    {
        var hub = new NotificationHub();
        var first = MockEnvironmentCatalog.Field();
        var second = MockEnvironmentCatalog.Angle();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([new AxisControllerSession(first, hub), new AxisControllerSession(second, hub)], leases);
        leases.AcquireRunExclusive(second.Definition.Device.Id, RunId.New(), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AcquireRunAsync(registry.Devices.Select(d => d.Id), RunId.New(), CancellationToken.None));
        Assert.False(leases.IsLeased(first.Definition.Device.Id, DateTimeOffset.UtcNow));
        Assert.False((await first.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task ManualCommandsCannotBypassAnUnclaimedLease()
    {
        var hub = new NotificationHub();
        var axis = MockEnvironmentCatalog.Field();
        var leases = new InstrumentLeaseService(TimeSpan.FromMinutes(1));
        var registry = new DeviceRegistry([new AxisControllerSession(axis, hub)], leases);
        leases.AcquireExclusive(axis.Definition.Device.Id, "external session", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ConnectAsync(axis.Definition.Device.Id, CancellationToken.None));
        Assert.False((await axis.GetSnapshotAsync(CancellationToken.None)).Connected);
        leases.Release(axis.Definition.Device.Id, "external session");
        await registry.ConnectAsync(axis.Definition.Device.Id, CancellationToken.None);
    }

    [Fact]
    public async Task ClaimFailureRollsBackOnlyItsOwnNewClaims()
    {
        var hub = new NotificationHub();
        var first = new AxisControllerSession(MockEnvironmentCatalog.Field(), hub);
        var second = new AxisControllerSession(MockEnvironmentCatalog.Angle(), hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([first, second], leases);
        var foreignOwner = RunId.New();
        await second.ClaimRunAsync(foreignOwner, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AcquireRunAsync([first.Descriptor.Id, second.Descriptor.Id], RunId.New(), CancellationToken.None));
        Assert.False(leases.IsLeased(first.Descriptor.Id, DateTimeOffset.UtcNow));
        Assert.False(leases.IsLeased(second.Descriptor.Id, DateTimeOffset.UtcNow));
        await first.ConnectDeviceAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.ConnectDeviceAsync(CancellationToken.None));
        await second.ReleaseRunAsync(foreignOwner, CancellationToken.None);
    }

    [Fact]
    public async Task FailureOfOneStopDoesNotSkipOthersAndRetainsTheWholeGroupForRecovery()
    {
        var hub = new NotificationHub();
        var source = new MockKeithley2450SourceMeter();
        var bad = new ControlledAxis(MockEnvironmentCatalog.Field()) { HoldError = new IOException("field controller unreachable") };
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([new InstrumentManager(source, hub), new AxisControllerSession(bad, hub)], leases);
        var resources = await registry.AcquireRunAsync(registry.Devices.Select(d => d.Id), RunId.New(), CancellationToken.None);
        await resources.ConnectAllAsync(CancellationToken.None);
        await resources.EnableSourceOutputAsync(source.Id, CancellationToken.None);
        var failure = Assert.Single(await resources.StopAllAsync(CancellationToken.None));
        Assert.Equal(bad.Definition.Device.Id, failure.InstrumentId);
        Assert.Equal("field controller unreachable", failure.Error.Message);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(CancellationToken.None));
        Assert.All(registry.Devices, d => Assert.True(leases.IsLeased(d.Id, DateTimeOffset.UtcNow)));
        bad.HoldError = null;
        Assert.Empty(await resources.StopAllAsync(CancellationToken.None, retryFailed: true));
        await resources.ReleaseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PendingStopTimeoutKeepsItsTaskAndOtherDevicesStillStop()
    {
        var hub = new NotificationHub();
        var bad = new ControlledAxis(MockEnvironmentCatalog.Field())
        {
            HoldGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var other = MockEnvironmentCatalog.Angle();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([new AxisControllerSession(bad, hub), new AxisControllerSession(other, hub)], leases);
        var resources = await registry.AcquireRunAsync(registry.Devices.Select(d => d.Id), RunId.New(), CancellationToken.None, TimeSpan.FromMilliseconds(100));
        await resources.ConnectAllAsync(CancellationToken.None);
        var failure = Assert.Single(await resources.StopAllAsync(CancellationToken.None));
        Assert.IsType<TimeoutException>(failure.Error);
        Assert.False((await other.GetSnapshotAsync(CancellationToken.None)).Moving);
        Assert.Single(await resources.StopAllAsync(CancellationToken.None, retryFailed: true));
        Assert.Equal(1, bad.HoldCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ConnectAllAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(CancellationToken.None));
        bad.HoldGate.SetResult();
        await bad.HoldReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bad.HoldGate = null;
        var recovered = await resources.StopAllAsync(CancellationToken.None, retryFailed: true);
        if (recovered.Count > 0) recovered = await resources.StopAllAsync(CancellationToken.None, retryFailed: true);
        Assert.Empty(recovered);
        await resources.ReleaseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedInitialConnectionCanRecoverWithinItsExistingResourceGroup()
    {
        var hub = new NotificationHub();
        var bad = new ControlledAxis(MockEnvironmentCatalog.Field()) { ConnectError = new IOException("connection unavailable") };
        var other = MockEnvironmentCatalog.Angle();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([new AxisControllerSession(bad, hub), new AxisControllerSession(other, hub)], leases);
        var resources = await registry.AcquireRunAsync(registry.Devices.Select(d => d.Id), RunId.New(), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => resources.ConnectAllAsync(CancellationToken.None));
        Assert.NotEmpty(await resources.StopAllAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(CancellationToken.None));
        bad.ConnectError = null;
        await resources.ConnectAllAsync(CancellationToken.None);
        Assert.Empty(await resources.StopAllAsync(CancellationToken.None));
        await resources.ReleaseAsync(CancellationToken.None);
        Assert.All(registry.Devices, d => Assert.False(leases.IsLeased(d.Id, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task MutationAfterConfirmedStopRequiresAnotherStopBeforeRelease()
    {
        var hub = new NotificationHub();
        var angle = MockEnvironmentCatalog.Angle();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([new AxisControllerSession(angle, hub)], leases);
        var resources = await registry.AcquireRunAsync([angle.Definition.Device.Id], RunId.New(), CancellationToken.None);
        await resources.ConnectAllAsync(CancellationToken.None);
        Assert.Empty(await resources.StopAllAsync(CancellationToken.None));
        await resources.WriteAsync(angle.Definition.Device.Id, angle.Definition.Target.Id, new(10, PhysicalUnit.Degree), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(CancellationToken.None));
        Assert.Empty(await resources.StopAllAsync(CancellationToken.None));
        await resources.ReleaseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AxisRatesReadOnlyParametersUnitsAndUnsupportedCapabilitiesAreValidated()
    {
        var hub = new NotificationHub();
        var clock = new ManualTimeProvider();
        var temperature = MockEnvironmentCatalog.Temperature(clock);
        var registry = new DeviceRegistry([new AxisControllerSession(temperature, hub)], new(TimeSpan.FromSeconds(5)));
        var id = temperature.Definition.Device.Id;
        await registry.ConnectAsync(id, CancellationToken.None);
        await registry.WriteAsync(id, temperature.Definition.Target.Id, new(310, PhysicalUnit.Kelvin), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(301, (await registry.ReadAsync(id, temperature.Definition.Readback.Id, CancellationToken.None)).Value, 10);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.WriteAsync(id, temperature.Definition.Target.Id, new(1, PhysicalUnit.Volt), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.WriteAsync(id, temperature.Definition.Readback.Id, new(300, PhysicalUnit.Kelvin), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.WriteAsync(id, temperature.Definition.Rate.Id, new(0, PhysicalUnit.KelvinPerMinute), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => registry.ReadAsync(id, new("unsupported"), CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.ConnectAsync(new("unknown"), CancellationToken.None));
    }

    [Fact]
    public async Task SourceModeUnitReadbackAndDisconnectAreRealOperations()
    {
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([manager], leases);
        await registry.ConnectAsync(driver.Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.WriteAsync(driver.Id,
            Keithley2450Parameters.SourceCurrent.Id, new(1e-3, PhysicalUnit.Ampere), CancellationToken.None));
        Assert.Equal(InstrumentLifecycleState.Idle, (await registry.GetStatusAsync(driver.Id, CancellationToken.None)).Lifecycle);
        await registry.WriteAsync(driver.Id, Keithley2450Parameters.SourceVoltage.Id, new(0.25, PhysicalUnit.Volt), CancellationToken.None);
        Assert.Equal(0.25, (await registry.ReadAsync(driver.Id, Keithley2450Parameters.SourceVoltage.Id, CancellationToken.None)).Value);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.WriteAsync(driver.Id, Keithley2450Parameters.SourceVoltage.Id, new(211, PhysicalUnit.Volt), CancellationToken.None));
        var resources = await registry.AcquireRunAsync([driver.Id], RunId.New(), CancellationToken.None);
        await resources.ConnectAllAsync(CancellationToken.None);
        await resources.ConfigureSourceMeterAsync(driver.Id, new(SourceFunction.Current, 1e-3, 10), new(SenseFunction.Voltage, SensingMode.FourWire, 1, true), CancellationToken.None);
        await resources.EnableSourceOutputAsync(driver.Id, CancellationToken.None);
        var voltage = await resources.ReadAsync(driver.Id, Keithley2450Parameters.MeasuredVoltage.Id, CancellationToken.None);
        Assert.Equal(PhysicalUnit.Volt, voltage.Unit);
        Assert.True(voltage.Value > 0.4);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.DisconnectAsync(CancellationToken.None));
        Assert.Empty(await resources.StopAllAsync(CancellationToken.None));
        await resources.ReleaseAsync(CancellationToken.None);
        await registry.DisconnectAsync(driver.Id, CancellationToken.None);
        Assert.False((await registry.GetStatusAsync(driver.Id, CancellationToken.None)).Connected);
        await registry.ConnectAsync(driver.Id, CancellationToken.None);
    }

    [Fact]
    public void DuplicateDeviceIdsAndProfilesAreRejected()
    {
        var hub = new NotificationHub();
        var first = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var second = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        Assert.Throws<ArgumentException>(() => new DeviceRegistry([first, second], new(TimeSpan.FromSeconds(5))));
        var independent = new InstrumentManager(new MockKeithley2450SourceMeter(new("other-k2450")), hub);
        Assert.Equal(2, new DeviceRegistry([first, independent], new(TimeSpan.FromSeconds(5))).Devices.Count);
    }

    private static List<WorkbenchNotification> Drain(NotificationHub hub)
    {
        var result = new List<WorkbenchNotification>();
        while (hub.Reader.TryRead(out var notification)) result.Add(notification);
        return result;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private sealed class ControlledAxis(MockAxisController inner) : IAxisControllerPort
    {
        public Exception? ConnectError { get; set; }
        public Exception? HoldError { get; set; }
        public TaskCompletionSource? HoldGate { get; set; }
        public TaskCompletionSource HoldReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HoldCalls { get; private set; }
        public AxisControllerDefinition Definition => inner.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => ConnectError is { } error
            ? Task.FromException<AxisControllerSnapshot>(error) : inner.ConnectAsync(token);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task SetTargetAsync(Quantity target, CancellationToken token) => inner.SetTargetAsync(target, token);
        public Task SetRateAsync(Quantity rate, CancellationToken token) => inner.SetRateAsync(rate, token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public async Task HoldAsync(CancellationToken token)
        {
            HoldCalls++;
            if (HoldError is { } error) throw error;
            if (HoldGate is { } pending) { await pending.Task; await inner.HoldAsync(CancellationToken.None); }
            else await inner.HoldAsync(token);
            HoldReturned.TrySetResult();
        }
    }
}
