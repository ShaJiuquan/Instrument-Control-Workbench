using System.Runtime.ExceptionServices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed class MeasurementRunService
{
    private readonly InstrumentManager instrumentManager;
    private readonly InstrumentLeaseService leaseService;
    private readonly NotificationHub notifications;
    private readonly IRunResultListener[] listeners;
    private readonly TimeSpan safetyTimeout;
    private readonly object admissionGate = new();
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private RunExecutionContext? active;

    public MeasurementRunService(InstrumentManager instrumentManager, InstrumentLeaseService leaseService,
        NotificationHub notifications, IEnumerable<IRunResultListener>? resultListeners = null, TimeSpan? safetyTimeout = null)
    {
        this.instrumentManager = instrumentManager;
        this.leaseService = leaseService;
        this.notifications = notifications;
        this.safetyTimeout = safetyTimeout ?? TimeSpan.FromSeconds(5);
        if (this.safetyTimeout <= TimeSpan.Zero || this.safetyTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(safetyTimeout), "Safety timeout must be positive and fit the runtime timer range.");
        History = new InMemoryRunHistory();
        listeners = [History, .. resultListeners ?? []];
        if (listeners.Any(l => l is null) || listeners.Distinct().Count() != listeners.Length)
            throw new ArgumentException("Result listeners must be non-null and unique.", nameof(resultListeners));
    }

    public bool HasActiveRun { get { lock (admissionGate) return active is not null; } }
    public RunExecutionPhase Phase { get { lock (admissionGate) return active?.Phase ?? RunExecutionPhase.Idle; } }
    public RunDataSet? LatestDataSet { get; private set; }
    public RunOutcome? LatestOutcome { get; private set; }
    public InMemoryRunHistory History { get; }

    public async Task StartIvSweepAsync(IvSweepRecipe recipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.Validate();
        RunExecutionContext run;
        lock (admissionGate)
        {
            if (active is not null) throw new InvalidOperationException("A run is active or requires safety recovery.");
            run = new(cancellationToken);
            try { leaseService.AcquireRunExclusive(instrumentManager.InstrumentId, run.Id, run.StartedAt); }
            catch { run.Dispose(); throw; }
            active = run;
            LatestDataSet = null;
            LatestOutcome = null;
        }

        var errors = new List<Exception>();
        var startedListeners = new List<IRunResultListener>();
        var result = MeasurementRunState.Completed;
        try
        {
            // Claim uses an independent token: even pre-cancelled runs must own
            // the instrument before any attempted cleanup can touch it.
            await instrumentManager.ClaimRunAsync(run.Id, CancellationToken.None);
            run.InstrumentClaimed = true;
            await PublishAsync(run, WorkbenchNotificationKind.RunStarted, $"Started {recipe.Name}.", run.Token);
            await instrumentManager.ConnectAsync(run.Token, run.Id);
            await instrumentManager.ConfigureAsync(new(SourceFunction.Voltage, recipe.StartVoltage, recipe.ComplianceAmps),
                new(SenseFunction.Current, SensingMode.FourWire, 1.0, true), run.Token, run.Id);
            var elapsed = new ParameterDescriptor(new("elapsed"), "Elapsed time", PhysicalUnit.Second, ParameterAccess.Read,
                new(0, PhysicalUnit.Second));
            var dataSet = new RunDataSet(run.Id, recipe.Name, run.StartedAt,
                new DataSetSchema([new(Keithley2450Parameters.SourceVoltage), new(elapsed),
                    new(Keithley2450Parameters.MeasuredCurrent, [Keithley2450Parameters.SourceVoltage.Id, elapsed.Id])]),
                new Dictionary<string, Quantity>
                {
                    ["start_voltage"] = new(recipe.StartVoltage, PhysicalUnit.Volt),
                    ["stop_voltage"] = new(recipe.StopVoltage, PhysicalUnit.Volt),
                    ["points"] = new(recipe.Points, PhysicalUnit.Dimensionless),
                    ["compliance"] = new(recipe.ComplianceAmps, PhysicalUnit.Ampere),
                    ["dwell"] = new(recipe.DwellSeconds, PhysicalUnit.Second),
                }, [await instrumentManager.CaptureParametersAsync(run.Token, run.Id)]);
            run.DataSet = dataSet;
            LatestDataSet = dataSet;
            foreach (var listener in listeners)
            {
                await listener.OnStartedAsync(dataSet.Capture(), run.Token);
                startedListeners.Add(listener);
            }
            await instrumentManager.EnterRunAsync(run.Token, run.Id);
            run.Phase = RunExecutionPhase.Running;
            foreach (var voltage in recipe.VoltagePoints())
            {
                run.Token.ThrowIfCancellationRequested();
                await PauseCheckpointAsync(run);
                var reading = await instrumentManager.SetAndReadAsync(
                    new(SourceFunction.Voltage, voltage, recipe.ComplianceAmps), run.Token, run.Id);
                if (reading.Unit != "A") throw new InvalidOperationException("IV current readback must use amperes.");
                var point = new MeasurementPoint(reading.Timestamp, run.Elapsed.Elapsed, voltage, reading.Value, "V", reading.Unit);
                var row = new MeasurementDataRow(reading.Timestamp, new Dictionary<ParameterId, Quantity>
                {
                    [Keithley2450Parameters.SourceVoltage.Id] = new(voltage, PhysicalUnit.Volt),
                    [elapsed.Id] = new(point.Elapsed.TotalSeconds, PhysicalUnit.Second),
                    [Keithley2450Parameters.MeasuredCurrent.Id] = new(reading.Value, PhysicalUnit.Ampere),
                });
                dataSet.Append(row);
                // History receives the committed row first. External listeners
                // must observe cancellation; final snapshots retain partial data.
                foreach (var listener in startedListeners)
                    await listener.OnRowAsync(run.Id, row, listener == History ? CancellationToken.None : run.Token);
                await notifications.PublishAsync(new(WorkbenchNotificationKind.MeasurementPointReceived,
                    $"Point {voltage:0.###} V -> {reading.Value:0.###E+0} {reading.Unit}",
                    instrumentManager.InstrumentId, Point: point, RunId: run.Id.ToString()), CancellationToken.None);
                await DwellAsync(run, TimeSpan.FromSeconds(recipe.DwellSeconds));
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            result = MeasurementRunState.Aborted;
        }
        catch (Exception ex)
        {
            result = MeasurementRunState.Faulted;
            errors.Add(ex);
        }

        run.Phase = RunExecutionPhase.Finalizing;
        await PublishAsync(run, WorkbenchNotificationKind.RunFinalizing, "Stopping output and finalizing results.", CancellationToken.None);
        var safetyError = run.InstrumentClaimed ? await ConfirmSafetyAsync(run) : null;
        if (safetyError is not null)
        {
            if (!errors.Contains(safetyError)) errors.Add(safetyError);
            result = MeasurementRunState.Faulted;
        }
        if (run.DataSet is { } completed)
        {
            completed.Complete(result, DateTimeOffset.UtcNow < run.StartedAt ? run.StartedAt : DateTimeOffset.UtcNow);
            var snapshot = completed.Capture();
            foreach (var listener in startedListeners)
            {
                try { await listener.OnFinalizedAsync(snapshot, CancellationToken.None); }
                catch (Exception ex) { errors.Add(ex); result = MeasurementRunState.Faulted; }
            }
        }
        LatestOutcome = new(run.Id, result, Array.AsReadOnly(errors.ToArray()));
        await PublishAsync(run, result switch
        {
            MeasurementRunState.Completed => WorkbenchNotificationKind.RunCompleted,
            MeasurementRunState.Aborted => WorkbenchNotificationKind.RunAborted,
            _ => WorkbenchNotificationKind.RunFaulted,
        }, errors.Count == 0 ? $"{recipe.Name}: {result}." : string.Join("; ", errors.Select(e => e.Message)), CancellationToken.None);
        if (safetyError is not null) run.Phase = RunExecutionPhase.SafetyRecoveryRequired;
        if (safetyError is null) await ReleaseRunAsync(run);
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Execution or finalization failed.", errors);
    }

    public void Abort() { lock (admissionGate) active?.Abort(); }
    public void Pause()
    {
        lock (admissionGate)
            (active ?? throw new InvalidOperationException("No run is active.")).Pause();
    }
    public void Resume() { lock (admissionGate) active?.Resume(); }

    public async Task RecoverSafetyAsync(CancellationToken cancellationToken)
    {
        await recoveryGate.WaitAsync(cancellationToken);
        try
        {
            RunExecutionContext run;
            lock (admissionGate)
            {
                run = active ?? throw new InvalidOperationException("No run requires safety recovery.");
                if (run.Phase != RunExecutionPhase.SafetyRecoveryRequired)
                    throw new InvalidOperationException("Run finalization has not finished.");
            }
            // Do not start another stop while the timed-out driver call is alive.
            // A completed failed attempt may be retried explicitly by the operator.
            if (run.SafetyTask is { IsCompleted: true } previous && await previous is not null)
                run.SafetyTask = null;
            var error = await ConfirmSafetyAsync(run).WaitAsync(cancellationToken);
            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
            await ReleaseRunAsync(run);
        }
        finally { recoveryGate.Release(); }
    }

    private async Task PauseCheckpointAsync(RunExecutionContext run)
    {
        if (run.PauseGate is not { } resume) return;
        var error = await ConfirmSafetyAsync(run);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        run.SafetyTask = null;
        run.Phase = RunExecutionPhase.Paused;
        await PublishAsync(run, WorkbenchNotificationKind.RunPaused, "Paused with output confirmed off; resources remain reserved.", CancellationToken.None);
        await resume.WaitAsync(run.Token);
        run.Token.ThrowIfCancellationRequested();
        await instrumentManager.EnterRunAsync(run.Token, run.Id);
        run.Phase = RunExecutionPhase.Running;
        await PublishAsync(run, WorkbenchNotificationKind.RunResumed, "Run resumed.", run.Token);
    }

    private async Task DwellAsync(RunExecutionContext run, TimeSpan remaining)
    {
        while (remaining > TimeSpan.Zero)
        {
            run.Token.ThrowIfCancellationRequested();
            var pauseSignal = run.PauseRequested;
            using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var timer = Task.Delay(remaining, timerCancellation.Token);
            var winner = await Task.WhenAny(timer, pauseSignal);
            if (winner == timer) { await timer; return; }
            timerCancellation.Cancel();
            try { await timer; }
            catch (OperationCanceledException) when (timerCancellation.IsCancellationRequested) { }
            run.Token.ThrowIfCancellationRequested();
            remaining -= elapsed.Elapsed;
            await PauseCheckpointAsync(run);
        }
    }

    private async Task<Exception?> ConfirmSafetyAsync(RunExecutionContext run)
    {
        run.SafetyTask ??= StopAndCaptureErrorAsync(run);
        try { return await run.SafetyTask.WaitAsync(safetyTimeout); }
        catch (TimeoutException ex)
        {
            return new TimeoutException("Output-off confirmation timed out; the instrument remains reserved for safety recovery.", ex);
        }
    }

    private async Task<Exception?> StopAndCaptureErrorAsync(RunExecutionContext run)
    {
        using var deadline = new CancellationTokenSource(safetyTimeout);
        try
        {
            await instrumentManager.SafeStopAsync("run cleanup", deadline.Token, run.Id);
            return null;
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            return new TimeoutException("Output-off confirmation exceeded the safety deadline; recovery is required.", ex);
        }
        catch (Exception ex) { return ex; }
    }

    private async Task ReleaseRunAsync(RunExecutionContext run)
    {
        if (run.InstrumentClaimed) await instrumentManager.ReleaseRunAsync(run.Id, CancellationToken.None);
        leaseService.Release(instrumentManager.InstrumentId, run.Id.ToString());
        lock (admissionGate)
        {
            if (active != run) throw new InvalidOperationException("Run ownership changed unexpectedly.");
            active = null;
            run.Dispose();
        }
    }

    private ValueTask PublishAsync(RunExecutionContext run, WorkbenchNotificationKind kind, string message, CancellationToken token)
        => notifications.PublishAsync(new(kind, message, instrumentManager.InstrumentId, RunId: run.Id.ToString()), token);
}
