using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed record ExperimentPlanPreview(DataSetSchema Schema, IReadOnlyList<InstrumentId> Devices, long MaximumStepExecutions, bool HasDynamicLoops = false);
internal sealed record CompiledExperiment(ExperimentPlan Plan, DataSetSchema Schema, IReadOnlyList<InstrumentId> Devices, long MaximumStepExecutions, IReadOnlyDictionary<StepId, ResolvedSampleRoute> SampleRoutes, bool HasDynamicLoops);

// Compilation reads capability metadata only. No lease or driver operation occurs
// until every branch, scan value, unit and result dependency has been checked.
internal static class ExperimentPlanCompiler
{
    public static readonly ParameterDescriptor Elapsed = new(new("elapsed"), "Elapsed time", PhysicalUnit.Second,
        ParameterAccess.Read, new(0, PhysicalUnit.Second));

    public static CompiledExperiment Compile(ExperimentPlan plan, DeviceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var devices = registry.Devices.ToDictionary(d => d.Id);
        var needed = new HashSet<InstrumentId>();
        var columns = new Dictionary<ParameterId, DataColumn> { [Elapsed.Id] = new(Elapsed) };
        var environmentColumns = new HashSet<ParameterId>();
        var environmentPolicies = new Dictionary<ParameterId, IReadOnlyList<EnvironmentRead>>();
        var steps = new HashSet<StepId>();
        var variables = new Dictionary<ParameterId, PhysicalUnit>();
        var preparations = plan.Preparations.ToDictionary(p => p.Device);
        var currentPreparations = plan.CurrentSourcePreparations.ToDictionary(p => p.Device);
        var sampleRoutes = new Dictionary<StepId, ResolvedSampleRoute>();
        var writes = new List<(ParameterAddress Address, IReadOnlyList<Quantity>? Values)>();
        var hasDynamicLoops = false;
        foreach (var preparation in plan.Preparations)
        {
            var device = Source(preparation.Device);
            Keithley2450Limits.ValidateConfiguration(preparation.ToSetpoint(), preparation.Sense);
            var voltage = preparation.Function == SourceFunction.Voltage;
            device.GetParameter(voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id).ValidateWrite(preparation.Level);
            device.GetParameter(voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id).ValidateWrite(preparation.Compliance);
            device.GetParameter(Keithley2450Parameters.Nplc.Id).ValidateWrite(new(preparation.Sense.Nplc, PhysicalUnit.Dimensionless));
            if (preparation.Sense.Acquisition is { } acquisition)
                foreach (var parameter in acquisition.CaptureParameters(preparation.Sense.Function)) device.GetParameter(parameter.Descriptor.Id).ValidateWrite(parameter.Value);
            if (preparation.Sense.ManualRange is { } preparedSenseRange) device.GetParameter(Keithley2450Parameters.ManualSenseRange(preparation.Sense.Function).Id).ValidateWrite(preparedSenseRange);
            if (preparation.ManualSourceRange is { } preparedRange)
                device.GetParameter(voltage ? Keithley2450Parameters.ManualVoltageRange.Id : Keithley2450Parameters.ManualCurrentRange.Id).ValidateWrite(preparedRange);
        }
        foreach (var preparation in plan.CurrentSourcePreparations)
        {
            var device = Device(preparation.Device);
            if (device.Model != "K6221" || device.StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Pure current-source preparation requires its K6221 DC profile.");
            device.GetParameter(Keithley6221Parameters.Current.Id).ValidateWrite(preparation.Settings.Current);
            device.GetParameter(Keithley6221Parameters.Compliance.Id).ValidateWrite(preparation.Settings.Compliance);
            if (preparation.Settings.SourceDelay is { } delay) device.GetParameter(Keithley6221Parameters.SourceDelay.Id).ValidateWrite(delay);
        }
        var cost = Visit(plan.Root, variables, [], 0);
        if (cost > plan.MaximumStepExecutions) throw new ArgumentOutOfRangeException(nameof(plan), "Plan exceeds its step execution budget.");
        // Check coupled ranges statically only when the counterpart is fixed.
        // Plans that change both source fields are checked against actual device
        // configuration at each write, rather than rejected using stale preparation.
        var changedAddresses = writes.Select(w => w.Address).ToHashSet();
        foreach (var write in writes)
        {
            if (Device(write.Address.Device).Model == "K6221" && write.Values is { } currentValues)
            {
                foreach (var value in currentValues) Keithley6221Parameters.ValidateWrite(write.Address.Parameter, value);
                if (currentPreparations.TryGetValue(write.Address.Device, out var preparedCurrent) && preparedCurrent.Settings.ManualRange is not null &&
                    !changedAddresses.Any(a => a.Device == write.Address.Device && (a.Parameter == Keithley6221Parameters.ManualRange.Id || a.Parameter == Keithley6221Parameters.EnableAutoRange.Id)) && write.Address.Parameter == Keithley6221Parameters.Current.Id)
                    foreach (var value in currentValues) _ = new Keithley6221Settings(value, preparedCurrent.Settings.Compliance, preparedCurrent.Settings.ManualRange);
            }
            if (write.Values is { } meterValues && Keithley2182Parameters.IsParameter(write.Address.Parameter))
                foreach (var value in meterValues) Keithley2182Parameters.ValidateWrite(write.Address.Parameter, value);
            if (write.Values is { } functionValues && write.Address.Parameter == Keithley2450Parameters.ActiveSenseFunction.Id && functionValues.Any(v => v.Value != Math.Truncate(v.Value))) throw new ArgumentException("Sense function requires an integral enum code.");
            if (write.Values is { } acquisitionValues && Keithley2450AcquisitionSettings.IsParameter(write.Address.Parameter))
                foreach (var value in acquisitionValues) Keithley2450AcquisitionSettings.ValidateParameter(write.Address.Parameter, value);
            if (write.Values is { } senseValues && (write.Address.Parameter == Keithley2450Parameters.ManualSenseVoltageRange.Id || write.Address.Parameter == Keithley2450Parameters.ManualSenseCurrentRange.Id))
            {
                var senseFunction = write.Address.Parameter == Keithley2450Parameters.ManualSenseVoltageRange.Id ? SenseFunction.Voltage : SenseFunction.Current;
                foreach (var value in senseValues) Keithley2450SenseRanges.Validate(senseFunction, value);
                if (preparations.TryGetValue(write.Address.Device, out var sensePreparation))
                {
                    if (sensePreparation.Sense.Function != senseFunction) throw new ArgumentException("Manual sense range differs from the prepared sensing function.");
                    var compliance = sensePreparation.Function == SourceFunction.Voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id;
                    var level = sensePreparation.Function == SourceFunction.Voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id;
                    var sourceRange = sensePreparation.Function == SourceFunction.Voltage ? Keithley2450Parameters.ManualVoltageRange.Id : Keithley2450Parameters.ManualCurrentRange.Id;
                    if (senseFunction != (sensePreparation.Function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage))
                        throw new NotSupportedException("Independent manual sense ranges require complementary sourcing and sensing.");
                    var sourceRangeChanges = changedAddresses.Contains(new(write.Address.Device, sourceRange)) || changedAddresses.Contains(new(write.Address.Device, Keithley2450Parameters.EnableSourceAutoRange.Id));
                    if (!changedAddresses.Contains(new(write.Address.Device, compliance)))
                        foreach (var value in senseValues)
                        {
                            var requestedSense = sensePreparation.Sense with { AutoRange = false, ManualRange = value };
                            var baseline = sensePreparation.ToSetpoint() with { Level = 0, ManualRange = sourceRangeChanges ? null : sensePreparation.ManualSourceRange };
                            Keithley2450Limits.ValidateConfiguration(baseline, requestedSense);
                            if (!sourceRangeChanges && !changedAddresses.Contains(new(write.Address.Device, level)))
                                Keithley2450Limits.ValidateConfiguration(sensePreparation.ToSetpoint(), requestedSense);
                        }
                }
            }
            if (write.Values is { } rangeValues && (write.Address.Parameter == Keithley2450Parameters.ManualVoltageRange.Id || write.Address.Parameter == Keithley2450Parameters.ManualCurrentRange.Id))
            {
                var rangeFunction = write.Address.Parameter == Keithley2450Parameters.ManualVoltageRange.Id ? SourceFunction.Voltage : SourceFunction.Current;
                if (preparations.TryGetValue(write.Address.Device, out var rangePreparation) && rangePreparation.Function != rangeFunction)
                    throw new ArgumentException("Manual range parameter differs from the prepared source function.");
                foreach (var rangeValue in rangeValues) Keithley2450SourceRanges.Validate(rangeFunction, rangeValue);
                if (rangePreparation is not null)
                {
                    var levelParameter = rangeFunction == SourceFunction.Voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id;
                    var complianceParameter = rangeFunction == SourceFunction.Voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id;
                    if (!changedAddresses.Contains(new(write.Address.Device, levelParameter)) && !changedAddresses.Contains(new(write.Address.Device, complianceParameter)))
                        foreach (var rangeValue in rangeValues) Keithley2450Limits.Validate(rangePreparation.ToSetpoint() with { ManualRange = rangeValue });
                }
            }
            if (!preparations.TryGetValue(write.Address.Device, out var preparation) || write.Values is null) continue;
            var voltage = preparation.Function == SourceFunction.Voltage;
            var levelId = voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id;
            var complianceId = voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id;
            var levelWrite = write.Address.Parameter == levelId;
            if (!levelWrite && write.Address.Parameter != complianceId) continue;
            var counterpart = levelWrite ? complianceId : levelId;
            var rangeId = voltage ? Keithley2450Parameters.ManualVoltageRange.Id : Keithley2450Parameters.ManualCurrentRange.Id;
            var rangeChanged = changedAddresses.Contains(new(write.Address.Device, rangeId)) || changedAddresses.Contains(new(write.Address.Device, Keithley2450Parameters.EnableSourceAutoRange.Id));
            if (!rangeChanged && preparation.ManualSourceRange is { } fixedRange)
                foreach (var value in write.Values)
                    if (levelWrite) Keithley2450SourceRanges.ValidateLevel(preparation.Function, value.Value, fixedRange);
                    else Keithley2450Limits.Validate(new SourceSetpoint(preparation.Function, 0, value.Value, fixedRange));
            if (preparation.Sense.ManualRange is not null &&
                !changedAddresses.Contains(new(write.Address.Device, Keithley2450Parameters.ManualSenseRange(preparation.Sense.Function).Id)) &&
                !changedAddresses.Contains(new(write.Address.Device, Keithley2450Parameters.EnableSenseAutoRange.Id)))
                foreach (var value in write.Values)
                {
                    var baseline = rangeChanged ? preparation.ToSetpoint() with { ManualRange = null } : preparation.ToSetpoint();
                    Keithley2450Limits.ValidateConfiguration(levelWrite ? baseline with { Level = value.Value } : baseline with { Compliance = value.Value }, preparation.Sense);
                }
            if (changedAddresses.Contains(new(write.Address.Device, counterpart))) continue;
            foreach (var value in write.Values)
            {
                var baseline = rangeChanged ? preparation.ToSetpoint() with { ManualRange = null } : preparation.ToSetpoint();
                Keithley2450Limits.Validate(levelWrite ? baseline with { Level = value.Value } : baseline with { Compliance = value.Value });
            }
        }
        return new(plan, new(columns.Values), Array.AsReadOnly(needed.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()), hasDynamicLoops ? plan.MaximumStepExecutions : cost, new ReadOnlyDictionary<StepId, ResolvedSampleRoute>(sampleRoutes), hasDynamicLoops);

        DeviceDescriptor Device(InstrumentId id)
        {
            if (!devices.TryGetValue(id, out var device)) throw new KeyNotFoundException($"Unknown device {id}.");
            needed.Add(id);
            return device;
        }
        DeviceDescriptor Source(InstrumentId id)
        {
            var device = Device(id);
            if (device.Model != "K2450" || device.StopPolicy != DeviceStopPolicy.OutputOff)
                throw new NotSupportedException($"{id} has no implemented source-meter execution profile.");
            return device;
        }
        ParameterDescriptor Parameter(ParameterAddress address, ParameterAccess access)
        {
            ArgumentNullException.ThrowIfNull(address);
            var parameter = Device(address.Device).GetParameter(address.Parameter);
            if (!parameter.Access.HasFlag(access)) throw new InvalidOperationException($"{address.Parameter} does not allow {access}.");
            return parameter;
        }
        PhysicalUnit ValueUnit(PlanValue value, Dictionary<ParameterId, PhysicalUnit> known)
        {
            ArgumentNullException.ThrowIfNull(value);
            return value switch
            {
                ConstantValue constant => constant.Value.Unit,
                VariableValue variable when variable.Variable is not null && known.TryGetValue(variable.Variable, out var unit) => unit,
                VariableValue variable => throw new ArgumentException($"Variable {variable.Variable} is not defined on every path before use."),
                QuantityFromNumberValue qualified when known.TryGetValue(qualified.Variable, out var inputUnit) && inputUnit == PhysicalUnit.Dimensionless => qualified.Unit,
                QuantityFromNumberValue qualified => throw new ArgumentException($"Numeric input {qualified.Variable} must be defined and dimensionless before unit qualification."),
                _ => throw new NotSupportedException($"Unsupported value expression {value.GetType().Name}."),
            };
        }
        void BindVariable(ParameterId id, PhysicalUnit unit, Dictionary<ParameterId, PhysicalUnit> known)
        {
            ArgumentNullException.ThrowIfNull(id);
            if (known.TryGetValue(id, out var existing) && existing != unit) throw new ArgumentException($"Variable {id} cannot change units.");
            known[id] = unit;
        }
        void AddColumn(ParameterId id, ParameterDescriptor parameter, ParameterAddress? source, IReadOnlyList<ParameterId> dependencies, bool environment = false)
        {
            if (id == Elapsed.Id) throw new ArgumentException("The elapsed column is reserved.");
            var descriptor = new ParameterDescriptor(id, parameter.Label, parameter.Unit, ParameterAccess.Read, parameter.Minimum, parameter.Maximum);
            if (columns.TryGetValue(id, out var previous))
            {
                if (environmentColumns.Contains(id) != environment) throw new ArgumentException("Environment and primary acquisition columns must remain distinct.");
                if (previous.Parameter != descriptor || previous.Source != source || !previous.DependsOn.SequenceEqual(dependencies))
                    throw new ArgumentException($"Column {id} has inconsistent sources or scan dependencies; use a distinct column ID.");
                return;
            }
            columns.Add(id, new(descriptor, dependencies, optional: true, source: source));
            if (environment) environmentColumns.Add(id);
        }
        bool CanReportWorkflowFailure(ExperimentStep step) => step switch
        {
            ConditionStep => true,
            ResultDecoratorStep { Effect: ResultEffect.ForceSuccess } => false,
            ResultDecoratorStep => true,
            FallbackStep fallback => fallback.Children.All(CanReportWorkflowFailure),
            RetryStep retry => CanReportWorkflowFailure(retry.Body),
            TimeoutStep timeout => CanReportWorkflowFailure(timeout.Body),
            SubtreeStep subtree => subtree.Children.Any(CanReportWorkflowFailure),
            WhileStep loop => CanReportWorkflowFailure(loop.Body),
            OutcomeMutationStep hook => CanReportWorkflowFailure(hook.Body),
            BranchOnOutcomeStep branch => CanReportWorkflowFailure(branch.Then) || branch.Else is { } otherBranch && CanReportWorkflowFailure(otherBranch),
            SequenceStep sequence => sequence.Children.Any(CanReportWorkflowFailure),
            RepeatStep repeat => CanReportWorkflowFailure(repeat.Body),
            ScanStep scan => CanReportWorkflowFailure(scan.Body),
            ForEachStep loop => CanReportWorkflowFailure(loop.Body),
            IfElseStep conditional => CanReportWorkflowFailure(conditional.Then) || conditional.Else is { } other && CanReportWorkflowFailure(other),
            _ => false, // Validated IO/acquisition leaves either succeed or throw; controls cannot swallow those errors.
        };
        void RetainCommonVariables(Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<Dictionary<ParameterId, PhysicalUnit>> paths)
        {
            var common = paths[0].Where(item => paths.All(path => path.TryGetValue(item.Key, out var unit) && unit == item.Value)).ToArray();
            known.Clear(); foreach (var item in common) BindVariable(item.Key, item.Value, known);
        }
        void CheckCondition(ValueCondition condition, Dictionary<ParameterId, PhysicalUnit> known)
        {
            ArgumentNullException.ThrowIfNull(condition);
            if (!Enum.IsDefined(condition.Operator)) throw new ArgumentOutOfRangeException(nameof(condition.Operator));
            var left = ValueUnit(new VariableValue(condition.Variable), known);
            if (left != ValueUnit(condition.Value, known)) throw new ArgumentException("Comparison operands must have matching units.");
            if (condition.EqualityTolerance is { } equalityTolerance && (equalityTolerance.Value <= 0 || equalityTolerance.Unit != left || condition.Operator is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual)))
                throw new ArgumentException("Equality tolerance requires positive matching units and an equality operator.");
        }
        void ValidateSourceSense(InstrumentId device, SourceFunction function, ParameterId voltageColumn, ParameterId currentColumn,
            Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<ParameterId> axes)
        {
            var sourceDevice = Source(device);
            if (!preparations.TryGetValue(device, out var preparation) || preparation.Function != function ||
                preparation.Sense.Function != (function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage))
                throw new ArgumentException("Paired reads need an explicit preparation with matching source and opposite sensing function.");
            AddColumn(voltageColumn, new(voltageColumn,
                function == SourceFunction.Voltage ? "Actual source voltage" : "Measured voltage", PhysicalUnit.Volt, ParameterAccess.Read),
                new(device, sourceDevice.GetParameter(Keithley2450Parameters.MeasuredVoltage.Id).Id), [.. axes, Elapsed.Id]);
            AddColumn(currentColumn, new(currentColumn,
                function == SourceFunction.Current ? "Actual source current" : "Measured current", PhysicalUnit.Ampere, ParameterAccess.Read),
                new(device, sourceDevice.GetParameter(Keithley2450Parameters.MeasuredCurrent.Id).Id), [.. axes, Elapsed.Id]);
            BindVariable(voltageColumn, PhysicalUnit.Volt, known); BindVariable(currentColumn, PhysicalUnit.Ampere, known);
        }
        // Shared compiler boundary for direct and sample-independent acquisition.
        void ValidateIndependentResistance(ReadCurrentResistanceStep independent, Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<ParameterId> axes)
        {
            var excitation = Device(independent.Source); var pureCurrent = excitation.Model == "K6221";
            if (excitation.StopPolicy != DeviceStopPolicy.OutputOff || pureCurrent && (!currentPreparations.ContainsKey(independent.Source) || independent.CurrentReference != CurrentReference.ProgrammedSetpoint) ||
                !pureCurrent && (excitation.Model != "K2450" || !preparations.TryGetValue(independent.Source, out var currentPreparation) || currentPreparation.Function != SourceFunction.Current ||
                independent.CurrentReference == CurrentReference.MeasuredSourceReadback && currentPreparation.Sense.Function != SenseFunction.Voltage))
                throw new ArgumentException("Independent resistance requires current-source preparation and complementary sensing for measured source readback.");
            var independentMeter = Device(independent.Meter);
            if (independentMeter.Model != "K2182" || independentMeter.StopPolicy != DeviceStopPolicy.AbortAcquisition)
                throw new NotSupportedException("Independent resistance requires the supported passive K2182 profile.");
            var voltageInput = Parameter(new(independent.Meter, Keithley2182Parameters.Voltage.Id), ParameterAccess.Read);
            var currentParameter = pureCurrent ? Keithley6221Parameters.Current : independent.CurrentReference == CurrentReference.ProgrammedSetpoint ? Keithley2450Parameters.SourceCurrent : Keithley2450Parameters.MeasuredCurrent;
            excitation.GetParameter(currentParameter.Id);
            if (independent.ExpectedProgrammedCurrent is { } expectedCurrent &&
                (ValueUnit(expectedCurrent, known) != PhysicalUnit.Ampere || expectedCurrent is VariableValue expectedVariable && !axes.Contains(expectedVariable.Variable)))
                throw new ArgumentException("Expected excitation must be Ampere and variables must be persisted active axes.");
            if (independent.RequiredRoute is { } requiredRoute)
            {
                Device(requiredRoute.Device); var requiredMatrix = registry.GetMatrixDefinition(requiredRoute.Device); requiredMatrix.Validate(requiredRoute.Closed);
                if (!requiredMatrix.GuardedSources.Contains(independent.Source)) throw new ArgumentException("Independent route must guard its excitation source.");
                foreach (var guard in requiredMatrix.GuardedSources) if (Device(guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Independent route guards require output-off policies.");
            }
            AddColumn(independent.VoltageColumn, voltageInput, new(independent.Meter, voltageInput.Id), [.. axes, Elapsed.Id]);
            AddColumn(independent.CurrentColumn, new(independent.CurrentColumn, independent.CurrentReference == CurrentReference.ProgrammedSetpoint ? "Confirmed programmed current" : "Measured source current", PhysicalUnit.Ampere, ParameterAccess.Read), new(independent.Source, currentParameter.Id), [.. axes, Elapsed.Id]);
            AddColumn(independent.ResistanceColumn, new(independent.ResistanceColumn, "Independent resistance (V/I)", PhysicalUnit.Ohm, ParameterAccess.Read), null, [.. axes, Elapsed.Id, independent.VoltageColumn, independent.CurrentColumn]);
            if (independent.Retry is { ContinueOnAcquisitionFailure: true })
                foreach (var column in new[] { independent.VoltageColumn, independent.CurrentColumn, independent.ResistanceColumn }) known.Remove(column);
            else { BindVariable(independent.VoltageColumn, PhysicalUnit.Volt, known); BindVariable(independent.CurrentColumn, PhysicalUnit.Ampere, known); BindVariable(independent.ResistanceColumn, PhysicalUnit.Ohm, known); }
            ValidateEnvironmentPolicy([independent.VoltageColumn, independent.CurrentColumn, independent.ResistanceColumn], independent.EnvironmentReads); ValidateEnvironment(independent.EnvironmentReads, axes);
        }
        void ValidateResistance(ReadResistanceStep resistance, Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<ParameterId> axes)
        {
            ValidateSourceSense(resistance.Device, resistance.Function, resistance.VoltageColumn, resistance.CurrentColumn, known, axes);
            ValidateEnvironmentPolicy([resistance.VoltageColumn, resistance.CurrentColumn, resistance.ResistanceColumn], resistance.EnvironmentReads);
            ValidateEnvironment(resistance.EnvironmentReads, axes);
            AddColumn(resistance.ResistanceColumn, new(resistance.ResistanceColumn, "Resistance (V/I)", PhysicalUnit.Ohm, ParameterAccess.Read),
                null, [.. axes, Elapsed.Id, resistance.VoltageColumn, resistance.CurrentColumn]);
            BindVariable(resistance.ResistanceColumn, PhysicalUnit.Ohm, known);
        }
        void ValidateEnvironment(IReadOnlyList<EnvironmentRead> reads, IReadOnlyList<ParameterId> axes)
        {
            foreach (var read in reads)
            {
                var unit = EnvironmentRead.Unit(read.Role);
                ParameterDescriptor descriptor = new(read.Column, read.Role + " readback", unit, ParameterAccess.Read);
                if (read.Address is { } address)
                {
                    descriptor = Parameter(address, ParameterAccess.Read);
                    var controller = registry.GetAxisDefinition(address.Device);
                    if (controller.Readback.Id != address.Parameter || descriptor.Unit != unit)
                        throw new ArgumentException("Environment capture requires the matching controller readback, never its target.");
                }
                AddColumn(read.Column, descriptor, read.Address, [.. axes, Elapsed.Id], environment: true);
            }
        }
        void ValidateEnvironmentPolicy(IEnumerable<ParameterId> acquisitionColumns, IReadOnlyList<EnvironmentRead> reads)
        {
            foreach (var column in acquisitionColumns)
                if (environmentPolicies.TryGetValue(column, out var previous) && !previous.SequenceEqual(reads))
                    throw new ArgumentException("Shared acquisition columns require one consistent environment policy; use distinct IDs.");
                else environmentPolicies[column] = reads;
        }
        long Visit(ExperimentStep step, Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<ParameterId> axes, int depth)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (depth > 128 || steps.Count >= 10_000) throw new ArgumentOutOfRangeException(nameof(plan), "Plan exceeds its depth or node limit.");
            if (!steps.Add(step.Id)) throw new ArgumentException($"Duplicate step ID {step.Id}.");
            switch (step)
            {
                case TemperatureStabilityStep settings:
                    Device(settings.Device);
                    if (!registry.GetAxisDefinition(settings.Device).SupportsSoftwareTemperatureRamp)
                        throw new NotSupportedException("Controller has no temperature-stability profile.");
                    return 1;
                case WaitTemperatureStep temperatureWait:
                    Device(temperatureWait.Device);
                    var temperatureDefinition = registry.GetAxisDefinition(temperatureWait.Device);
                    if (!temperatureDefinition.SupportsSoftwareTemperatureRamp || ValueUnit(temperatureWait.Target, known) != PhysicalUnit.Kelvin)
                        throw new ArgumentException("Temperature waits require a temperature-control profile and Kelvin target.");
                    if (temperatureWait.Target is ConstantValue waitTarget) temperatureDefinition.Target.Validate(waitTarget.Value);
                    return 1;
                case TemperatureControlStep command:
                    Device(command.Device);
                    if (!registry.SupportsTemperatureConfiguration(command.Device))
                        throw new NotSupportedException("Controller does not implement independent temperature configuration.");
                    if (command.Rate is { } commandRate) registry.GetAxisDefinition(command.Device).Rate.ValidateWrite(commandRate);
                    return 1;
                case HardwareTemperatureSetpointStep hardwareTemperature:
                    Device(hardwareTemperature.Device);
                    var hardwareController = registry.GetAxisDefinition(hardwareTemperature.Device);
                    if (!hardwareController.SupportsSoftwareTemperatureRamp || !hardwareTemperature.EnableControl && !registry.SupportsTemperatureConfiguration(hardwareTemperature.Device))
                        throw new NotSupportedException("Controller does not implement requested hardware temperature setpoint capability.");
                    if (ValueUnit(hardwareTemperature.Target, known) != PhysicalUnit.Kelvin) throw new ArgumentException("Temperature targets require Kelvin.");
                    if (hardwareTemperature.Target is ConstantValue hardwareTarget) hardwareController.Target.ValidateWrite(hardwareTarget.Value);
                    hardwareController.Rate.ValidateWrite(hardwareTemperature.Rate);
                    return 1;
                case SoftwareTemperatureRampStep temperature:
                    Device(temperature.Device);
                    var controller = registry.GetAxisDefinition(temperature.Device);
                    if (!controller.SupportsSoftwareTemperatureRamp || !temperature.EnableControl && !registry.SupportsTemperatureConfiguration(temperature.Device))
                        throw new NotSupportedException("Controller does not implement requested software temperature ramp capability.");
                    if (ValueUnit(temperature.Target, known) != PhysicalUnit.Kelvin) throw new ArgumentException("Temperature targets require Kelvin.");
                    if (temperature.Target is ConstantValue temperatureTarget)
                    {
                        controller.Target.ValidateWrite(temperatureTarget.Value);
                        if (temperature.Rate is not null) controller.Target.ValidateWrite(new(TemperatureRampPath.MilliKelvin(temperatureTarget.Value) / 1000.0, PhysicalUnit.Kelvin));
                    }
                    // Live readback is not available at preflight: bound the path
                    // using the complete declared controller range.
                    return temperature.Rate is { } temperatureRate ? checked(1 + Math.Max(1, new TemperatureRampPath(controller.Target.Minimum!.Value, controller.Target.Maximum!.Value, temperatureRate).UpdateCount)) : 2;
                case ReadSourceSenseStep pair:
                    ValidateSourceSense(pair.Device, pair.Function, pair.VoltageColumn, pair.CurrentColumn, known, axes);
                    ValidateEnvironmentPolicy([pair.VoltageColumn, pair.CurrentColumn], pair.EnvironmentReads);
                    ValidateEnvironment(pair.EnvironmentReads, axes);
                    return 1;
                case MeasurementGroupStep point:
                    return checked(1 + point.Members.Sum(member => Visit(member.Body, known, axes, depth + 1)));
                case SampleAnalysisStep analysis:
                    analysis.ValidateRouting(plan.Routing ?? throw new ArgumentException("Sample analysis requires a frozen routing configuration."));
                    var analysisCost = checked(1 + analysis.Readings.Sum(child => Visit(child, known, axes, depth + 1)));
                    foreach (var result in analysis.Columns)
                    {
                        var unit = SampleResistanceAnalysis.Unit(result.Kind);
                        var label = result.Kind switch
                        {
                            SampleDerivedQuantity.LongitudinalResistance => "Longitudinal resistance",
                            SampleDerivedQuantity.TransverseResistance => "Raw transverse resistance",
                            SampleDerivedQuantity.HorizontalMeanResistance => "Horizontal mean absolute resistance",
                            SampleDerivedQuantity.VerticalMeanResistance => "Vertical mean absolute resistance",
                            SampleDerivedQuantity.SheetResistance => "Sheet resistance",
                            SampleDerivedQuantity.Resistivity => "Longitudinal resistivity",
                            _ => throw new ArgumentOutOfRangeException(nameof(result)),
                        };
                        AddColumn(result.Column, new(result.Column, label, unit, ParameterAccess.Read), null, [.. axes, Elapsed.Id]);
                        // Geometry/numeric-dependent results may be unavailable. They
                        // are optional data columns, not guaranteed plan variables.
                        var canBeUnavailable = result.Kind switch
                        {
                            SampleDerivedQuantity.LongitudinalResistance => analysis.Readings[0].Acquisition is ReadCurrentResistanceStep { Retry.ContinueOnAcquisitionFailure: true },
                            SampleDerivedQuantity.TransverseResistance => analysis.Readings[1].Acquisition is ReadCurrentResistanceStep { Retry.ContinueOnAcquisitionFailure: true },
                            SampleDerivedQuantity.HorizontalMeanResistance => analysis.Readings.Take(4).Any(r => r.Acquisition is ReadCurrentResistanceStep { Retry.ContinueOnAcquisitionFailure: true }),
                            SampleDerivedQuantity.VerticalMeanResistance => analysis.Readings.Skip(4).Any(r => r.Acquisition is ReadCurrentResistanceStep { Retry.ContinueOnAcquisitionFailure: true }),
                            _ => true,
                        };
                        if (!canBeUnavailable) BindVariable(result.Column, unit, known);
                        else
                        {
                            if (known.TryGetValue(result.Column, out var previousUnit) && previousUnit != unit) throw new ArgumentException($"Variable {result.Column} cannot change units.");
                            known.Remove(result.Column);
                        }
                    }
                    return analysisCost;
                case ConditionStep condition:
                    CheckCondition(condition.Condition, known); return 1;
                case MutateValueStep mutation:
                    var mutationUnit = mutation.Action == VariableMutation.Multiply ? known.GetValueOrDefault(mutation.Variable, PhysicalUnit.Dimensionless) : mutation.Value.Unit;
                    BindVariable(mutation.Variable, mutationUnit, known); return 1;
                case OutcomeMutationStep hook:
                    var beforeHook = new Dictionary<ParameterId, PhysicalUnit>(known);
                    var hookCost = Visit(hook.Body, known, axes, depth + 1);
                    if (hook.Failure is { Action: not VariableMutation.Multiply } possibleFailure && known.TryGetValue(possibleFailure.Variable, out var possibleUnit) && possibleUnit != possibleFailure.Value.Unit)
                        throw new ArgumentException("Failure mutation cannot change a unit possibly established by the body.");
                    var successHookCost = hook.Success is { } successHook ? Visit(successHook, known, axes, depth + 1) : 0;
                    var failureHookCost = hook.Failure is { } failureHook ? Visit(failureHook, beforeHook, axes, depth + 1) : 0;
                    return checked(1 + hookCost + Math.Max(successHookCost, failureHookCost));
                case WhileStep loop:
                    CheckCondition(loop.Condition, known); hasDynamicLoops = true;
                    AddColumn(loop.IterationColumn, new(loop.IterationColumn, "While iteration (1-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    var bodyKnown = new Dictionary<ParameterId, PhysicalUnit>(known); BindVariable(loop.IterationColumn, PhysicalUnit.Dimensionless, bodyKnown);
                    var loopBodyCost = Visit(loop.Body, bodyKnown, [.. axes, loop.IterationColumn], depth + 1);
                    RetainCommonVariables(known, [new Dictionary<ParameterId, PhysicalUnit>(known), bodyKnown]);
                    return checked(1 + loopBodyCost);
                case FallbackStep fallback:
                    AddColumn(fallback.BranchColumn, new(fallback.BranchColumn, "Fallback branch (1-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    AddColumn(fallback.TotalColumn, new(fallback.TotalColumn, "Fallback branch total", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    BindVariable(fallback.BranchColumn, PhysicalUnit.Dimensionless, known); BindVariable(fallback.TotalColumn, PhysicalUnit.Dimensionless, known);
                    var branches = new List<Dictionary<ParameterId, PhysicalUnit>>(); long fallbackCost = 1;
                    foreach (var child in fallback.Children)
                    {
                        var branch = new Dictionary<ParameterId, PhysicalUnit>(known); branches.Add(branch);
                        fallbackCost = checked(fallbackCost + Visit(child, branch, [.. axes, fallback.BranchColumn, fallback.TotalColumn], depth + 1));
                    }
                    RetainCommonVariables(known, branches); return fallbackCost;
                case RetryStep retry:
                    AddColumn(retry.AttemptColumn, new(retry.AttemptColumn, "Workflow attempt (1-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    AddColumn(retry.TotalColumn, new(retry.TotalColumn, "Maximum workflow attempts", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    BindVariable(retry.AttemptColumn, PhysicalUnit.Dimensionless, known); BindVariable(retry.TotalColumn, PhysicalUnit.Dimensionless, known);
                    return checked(1 + retry.MaxAttempts * Visit(retry.Body, known, [.. axes, retry.AttemptColumn, retry.TotalColumn], depth + 1));
                case ResultDecoratorStep decorator:
                    var beforeDecoration = new Dictionary<ParameterId, PhysicalUnit>(known);
                    var decorated = new Dictionary<ParameterId, PhysicalUnit>(known);
                    var decoratedCost = Visit(decorator.Body, decorated, axes, depth + 1);
                    // An IO-only child either completes its assignments or
                    // throws, even when the decorator changes its outcome.
                    if (!CanReportWorkflowFailure(decorator.Body))
                    { known.Clear(); foreach (var item in decorated) BindVariable(item.Key, item.Value, known); }
                    else RetainCommonVariables(known, [beforeDecoration, decorated]);
                    return checked(1 + decoratedCost);
                case TimeoutStep timeout:
                    return checked(1 + Visit(timeout.Body, known, axes, depth + 1));
                case SubtreeStep subtree:
                    var local = new Dictionary<ParameterId, PhysicalUnit>(known);
                    foreach (var input in subtree.Inputs) local[input.Destination] = ValueUnit(new VariableValue(input.Source), known);
                    long subtreeCost = 1;
                    foreach (var child in subtree.Children) subtreeCost = checked(subtreeCost + Visit(child, local, axes, depth + 1));
                    foreach (var output in subtree.Outputs) BindVariable(output.Destination, ValueUnit(new VariableValue(output.Source), local), known);
                    return subtreeCost;
                case SequenceStep sequence:
                    return checked(1 + sequence.Children.Sum(child => Visit(child, known, axes, depth + 1)));
                case RepeatStep repeat:
                    return checked(1 + repeat.Cycles * Visit(repeat.Body, known, axes, depth + 1));
                case ScanStep scan:
                    var axis = Parameter(scan.Axis, ParameterAccess.Write);
                    foreach (var point in scan.Values.Values) axis.ValidateWrite(point);
                    writes.Add((scan.Axis, scan.Values.Values));
                    if (axes.Contains(scan.Column)) throw new ArgumentException($"Scan variable {scan.Column} is already active.");
                    AddColumn(scan.Column, axis, scan.Axis, []);
                    BindVariable(scan.Column, axis.Unit, known);
                    return checked(1 + scan.Values.Values.Count * Visit(scan.Body, known, [.. axes, scan.Column], depth + 1));
                case ForEachStep loop:
                    var index = new ParameterId($"{loop.Variable}.index");
                    var total = new ParameterId($"{loop.Variable}.total");
                    if (axes.Contains(loop.Variable)) throw new ArgumentException($"Iterator {loop.Variable} is already active.");
                    AddColumn(loop.Variable, new(loop.Variable, loop.Timing == IterationTiming.DelayBeforePoint ? "Delay before point" : "Iterator value", loop.Values.Unit, ParameterAccess.Read), null, []);
                    AddColumn(index, new(index, loop.IndexBase == IterationIndexBase.OneBased ? "Iterator index (1-based)" : "Iterator index (0-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    AddColumn(total, new(total, "Iterator total", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    BindVariable(loop.Variable, loop.Values.Unit, known);
                    BindVariable(index, PhysicalUnit.Dimensionless, known); BindVariable(total, PhysicalUnit.Dimensionless, known);
                    return checked(1 + loop.Values.Values.Count * Visit(loop.Body, known, [.. axes, loop.Variable, index, total], depth + 1));
                case SetParameterStep set:
                    var parameter = Parameter(set.Address, ParameterAccess.Write);
                    if (parameter.Unit != ValueUnit(set.Value, known)) throw new ArgumentException($"{set.Address.Parameter} value has the wrong unit.");
                    if (set.Value is ConstantValue literal)
                    { parameter.ValidateWrite(literal.Value); writes.Add((set.Address, [literal.Value])); }
                    else writes.Add((set.Address, null));
                    return 1;
                case AssignValueStep assign:
                    BindVariable(assign.Variable, ValueUnit(assign.Value, known), known);
                    return 1;
                case VerifyMatrixRouteStep verification:
                    Device(verification.Device);
                    var verifiedMatrix = registry.GetMatrixDefinition(verification.Device);
                    verifiedMatrix.Validate(verification.Closed);
                    foreach (var guard in verifiedMatrix.GuardedSources)
                        if (Device(guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Matrix guards must use output-off policy.");
                    return 1;
                case RouteMatrixStep route:
                    Device(route.Device);
                    var matrix = registry.GetMatrixDefinition(route.Device);
                    matrix.Validate(route.Closed);
                    foreach (var guardedSource in matrix.GuardedSources)
                        if (Device(guardedSource).StopPolicy != DeviceStopPolicy.OutputOff)
                            throw new ArgumentException($"{guardedSource}: matrix excitation guard must use output-off policy.");
                    return 1;
                case SourceOutputStep output:
                    var outputDevice = Device(output.Device);
                    if (outputDevice.Model is not ("K2450" or "K6221") || outputDevice.StopPolicy != DeviceStopPolicy.OutputOff) throw new NotSupportedException("Device has no implemented output-source profile.");
                    return 1;
                case ReadStep read:
                    ValidateEnvironmentPolicy(read.Channels.Select(c => c.Column), read.EnvironmentReads);
                    foreach (var channel in read.Channels)
                    {
                        ArgumentNullException.ThrowIfNull(channel.Column);
                        var measurement = Parameter(channel.Address, ParameterAccess.Read);
                        AddColumn(channel.Column, measurement, channel.Address, [.. axes, Elapsed.Id]);
                        BindVariable(channel.Column, measurement.Unit, known);
                    }
                    ValidateEnvironment(read.EnvironmentReads, axes);
                    return 1;
                case ReadCurrentResistanceStep independent:
                    ValidateIndependentResistance(independent, known, axes);
                    return 1;
                case ReadResistanceStep resistance:
                    ValidateResistance(resistance, known, axes);
                    return 1;
                case ReadSampleResistanceStep sampleRead:
                    var context = (plan.Routing ?? throw new ArgumentException("Sample measurements require a frozen routing configuration.")).Resolve(sampleRead.Binding);
                    if (context.SourceMeter != sampleRead.Acquisition.Device) throw new ArgumentException("Sample wiring belongs to a different source meter.");
                    if (sampleRead.Acquisition is ReadCurrentResistanceStep sampleIndependent)
                    {
                        if (context.VoltageMeter != sampleIndependent.Meter || !Equals(sampleIndependent.RequiredRoute, new MatrixRouteExpectation(context.Matrix, context.Closed)))
                            throw new ArgumentException("Independent sample acquisition must match its frozen source/meter and complete electrode route.");
                        ValidateIndependentResistance(sampleIndependent, known, axes);
                    }
                    else
                    {
                        if (context.VoltageMeter is not null) throw new ArgumentException("Integrated samples cannot use independent meter wiring.");
                        ValidateResistance((ReadResistanceStep)sampleRead.Acquisition, known, axes);
                        if (preparations[sampleRead.Acquisition.Device].Sense.Mode != context.SensingMode)
                            throw new ArgumentException("Source preparation sensing mode does not match the selected sample topology.");
                    }
                    Device(context.Matrix);
                    var sampleMatrix = registry.GetMatrixDefinition(context.Matrix);
                    sampleMatrix.Validate(context.Closed);
                    if (!sampleMatrix.GuardedSources.Contains(context.SourceMeter)) throw new ArgumentException("The sample source must be a declared matrix excitation guard.");
                    foreach (var guard in sampleMatrix.GuardedSources)
                        if (Device(guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Sample matrix guards must confirm output-off.");
                    sampleRoutes.Add(step.Id, context);
                    return 1;
                case DelayStep:
                    return 1;
                case WaitStableStep wait:
                    var readback = Parameter(wait.Readback, ParameterAccess.Read);
                    if (readback.Unit != wait.Tolerance.Unit || readback.Unit != ValueUnit(wait.Target, known))
                        throw new ArgumentException("Stability target, tolerance and readback units must match.");
                    if (wait.Target is ConstantValue target) readback.Validate(target.Value);
                    return 1;
                case IfElseStep conditional:
                    CheckCondition(conditional.Condition, known);
                    var thenKnown = new Dictionary<ParameterId, PhysicalUnit>(known);
                    var elseKnown = new Dictionary<ParameterId, PhysicalUnit>(known);
                    var thenCost = Visit(conditional.Then, thenKnown, axes, depth + 1);
                    var elseCost = conditional.Else is { } otherwise ? Visit(otherwise, elseKnown, axes, depth + 1) : 0;
                    RetainCommonVariables(known, [thenKnown, elseKnown]);
                    return checked(1 + Math.Max(thenCost, elseCost));
                case BranchOnOutcomeStep branch:
                    var beforeCondition = new Dictionary<ParameterId, PhysicalUnit>(known);
                    var afterCondition = new Dictionary<ParameterId, PhysicalUnit>(known);
                    var conditionCost = Visit(branch.Condition, afterCondition, axes, depth + 1);
                    var failureKnown = new Dictionary<ParameterId, PhysicalUnit>(afterCondition);
                    if (CanReportWorkflowFailure(branch.Condition)) RetainCommonVariables(failureKnown, [beforeCondition, afterCondition]);
                    // A forced result of an otherwise non-failing child has
                    // completed the same observations on its failure path.
                    if (branch.Condition is ResultDecoratorStep { Effect: ResultEffect.ForceFailure or ResultEffect.Invert } forced && !CanReportWorkflowFailure(forced.Body))
                        failureKnown = new(afterCondition);
                    var successCost = Visit(branch.Then, afterCondition, axes, depth + 1);
                    var failureCost = branch.Else is { } alternative ? Visit(alternative, failureKnown, axes, depth + 1) : 0;
                    RetainCommonVariables(known, [afterCondition, failureKnown]);
                    return checked(1 + conditionCost + Math.Max(successCost, failureCost));
                default:
                    throw new NotSupportedException($"No executor exists for {step.GetType().Name}.");
            }
        }
    }
}
