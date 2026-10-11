using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Core.Data;

// Metadata projection: no full plan/schema/notes/attributes/blackboard/rows retained.
public sealed record RunArchiveSummary(RunId Id, string Name, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    MeasurementRunState State, int? RowCount, long? DataBytes, SampleId? SampleId, string? SampleName,
    int InstrumentCount, bool HasPlan, int? BlackboardEntryCount)
{
    public bool IsFinalized => FinishedAt is not null;
    public long TextCharacters => (long)Name.Length + (SampleName?.Length ?? 0) + (SampleId?.Value.Length ?? 0);
}
public sealed record RunArchiveCatalogPage(IReadOnlyList<RunArchiveSummary> Runs, IReadOnlyList<RunArchiveIssue> Issues,
    RunArchiveCatalogCursor? NextCursor, long MatchingRuns, long ScannedRuns, long IssueCount)
{
    public bool HasMore => NextCursor is not null;
    public bool HasOmittedIssues => IssueCount > Issues.Count;
}

// Every record in the captured byte boundary is validated before this returns.
// An unfinalized boundary remains unfinalized even if the writer later finishes.
public sealed record ArchivedRunPage(RunArchiveMetadata Metadata, int Offset, IReadOnlyList<MeasurementDataRow> Rows,
    int ObservedRowCount, bool HasIncompleteTail, RunArchiveReadVersion Version, int ReturnedDataBytes)
{
    public bool HasMore => (long)Offset + Rows.Count < ObservedRowCount;
    public int? NextOffset => HasMore ? checked(Offset + Rows.Count) : null;
    public long FirstRowIndex => (long)Offset + 1;
}
