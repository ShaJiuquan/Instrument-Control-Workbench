using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task InjectedPointThresholdIsExplicitlyQualifiedForPhysicalAxis()
    {
        var b = new Bench(); var condition = NumericChoice("field", "gt", 0); condition["params"]!.AsObject().Remove("value");
        var iterator = Node("for_each", new() { ["points"] = new JsonArray(-0.01, 0.01) }, condition);
        var scan = Node("scan", new() { ["points"] = new JsonArray(0.0), ["wait_mode"] = "reached" }, iterator); scan["name"] = "scan_field";
        var document = b.Workspace.Import(scan.ToJsonString(), b.Bindings); Assert.Contains("quantityFromNumber", document.Text);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(document.Text).Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(2, rows.Count); Assert.True(rows[0].Values.ContainsKey(new("ppms.root.0.0.0.0.value"))); Assert.True(rows[1].Values.ContainsKey(new("ppms.root.0.0.1.0.value")));
        Assert.All(rows, b.Runs.LatestDataSet.Schema.ValidateRow);
        Assert.All(rows, row => Assert.Equal(PhysicalUnit.Dimensionless, row.Values[new("ppms.root.0.axis")].Unit));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("physical")]
    public async Task QuantityQualificationCannotInventOrRelabelPhysicalInput(string invalid)
    {
        var b = new Bench(); var steps = new List<ExperimentStep>();
        if (invalid == "physical") steps.Add(Local("seed", "number", new(1, PhysicalUnit.Volt)));
        steps.Add(new AssignValueStep(new("qualify"), new("target"), new QuantityFromNumberValue(new("number"), PhysicalUnit.Tesla)));
        steps.Add(b.ReadField("after"));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("Invalid qualification", new SequenceStep(new("root"), steps))));
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unknown_unit")]
    [InlineData("missing_variable")]
    [InlineData("extra")]
    [InlineData("mixed")]
    public void NativeQuantityQualificationRejectsMalformedExpression(string invalid)
    {
        var codec = new JsonExperimentPlanDocument(); var plan = new ExperimentPlan("Quantity", new AssignValueStep(new("assign"), new("value"), new QuantityFromNumberValue(new("number"), PhysicalUnit.Tesla)));
        var node = JsonNode.Parse(codec.Serialize(plan))!.AsObject(); var value = node["root"]!["value"]!.AsObject(); var qualified = value["quantityFromNumber"]!.AsObject();
        switch (invalid)
        {
            case "null": value["quantityFromNumber"] = null; break;
            case "unknown_unit": qualified["unit"] = "99"; break;
            case "missing_variable": qualified.Remove("variable"); break;
            case "extra": qualified["scale"] = 2; break;
            case "mixed": value["variable"] = "other"; break;
        }
        Assert.Throws<FormatException>(() => codec.Parse(node.ToJsonString()));
    }
}
