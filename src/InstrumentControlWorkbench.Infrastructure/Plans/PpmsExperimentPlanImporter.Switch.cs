using System.Globalization;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep ThresholdSwitch(JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        foreach (var field in p)
        {
            if (field.Key == "variable") continue;
            if (!field.Key.StartsWith("case_", StringComparison.Ordinal) ||
                !int.TryParse(field.Key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index is < 1 or > 10_000 || field.Key != "case_" + index.ToString(CultureInfo.InvariantCulture))
                throw new FormatException($"Unsupported switch parameter '{field.Key}'.");
            _ = Number(p, field.Key); // Validate even unused/gapped fields; never silently accept malformed thresholds.
        }
        var children = Children(node, path, c, depth);
        // PPMS exits before reading params/axes for an empty switch.
        if (children.Count == 0) return new SequenceStep(new(path), []);
        var variable = Text(p, "variable"); var unit = PhysicalUnit.Dimensionless;
        ParameterId selector;
        ParameterId? iterator = null;
        // Iterator context belongs to the closest containing child subtree,
        // including switches below intermediate sequence/decorator nodes.
        for (var contextPath = path; contextPath.Length != 0;)
        {
            if (c.IteratorChildren.TryGetValue(contextPath, out iterator)) break;
            var dot = contextPath.LastIndexOf('.'); if (dot < 0) break; contextPath = contextPath[..dot];
        }
        if (iterator is not null && variable is "value" or "iterator_value" or "index" or "total")
            selector = new(variable is "value" or "iterator_value" ? iterator.Value : iterator.Value + "." + variable);
        else
        {
            unit = NumericUnit(variable, c); selector = new("ppms.axis." + variable);
        }
        var thresholds = new List<Quantity>();
        for (var index = 1; index <= children.Count; index++)
        {
            var key = "case_" + index.ToString(CultureInfo.InvariantCulture);
            if (!p.ContainsKey(key)) break; // PPMS only consumes the contiguous case_1.. prefix.
            thresholds.Add(new(Number(p, key), unit));
        }
        return new ThresholdSwitchStep(new(path), selector, children, thresholds);
    }
}
