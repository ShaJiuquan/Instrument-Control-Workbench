using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanOutlineTests
{
    [Theory]
    [InlineData("fallback")]
    [InlineData("retry")]
    [InlineData("timeout")]
    [InlineData("subtree")]
    [InlineData("while")]
    [InlineData("hook")]
    [InlineData("outcome")]
    [InlineData("switch")]
    [InlineData("point")]
    [InlineData("decorator")]
    public async Task ValidatedNestedControlsKeepTheirAuthoredChildrenVisibleAndTraceable(string wrapper)
    {
        var hub = new NotificationHub(); var source = new MockKeithley2450SourceMeter(); var manager = new InstrumentManager(source, hub);
        var field = MockEnvironmentCatalog.Field(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([manager, new AxisControllerSession(field, hub)], leases);
        var runs = new MeasurementRunService(manager, leases, hub, deviceRegistry: registry);
        ExperimentStep leaf = wrapper is "fallback" or "retry" or "decorator"
            ? new ConditionStep(new("leaf"), new(new("value"), ComparisonOperator.Equal, new ConstantValue(new(2, PhysicalUnit.Dimensionless))))
            : wrapper == "point" ? new ReadStep(new("leaf"), [new(new("field"), new(field.Definition.Device.Id, field.Definition.Readback.Id))])
            : new DelayStep(new("leaf"), TimeSpan.Zero);
        ExperimentStep body = wrapper switch
        {
            "fallback" => new FallbackStep(new("control"), [leaf, new SequenceStep(new("alternative"), [])]),
            "retry" => new RetryStep(new("control"), 2, TimeSpan.Zero, leaf),
            "timeout" => new TimeoutStep(new("control"), TimeSpan.FromSeconds(1), leaf),
            "subtree" => new SubtreeStep(new("control"), [leaf]),
            "while" => new WhileStep(new("control"), new(new("value"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))), leaf),
            "hook" => new OutcomeMutationStep(new("control"), leaf, new(new("success"), new("value"), VariableMutation.Add, new(1, PhysicalUnit.Dimensionless)), new(new("failure"), new("value"), VariableMutation.Subtract, new(1, PhysicalUnit.Dimensionless))),
            "outcome" => new BranchOnOutcomeStep(new("control"), new ConditionStep(new("condition"), new(new("value"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless)))), leaf, new SequenceStep(new("alternative"), [])),
            "switch" => new ThresholdSwitchStep(new("control"), new("value"), [leaf, new SequenceStep(new("alternative"), [])], [new(0, PhysicalUnit.Dimensionless)]),
            "point" => new MeasurementGroupStep(new("control"), [new("field", new SequenceStep(new("member"), [new DelayStep(new("setup"), TimeSpan.Zero), leaf])),
                new("other field", new ReadStep(new("other_read"), [new(new("other_field"), new(field.Definition.Device.Id, field.Definition.Readback.Id))]))]),
            _ => new ResultDecoratorStep(new("control"), ResultEffect.ForceSuccess, leaf),
        };
        var plan = new ExperimentPlan("Nested controls", new SequenceStep(new("root"), [new AssignValueStep(new("assign"), new("value"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))), body]));
        await using var vm = new PlanWorkspaceViewModel(new(new JsonExperimentPlanDocument(), runs), plan, "unused.json", () => true, () => { });
        var child = Assert.Single(vm.Outline, n => n.Id == leaf.Id); Assert.True(vm.IsValidated);
        if (wrapper == "hook") { Assert.Contains(vm.Outline, n => n.Id.Value == "success"); Assert.Contains(vm.Outline, n => n.Id.Value == "failure"); }
        if (wrapper == "outcome") Assert.Contains(vm.Outline, n => n.Id.Value == "condition");
        if (wrapper == "point") Assert.Contains(vm.Outline, n => n.Id.Value == "other_read");
        vm.BeginRun(); vm.ApplyFact(new(WorkbenchNotificationKind.RunStarted, "started", RunId: "own"));
        vm.ApplyFact(new(WorkbenchNotificationKind.StepStarted, "leaf", RunId: "foreign", Step: leaf.Id)); Assert.False(child.IsActive);
        vm.ApplyFact(new(WorkbenchNotificationKind.StepStarted, "leaf", RunId: "own", Step: leaf.Id)); Assert.True(child.IsActive); Assert.Equal("leaf", vm.CurrentStep);
        vm.ApplyFact(new(WorkbenchNotificationKind.RunCompleted, "finished", RunId: "own")); Assert.False(child.IsActive);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
}
