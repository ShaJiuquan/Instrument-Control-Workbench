using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep NumericBranch(JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        Fields(p, "variable", "op", "value", "iterator_value", "index", "total");
        var variable = Text(p, "variable");
        var op = NumericOperator(Text(p, "op", "gt"));
        // Explicit injection fields are validated even if this variable does
        // not consume them; silently dropping malformed context hides typos.
        foreach (var name in new[] { "iterator_value", "index", "total" })
            if (p.ContainsKey(name)) _ = Number(p, name);
        c.IteratorChildren.TryGetValue(path, out var iterator);
        var unit = PhysicalUnit.Dimensionless;
        PlanValue left;
        var injectedName = variable switch { "value" or "iterator_value" => "iterator_value", "index" => "index", "total" => "total", _ => null };
        if (injectedName is not null)
        {
            if (p.ContainsKey(injectedName)) left = new ConstantValue(new(Number(p, injectedName), unit));
            else if (iterator is not null) left = new VariableValue(new(injectedName == "iterator_value" ? iterator.Value : iterator.Value + "." + injectedName));
            else if (c.NumericVariables.ContainsKey(variable)) left = new VariableValue(new("ppms.axis." + variable));
            else throw new FormatException($"Variable '{variable}' has no injected iterator context.");
        }
        else
        {
            unit = NumericUnit(variable, c);
            left = new VariableValue(new("ppms.axis." + variable));
        }
        PlanValue right = p.ContainsKey("value") ? new ConstantValue(new(Number(p, "value"), unit))
            : iterator is not null ? new QuantityFromNumberValue(iterator, unit) : throw new FormatException("Numeric condition requires value.");
        var children = Children(node, path, c, depth);
        if (children.Count is not (1 or 2)) throw new FormatException("Numeric if_then_else requires Then and optional Else.");
        var conditionVariable = new ParameterId(path + ".condition.value");
        var condition = new ValueCondition(conditionVariable, op, right,
            op is ComparisonOperator.Equal or ComparisonOperator.NotEqual ? new Quantity(1e-9, unit) : null);
        return new SequenceStep(new(path), [new AssignValueStep(new(path + ".condition.input"), conditionVariable, left),
            new IfElseStep(new(path + ".branch"), condition, children[0], children.Count == 2 ? children[1] : null)]);
    }

    private static ComparisonOperator NumericOperator(string op) => op switch
    {
        "gt" or ">" => ComparisonOperator.Greater,
        "lt" or "<" => ComparisonOperator.Less,
        "gte" or ">=" => ComparisonOperator.GreaterOrEqual,
        "lte" or "<=" => ComparisonOperator.LessOrEqual,
        "eq" or "==" => ComparisonOperator.Equal,
        "neq" or "!=" => ComparisonOperator.NotEqual,
        _ => throw new FormatException("Unsupported numeric comparison operator."),
    };
    private static PhysicalUnit NumericUnit(string variable, ImportContext c) => variable switch
    {
        "field" => PhysicalUnit.Tesla,
        "temperature" => PhysicalUnit.Kelvin,
        "angle" => PhysicalUnit.Degree,
        "voltage" or "bias" => PhysicalUnit.Volt,
        "current" => PhysicalUnit.Ampere,
        "scan_time" => PhysicalUnit.Second,
        "scan_number" => PhysicalUnit.Dimensionless,
        _ when c.NumericVariables.TryGetValue(variable, out var unit) => unit,
        _ => throw new NotSupportedException($"Numeric variable '{variable}' has no migrated unit/axis contract."),
    };
    private static ValueCondition BlackboardCondition(JsonObject p, ImportContext c, string defaultOp)
    {
        Fields(p, "variable", "op", "value"); var variable = Text(p, "variable"); var unit = NumericUnit(variable, c);
        var op = NumericOperator(Text(p, "op", defaultOp));
        return new(new("ppms.axis." + variable), op, new ConstantValue(new(Number(p, "value"), unit)),
            op is ComparisonOperator.Equal or ComparisonOperator.NotEqual ? new Quantity(1e-9, unit) : null);
    }
    private static MutateValueStep DecodeMutation(JsonObject p, string path, ImportContext c)
    {
        Fields(p, "variable", "action", "value"); var variable = Text(p, "variable"); _ = new ParameterId(variable);
        var action = Text(p, "action", "set") switch
        {
            "set" => VariableMutation.Set,
            "add" => VariableMutation.Add,
            "sub" => VariableMutation.Subtract,
            "mul" => VariableMutation.Multiply,
            _ => throw new FormatException("Unsupported variable mutation action."),
        };
        c.NumericVariables.TryAdd(variable, PhysicalUnit.Dimensionless); var unit = NumericUnit(variable, c);
        c.NumericVariables[variable] = unit;
        return new(new(path), new("ppms.axis." + variable), action, new(Number(p, "value"), action == VariableMutation.Multiply ? PhysicalUnit.Dimensionless : unit));
    }

    private static ExperimentStep PureIterator(JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        var nested = p["points"] is JsonObject or JsonArray;
        if (nested) Fields(p, "points");
        else Fields(p, "mode", "points_mode", "start", "end", "points", "step", "values", "segments");
        var spec = nested ? p["points"]! : p.DeepClone();
        if (!nested && p.ContainsKey("points_mode"))
        {
            if (p.ContainsKey("mode") && Text(p, "mode") != Text(p, "points_mode")) throw new FormatException("Conflicting iterator point modes.");
            spec["mode"] = Text(p, "points_mode"); spec.AsObject().Remove("points_mode");
        }
        var values = Points(spec, PhysicalUnit.Dimensionless, c, 0);
        var iterator = new ParameterId(path + ".axis");
        var children = node["children"] as JsonArray ?? throw new FormatException("for_each requires children.");
        for (var i = 0; i < children.Count; i++) c.IteratorChildren[path + "." + i] = iterator;
        return new ForEachStep(new(path + ".iterator"), iterator, values, new SequenceStep(new(path + ".body"), Children(node, path, c, depth)), indexBase: IterationIndexBase.ZeroBased);
    }

    private static IEnumerable<ExperimentStep> ScanAxisVariables(string name, ParameterId column, string path)
    {
        var aliases = name switch
        {
            "scan_voltage" => new[] { "voltage", "bias" },
            "scan_number" or "scan_time" => new[] { name },
            _ => new[] { name[5..] },
        };
        return aliases.Select(alias => new AssignValueStep(new(path + ".publish." + alias), new("ppms.axis." + alias), new VariableValue(column)));
    }
}
