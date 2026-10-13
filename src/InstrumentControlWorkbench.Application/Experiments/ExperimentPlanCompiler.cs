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
internal sealed record CompiledExperiment(ExperimentPlan Plan, DataSetSchema Schema, IReadOnlyList<InstrumentId> Devices, long MaximumStepExecutions, IReadOnlyDictionary<StepId, ResolvedSampleRoute> SampleRoutes, bool HasDynamicLoops, IReadOnlyList<PlanVariableAvailability>? InspectedVariables = null);

// Compilation reads capability metadata only. No lease or driver operation occurs
// until every branch, scan value, unit and result dependency has been checked.
internal static partial class ExperimentPlanCompiler
{
    public static readonly ParameterDescriptor Elapsed = new(new("elapsed"), "Elapsed time", PhysicalUnit.Second,
        ParameterAccess.Read, new(0, PhysicalUnit.Second));

    public static CompiledExperiment Compile(ExperimentPlan plan, DeviceRegistry registry, StepId? inspectStep = null, PlanVariablePosition inspectPosition = PlanVariablePosition.BeforeStep)
        => CompileCore(plan, registry, inspectStep, inspectPosition, null);

    private static CompiledExperiment CompileCore(ExperimentPlan plan, DeviceRegistry registry, StepId? inspectStep, PlanVariablePosition inspectPosition, CalculationProbe? calculationProbe)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(inspectPosition)) throw new ArgumentOutOfRangeException(nameof(inspectPosition));
        IReadOnlyList<PlanVariableAvailability>? inspectedVariables = null;
        var visiting = new Stack<StepId>();
        var activeCoolingDevices = new HashSet<InstrumentId>();
        var activeIterationValues = new Dictionary<ParameterId, ScanValues>();
        var trackDefinitions = inspectStep is not null || calculationProbe is not null;
        var devices = registry.Devices.ToDictionary(d => d.Id);
        var needed = new HashSet<InstrumentId>();
        var lockInColumnOwners = new Dictionary<ParameterId, StepId>();
        var columns = new Dictionary<ParameterId, DataColumn> { [Elapsed.Id] = new(Elapsed) };
        var environmentColumns = new HashSet<ParameterId>();
        var environmentPolicies = new Dictionary<ParameterId, IReadOnlyList<EnvironmentRead>>();
        var steps = new HashSet<StepId>();
        var variables = new VariableFlowState();
        var preparations = plan.Preparations.ToDictionary(p => p.Device);
        var currentPreparations = plan.CurrentSourcePreparations.ToDictionary(p => p.Device);
        var sampleRoutes = new Dictionary<StepId, ResolvedSampleRoute>();
        var writes = new List<(ParameterAddress Address, IReadOnlyList<Quantity>? Values)>();
        var hasDynamicLoops = false;
        var variableScope = 0;
        var bindings = new List<(ParameterId Variable, BlackboardValueType Type, int Scope)>();
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
        if (inspectStep is not null && inspectedVariables is null) throw new ArgumentException("Variable inspection position does not exist or cannot accept a child.", nameof(inspectStep));
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
            if (Device(write.Address.Device).Model == "SR830" && write.Values is { } lockInValues)
                foreach (var value in lockInValues) Sr830Parameters.ValidateWrite(write.Address.Parameter, value);
            if (Device(write.Address.Device).Model == "K6514" && write.Values is { } electrometerValues)
                foreach (var value in electrometerValues) Keithley6514Parameters.ValidateWrite(write.Address.Parameter, value);
            if (Device(write.Address.Device).Model == "K6485" && write.Values is { } picoValues)
                foreach (var value in picoValues) Keithley6485Parameters.ValidateWrite(write.Address.Parameter, value);
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
        return new(plan, new(columns.Values), Array.AsReadOnly(needed.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()), hasDynamicLoops ? plan.MaximumStepExecutions : cost, new ReadOnlyDictionary<StepId, ResolvedSampleRoute>(sampleRoutes), hasDynamicLoops, inspectedVariables);

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
        BlackboardValueType ValueType(PlanValue value, VariableFlowState known)
        {
            ArgumentNullException.ThrowIfNull(value);
            return value switch
            {
                ConstantValue constant => BlackboardValueType.Quantity(constant.Value.Unit),
                ArithmeticValue arithmetic => BlackboardValueType.Quantity(NumericPlanValue.InferUnit(arithmetic, id => ValueUnit(new VariableValue(id), known))),
                BooleanValue => BlackboardValueType.Boolean,
                TextValue => BlackboardValueType.Text,
                VariableValue variable when variable.Variable is not null && known.TryGetValue(variable.Variable, out var type) => type,
                VariableValue variable => throw new ArgumentException($"Variable {variable.Variable} is not defined on every path before use."),
                QuantityFromNumberValue qualified when known.TryGetValue(qualified.Variable, out var inputType) && inputType == BlackboardValueType.Quantity(PhysicalUnit.Dimensionless) => BlackboardValueType.Quantity(qualified.Unit),
                QuantityFromNumberValue qualified => throw new ArgumentException($"Numeric input {qualified.Variable} must be defined and dimensionless before unit qualification."),
                _ => throw new NotSupportedException($"Unsupported value expression {value.GetType().Name}."),
            };
        }
        PhysicalUnit ValueUnit(PlanValue value, VariableFlowState known)
            => ValueType(value, known).Unit ?? throw new ArgumentException("A physical operand must be a quantity; booleans and text cannot be coerced to numbers.");
        void BindVariable(ParameterId id, BlackboardValueType type, VariableFlowState known)
        {
            ArgumentNullException.ThrowIfNull(id);
            if (known.TryGetValue(id, out var existing) && existing != type) throw new ArgumentException($"Variable {id} cannot change type or units.");
            known[id] = type; if (trackDefinitions) known.Define(id, visiting.Peek()); bindings.Add((id, type, variableScope));
        }
        void BindQuantity(ParameterId id, PhysicalUnit unit, VariableFlowState known)
            => BindVariable(id, BlackboardValueType.Quantity(unit), known);
        void AddColumn(ParameterId id, ParameterDescriptor parameter, ParameterAddress? source, IReadOnlyList<ParameterId> dependencies, bool environment = false)
        {
            if (lockInColumnOwners.TryGetValue(id, out var frameOwner) && frameOwner != visiting.Peek()) throw new ArgumentException("Lock-in frame columns cannot be reused by another acquisition.");
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
            TemperatureCoolingStep cooling => CanReportWorkflowFailure(cooling.Body),
            WhileStep loop => CanReportWorkflowFailure(loop.Body),
            OutcomeMutationStep hook => CanReportWorkflowFailure(hook.Body),
            BranchOnOutcomeStep branch => CanReportWorkflowFailure(branch.Then) || branch.Else is { } otherBranch && CanReportWorkflowFailure(otherBranch),
            SequenceStep sequence => sequence.Children.Any(CanReportWorkflowFailure),
            RepeatStep repeat => CanReportWorkflowFailure(repeat.Body),
            ScanStep scan => CanReportWorkflowFailure(scan.Body),
            ForEachStep loop => CanReportWorkflowFailure(loop.Body),
            IfElseStep conditional => CanReportWorkflowFailure(conditional.Then) || conditional.Else is { } other && CanReportWorkflowFailure(other),
            ThresholdSwitchStep choice => choice.Children.Any(CanReportWorkflowFailure),
            _ => false, // Validated IO/acquisition leaves either succeed or throw; controls cannot swallow those errors.
        };
        void RetainCommonVariables(VariableFlowState known, IReadOnlyList<VariableFlowState> paths)
        {
            var common = paths[0].Where(item => paths.All(path => path.TryGetValue(item.Key, out var unit) && unit == item.Value)).ToArray();
            var origins = !trackDefinitions ? null : common.ToDictionary(item => item.Key, item => paths.SelectMany(path => path.Definitions(item.Key)).Distinct().ToArray());
            known.Clear(); foreach (var item in common)
            {
                BindVariable(item.Key, item.Value, known);
                if (trackDefinitions) known.SetDefinitions(item.Key, origins![item.Key]);
            }
        }
        void CheckCondition(ValueCondition condition, VariableFlowState known)
        {
            ArgumentNullException.ThrowIfNull(condition);
            if (!Enum.IsDefined(condition.Operator)) throw new ArgumentOutOfRangeException(nameof(condition.Operator));
            var left = ValueType(new VariableValue(condition.Variable), known);
            if (left != ValueType(condition.Value, known)) throw new ArgumentException("Comparison operands must have matching types and units.");
            if (left.Kind != BlackboardValueKind.Quantity && condition.Operator is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual))
                throw new ArgumentException("Boolean and text conditions support equality and inequality only.");
            if (condition.EqualityTolerance is { } equalityTolerance && (equalityTolerance.Value <= 0 || equalityTolerance.Unit != left.Unit || condition.Operator is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual)))
                throw new ArgumentException("Equality tolerance requires positive matching quantity units and an equality operator.");
        }
        void ValidateSourceSense(InstrumentId device, SourceFunction function, ParameterId voltageColumn, ParameterId currentColumn,
            VariableFlowState known, IReadOnlyList<ParameterId> axes)
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
            BindQuantity(voltageColumn, PhysicalUnit.Volt, known); BindQuantity(currentColumn, PhysicalUnit.Ampere, known);
        }
        // Shared compiler boundary for direct and sample-independent acquisition.
        void ValidateIndependentResistance(ReadCurrentResistanceStep independent, VariableFlowState known, IReadOnlyList<ParameterId> axes)
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
                (ValueUnit(expectedCurrent, known) != PhysicalUnit.Ampere || NumericPlanValue.Variables(expectedCurrent).Any(variable => !axes.Contains(variable))))
                throw new ArgumentException("Expected excitation must be Ampere and variables must be persisted active axes.");
            if (independent.ExpectedProgrammedCurrent is { } staticExpected && NumericPlanValue.TryConstant(staticExpected, out var expectedLiteral) && Math.Abs(expectedLiteral.Value) <= independent.MinimumCurrent.Value)
                throw new ArgumentException("Expected excitation must exceed the minimum-current threshold.");
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
            else { BindQuantity(independent.VoltageColumn, PhysicalUnit.Volt, known); BindQuantity(independent.CurrentColumn, PhysicalUnit.Ampere, known); BindQuantity(independent.ResistanceColumn, PhysicalUnit.Ohm, known); }
            ValidateEnvironmentPolicy([independent.VoltageColumn, independent.CurrentColumn, independent.ResistanceColumn], independent.EnvironmentReads); ValidateEnvironment(independent.EnvironmentReads, axes);
        }
        long ValidateCooling(TemperatureCoolingStep cooling, VariableFlowState known, IReadOnlyList<ParameterId> axes, int depth)
        {
            var device = Device(cooling.Device); var controller = registry.GetAxisDefinition(cooling.Device);
            if (!registry.SupportsTemperatureConfiguration(cooling.Device) || controller.Target.Unit != PhysicalUnit.Kelvin || activeCoolingDevices.Contains(cooling.Device)) throw new ArgumentException("Cooling needs independent Kelvin control outside another cooling scan of the same controller.");
            controller.Target.ValidateWrite(cooling.CoolingTarget); controller.Readback.Validate(cooling.EndTemperature);
            foreach (var threshold in cooling.Thresholds.Values) controller.Readback.Validate(threshold);
            if (axes.Contains(cooling.Column) || axes.Any(axis => columns.TryGetValue(axis, out var column) && column.Source?.Device == cooling.Device)) throw new ArgumentException("Cooling cannot take over an enclosing controller scan.");
            AddColumn(cooling.Column, new(cooling.Column, "Actual temperature at cooling sample start", PhysicalUnit.Kelvin, ParameterAccess.Read, controller.Readback.Minimum, controller.Readback.Maximum), new(cooling.Device, controller.Readback.Id), []);
            AddColumn(cooling.IndexColumn, new(cooling.IndexColumn, "Cooling sample index (0-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless)), null, []);
            AddColumn(cooling.TotalColumn, new(cooling.TotalColumn, "Cooling threshold count; 0 for continuous sampling", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless)), null, []);
            BindQuantity(cooling.Column, PhysicalUnit.Kelvin, known); BindQuantity(cooling.IndexColumn, PhysicalUnit.Dimensionless, known); BindQuantity(cooling.TotalColumn, PhysicalUnit.Dimensionless, known);
            activeCoolingDevices.Add(cooling.Device);
            long bodyCost;
            try { bodyCost = Visit(cooling.Body, known, [.. axes, cooling.Column, cooling.IndexColumn, cooling.TotalColumn], depth + 1); }
            finally { activeCoolingDevices.Remove(cooling.Device); }
            if (cooling.Sampling != CoolingSamplingMode.Threshold) { hasDynamicLoops = true; return checked(2 + bodyCost); }
            return checked(1 + cooling.Thresholds.Values.Count * (1 + bodyCost));
        }
        long ValidateContinuity(ReadElectrodeContinuityStep read, VariableFlowState known, IReadOnlyList<ParameterId> axes)
        {
            if (read.Columns.Any(axes.Contains)) throw new ArgumentException("Continuity result columns cannot overwrite active axes.");
            ValidateSourceSense(read.Source.Device, SourceFunction.Voltage, read.VoltageColumn, read.CurrentColumn, known, axes);
            if (preparations[read.Source.Device].Sense.Mode != SensingMode.TwoWire || axes.Any(axis => columns.TryGetValue(axis, out var column) && column.Source?.Device == read.Source.Device))
                throw new ArgumentException("Continuity requires frozen two-wire preparation outside a source scan.");
            if (read.RequiredRoute is { } route)
            {
                Device(route.Device); var matrix = registry.GetMatrixDefinition(route.Device); matrix.Validate(route.Closed);
                if (!matrix.GuardedSources.Contains(read.Source.Device)) throw new ArgumentException("Continuity matrix must guard its excitation source.");
                foreach (var guard in matrix.GuardedSources) if (Device(guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Continuity guards require output-off policy.");
            }
            AddColumn(read.ResistanceColumn, new(read.ResistanceColumn, "Absolute continuity resistance", PhysicalUnit.Ohm, ParameterAccess.Read, new(0, PhysicalUnit.Ohm)), null, [.. axes, Elapsed.Id, read.VoltageColumn, read.CurrentColumn]);
            AddColumn(read.StatusColumn, new(read.StatusColumn, "Continuity: 0 connected, 1 open, 2 not_configured", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless), new(2, PhysicalUnit.Dimensionless)), null, [.. axes, Elapsed.Id]);
            AddColumn(read.ReasonColumn, new(read.ReasonColumn, "Continuity reason: 0 resistance, 1 zero_current, 2 overrange, 3 not_configured", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless), new(3, PhysicalUnit.Dimensionless)), null, [.. axes, Elapsed.Id]);
            known.Remove(read.VoltageColumn); known.Remove(read.CurrentColumn); known.Remove(read.ResistanceColumn);
            BindQuantity(read.StatusColumn, PhysicalUnit.Dimensionless, known); BindQuantity(read.ReasonColumn, PhysicalUnit.Dimensionless, known); return 1;
        }
        long ValidatePolarization(ReadPolarizationStep pulse, VariableFlowState known, IReadOnlyList<ParameterId> axes)
        {
            if (ValueUnit(pulse.PolarizationTarget, known) != PhysicalUnit.Volt) throw new ArgumentException("Polarization target must resolve to Volt.");
            if (pulse.PolarizationTarget is VariableValue variable && activeIterationValues.TryGetValue(variable.Variable, out var points))
                foreach (var point in points.Values) _ = pulse.Setpoint(point);
            ValidateSourceSense(pulse.Device, SourceFunction.Voltage, pulse.VoltageColumn, pulse.CurrentColumn, known, axes);
            if (preparations[pulse.Device].Sense.Mode != pulse.Sense.Mode || axes.Any(axis => columns.TryGetValue(axis, out var column) && column.Source?.Device == pulse.Device))
                throw new ArgumentException("Polarization must match frozen sensing mode and cannot take over an enclosing source scan.");
            if (pulse.RequiredRoute is { } route)
            {
                Device(route.Device); var matrix = registry.GetMatrixDefinition(route.Device); matrix.Validate(route.Closed);
                if (!matrix.GuardedSources.Contains(pulse.Device)) throw new ArgumentException("Polarization matrix route must guard its source.");
                foreach (var guard in matrix.GuardedSources) if (Device(guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Polarization matrix guards must have output-off policy.");
            }
            AddColumn(pulse.AttemptsColumn, new(pulse.AttemptsColumn, "Complete pulse attempt count", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(1, PhysicalUnit.Dimensionless), new(pulse.MaximumAttempts, PhysicalUnit.Dimensionless)), null, [.. axes, Elapsed.Id]);
            BindQuantity(pulse.AttemptsColumn, PhysicalUnit.Dimensionless, known);
            if (pulse.RequestedPolarizationColumn is { } requested)
            {
                AddColumn(requested, new(requested, "Requested polarization voltage", PhysicalUnit.Volt, ParameterAccess.Read), null, [.. axes, Elapsed.Id]);
                BindQuantity(requested, PhysicalUnit.Volt, known);
            }
            ValidateEnvironmentPolicy([pulse.VoltageColumn, pulse.CurrentColumn, pulse.AttemptsColumn, .. pulse.RequestedPolarizationColumn is { } requestedColumn ? new[] { requestedColumn } : []], pulse.EnvironmentReads); ValidateEnvironment(pulse.EnvironmentReads, axes);
            return pulse.MaximumAttempts;
        }
        long ValidateIteration(ForEachStep loop, VariableFlowState known, IReadOnlyList<ParameterId> axes, int depth)
        {
            var index = new ParameterId($"{loop.Variable}.index");
            var total = new ParameterId($"{loop.Variable}.total");
            if (axes.Contains(loop.Variable)) throw new ArgumentException($"Iterator {loop.Variable} is already active.");
            AddColumn(loop.Variable, new(loop.Variable, loop.Timing == IterationTiming.DelayBeforePoint ? "Delay before point" : "Iterator value", loop.Values.Unit, ParameterAccess.Read), null, []);
            AddColumn(index, new(index, loop.IndexBase == IterationIndexBase.OneBased ? "Iterator index (1-based)" : "Iterator index (0-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
            AddColumn(total, new(total, "Iterator total", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
            BindQuantity(loop.Variable, loop.Values.Unit, known);
            BindQuantity(index, PhysicalUnit.Dimensionless, known); BindQuantity(total, PhysicalUnit.Dimensionless, known);
            activeIterationValues.Add(loop.Variable, loop.Values);
            try { return checked(1 + loop.Values.Values.Count * Visit(loop.Body, known, [.. axes, loop.Variable, index, total], depth + 1)); }
            finally { activeIterationValues.Remove(loop.Variable); }
        }
        void ValidateResistance(ReadResistanceStep resistance, VariableFlowState known, IReadOnlyList<ParameterId> axes)
        {
            ValidateSourceSense(resistance.Device, resistance.Function, resistance.VoltageColumn, resistance.CurrentColumn, known, axes);
            ValidateEnvironmentPolicy([resistance.VoltageColumn, resistance.CurrentColumn, resistance.ResistanceColumn], resistance.EnvironmentReads);
            ValidateEnvironment(resistance.EnvironmentReads, axes);
            AddColumn(resistance.ResistanceColumn, new(resistance.ResistanceColumn, "Resistance (V/I)", PhysicalUnit.Ohm, ParameterAccess.Read),
                null, [.. axes, Elapsed.Id, resistance.VoltageColumn, resistance.CurrentColumn]);
            BindQuantity(resistance.ResistanceColumn, PhysicalUnit.Ohm, known);
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
        long Visit(ExperimentStep step, VariableFlowState known, IReadOnlyList<ParameterId> axes, int depth)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (depth > 128 || steps.Count >= 10_000) throw new ArgumentOutOfRangeException(nameof(plan), "Plan exceeds its depth or node limit.");
            if (!steps.Add(step.Id)) throw new ArgumentException($"Duplicate step ID {step.Id}.");
            if (step.Id == inspectStep && inspectPosition == PlanVariablePosition.BeforeStep) inspectedVariables = known.Capture(axes);
            if (activeCoolingDevices.Any(device => ChangesCoolingController(step, device))) throw new ArgumentException("Cooling children cannot reconfigure the active controller.");
            visiting.Push(step.Id);
            try { return step is TemperatureCoolingStep ? ValidateCooling((TemperatureCoolingStep)step, known, axes, depth) : step is ReadElectrodeContinuityStep ? ValidateContinuity((ReadElectrodeContinuityStep)step, known, axes) : step is ReadPolarizationStep ? ValidatePolarization((ReadPolarizationStep)step, known, axes) : step is ForEachStep ? ValidateIteration((ForEachStep)step, known, axes, depth) : VisitBody(step, known, axes, depth); }
            finally { visiting.Pop(); }
        }
        long VisitBody(ExperimentStep step, VariableFlowState known, IReadOnlyList<ParameterId> axes, int depth)
        {
            switch (step)
            {
                case TemperatureStabilityStep settings:
                    Device(settings.Device);
                    if (!registry.GetAxisDefinition(settings.Device).SupportsSoftwareTemperatureRamp)
                        throw new NotSupportedException("Controller has no temperature-stability profile.");
                    return 1;
                case WaitTemperatureStateStep stateWait:
                    Device(stateWait.Device);
                    if (!registry.GetAxisDefinition(stateWait.Device).SupportsSoftwareTemperatureRamp) throw new NotSupportedException("Controller has no temperature-state profile.");
                    return 1;
                case WaitFieldStep fieldWait:
                    Device(fieldWait.Device);
                    if (registry.GetAxisDefinition(fieldWait.Device).Target.Unit != PhysicalUnit.Tesla) throw new ArgumentException("Field waits require a Tesla controller.");
                    return 1;
                case WaitAngleStep angleWait:
                    Device(angleWait.Device); var angleDefinition = registry.GetAxisDefinition(angleWait.Device);
                    if (angleDefinition.Target.Unit != PhysicalUnit.Degree) throw new ArgumentException("Angle waits require a Degree controller.");
                    if (angleWait.Target is { } angleTarget)
                    {
                        if (ValueUnit(angleTarget, known) != PhysicalUnit.Degree) throw new ArgumentException("Angle target requires Degree.");
                        if (NumericPlanValue.TryConstant(angleTarget, out var constantAngle)) angleDefinition.Target.Validate(constantAngle);
                    }
                    return 1;
                case WaitTemperatureStep temperatureWait:
                    Device(temperatureWait.Device);
                    var temperatureDefinition = registry.GetAxisDefinition(temperatureWait.Device);
                    if (!temperatureDefinition.SupportsSoftwareTemperatureRamp || ValueUnit(temperatureWait.Target, known) != PhysicalUnit.Kelvin)
                        throw new ArgumentException("Temperature waits require a temperature-control profile and Kelvin target.");
                    if (NumericPlanValue.TryConstant(temperatureWait.Target, out var waitTarget)) temperatureDefinition.Target.Validate(waitTarget);
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
                    if (NumericPlanValue.TryConstant(hardwareTemperature.Target, out var hardwareTarget)) hardwareController.Target.ValidateWrite(hardwareTarget);
                    hardwareController.Rate.ValidateWrite(hardwareTemperature.Rate);
                    return 1;
                case SoftwareTemperatureRampStep temperature:
                    Device(temperature.Device);
                    var controller = registry.GetAxisDefinition(temperature.Device);
                    if (!controller.SupportsSoftwareTemperatureRamp || !temperature.EnableControl && !registry.SupportsTemperatureConfiguration(temperature.Device))
                        throw new NotSupportedException("Controller does not implement requested software temperature ramp capability.");
                    if (ValueUnit(temperature.Target, known) != PhysicalUnit.Kelvin) throw new ArgumentException("Temperature targets require Kelvin.");
                    if (NumericPlanValue.TryConstant(temperature.Target, out var temperatureTarget))
                    {
                        controller.Target.ValidateWrite(temperatureTarget);
                        if (temperature.Rate is not null) controller.Target.ValidateWrite(new(TemperatureRampPath.MilliKelvin(temperatureTarget) / 1000.0, PhysicalUnit.Kelvin));
                    }
                    // Live readback is not available at preflight: bound the path
                    // using the complete declared controller range.
                    return temperature.Rate is { } temperatureRate ? checked(1 + Math.Max(1, new TemperatureRampPath(controller.Target.Minimum!.Value, controller.Target.Maximum!.Value, temperatureRate).UpdateCount)) : 2;
                case HomeRotationStep home:
                    Device(home.Device);
                    if (!registry.GetAxisDefinition(home.Device).SupportsHoming) throw new NotSupportedException("Controller has no dedicated rotational homing capability.");
                    return 1;
                case ReadSr830FrameStep lockIn:
                    Device(lockIn.Device);
                    if (!registry.SupportsSr830Frame(lockIn.Device)) throw new NotSupportedException("Device has no passive SR830 frame capability.");
                    if (lockIn.IncludeReference && !registry.SupportsSr830Reference(lockIn.Device)) throw new NotSupportedException("Device has no reference readback capability.");
                    foreach (var channel in lockIn.Channels)
                    {
                        if (columns.ContainsKey(channel.Column)) throw new ArgumentException("Lock-in frame columns require distinct acquisition identities.");
                        lockInColumnOwners.Add(channel.Column, lockIn.Id);
                        var measured = Parameter(channel.Address, ParameterAccess.Read);
                        AddColumn(channel.Column, measured, channel.Address, [.. axes, Elapsed.Id]); BindQuantity(channel.Column, measured.Unit, known);
                    }
                    return 1;
                case ReadSourceSenseStep pair:
                    ValidateSourceSense(pair.Device, pair.Function, pair.VoltageColumn, pair.CurrentColumn, known, axes);
                    ValidateEnvironmentPolicy([pair.VoltageColumn, pair.CurrentColumn], pair.EnvironmentReads);
                    ValidateEnvironment(pair.EnvironmentReads, axes);
                    return 1;
                case CalculatedMeasurementStep calculation:
                    if (calculationProbe is { Kind: PlanStructureEditKind.Replace } replaceProbe && calculation.Id == replaceProbe.Target)
                    {
                        if (replaceProbe.Draft.Id != calculation.Id) throw new ArgumentException("A calculation replacement must retain its step ID.");
                        Visit(replaceProbe.Draft.Body, known, axes, depth + 1);
                        throw new CalculationInspectionComplete(replaceProbe, DescribeRowCalculations(replaceProbe, axes, known, columns));
                    }
                    var calculationCost = Visit(calculation.Body, known, axes, depth + 1);
                    var inputs = calculation.InputColumns.Concat(axes).Append(Elapsed.Id).ToHashSet();
                    foreach (var result in calculation.Columns)
                    {
                        var references = NumericPlanValue.Variables(result.Expression);
                        if (references.Any(reference => !inputs.Contains(reference)) || columns.ContainsKey(result.Column)) throw new ArgumentException("Calculated columns require current-row inputs, ordered results and globally unique result IDs.");
                        var resultUnit = NumericPlanValue.InferUnit(result.Expression, reference => columns[reference].Parameter.Unit);
                        AddColumn(result.Column, new(result.Column, result.Column.Value, resultUnit, ParameterAccess.Read), null, references.Concat(axes).Append(Elapsed.Id).Distinct().ToArray());
                        BindQuantity(result.Column, resultUnit, known); inputs.Add(result.Column);
                    }
                    return checked(1 + calculationCost);
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
                        if (!canBeUnavailable) BindQuantity(result.Column, unit, known);
                        else
                        {
                            if (known.TryGetValue(result.Column, out var previousUnit) && previousUnit != BlackboardValueType.Quantity(unit)) throw new ArgumentException($"Variable {result.Column} cannot change units.");
                            known.Remove(result.Column);
                        }
                    }
                    return analysisCost;
                case ConditionStep condition:
                    CheckCondition(condition.Condition, known); return 1;
                case MutateValueStep mutation:
                    var mutationUnit = mutation.Action == VariableMutation.Multiply && known.ContainsKey(mutation.Variable) ? ValueUnit(new VariableValue(mutation.Variable), known) : mutation.Action == VariableMutation.Multiply ? PhysicalUnit.Dimensionless : mutation.Value.Unit;
                    BindQuantity(mutation.Variable, mutationUnit, known); return 1;
                case OutcomeMutationStep hook:
                    var beforeHook = new VariableFlowState(known);
                    var firstBinding = bindings.Count;
                    var hookCost = Visit(hook.Body, known, axes, depth + 1);
                    // Definite-availability joins omit optional branch variables.
                    // A hook can still encounter their values on the chosen path.
                    foreach (var action in new[] { hook.Success, hook.Failure }.OfType<MutateValueStep>())
                    {
                        var expectedType = action.Action == VariableMutation.Multiply
                            ? known.GetValueOrDefault(action.Variable, beforeHook.GetValueOrDefault(action.Variable, BlackboardValueType.Quantity(PhysicalUnit.Dimensionless)))
                            : BlackboardValueType.Quantity(action.Value.Unit);
                        if (bindings.Skip(firstBinding).Any(binding => binding.Scope == variableScope && binding.Variable == action.Variable &&
                            (binding.Type.Kind != BlackboardValueKind.Quantity || binding.Type != expectedType)))
                            throw new ArgumentException("Outcome mutation cannot change a type or unit possibly established by the body.");
                    }
                    var successHookCost = hook.Success is { } successHook ? Visit(successHook, known, axes, depth + 1) : 0;
                    var failureHookCost = hook.Failure is { } failureHook ? Visit(failureHook, beforeHook, axes, depth + 1) : 0;
                    return checked(1 + hookCost + Math.Max(successHookCost, failureHookCost));
                case WhileStep loop:
                    CheckCondition(loop.Condition, known); hasDynamicLoops = true;
                    AddColumn(loop.IterationColumn, new(loop.IterationColumn, "While iteration (1-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    var bodyKnown = new VariableFlowState(known); BindQuantity(loop.IterationColumn, PhysicalUnit.Dimensionless, bodyKnown);
                    var loopBodyCost = Visit(loop.Body, bodyKnown, [.. axes, loop.IterationColumn], depth + 1);
                    RetainCommonVariables(known, [new VariableFlowState(known), bodyKnown]);
                    return checked(1 + loopBodyCost);
                case FallbackStep fallback:
                    AddColumn(fallback.BranchColumn, new(fallback.BranchColumn, "Fallback branch (1-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    AddColumn(fallback.TotalColumn, new(fallback.TotalColumn, "Fallback branch total", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    BindQuantity(fallback.BranchColumn, PhysicalUnit.Dimensionless, known); BindQuantity(fallback.TotalColumn, PhysicalUnit.Dimensionless, known);
                    var branches = new List<VariableFlowState>(); long fallbackCost = 1;
                    foreach (var child in fallback.Children)
                    {
                        var branch = new VariableFlowState(known); branches.Add(branch);
                        fallbackCost = checked(fallbackCost + Visit(child, branch, [.. axes, fallback.BranchColumn, fallback.TotalColumn], depth + 1));
                    }
                    RetainCommonVariables(known, branches); return fallbackCost;
                case RetryStep retry:
                    AddColumn(retry.AttemptColumn, new(retry.AttemptColumn, "Workflow attempt (1-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    AddColumn(retry.TotalColumn, new(retry.TotalColumn, "Maximum workflow attempts", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
                    BindQuantity(retry.AttemptColumn, PhysicalUnit.Dimensionless, known); BindQuantity(retry.TotalColumn, PhysicalUnit.Dimensionless, known);
                    return checked(1 + retry.MaxAttempts * Visit(retry.Body, known, [.. axes, retry.AttemptColumn, retry.TotalColumn], depth + 1));
                case ResultDecoratorStep decorator:
                    var beforeDecoration = new VariableFlowState(known);
                    var decorated = new VariableFlowState(known);
                    var decoratedCost = Visit(decorator.Body, decorated, axes, depth + 1);
                    // An IO-only child either completes its assignments or
                    // throws, even when the decorator changes its outcome.
                    if (!CanReportWorkflowFailure(decorator.Body))
                    { RetainCommonVariables(known, [decorated]); }
                    else RetainCommonVariables(known, [beforeDecoration, decorated]);
                    return checked(1 + decoratedCost);
                case TimeoutStep timeout:
                    return checked(1 + Visit(timeout.Body, known, axes, depth + 1));
                case SubtreeStep subtree:
                    var local = new VariableFlowState(known);
                    foreach (var input in subtree.Inputs) { local[input.Destination] = ValueType(new VariableValue(input.Source), known); if (trackDefinitions) local.Define(input.Destination, subtree.Id); }
                    long subtreeCost = 1;
                    variableScope++;
                    try { foreach (var child in subtree.Children) subtreeCost = checked(subtreeCost + Visit(child, local, axes, depth + 1)); }
                    finally { variableScope--; }
                    if (calculationProbe is { Kind: PlanStructureEditKind.Append } subtreeProbe && subtree.Id == subtreeProbe.Target)
                    {
                        variableScope++;
                        try
                        {
                            Visit(subtreeProbe.Draft.Body, local, axes, depth + 1);
                            throw new CalculationInspectionComplete(subtreeProbe, DescribeRowCalculations(subtreeProbe, axes, local, columns));
                        }
                        finally { variableScope--; }
                    }
                    if (subtree.Id == inspectStep && inspectPosition == PlanVariablePosition.AppendChild) inspectedVariables = local.Capture(axes);
                    foreach (var output in subtree.Outputs) BindVariable(output.Destination, ValueType(new VariableValue(output.Source), local), known);
                    return subtreeCost;
                case SequenceStep sequence:
                    var sequenceCost = checked(1 + sequence.Children.Sum(child => Visit(child, known, axes, depth + 1)));
                    if (calculationProbe is { Kind: PlanStructureEditKind.Append } sequenceProbe && sequence.Id == sequenceProbe.Target)
                    {
                        Visit(sequenceProbe.Draft.Body, known, axes, depth + 1);
                        throw new CalculationInspectionComplete(sequenceProbe, DescribeRowCalculations(sequenceProbe, axes, known, columns));
                    }
                    if (sequence.Id == inspectStep && inspectPosition == PlanVariablePosition.AppendChild) inspectedVariables = known.Capture(axes);
                    return sequenceCost;
                case RepeatStep repeat:
                    return checked(1 + repeat.Cycles * Visit(repeat.Body, known, axes, depth + 1));
                case ScanStep scan:
                    var axis = Parameter(scan.Axis, ParameterAccess.Write);
                    foreach (var point in scan.Values.Values) axis.ValidateWrite(point);
                    writes.Add((scan.Axis, scan.Values.Values));
                    if (axes.Contains(scan.Column)) throw new ArgumentException($"Scan variable {scan.Column} is already active.");
                    AddColumn(scan.Column, axis, scan.Axis, []);
                    BindQuantity(scan.Column, axis.Unit, known);
                    return checked(1 + scan.Values.Values.Count * Visit(scan.Body, known, [.. axes, scan.Column], depth + 1));
                case SetParameterStep set:
                    var parameter = Parameter(set.Address, ParameterAccess.Write);
                    if (parameter.Unit != ValueUnit(set.Value, known)) throw new ArgumentException($"{set.Address.Parameter} value has the wrong unit.");
                    if (NumericPlanValue.TryConstant(set.Value, out var literal))
                    { parameter.ValidateWrite(literal); writes.Add((set.Address, [literal])); }
                    else writes.Add((set.Address, null));
                    return 1;
                case AssignValueStep assign:
                    BindVariable(assign.Variable, ValueType(assign.Value, known), known);
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
                    ValidateElectrometerFunctions(read, registry);
                    if (read.RequiredRoute is { } readRoute)
                    {
                        Device(readRoute.Device);
                        var readMatrix = registry.GetMatrixDefinition(readRoute.Device);
                        readMatrix.Validate(readRoute.Closed);
                        if (read.ExcitationSource is { } excitationSource && !readMatrix.GuardedSources.Contains(excitationSource))
                            throw new ArgumentException("Read route must guard its declared excitation source.");
                        foreach (var guard in readMatrix.GuardedSources)
                            if (Device(guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Read route guards must use output-off policy.");
                    }
                    ValidateEnvironmentPolicy(read.Channels.Select(c => c.Column), read.EnvironmentReads);
                    foreach (var channel in read.Channels)
                    {
                        ArgumentNullException.ThrowIfNull(channel.Column);
                        var measurement = Parameter(channel.Address, ParameterAccess.Read);
                        AddColumn(channel.Column, measurement, channel.Address, [.. axes, Elapsed.Id]);
                        BindQuantity(channel.Column, measurement.Unit, known);
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
                    if (NumericPlanValue.TryConstant(wait.Target, out var target)) readback.Validate(target);
                    return 1;
                case ThresholdSwitchStep choice:
                    if (choice.Children.Count == 0) return 1;
                    var selectorType = ValueType(new VariableValue(choice.Variable), known);
                    if (selectorType.Kind != BlackboardValueKind.Quantity || choice.Thresholds.Any(q => q.Unit != selectorType.Unit))
                        throw new ArgumentException("Switch requires an available numeric selector with matching threshold units.");
                    VariableFlowState? switchCommon = null; long switchCost = 0;
                    foreach (var child in choice.Children)
                    {
                        var pathKnown = new VariableFlowState(known);
                        switchCost = Math.Max(switchCost, Visit(child, pathKnown, axes, depth + 1));
                        // Retain the common state incrementally, rather than
                        // holding one entire known-variable map per branch.
                        if (switchCommon is null) switchCommon = pathKnown;
                        else RetainCommonVariables(switchCommon, [switchCommon, pathKnown]);
                    }
                    RetainCommonVariables(known, [switchCommon!]);
                    return checked(1 + switchCost);
                case IfElseStep conditional:
                    CheckCondition(conditional.Condition, known);
                    var thenKnown = new VariableFlowState(known);
                    var elseKnown = new VariableFlowState(known);
                    var thenCost = Visit(conditional.Then, thenKnown, axes, depth + 1);
                    var elseCost = conditional.Else is { } otherwise ? Visit(otherwise, elseKnown, axes, depth + 1) : 0;
                    RetainCommonVariables(known, [thenKnown, elseKnown]);
                    return checked(1 + Math.Max(thenCost, elseCost));
                case BranchOnOutcomeStep branch:
                    var beforeCondition = new VariableFlowState(known);
                    var afterCondition = new VariableFlowState(known);
                    var conditionCost = Visit(branch.Condition, afterCondition, axes, depth + 1);
                    var failureKnown = new VariableFlowState(afterCondition);
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
    // Keep additional read checks outside the deep recursive visitor's stack frame.
    private static void ValidateElectrometerFunctions(ReadStep read, DeviceRegistry registry)
    {
        foreach (var deviceChannels in read.Channels.GroupBy(c => c.Address.Device))
            if (registry.Devices.SingleOrDefault(d => d.Id == deviceChannels.Key)?.Model == "K6514" &&
                deviceChannels.Select(c => Keithley6514Parameters.ReadingFunction(c.Address.Parameter)).Where(f => f is not null).Distinct().Count() > 1)
                throw new ArgumentException("One electrometer read step cannot acquire different functions; author explicit zero-check/function/read sequences.");
    }

    private static bool ChangesCoolingController(ExperimentStep step, InstrumentId device) => step switch
    {
        SetParameterStep write => write.Address.Device == device,
        ScanStep scan => scan.Axis.Device == device,
        TemperatureControlStep change => change.Device == device,
        TemperatureStabilityStep change => change.Device == device,
        HardwareTemperatureSetpointStep change => change.Device == device,
        SoftwareTemperatureRampStep change => change.Device == device,
        TemperatureCoolingStep cooling => cooling.Device == device,
        _ => false,
    };

}
