using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class FieldWaitExecutionTests
{
    [Theory]
    [InlineData("wait_for")]
    [InlineData("state")]
    public async Task HoldingStateAliasAndDurationAliasUseActualPredicate(string alias)
    {
        var b = new Bench(); var plan = new PpmsExperimentPlanImporter().Parse(Node("wait_field", new() { [alias] = "holding", ["stable_duration_s"] = 0.01, ["timeout"] = 1 }), b.Bindings);
        var root = Assert.IsType<TimeoutStep>(plan.Root); Assert.Equal(TimeSpan.FromMilliseconds(10), Assert.IsType<WaitFieldStep>(root.Body).StableFor);
        await b.Runs.StartPlanAsync(plan, Token); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("negative_duration")]
    [InlineData("duration_aliases")]
    [InlineData("negative_tolerance")]
    [InlineData("state_aliases")]
    [InlineData("unknown_state")]
    [InlineData("null_tolerance")]
    [InlineData("text_tolerance")]
    [InlineData("target")]
    [InlineData("negative_timeout")]
    [InlineData("zero_poll")]
    [InlineData("fractional_poll")]
    [InlineData("oversized_poll")]
    [InlineData("extra")]
    public async Task MalformedFieldWaitCannotBeSilentlyClampedOrDefaulted(string fault)
    {
        var b = new Bench(); var p = new JsonObject();
        switch (fault)
        {
            case "negative_duration": p["duration_s"] = -1; break;
            case "duration_aliases": p["duration_s"] = 0; p["stable_duration_s"] = 0; break;
            case "negative_tolerance": p["tolerance_t"] = -1; break;
            case "state_aliases": p["wait_for"] = "holding"; p["state"] = "holding"; break;
            case "unknown_state": p["wait_for"] = "persistent"; break;
            case "null_tolerance": p["tolerance_t"] = null; break;
            case "text_tolerance": p["tolerance_t"] = "0.0005"; break;
            case "target": p["target_t"] = 0; break;
            case "negative_timeout": p["timeout_s"] = -1; break;
            case "zero_poll": p["poll_ms"] = 0; break;
            case "fractional_poll": p["poll_ms"] = 1.5; break;
            case "oversized_poll": p["poll_ms"] = (double)uint.MaxValue; break;
            case "extra": p["vendor_status"] = "holding"; break;
        }
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(Node("wait_field", p), b.Bindings)); Assert.Contains("source line 9", error.Message);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task UnitAndCapabilityMismatchRejectBeforeIoAndDefaultTimeoutIsExplicit()
    {
        var b = new Bench(); var angle = MockEnvironmentCatalog.Angle(); var wrong = new ExperimentImportBindings([], [KeyValuePair.Create("field", angle.Definition)]);
        Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(Node("wait_field"), wrong)); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
        var plan = new PpmsExperimentPlanImporter().Parse(Node("wait_field"), b.Bindings); Assert.Equal(TimeSpan.FromSeconds(3600), Assert.IsType<TimeoutStep>(plan.Root).Timeout);
    }
    [Theory]
    [InlineData("poll")]
    [InlineData("tolerance")]
    [InlineData("duration")]
    [InlineData("unknown")]
    [InlineData("target")]
    public void NativeFieldWaitFormatHasNoFabricatedTargetOrState(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var json = JsonNode.Parse(codec.Serialize(new("Field wait", new WaitFieldStep(new("wait"), new("field"), new(0.0005, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(100)))))!;
        var root = json["root"]!;
        switch (damage)
        {
            case "poll": root["pollSeconds"] = 0; break;
            case "tolerance": root["tolerance"]!["unit"] = "Degree"; break;
            case "duration": root["stableSeconds"] = -1; break;
            case "unknown": root["wait_for"] = "holding"; break;
            case "target": root["target"] = 0; break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(json.ToJsonString()));
    }
}
