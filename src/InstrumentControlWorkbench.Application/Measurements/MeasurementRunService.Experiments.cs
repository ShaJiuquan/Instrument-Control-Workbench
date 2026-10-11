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
    private async Task<StepOutcome> ExecuteStepAsync(ExperimentStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started, bool ivPlot)
    {
        run.Token.ThrowIfCancellationRequested();
        AdvancePlanExecution(run, step.Id);
        if (run.ExecutedSteps % 256 == 0)
        {
            // Command-only loops also yield so the desktop can request abort/pause.
            await Task.Yield();
            run.Token.ThrowIfCancellationRequested();
        }
        await PauseCheckpointAsync(run);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.StepStarted, $"Executing {step.Id}.",
            RunId: run.Id.ToString(), Step: step.Id), run.Token);
        var outcome = StepOutcome.Success;
        switch (step)
        {
            case ConditionStep condition:
                outcome = EvaluateCondition(condition.Condition, run) ? StepOutcome.Success : StepOutcome.Failure; break;
            case MutateValueStep mutation:
                var current = run.Variables.ContainsKey(mutation.Variable) ? Resolve(new VariableValue(mutation.Variable), run) : new Quantity(0, mutation.Action == VariableMutation.Multiply ? PhysicalUnit.Dimensionless : mutation.Value.Unit);
                if (mutation.Action != VariableMutation.Multiply && current.Unit != mutation.Value.Unit) throw new InvalidOperationException("Mutation units changed during execution.");
                var mutated = mutation.Action switch
                {
                    VariableMutation.Set => mutation.Value.Value,
                    VariableMutation.Add => current.Value + mutation.Value.Value,
                    VariableMutation.Subtract => current.Value - mutation.Value.Value,
                    VariableMutation.Multiply => current.Value * mutation.Value.Value,
                    _ => throw new NotSupportedException("Unknown variable mutation."),
                };
                run.SetVariable(mutation.Variable, new Quantity(mutated, current.Unit), mutation.Id); break;
            case OutcomeMutationStep hook:
                outcome = await ExecuteStepAsync(hook.Body, run, started, ivPlot);
                var action = outcome == StepOutcome.Success ? hook.Success : hook.Failure;
                if (action is not null) _ = await ExecuteStepAsync(action, run, started, ivPlot);
                break;
            case WhileStep loop:
                var beforeWhile = new Dictionary<ParameterId, Quantity>(run.Axes); long iteration = 0;
                try
                {
                    while (true)
                    {
                        run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                        if (!EvaluateCondition(loop.Condition, run)) break;
                        run.Axes[loop.IterationColumn] = new(++iteration, PhysicalUnit.Dimensionless); run.SetVariable(loop.IterationColumn, run.Axes[loop.IterationColumn], loop.Id);
                        if ((outcome = await ExecuteStepAsync(loop.Body, run, started, ivPlot)) == StepOutcome.Failure) break;
                    }
                }
                finally { RestoreAxes(run, beforeWhile); }
                break;
            case FallbackStep fallback:
                outcome = await ExecuteFallbackAsync(fallback, run, started, ivPlot); break;
            case RetryStep retry:
                outcome = await ExecuteRetryAsync(retry, run, started, ivPlot); break;
            case TimeoutStep timeout:
                outcome = await ExecuteTimeoutAsync(timeout, run, started, ivPlot); break;
            case SubtreeStep subtree:
                outcome = await ExecuteSubtreeAsync(subtree, run, started, ivPlot); break;
            case BranchOnOutcomeStep branch:
                var selectedBranch = await ExecuteStepAsync(branch.Condition, run, started, ivPlot) == StepOutcome.Success ? branch.Then : branch.Else;
                if (selectedBranch is not null) outcome = await ExecuteStepAsync(selectedBranch, run, started, ivPlot);
                break;
            case ResultDecoratorStep decorator:
                var childOutcome = await ExecuteStepAsync(decorator.Body, run, started, ivPlot);
                outcome = decorator.Effect switch
                {
                    ResultEffect.ForceSuccess => StepOutcome.Success,
                    ResultEffect.ForceFailure => StepOutcome.Failure,
                    ResultEffect.Invert => childOutcome == StepOutcome.Success ? StepOutcome.Failure : StepOutcome.Success,
                    _ => throw new NotSupportedException("Unknown result effect."),
                };
                break;
            case MeasurementGroupStep point:
                await ExecuteMeasurementGroupAsync(point, run, started, ivPlot); break;
            case SampleAnalysisStep analysis:
                await ExecuteSampleAnalysisAsync(analysis, run, started);
                break;
            case SequenceStep sequence:
                foreach (var child in sequence.Children)
                    if ((outcome = await ExecuteStepAsync(child, run, started, ivPlot)) == StepOutcome.Failure) break;
                break;
            case RepeatStep repeat:
                for (var i = 0; i < repeat.Cycles; i++)
                    if ((outcome = await ExecuteStepAsync(repeat.Body, run, started, ivPlot)) == StepOutcome.Failure) break;
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
                        run.SetVariable(scan.Column, value, scan.Id);
                        run.Axes[scan.Column] = value;
                        if ((outcome = await ExecuteStepAsync(scan.Body, run, started, ivPlot)) == StepOutcome.Failure) break;
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
                        if (loop.Timing == IterationTiming.DelayBeforePoint)
                            await DwellAsync(run, TimeSpan.FromSeconds(loop.Values.Values[i].Value));
                        run.Token.ThrowIfCancellationRequested();
                        await PauseCheckpointAsync(run);
                        run.Axes[loop.Variable] = loop.Values.Values[i]; run.SetVariable(loop.Variable, run.Axes[loop.Variable], loop.Id);
                        run.Axes[index] = new(i + (int)loop.IndexBase, PhysicalUnit.Dimensionless); run.SetVariable(index, run.Axes[index], loop.Id);
                        run.Axes[total] = new(loop.Values.Values.Count, PhysicalUnit.Dimensionless); run.SetVariable(total, run.Axes[total], loop.Id);
                        if ((outcome = await ExecuteStepAsync(loop.Body, run, started, ivPlot)) == StepOutcome.Failure) break;
                    }
                }
                finally { RestoreAxes(run, beforeLoop); }
                break;
            case HardwareTemperatureSetpointStep hardwareTemperature:
                await run.Resources!.SetHardwareTemperatureTargetAsync(hardwareTemperature.Device, Resolve(hardwareTemperature.Target, run), hardwareTemperature.Rate, hardwareTemperature.EnableControl, run.Token);
                break;
            case SoftwareTemperatureRampStep temperature:
                await ExecuteTemperatureRampAsync(temperature, run);
                break;
            case TemperatureStabilityStep settings:
                await run.Resources!.ConfigureTemperatureStabilityAsync(settings.Device, settings.Change, run.Token);
                break;
            case WaitTemperatureStep temperatureWait:
                await WaitTemperatureAsync(temperatureWait, run);
                break;
            case TemperatureControlStep command:
                await run.Resources!.ConfigureTemperatureAsync(command.Device, command.Action, command.Rate, run.Token);
                break;
            case SetParameterStep set:
                await run.Resources!.WriteAsync(set.Address.Device, set.Address.Parameter, Resolve(set.Value, run), run.Token);
                break;
            case AssignValueStep assign:
                run.SetVariable(assign.Variable, ResolveValue(assign.Value, run), assign.Id);
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
                    await run.Resources.EnableSourceOutputAsync(context.SourceMeter, run.Token, sampleRead.Acquisition is ReadResistanceStep ? context.SensingMode : null);
                }
                await DwellAsync(run, sampleRead.AcquisitionSettle);
                await run.Resources!.VerifyMatrixRouteAsync(context.Matrix, context.Closed, run.Token);
                if (sampleRead.Acquisition is ReadCurrentResistanceStep sampleCurrent)
                    await AcquireCurrentResistanceRowAsync(sampleCurrent, run, started, context);
                else await AcquireResistanceRowAsync((ReadResistanceStep)sampleRead.Acquisition, run, started, context);
                if (!sampleRead.RoutingManagedExternally) await run.Resources!.DisableSourceOutputAsync(context.SourceMeter, run.Token);
                break;
            case ReadCurrentResistanceStep currentResistance:
                await AcquireCurrentResistanceRowAsync(currentResistance, run, started);
                break;
            case ReadResistanceStep resistance:
                await AcquireResistanceRowAsync(resistance, run, started);
                break;
            case WaitStableStep wait:
                await WaitStableAsync(wait, run);
                break;
            case ThresholdSwitchStep choice:
                if (choice.Children.Count != 0)
                {
                    if (ResolveValue(new VariableValue(choice.Variable), run) is not BlackboardQuantity selector)
                        throw new InvalidOperationException("Switch selector is no longer numeric.");
                    outcome = await ExecuteStepAsync(choice.Children[choice.SelectBranch(selector.Value)!.Value], run, started, ivPlot);
                }
                break;
            case IfElseStep conditional:
                var selected = EvaluateCondition(conditional.Condition, run) ? conditional.Then : conditional.Else;
                if (selected is not null) outcome = await ExecuteStepAsync(selected, run, started, ivPlot);
                break;
            default: throw new NotSupportedException($"No executor exists for {step.GetType().Name}.");
        }
        if (outcome == StepOutcome.Failure && step is ConditionStep or ResultDecoratorStep) run.LastWorkflowFailure = step.Id;
        run.Token.ThrowIfCancellationRequested();
        await notifications.PublishAsync(new(WorkbenchNotificationKind.StepCompleted, $"{step.Id}: {outcome}.",
            RunId: run.Id.ToString(), Step: step.Id, Outcome: outcome), CancellationToken.None);
        return outcome;
    }

    private static bool EvaluateCondition(ValueCondition condition, RunExecutionContext run)
    {
        var left = ResolveValue(new VariableValue(condition.Variable), run); var right = ResolveValue(condition.Value, run);
        if (left.Type != right.Type) throw new InvalidOperationException("Comparison types or units changed during execution.");
        if (left is not BlackboardQuantity numeric)
        {
            if (condition.EqualityTolerance is not null || condition.Operator is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual))
                throw new InvalidOperationException("Boolean and text conditions require equality without a tolerance.");
            var equal = left == right; // Record equality: strings use ordinal, case-sensitive comparison.
            return condition.Operator == ComparisonOperator.Equal ? equal : !equal;
        }
        var other = ((BlackboardQuantity)right).Value;
        if (condition.EqualityTolerance is { } tolerance)
        {
            if (tolerance.Value <= 0 || tolerance.Unit != numeric.Value.Unit || condition.Operator is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual))
                throw new InvalidOperationException("Equality tolerance changed during execution.");
            var equal = Math.Abs(numeric.Value.Value - other.Value) < tolerance.Value;
            return condition.Operator == ComparisonOperator.Equal ? equal : !equal;
        }
        return Compare(numeric.Value.Value, other.Value, condition.Operator);
    }

    private static void AdvancePlanExecution(RunExecutionContext run, StepId step)
    {
        var maximum = run.DataSet!.Plan!.MaximumStepExecutions;
        if (run.ExecutedSteps >= maximum) throw new PlanExecutionBudgetException(step, maximum);
        run.ExecutedSteps++;
    }

    private async Task AcquireRowAsync(ReadStep read, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        var values = new Dictionary<ParameterId, Quantity>(run.Axes);
        var quality = new List<AcquisitionQuality>();
        foreach (var channel in read.Channels)
        {
            var observation = await run.Resources!.ReadObservationAsync(channel.Address.Device, channel.Address.Parameter, run.Token);
            var value = observation.Value;
            quality.Add(new(read.Id, channel.Address.Device, channel.Column, observation.CapturedAt, observation.Status, observation.AcquisitionConfiguration, observation.MeterConfiguration));
            values[channel.Column] = value;
            run.SetVariable(channel.Column, value, read.Id);
        }
        var environment = await ReadEnvironmentAsync(read.EnvironmentReads, values, run);
        var now = DateTimeOffset.UtcNow;
        values[ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second);
        var row = new MeasurementDataRow(now, values, environment: environment, quality: quality);
        await CommitRowAsync(row, read.Id, run, started);
    }

    private async Task CommitRowAsync(MeasurementDataRow row, StepId step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        run.DataSet!.Append(row); run.RowsAcquired++;
        run.LastRow = row;
        run.MeasurementInputs?.Add(row);
        // Commit to local history first even if an external listener cancels.
        foreach (var listener in started)
            await listener.OnRowAsync(run.Id, row, listener == History ? CancellationToken.None : run.Token);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DataRowReceived,
            $"Acquired row {run.RowsAcquired}; quality {row.QualityState} ({row.QualityIssues}); observed column evidence {row.ObservedQualityColumns}/{row.Quality.Count}.", RunId: run.Id.ToString(), Step: step, Row: row), CancellationToken.None);
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
        run.SetVariable(step.VoltageColumn, voltage, step.Id); run.SetVariable(step.CurrentColumn, current, step.Id);
        var environment = await ReadEnvironmentAsync(step.EnvironmentReads, values, run);
        await CommitRowAsync(new(frame.Timestamp, values, environment: environment, quality: new[] { step.VoltageColumn, step.CurrentColumn }.Select(c => new AcquisitionQuality(step.Id, step.Device, c, frame.Timestamp, frame.Status, frame.AcquisitionConfiguration))), step.Id, run, started);
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
        var environment = await ReadEnvironmentAsync(step.EnvironmentReads, values, run);
        var row = new MeasurementDataRow(reading.Timestamp, values, context, environment: environment, quality: new[] { step.VoltageColumn, step.CurrentColumn, step.ResistanceColumn }.Select(c => new AcquisitionQuality(step.Id, step.Device, c, reading.Timestamp, reading.Status, reading.AcquisitionConfiguration)));
        run.SetVariable(step.VoltageColumn, voltage, step.Id); run.SetVariable(step.CurrentColumn, current, step.Id);
        run.SetVariable(step.ResistanceColumn, values[step.ResistanceColumn], step.Id);
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

    private static BlackboardValue ResolveValue(PlanValue value, RunExecutionContext run) => value switch
    {
        ConstantValue constant => new BlackboardQuantity(constant.Value),
        BooleanValue boolean => new BlackboardBoolean(boolean.Value),
        TextValue text => new BlackboardText(text.Value),
        VariableValue variable when run.Variables.TryGetValue(variable.Variable, out var entry) => entry.Value,
        QuantityFromNumberValue qualified when run.Variables.TryGetValue(qualified.Variable, out var entry) && entry.Value is BlackboardQuantity { Value.Unit: PhysicalUnit.Dimensionless } number => new BlackboardQuantity(new(number.Value.Value, qualified.Unit)),
        _ => throw new InvalidOperationException("A plan variable has no valid value on this execution path."),
    };
    private static Quantity Resolve(PlanValue value, RunExecutionContext run)
        => ResolveValue(value, run) is BlackboardQuantity number ? number.Value : throw new InvalidOperationException("A physical operand must be a quantity.");

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
