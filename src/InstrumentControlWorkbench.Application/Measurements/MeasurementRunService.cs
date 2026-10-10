using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;

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
    public RunDataSet? LatestDataSet { get; private set; }

    public async Task StartIvSweepAsync(IvSweepRecipe recipe, CancellationToken cancellationToken)
    {
        if (activeRunCancellation is not null)
        {
            throw new InvalidOperationException("A measurement run is already active.");
        }

        var dataSetId = RunId.New();
        var runId = dataSetId.ToString();
        var startedAt = DateTimeOffset.Now;
        leaseService.AcquireExclusive(instrumentManager.InstrumentId, runId, startedAt);
        activeRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RunDataSet? dataSet = null;

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
            var elapsed = new ParameterDescriptor(new("elapsed"), "Elapsed time", PhysicalUnit.Second, ParameterAccess.Read,
                new(0, PhysicalUnit.Second));
            dataSet = new RunDataSet(dataSetId, recipe.Name, startedAt,
                new DataSetSchema([new(Keithley2450Parameters.SourceVoltage), new(elapsed),
                    new(Keithley2450Parameters.MeasuredCurrent, [Keithley2450Parameters.SourceVoltage.Id, elapsed.Id])]),
                new Dictionary<string, Quantity>
                {
                    ["start_voltage"] = new(recipe.StartVoltage, PhysicalUnit.Volt),
                    ["stop_voltage"] = new(recipe.StopVoltage, PhysicalUnit.Volt),
                    ["points"] = new(recipe.Points, PhysicalUnit.Dimensionless),
                    ["compliance"] = new(recipe.ComplianceAmps, PhysicalUnit.Ampere),
                    ["dwell"] = new(recipe.DwellSeconds, PhysicalUnit.Second),
                }, [await instrumentManager.CaptureParametersAsync(activeRunCancellation.Token)]);
            LatestDataSet = dataSet;
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

                if (reading.Unit != "A") throw new InvalidOperationException("IV current readback must use amperes.");
                dataSet.Append(new MeasurementDataRow(reading.Timestamp, new Dictionary<ParameterId, Quantity>
                {
                    [Keithley2450Parameters.SourceVoltage.Id] = new(voltage, PhysicalUnit.Volt),
                    [elapsed.Id] = new(point.Elapsed.TotalSeconds, PhysicalUnit.Second),
                    [Keithley2450Parameters.MeasuredCurrent.Id] = new(reading.Value, PhysicalUnit.Ampere),
                }));

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
            dataSet.Complete(MeasurementRunState.Completed, DateTimeOffset.Now);
            await notifications.PublishAsync(
                new WorkbenchNotification(WorkbenchNotificationKind.RunCompleted, $"{recipe.Name} completed.", instrumentManager.InstrumentId, RunId: runId),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await instrumentManager.SafeStopAsync("operator abort", CancellationToken.None);
            dataSet?.Complete(MeasurementRunState.Aborted, DateTimeOffset.Now);
            await notifications.PublishAsync(
                new WorkbenchNotification(WorkbenchNotificationKind.RunAborted, $"{recipe.Name} aborted.", instrumentManager.InstrumentId, RunId: runId),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await instrumentManager.SafeStopAsync("fault cleanup", CancellationToken.None);
            if (dataSet?.State == MeasurementRunState.Running) dataSet.Complete(MeasurementRunState.Faulted, DateTimeOffset.Now);
            await notifications.PublishAsync(
                new WorkbenchNotification(WorkbenchNotificationKind.RunFaulted, ex.Message, instrumentManager.InstrumentId, RunId: runId),
                CancellationToken.None);
            throw;
        }
        finally
        {
            try
            {
                if (dataSet?.State == MeasurementRunState.Running) dataSet.Complete(MeasurementRunState.Faulted, DateTimeOffset.Now);
            }
            finally
            {
                leaseService.Release(instrumentManager.InstrumentId, runId);
                activeRunCancellation?.Dispose();
                activeRunCancellation = null;
            }
        }
    }

    public void Abort()
    {
        activeRunCancellation?.Cancel();
    }
}
