using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class RunBlackboardSnapshotTests
{
    private static readonly StepId Source = new("seed");
    private static BlackboardEntry Entry => new(new BlackboardBoolean(true), Source, DateTimeOffset.UtcNow, TimeSpan.Zero);
    private static RunDataSet Data(RunId id) => new(id, "snapshot", DateTimeOffset.UtcNow,
        new([new(new(new("elapsed"), "Elapsed", PhysicalUnit.Second, ParameterAccess.Read))]), new Dictionary<string, Quantity>(), [],
        new ExperimentPlan("snapshot", new AssignValueStep(Source, new("flag"), new BooleanValue(true))));

    [Fact]
    public void SnapshotFreezesCallerDictionaryAndRetainsRunIdentityAndMonotonicTime()
    {
        var id = RunId.New(); var entries = new Dictionary<ParameterId, BlackboardEntry> { [new("flag")] = Entry };
        var snapshot = new RunBlackboardSnapshot(id, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), entries);
        entries.Clear(); Assert.Single(snapshot.Entries); Assert.Equal(id, snapshot.Id);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<ParameterId, BlackboardEntry>)snapshot.Entries).Clear());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("null_key")]
    [InlineData("null_entry")]
    [InlineData("future_elapsed")]
    public void InvalidEntriesCannotBecomeHistoricalSnapshots(string damage)
    {
        var entry = Entry;
        var entries = damage switch
        {
            "duplicate" => new[] { KeyValuePair.Create(new ParameterId("flag"), entry), KeyValuePair.Create(new ParameterId("flag"), entry) },
            "null_key" => [new KeyValuePair<ParameterId, BlackboardEntry>(null!, entry)],
            "null_entry" => [new KeyValuePair<ParameterId, BlackboardEntry>(new("flag"), null!)],
            _ => [new KeyValuePair<ParameterId, BlackboardEntry>(new("flag"), new(new BlackboardBoolean(true), Source, DateTimeOffset.UtcNow, TimeSpan.FromTicks(1)))],
        };
        Assert.Throws<ArgumentException>(() => new RunBlackboardSnapshot(RunId.New(), DateTimeOffset.UtcNow, TimeSpan.Zero, entries));
    }

    [Fact]
    public void IdentityElapsedAndEntryCountAreBounded()
    {
        Assert.Throws<ArgumentNullException>(() => new RunBlackboardSnapshot(null!, DateTimeOffset.UtcNow, TimeSpan.Zero, []));
        Assert.Throws<ArgumentNullException>(() => new RunBlackboardSnapshot(RunId.New(), DateTimeOffset.UtcNow, TimeSpan.Zero, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunBlackboardSnapshot(RunId.New(), DateTimeOffset.UtcNow, TimeSpan.FromTicks(-1), []));
        Assert.Throws<ArgumentException>(() => new RunBlackboardSnapshot(RunId.New(), DateTimeOffset.UtcNow, TimeSpan.Zero,
            Enumerable.Range(0, RunBlackboardSnapshot.MaximumEntries + 1).Select(i => KeyValuePair.Create(new ParameterId("entry" + i), Entry))));
    }

    [Theory]
    [InlineData("wrong_run")]
    [InlineData("missing_source")]
    [InlineData("wrong_value")]
    [InlineData("wrong_variable")]
    [InlineData("wrong_kind")]
    public void FailedSnapshotValidationDoesNotPartiallyFinalizeDataSet(string damage)
    {
        var id = RunId.New(); var data = Data(id);
        var entry = new BlackboardEntry(new BlackboardBoolean(damage != "wrong_value"), damage == "missing_source" ? new("missing") : Source,
            DateTimeOffset.UtcNow, TimeSpan.Zero, damage == "wrong_kind" ? BlackboardWriteKind.SubtreeOutput : BlackboardWriteKind.Step);
        var snapshot = new RunBlackboardSnapshot(damage == "wrong_run" ? RunId.New() : id, DateTimeOffset.UtcNow, TimeSpan.Zero,
            [KeyValuePair.Create(new ParameterId(damage == "wrong_variable" ? "other" : "flag"), entry)]);
        Assert.Throws<ArgumentException>(() => data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow, snapshot));
        Assert.Equal(MeasurementRunState.Running, data.State); Assert.Null(data.Capture().Blackboard);
        data.Complete(MeasurementRunState.Faulted, DateTimeOffset.UtcNow); Assert.Equal(MeasurementRunState.Faulted, data.State);
    }

    [Fact]
    public void WallClockChangesDoNotInvalidateMonotonicProvenance()
    {
        var id = RunId.New(); var data = Data(id);
        var entry = new BlackboardEntry(new BlackboardBoolean(true), Source, DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromTicks(1));
        var snapshot = new RunBlackboardSnapshot(id, DateTimeOffset.UtcNow.AddDays(-1), TimeSpan.FromTicks(2), [KeyValuePair.Create(new ParameterId("flag"), entry)]);
        data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow, snapshot);
        Assert.Same(snapshot, data.Capture().Blackboard); Assert.True(entry.UpdatedAt > snapshot.CapturedAt);
    }
}
