using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Experiments;

internal sealed record CompiledExperiment(ExperimentPlan Plan, DataSetSchema Schema, IReadOnlyList<InstrumentId> Devices);

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
        return new(plan, new(columns.Values), Array.AsReadOnly(needed.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()));

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
        long Visit(ExperimentStep step, Dictionary<ParameterId, PhysicalUnit> known, IReadOnlyList<ParameterId> axes, int depth)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (depth > 128 || steps.Count >= 10_000) throw new ArgumentOutOfRangeException(nameof(plan), "Plan exceeds its depth or node limit.");
            if (!steps.Add(step.Id)) throw new ArgumentException($"Duplicate step ID {step.Id}.");
            switch (step)
            {
                case SequenceStep sequence:
                    return checked(1 + sequence.Children.Sum(child => Visit(child, known, axes, depth + 1)));
                case RepeatStep repeat:
                    return checked(1 + repeat.Cycles * Visit(repeat.Body, known, axes, depth + 1));
                case ScanStep scan:
                    var axis = Parameter(scan.Axis, ParameterAccess.Write);
                    foreach (var point in scan.Values.Values) axis.ValidateWrite(point);
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
                    if (set.Value is ConstantValue literal) parameter.ValidateWrite(literal.Value);
                    return 1;
                case AssignValueStep assign:
                    BindVariable(assign.Variable, ValueUnit(assign.Value, known), known);
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
