using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed class MeasurementRunService
{
    private readonly InstrumentManager instrumentManager;
    private readonly InstrumentLeaseService leaseService;
    private readonly NotificationHub notifications;
    private CancellationTokenSource? activeRunCancellation;

    public MeasurementRunService(
        InstrumentManager instrumentManager,
        InstrumentLeaseService leaseService,
        NotificationHub notifications)
    {
        this.instrumentManager = instrumentManager;
        this.leaseService = leaseService;
        this.notifications = notifications;
    }

    public bool HasActiveRun => activeRunCancellation is not null;

    public async Task StartIvSweepAsync(IvSweepRecipe recipe, CancellationToken cancellationToken)
    {
        if (activeRunCancellation is not null)
        {
            throw new InvalidOperationException("A measurement run is already active.");
        }

        var runId = $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        var startedAt = DateTimeOffset.Now;
        leaseService.AcquireExclusive(instrumentManager.InstrumentId, runId, startedAt);
        activeRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await notifications.PublishAsync(
                new WorkbenchNotification(WorkbenchNotificationKind.RunStarted, $"Started {recipe.Name}.", instrumentManager.InstrumentId, RunId: runId),
                activeRunCancellation.Token);
            await instrumentManager.ConnectAsync(activeRunCancellation.Token);
            await instrumentManager.ConfigureAsync(
                new SourceSetpoint(SourceFunction.Voltage, recipe.StartVoltage, recipe.ComplianceAmps),
                new SenseConfiguration(SenseFunction.Current, SensingMode.FourWire, 1.0, true),
                activeRunCancellation.Token);
            await instrumentManager.EnterRunAsync(activeRunCancellation.Token);

            foreach (var voltage in recipe.VoltagePoints())
            {
                activeRunCancellation.Token.ThrowIfCancellationRequested();
                leaseService.Refresh(instrumentManager.InstrumentId, runId, DateTimeOffset.Now);

                var reading = await instrumentManager.SetAndReadAsync(
                    new SourceSetpoint(SourceFunction.Voltage, voltage, recipe.ComplianceAmps),
                    activeRunCancellation.Token);
                var point = new MeasurementPoint(
                    reading.Timestamp,
                    reading.Timestamp - startedAt,
                    voltage,
                    reading.Value,
                    "V",
                    reading.Unit);

                await notifications.PublishAsync(
                    new WorkbenchNotification(
                        WorkbenchNotificationKind.MeasurementPointReceived,
                        $"Point {voltage:0.###} V -> {reading.Value:0.###E+0} {reading.Unit}",
                        instrumentManager.InstrumentId,
                        Point: point,
                        RunId: runId),
                    activeRunCancellation.Token);

                await Task.Delay(TimeSpan.FromSeconds(recipe.DwellSeconds), activeRunCancellation.Token);
            }

            await instrumentManager.SafeStopAsync("run completed", CancellationToken.None);
            await notifications.PublishAsync(
                new WorkbenchNotification(WorkbenchNotificationKind.RunCompleted, $"{recipe.Name} completed.", instrumentManager.InstrumentId, RunId: runId),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await instrumentManager.SafeStopAsync("operator abort", CancellationToken.None);
            await notifications.PublishAsync(
                new WorkbenchNotification(WorkbenchNotificationKind.RunAborted, $"{recipe.Name} aborted.", instrumentManager.InstrumentId, RunId: runId),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await instrumentManager.SafeStopAsync("fault cleanup", CancellationToken.None);
            await notifications.PublishAsync(
                new WorkbenchNotification(WorkbenchNotificationKind.RunFaulted, ex.Message, instrumentManager.InstrumentId, RunId: runId),
                CancellationToken.None);
            throw;
        }
        finally
        {
            leaseService.Release(instrumentManager.InstrumentId, runId);
            activeRunCancellation?.Dispose();
            activeRunCancellation = null;
        }
    }

    public void Abort()
    {
        activeRunCancellation?.Cancel();
    }
}
