using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task FailedPendingResourceClaimClosesKnownRunObserversWithoutClaimingADataSet()
    {
        var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var field = MockEnvironmentCatalog.Field();
        var session = new BlackboardClaimFailure(new AxisControllerSession(field, hub)); var runs = new MeasurementRunService(new DeviceRegistry([session], leases), hub);
        var task = runs.StartPlanAsync(new("Failed admission", new ReadStep(new("read"), [new(new("field"), new(field.Definition.Device.Id, field.Definition.Readback.Id))])), Token);
        try
        {
            await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(runs.ActiveRunId);
            var initial = await runs.ObserveBlackboardAsync(id); Assert.Equal(RunExecutionPhase.Starting, initial.Phase); Assert.Empty(initial.Snapshot!.Entries);
            var waiting = runs.ObserveBlackboardAsync(id, initial.Revision); Assert.False(waiting.IsCompleted);
            session.Continue.TrySetResult(); var error = await Assert.ThrowsAsync<IOException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Reservation rejected.", error.Message); var final = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(id, final.Id); Assert.True(final.IsTerminal); Assert.Equal(MeasurementRunState.Faulted, final.Outcome); Assert.Null(final.Snapshot);
            var latest = await runs.ObserveBlackboardAsync(id); Assert.Equal(RunExecutionPhase.Idle, latest.Phase); Assert.Null(latest.Snapshot);
            Assert.Null(runs.LatestDataSet); Assert.Null(runs.LatestOutcome); Assert.False(runs.HasActiveRun);
            Assert.False(leases.IsLeased(field.Definition.Device.Id, DateTimeOffset.UtcNow)); Assert.False((await field.GetSnapshotAsync(Token)).Connected);
            await runs.WaitForFinalizationAsync(Token);
        }
        finally { session.Continue.TrySetResult(); if (!task.IsCompleted) await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }

    // Reservation is held before failing; any device IO would invalidate this test.
    private sealed class BlackboardClaimFailure(IDeviceSession inner) : IDeviceSession
    {
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ClaimRunAsync(RunId owner, CancellationToken token) { Entered.TrySetResult(); await Continue.Task.WaitAsync(token); throw new IOException("Reservation rejected."); }
        public Task ReleaseRunAsync(RunId owner, CancellationToken token) => throw new InvalidOperationException("Unclaimed session must not be released.");
        public Task<DeviceStatus> ConnectDeviceAsync(CancellationToken token, RunId? owner = null) => throw new InvalidOperationException("Admission must not connect hardware.");
        public Task<DeviceStatus> GetDeviceStatusAsync(CancellationToken token, RunId? owner = null) => throw new InvalidOperationException("Admission must not query hardware.");
        public Task DisconnectDeviceAsync(CancellationToken token, RunId? owner = null) => throw new InvalidOperationException("Admission must not disconnect hardware.");
        public Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null) => throw new InvalidOperationException("Admission must not read hardware.");
        public Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null) => throw new InvalidOperationException("Admission must not write hardware.");
        public Task<DeviceStatus> StopDeviceAsync(CancellationToken token, RunId? owner = null) => throw new InvalidOperationException("Admission must not stop hardware.");
    }
}
