using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Theory]
    [InlineData("V")]
    [InlineData("I")]
    [InlineData("R")]
    public async Task LiveInvalidPointRemovesSeededValuesBeforeResultDelivery(string seeded)
    {
        var listener = new SampleRowListener(); await using var b = new Bench(listener);
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Missing frame");
        RunBlackboardObservation? delivered = null;
        listener.Row = async row =>
        {
            Assert.Single(row.AcquisitionFailures);
            delivered = await b.Runs.ObserveBlackboardAsync(b.Runs.ActiveRunId!);
            Assert.False(delivered.IsTerminal); Assert.False(delivered.Snapshot!.Entries.ContainsKey(new(seeded)));
            Assert.DoesNotContain(delivered.Snapshot.Entries.Keys, key => key.Value is "V" or "I" or "R");
        };
        var unit = seeded == "V" ? PhysicalUnit.Volt : seeded == "I" ? PhysicalUnit.Ampere : PhysicalUnit.Ohm;
        var original = RecoveryPlan(b); var plan = new ExperimentPlan("Invalid live value", new SequenceStep(new("outer"),
            [new AssignValueStep(new("seed"), new(seeded), new ConstantValue(new(1, unit))), original.Root]), currentSourcePreparations: [b.Preparation]);
        await b.Runs.StartPlanAsync(plan, Token); Assert.NotNull(delivered);
        var final = await b.Runs.ObserveBlackboardAsync(b.Runs.LatestOutcome!.Id);
        Assert.True(final.IsTerminal); Assert.Same(b.Runs.LatestBlackboardSnapshot, final.Snapshot); Assert.Empty(final.Snapshot!.Entries);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
}
