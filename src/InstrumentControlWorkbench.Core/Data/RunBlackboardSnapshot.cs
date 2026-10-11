using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

// A frozen run-owned observation, also used for terminal history snapshots.
public sealed record RunBlackboardSnapshot
{
    public const int MaximumEntries = 100_000;
    public RunBlackboardSnapshot(RunId id, DateTimeOffset capturedAt, TimeSpan elapsed, IEnumerable<KeyValuePair<ParameterId, BlackboardEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(id); ArgumentNullException.ThrowIfNull(entries);
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        var copy = new Dictionary<ParameterId, BlackboardEntry>();
        foreach (var item in entries)
        {
            if (copy.Count >= MaximumEntries || item.Key is null || item.Value is null || item.Value.Elapsed > elapsed || !copy.TryAdd(item.Key, item.Value))
                throw new ArgumentException("Blackboard snapshot requires bounded, distinct, non-null entries no later than its monotonic capture time.", nameof(entries));
        }
        Id = id; CapturedAt = capturedAt; Elapsed = elapsed;
        Entries = new ReadOnlyDictionary<ParameterId, BlackboardEntry>(copy);
    }
    public RunId Id { get; }
    public DateTimeOffset CapturedAt { get; }
    public TimeSpan Elapsed { get; }
    public IReadOnlyDictionary<ParameterId, BlackboardEntry> Entries { get; }
}
