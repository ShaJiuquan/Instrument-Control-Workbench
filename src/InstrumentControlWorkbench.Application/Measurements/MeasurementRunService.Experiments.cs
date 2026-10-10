using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task ExecuteStepAsync(ExperimentStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started, bool ivPlot)
    {
        run.Token.ThrowIfCancellationRequested();
        if (++run.ExecutedSteps % 256 == 0)
        {
            // Command-only loops also yield so the desktop can request abort/pause.
            await Task.Yield();
            run.Token.ThrowIfCancellationRequested();
        }
        await PauseCheckpointAsync(run);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.StepStarted, $"Executing {step.Id}.",
            RunId: run.Id.ToString(), Step: step.Id), run.Token);
        switch (step)
        {
            case SampleAnalysisStep analysis:
                await ExecuteSampleAnalysisAsync(analysis, run, started);
                break;
            case SequenceStep sequence:
                foreach (var child in sequence.Children) await ExecuteStepAsync(child, run, started, ivPlot);
                break;
            case RepeatStep repeat:
                for (var i = 0; i < repeat.Cycles; i++) await ExecuteStepAsync(repeat.Body, run, started, ivPlot);
                break;
            case ScanStep scan:
                var previousAxes = new Dictionary<ParameterId, Quantity>(run.Axes);
                try
                {
                    foreach (var value in scan.Values.Values)
                    {
                        run.Token.ThrowIfCancellationRequested();
                        await PauseCheckpointAsync(run);
                        await run.Resources!.WriteAsync(scan.Axis.Device, scan.Axis.Parameter, value, run.Token);
                        run.Variables[scan.Column] = value;
                        run.Axes[scan.Column] = value;
                        await ExecuteStepAsync(scan.Body, run, started, ivPlot);
                        await DwellAsync(run, scan.Dwell);
                    }
                }
                finally { RestoreAxes(run, previousAxes); }
                break;
            case ForEachStep loop:
                var beforeLoop = new Dictionary<ParameterId, Quantity>(run.Axes);
                try
                {
                    var index = new ParameterId($"{loop.Variable}.index");
                    var total = new ParameterId($"{loop.Variable}.total");
                    for (var i = 0; i < loop.Values.Values.Count; i++)
                    {
                        run.Token.ThrowIfCancellationRequested();
                        await PauseCheckpointAsync(run);
                        run.Variables[loop.Variable] = run.Axes[loop.Variable] = loop.Values.Values[i];
                        run.Variables[index] = run.Axes[index] = new(i + 1, PhysicalUnit.Dimensionless);
                        run.Variables[total] = run.Axes[total] = new(loop.Values.Values.Count, PhysicalUnit.Dimensionless);
                        await ExecuteStepAsync(loop.Body, run, started, ivPlot);
                    }
                }
                finally { RestoreAxes(run, beforeLoop); }
                break;
            case SoftwareTemperatureRampStep temperature:
                await ExecuteTemperatureRampAsync(temperature, run);
                break;
            case SetParameterStep set:
                await run.Resources!.WriteAsync(set.Address.Device, set.Address.Parameter, Resolve(set.Value, run), run.Token);
                break;
            case AssignValueStep assign:
                run.Variables[assign.Variable] = Resolve(assign.Value, run);
                break;
            case VerifyMatrixRouteStep verification:
                await run.Resources!.VerifyMatrixRouteAsync(verification.Device, verification.Closed, run.Token);
                break;
            case RouteMatrixStep route:
                await run.Resources!.RouteMatrixAsync(route.Device, route.Closed, run.Token);
                await DwellAsync(run, route.Settle);
                break;
            case SourceOutputStep output:
                if (output.Enabled) await run.Resources!.EnableSourceOutputAsync(output.Device, run.Token,
                    run.DataSet!.Plan!.Preparations.SingleOrDefault(p => p.Device == output.Device)?.Sense.Mode);
                else await run.Resources!.DisableSourceOutputAsync(output.Device, run.Token);
                break;
            case DelayStep delay:
                await DwellAsync(run, delay.Duration);
                break;
            case ReadStep read:
                await AcquireRowAsync(read, run, started);
                break;
            case ReadSourceSenseStep pair:
                await AcquireSourceSenseRowAsync(pair, run, started, ivPlot);
                break;
            case ReadSampleResistanceStep sampleRead:
                var context = run.SampleRoutes[sampleRead.Id];
                if (sampleRead.RoutingManagedExternally)
                    await run.Resources!.VerifyMatrixRouteAsync(context.Matrix, context.Closed, run.Token);
                else
                {
                    await run.Resources!.RouteMatrixAsync(context.Matrix, context.Closed, run.Token);
                    await DwellAsync(run, sampleRead.Settle);
                    await run.Resources.EnableSourceOutputAsync(context.SourceMeter, run.Token, context.SensingMode);
                }
                await DwellAsync(run, sampleRead.AcquisitionSettle);
                await run.Resources!.VerifyMatrixRouteAsync(context.Matrix, context.Closed, run.Token);
                await AcquireResistanceRowAsync(sampleRead.Acquisition, run, started, context);
                if (!sampleRead.RoutingManagedExternally) await run.Resources!.DisableSourceOutputAsync(context.SourceMeter, run.Token);
                break;
            case ReadResistanceStep resistance:
                await AcquireResistanceRowAsync(resistance, run, started);
                break;
            case WaitStableStep wait:
                await WaitStableAsync(wait, run);
                break;
            case IfElseStep conditional:
                var left = run.Variables[conditional.Condition.Variable];
                var right = Resolve(conditional.Condition.Value, run);
                if (left.Unit != right.Unit) throw new InvalidOperationException("Comparison units changed during execution.");
                var selected = Compare(left.Value, right.Value, conditional.Condition.Operator) ? conditional.Then : conditional.Else;
                if (selected is not null) await ExecuteStepAsync(selected, run, started, ivPlot);
                break;
            default: throw new NotSupportedException($"No executor exists for {step.GetType().Name}.");
        }
        await notifications.PublishAsync(new(WorkbenchNotificationKind.StepCompleted, $"Completed {step.Id}.",
            RunId: run.Id.ToString(), Step: step.Id), CancellationToken.None);
    }

    private async Task AcquireRowAsync(ReadStep read, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        var values = new Dictionary<ParameterId, Quantity>(run.Axes);
        foreach (var channel in read.Channels)
        {
            var value = await run.Resources!.ReadAsync(channel.Address.Device, channel.Address.Parameter, run.Token);
            values[channel.Column] = value;
            run.Variables[channel.Column] = value;
        }
        var now = DateTimeOffset.UtcNow;
        values[ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second);
        var row = new MeasurementDataRow(now, values);
        await CommitRowAsync(row, read.Id, run, started);
    }

    private async Task CommitRowAsync(MeasurementDataRow row, StepId step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        run.DataSet!.Append(row); run.RowsAcquired++;
        // Commit to local history first even if an external listener cancels.
        foreach (var listener in started)
            await listener.OnRowAsync(run.Id, row, listener == History ? CancellationToken.None : run.Token);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DataRowReceived,
            $"Acquired row {run.RowsAcquired}.", RunId: run.Id.ToString(), Step: step, Row: row), CancellationToken.None);
    }

    private async Task AcquireSourceSenseRowAsync(ReadSourceSenseStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started, bool ivPlot)
    {
        var mode = run.DataSet!.Plan!.Preparations.Single(p => p.Device == step.Device).Sense.Mode;
        var frame = await run.Resources!.ReadSourceSenseAsync(step.Device, step.Function, run.Token, mode);
        var voltage = step.Function == SourceFunction.Voltage ? frame.Source : frame.Sense;
        var current = step.Function == SourceFunction.Current ? frame.Source : frame.Sense;
        var values = new Dictionary<ParameterId, Quantity>(run.Axes)
        {
            [step.VoltageColumn] = voltage,
            [step.CurrentColumn] = current,
            [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second),
        };
        run.Variables[step.VoltageColumn] = voltage; run.Variables[step.CurrentColumn] = current;
        await CommitRowAsync(new(frame.Timestamp, values), step.Id, run, started);
        if (ivPlot)
        {
            var point = new MeasurementPoint(frame.Timestamp, run.Elapsed.Elapsed, voltage.Value, current.Value, "V", "A");
            await notifications.PublishAsync(new(WorkbenchNotificationKind.MeasurementPointReceived,
                $"Point {voltage.Value:0.###} V -> {current.Value:0.###E+0} A", step.Device, Point: point, RunId: run.Id.ToString()), CancellationToken.None);
        }
    }

    private async Task AcquireResistanceRowAsync(ReadResistanceStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started, ResolvedSampleRoute? context = null)
    {
        var reading = await run.Resources!.ReadSourceSenseAsync(step.Device, step.Function, run.Token, context?.SensingMode);
        var voltage = step.Function == SourceFunction.Voltage ? reading.Source : reading.Sense;
        var current = step.Function == SourceFunction.Current ? reading.Source : reading.Sense;
        if (Math.Abs(current.Value) <= step.MinimumCurrent.Value)
            throw new InvalidOperationException($"{step.Id}: measured current is zero or below the required threshold; resistance is undefined.");
        var values = new Dictionary<ParameterId, Quantity>(run.Axes)
        {
            [step.VoltageColumn] = voltage,
            [step.CurrentColumn] = current,
            [step.ResistanceColumn] = new(voltage.Value / current.Value, PhysicalUnit.Ohm),
            [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second),
        };
        var row = new MeasurementDataRow(reading.Timestamp, values, context);
        run.Variables[step.VoltageColumn] = voltage; run.Variables[step.CurrentColumn] = current;
        run.Variables[step.ResistanceColumn] = values[step.ResistanceColumn];
        await CommitRowAsync(row, step.Id, run, started);
    }

    private async Task WaitStableAsync(WaitStableStep wait, RunExecutionContext run)
    {
        var target = Resolve(wait.Target, run);
        var startedAt = run.ActiveElapsed;
        TimeSpan? stableSince = null;
        var pauseVersion = run.PauseVersion;
        while (true)
        {
            run.Token.ThrowIfCancellationRequested();
            await PauseCheckpointAsync(run);
            if (pauseVersion != run.PauseVersion) { stableSince = null; pauseVersion = run.PauseVersion; }
            if (run.ActiveElapsed - startedAt > wait.Timeout)
                throw new TimeoutException($"{wait.Id}: {wait.Readback.Parameter} failed to stabilize before the timeout.");
            var value = await run.Resources!.ReadAsync(wait.Readback.Device, wait.Readback.Parameter, run.Token);
            var now = run.ActiveElapsed;
            if (value.Unit != target.Unit) throw new InvalidOperationException("Stability readback has an unexpected unit.");
            if (Math.Abs(value.Value - target.Value) <= wait.Tolerance.Value)
            {
                stableSince ??= now;
                if (now - stableSince >= wait.StableFor && now - startedAt <= wait.Timeout) return;
            }
            else stableSince = null;
            var remaining = wait.Timeout - (now - startedAt);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException($"{wait.Id}: stability timeout.");
            await DwellAsync(run, remaining < wait.PollInterval ? remaining : wait.PollInterval);
        }
    }

    private static Quantity Resolve(PlanValue value, RunExecutionContext run) => value switch
    {
        ConstantValue constant => constant.Value,
        VariableValue variable when run.Variables.TryGetValue(variable.Variable, out var resolved) => resolved,
        _ => throw new InvalidOperationException("A plan variable has no value on this execution path."),
    };

    private static bool Compare(double left, double right, ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => left == right,
        ComparisonOperator.NotEqual => left != right,
        ComparisonOperator.Less => left < right,
        ComparisonOperator.LessOrEqual => left <= right,
        ComparisonOperator.Greater => left > right,
        ComparisonOperator.GreaterOrEqual => left >= right,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    private static void RestoreAxes(RunExecutionContext run, Dictionary<ParameterId, Quantity> previous)
    {
        run.Axes.Clear();
        foreach (var pair in previous) run.Axes.Add(pair.Key, pair.Value);
    }
}
