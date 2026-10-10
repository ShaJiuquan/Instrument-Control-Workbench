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

public sealed record ExperimentPlanPreview(DataSetSchema Schema, IReadOnlyList<InstrumentId> Devices, long MaximumStepExecutions);
internal sealed record CompiledExperiment(ExperimentPlan Plan, DataSetSchema Schema, IReadOnlyList<InstrumentId> Devices, long MaximumStepExecutions, IReadOnlyDictionary<StepId, ResolvedSampleRoute> SampleRoutes);

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
        var steps = new HashSet<StepId>();
        var variables = new Dictionary<ParameterId, PhysicalUnit>();
        var preparations = plan.Preparations.ToDictionary(p => p.Device);
        var sampleRoutes = new Dictionary<StepId, ResolvedSampleRoute>();
        var writes = new List<(ParameterAddress Address, IReadOnlyList<Quantity>? Values)>();
        foreach (var preparation in plan.Preparations)
        {
            var device = Source(preparation.Device);
            Keithley2450Limits.Validate(preparation.ToSetpoint());
            var voltage = preparation.Function == SourceFunction.Voltage;
            device.GetParameter(voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id).ValidateWrite(preparation.Level);
            device.GetParameter(voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id).ValidateWrite(preparation.Compliance);
            device.GetParameter(Keithley2450Parameters.Nplc.Id).ValidateWrite(new(preparation.Sense.Nplc, PhysicalUnit.Dimensionless));
        }
        var cost = Visit(plan.Root, variables, [], 0);
        if (cost > 10_000_000) throw new ArgumentOutOfRangeException(nameof(plan), "Plan exceeds the 10,000,000 step execution budget.");
        // Check coupled ranges statically only when the counterpart is fixed.
        // Plans that change both source fields are checked against actual device
        // configuration at each write, rather than rejected using stale preparation.
        var changedAddresses = writes.Select(w => w.Address).ToHashSet();
        foreach (var write in writes)
        {
            if (!preparations.TryGetValue(write.Address.Device, out var preparation) || write.Values is null) continue;
            var voltage = preparation.Function == SourceFunction.Voltage;
            var levelId = voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id;
            var complianceId = voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id;
            var levelWrite = write.Address.Parameter == levelId;
            if (!levelWrite && write.Address.Parameter != complianceId) continue;
            var counterpart = levelWrite ? complianceId : levelId;
            if (changedAddresses.Contains(new(write.Address.Device, counterpart))) continue;
            foreach (var value in write.Values)
                Keithley2450Limits.Validate(levelWrite ? preparation.ToSetpoint() with { Level = value.Value } :
                    preparation.ToSetpoint() with { Compliance = value.Value });
        }
        return new(plan, new(columns.Values), Array.AsReadOnly(needed.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()), cost, new ReadOnlyDictionary<StepId, ResolvedSampleRoute>(sampleRoutes));

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
                _ => throw new NotSupportedException($"Unsupported value expression {value.GetType().Name}."),
            };
        }
        void BindVariable(ParameterId id, PhysicalUnit unit, Dictionary<ParameterId, PhysicalUnit> known)
        {
            ArgumentNullException.ThrowIfNull(id);
            if (known.TryGetValue(id, out var existing) && existing != unit) throw new ArgumentException($"Variable {id} cannot change units.");
            known[id] = unit;
        }
        void AddColumn(ParameterId id, ParameterDescriptor parameter, ParameterAddress? source, IReadOnlyList<ParameterId> dependencies)
        {
            if (id == Elapsed.Id) throw new ArgumentException("The elapsed column is reserved.");
            var descriptor = new ParameterDescriptor(id, parameter.Label, parameter.Unit, ParameterAccess.Read, parameter.Minimum, parameter.Maximum);
            if (columns.TryGetValue(id, out var previous))
            {
                if (previous.Parameter != descriptor || previous.Source != source || !previous.DependsOn.SequenceEqual(dependencies))
                    throw new ArgumentException($"Column {id} has inconsistent sources or scan dependencies; use a distinct column ID.");
                return;
            }
            columns.Add(id, new(descriptor, dependencies, optional: true, source: source));
        }
        void CheckCondition(ValueCondition condition, Dictionary<ParameterId, PhysicalUnit> known)
        {
            ArgumentNullException.ThrowIfNull(condition);
            if (!Enum.IsDefined(condition.Operator)) throw new ArgumentOutOfRangeException(nameof(condition.Operator));
            var left = ValueUnit(new VariableValue(condition.Variable), known);
            if (left != ValueUnit(condition.Value, known)) throw new ArgumentException("Comparison operands must have matching units.");
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
        void ValidateResistance(ReadResistanceStep resistance, Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<ParameterId> axes)
        {
            ValidateSourceSense(resistance.Device, resistance.Function, resistance.VoltageColumn, resistance.CurrentColumn, known, axes);
            AddColumn(resistance.ResistanceColumn, new(resistance.ResistanceColumn, "Resistance (V/I)", PhysicalUnit.Ohm, ParameterAccess.Read),
                null, [.. axes, Elapsed.Id, resistance.VoltageColumn, resistance.CurrentColumn]);
            BindVariable(resistance.ResistanceColumn, PhysicalUnit.Ohm, known);
        }
        long Visit(ExperimentStep step, Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<ParameterId> axes, int depth)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (depth > 128 || steps.Count >= 10_000) throw new ArgumentOutOfRangeException(nameof(plan), "Plan exceeds its depth or node limit.");
            if (!steps.Add(step.Id)) throw new ArgumentException($"Duplicate step ID {step.Id}.");
            switch (step)
            {
                case SoftwareTemperatureRampStep temperature:
                    Device(temperature.Device);
                    var controller = registry.GetAxisDefinition(temperature.Device);
                    if (!controller.SupportsSoftwareTemperatureRamp) throw new NotSupportedException("Controller does not implement software temperature ramps.");
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
                    return 1;
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
                        if (result.Kind is not (SampleDerivedQuantity.SheetResistance or SampleDerivedQuantity.Resistivity)) BindVariable(result.Column, unit, known);
                        else
                        {
                            if (known.TryGetValue(result.Column, out var previousUnit) && previousUnit != unit) throw new ArgumentException($"Variable {result.Column} cannot change units.");
                            known.Remove(result.Column);
                        }
                    }
                    return analysisCost;
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
                    AddColumn(loop.Variable, new(loop.Variable, "Iterator value", loop.Values.Unit, ParameterAccess.Read), null, []);
                    AddColumn(index, new(index, "Iterator index (1-based)", PhysicalUnit.Dimensionless, ParameterAccess.Read), null, []);
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
                case RouteMatrixStep route:
                    Device(route.Device);
                    var matrix = registry.GetMatrixDefinition(route.Device);
                    matrix.Validate(route.Closed);
                    foreach (var guardedSource in matrix.GuardedSources)
                        if (Device(guardedSource).StopPolicy != DeviceStopPolicy.OutputOff)
                            throw new ArgumentException($"{guardedSource}: matrix excitation guard must use output-off policy.");
                    return 1;
                case SourceOutputStep output:
                    Source(output.Device);
                    return 1;
                case ReadStep read:
                    foreach (var channel in read.Channels)
                    {
                        ArgumentNullException.ThrowIfNull(channel.Column);
                        var measurement = Parameter(channel.Address, ParameterAccess.Read);
                        AddColumn(channel.Column, measurement, channel.Address, [.. axes, Elapsed.Id]);
                        BindVariable(channel.Column, measurement.Unit, known);
                    }
                    return 1;
                case ReadResistanceStep resistance:
                    ValidateResistance(resistance, known, axes);
                    return 1;
                case ReadSampleResistanceStep sampleRead:
                    var context = (plan.Routing ?? throw new ArgumentException("Sample measurements require a frozen routing configuration.")).Resolve(sampleRead.Binding);
                    if (context.SourceMeter != sampleRead.Acquisition.Device) throw new ArgumentException("Sample wiring belongs to a different source meter.");
                    ValidateResistance(sampleRead.Acquisition, known, axes);
                    if (preparations[sampleRead.Acquisition.Device].Sense.Mode != context.SensingMode)
                        throw new ArgumentException("Source preparation sensing mode does not match the selected sample topology.");
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
                    foreach (var item in thenKnown)
                        if (elseKnown.TryGetValue(item.Key, out var unit) && unit == item.Value) BindVariable(item.Key, unit, known);
                    return checked(1 + Math.Max(thenCost, elseCost));
                default:
                    throw new NotSupportedException($"No executor exists for {step.GetType().Name}.");
            }
        }
    }
}
