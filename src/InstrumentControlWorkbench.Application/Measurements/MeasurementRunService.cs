using System.Runtime.ExceptionServices;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private readonly InstrumentId? primaryInstrument;
    private readonly DeviceRegistry devices;
    private readonly NotificationHub notifications;
    private readonly IRunResultListener[] listeners;
    private readonly TimeSpan safetyTimeout;
    private readonly object admissionGate = new();
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private RunExecutionContext? active;

    public MeasurementRunService(InstrumentManager instrumentManager, InstrumentLeaseService leaseService,
        NotificationHub notifications, IEnumerable<IRunResultListener>? resultListeners = null, TimeSpan? safetyTimeout = null,
        DeviceRegistry? deviceRegistry = null)
        : this(deviceRegistry ?? new DeviceRegistry([instrumentManager], leaseService), notifications, resultListeners, safetyTimeout)
    {
        ArgumentNullException.ThrowIfNull(instrumentManager);
        primaryInstrument = instrumentManager.InstrumentId;
    }

    // Generic execution depends on registered capabilities, not a selected SMU.
    public MeasurementRunService(DeviceRegistry deviceRegistry, NotificationHub notifications,
        IEnumerable<IRunResultListener>? resultListeners = null, TimeSpan? safetyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(deviceRegistry); ArgumentNullException.ThrowIfNull(notifications);
        devices = deviceRegistry;
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
    public RunBlackboardSnapshot? LatestBlackboardSnapshot { get; private set; }
    public IReadOnlyDictionary<ParameterId, BlackboardEntry>? LatestBlackboard => LatestBlackboardSnapshot?.Entries;
    public InMemoryRunHistory History { get; }

    public Task StartIvSweepAsync(IvSweepRecipe recipe, CancellationToken cancellationToken, RunMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.Validate();
        var source = primaryInstrument ?? throw new InvalidOperationException("Select a primary source meter for the IV shortcut, or supply an explicitly bound experiment plan.");
        return StartAsync(ExperimentPlanCompiler.Compile(IvExperimentPlan.Create(source, recipe), devices),
            cancellationToken, ivPlot: true, metadata);
    }

    public Task StartVoltageResistanceAsync(VoltageResistanceRecipe recipe, CancellationToken token, RunMetadata? metadata = null)
        => StartPlanAsync(VoltageResistancePlan.Create(primaryInstrument ?? throw new InvalidOperationException("Select a primary source meter for the resistance shortcut, or supply an explicitly bound experiment plan."), recipe), token, metadata);

    public Task StartSampleResistanceAsync(SampleResistanceRecipe recipe, CancellationToken token, RunMetadata? metadata = null)
        => StartPlanAsync(SampleResistancePlan.Create(recipe), token, metadata);

    public Task StartAnalyzedSampleResistanceAsync(SampleResistanceRecipe recipe, CancellationToken token, RunMetadata? metadata = null)
        => StartPlanAsync(SampleResistanceAnalysisPlan.Create(recipe), token, metadata);

    public ExperimentPlanPreview PreviewPlan(ExperimentPlan plan)
    {
        var compiled = ExperimentPlanCompiler.Compile(plan, devices);
        return new(compiled.Schema, compiled.Devices, compiled.MaximumStepExecutions, compiled.HasDynamicLoops);
    }

    public Task StartPlanAsync(ExperimentPlan plan, CancellationToken cancellationToken, RunMetadata? metadata = null)
        => StartAsync(ExperimentPlanCompiler.Compile(plan, devices), cancellationToken, ivPlot: false, metadata);

    private async Task StartAsync(CompiledExperiment compiled, CancellationToken token, bool ivPlot, RunMetadata? metadata)
    {
        RunExecutionContext run;
        lock (admissionGate)
        {
            if (active is not null) throw new InvalidOperationException("A run is active or requires safety recovery.");
            run = new(token)
            {
                SampleRoutes = compiled.SampleRoutes,
                NotificationInstrument = primaryInstrument is { } primary && compiled.Devices.Contains(primary) ? primary
                    : compiled.Devices.Count == 1 ? compiled.Devices[0] : null,
            };
            active = run;
        }
        try
        {
            // Claim without the run token: pre-cancelled admission still rolls back
            // only its own claims, and cleanup never touches a foreign resource.
            if (compiled.Devices.Count > 0)
                run.Resources = await devices.AcquireRunAsync(compiled.Devices, run.Id, CancellationToken.None, safetyTimeout);
        }
        catch
        {
            lock (admissionGate) { active = null; run.Finalized.TrySetResult(); run.Dispose(); }
            throw;
        }
        LatestDataSet = null;
        LatestOutcome = null;
        LatestBlackboardSnapshot = null;
        var errors = new List<Exception>();
        var startedListeners = new List<IRunResultListener>();
        var result = MeasurementRunState.Completed;
        try
        {
            await PublishAsync(run, WorkbenchNotificationKind.RunStarted, $"Started {compiled.Plan.Name}.", run.Token);
            if (run.Resources is { } resources)
            {
                await resources.ConnectAllAsync(run.Token);
                foreach (var preparation in compiled.Plan.Preparations)
                {
                    var status = await resources.GetStatusAsync(preparation.Device, run.Token);
                    if (!status.SafetyConfirmed) await resources.DisableSourceOutputAsync(preparation.Device, run.Token);
                    await resources.ConfigureSourceMeterAsync(preparation.Device, preparation.ToSetpoint(), preparation.Sense, run.Token);
                }
                foreach (var preparation in compiled.Plan.CurrentSourcePreparations)
                {
                    var status = await resources.GetStatusAsync(preparation.Device, run.Token);
                    if (!status.SafetyConfirmed) await resources.DisableSourceOutputAsync(preparation.Device, run.Token);
                    await resources.ConfigureCurrentSourceAsync(preparation.Device, preparation.Settings, run.Token);
                }
            }
            var snapshots = run.Resources is null ? Array.Empty<InstrumentParameterSnapshot>() :
                await run.Resources.CaptureParametersAsync(run.Token);
            var dataSet = new RunDataSet(run.Id, compiled.Plan.Name, run.StartedAt, compiled.Schema, compiled.Plan.Metadata, snapshots, compiled.Plan, metadata);
            run.DataSet = dataSet;
            LatestDataSet = dataSet;
            foreach (var listener in listeners)
            {
                await listener.OnStartedAsync(dataSet.Capture(), run.Token);
                startedListeners.Add(listener);
            }
            run.Phase = RunExecutionPhase.Running;
            if (await ExecuteStepAsync(compiled.Plan.Root, run, startedListeners, ivPlot) == StepOutcome.Failure)
                throw new PlanFailedException(run.LastWorkflowFailure ?? compiled.Plan.Root.Id);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { result = MeasurementRunState.Aborted; }
        catch (Exception ex) { result = MeasurementRunState.Faulted; errors.Add(ex); }

        run.Phase = RunExecutionPhase.Finalizing;
        await PublishAsync(run, WorkbenchNotificationKind.RunFinalizing, "Confirming device stop policies and finalizing results.", CancellationToken.None);
        var safetyErrors = run.Resources is null ? [] : await run.Resources.StopAllAsync(CancellationToken.None);
        foreach (var failure in safetyErrors)
        {
            if (!errors.Contains(failure.Error)) errors.Add(failure.Error);
            result = MeasurementRunState.Faulted;
        }
        RunBlackboardSnapshot? blackboard = null;
        try { blackboard = new(run.Id, DateTimeOffset.UtcNow, run.Elapsed.Elapsed, run.Variables); }
        catch (Exception error) { errors.Add(error); result = MeasurementRunState.Faulted; }
        if (run.DataSet is { } completed)
        {
            var finishedAt = DateTimeOffset.UtcNow < run.StartedAt ? run.StartedAt : DateTimeOffset.UtcNow;
            try { completed.Complete(result, finishedAt, blackboard); }
            catch (Exception error)
            {
                errors.Add(error); result = MeasurementRunState.Faulted; blackboard = null;
                completed.Complete(result, finishedAt);
            }
            var snapshot = completed.Capture();
            foreach (var listener in startedListeners)
            {
                try { await listener.OnFinalizedAsync(snapshot, CancellationToken.None); }
                catch (Exception ex) { errors.Add(ex); result = MeasurementRunState.Faulted; }
            }
        }
        LatestBlackboardSnapshot = blackboard;
        LatestOutcome = new(run.Id, result, Array.AsReadOnly(errors.ToArray()));
        await PublishAsync(run, result switch
        {
            MeasurementRunState.Completed => WorkbenchNotificationKind.RunCompleted,
            MeasurementRunState.Aborted => WorkbenchNotificationKind.RunAborted,
            _ => WorkbenchNotificationKind.RunFaulted,
        }, errors.Count == 0 ? $"{compiled.Plan.Name}: {result}." : string.Join("; ", errors.Select(e => e.Message)), CancellationToken.None);
        if (safetyErrors.Count > 0) run.Phase = RunExecutionPhase.SafetyRecoveryRequired;
        else await ReleaseRunAsync(run);
        run.Finalized.TrySetResult();
        ThrowErrors(errors);
    }

    public Task WaitForFinalizationAsync(CancellationToken token)
    {
        lock (admissionGate) return active?.Finalized.Task.WaitAsync(token) ?? Task.CompletedTask;
    }

    public void Abort() { lock (admissionGate) active?.Abort(); }
    public void Pause()
    {
        lock (admissionGate) (active ?? throw new InvalidOperationException("No run is active.")).Pause();
    }
    public void Resume() { lock (admissionGate) active?.Resume(); }

    public async Task RecoverSafetyAsync(CancellationToken cancellationToken, bool reconnect = false)
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
            if (reconnect) await run.Resources!.ConnectAllAsync(cancellationToken);
            var errors = await run.Resources!.StopAllAsync(cancellationToken, retryFailed: true);
            ThrowErrors(errors.Select(e => e.Error).ToArray());
            await ReleaseRunAsync(run);
        }
        finally { recoveryGate.Release(); }
    }

    private async Task PauseCheckpointAsync(RunExecutionContext run)
    {
        if (run.PauseGate is not { } resume) return;
        var pausedAt = run.Elapsed.Elapsed;
        if (run.Resources is { } resources)
        {
            var saved = await resources.CaptureResumeStateAsync(run.Token);
            ThrowErrors((await resources.StopAllAsync(CancellationToken.None, preserveRouting: true)).Select(e => e.Error).ToArray());
            run.Phase = RunExecutionPhase.Paused;
            await PublishAsync(run, WorkbenchNotificationKind.RunPaused, "Paused with device pause policies confirmed; resources remain reserved.", CancellationToken.None);
            await resume.WaitAsync(run.Token);
            run.Token.ThrowIfCancellationRequested();
            await resources.RestoreResumeStateAsync(saved, run.Token);
        }
        else
        {
            run.Phase = RunExecutionPhase.Paused;
            await PublishAsync(run, WorkbenchNotificationKind.RunPaused, "Plan paused.", CancellationToken.None);
            await resume.WaitAsync(run.Token);
        }
        run.PausedDuration += run.Elapsed.Elapsed - pausedAt;
        run.PauseVersion++;
        run.Phase = RunExecutionPhase.Running;
        await PublishAsync(run, WorkbenchNotificationKind.RunResumed, "Run resumed.", run.Token);
    }

    private async Task DwellAsync(RunExecutionContext run, TimeSpan remaining)
    {
        await PauseCheckpointAsync(run);
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

    private async Task ReleaseRunAsync(RunExecutionContext run)
    {
        if (run.Resources is { } resources) await resources.ReleaseAsync(CancellationToken.None);
        lock (admissionGate)
        {
            if (active != run) throw new InvalidOperationException("Run ownership changed unexpectedly.");
            active = null;
            run.Dispose();
        }
    }

    private static void ThrowErrors(IReadOnlyList<Exception> errors)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Execution or finalization failed.", errors);
    }

    private ValueTask PublishAsync(RunExecutionContext run, WorkbenchNotificationKind kind, string message, CancellationToken token)
        => notifications.PublishAsync(new(kind, message, run.NotificationInstrument, RunId: run.Id.ToString()), token);
}
