using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record RunArchiveCatalogCursor
{
    public RunArchiveCatalogCursor(DateTimeOffset startedAt, RunId id) { ArgumentNullException.ThrowIfNull(id); StartedAt = startedAt; Id = id; }
    public DateTimeOffset StartedAt { get; }
    public RunId Id { get; }
}

public sealed record RunArchiveCatalogQuery
{
    public RunArchiveCatalogQuery(int pageSize = 50, string? nameContains = null, SampleId? sampleId = null,
        DateTimeOffset? startedFrom = null, DateTimeOffset? startedBefore = null, MeasurementRunState? state = null,
        bool finalizedOnly = false, RunArchiveCatalogCursor? after = null, int maximumTextCharacters = 65_536)
    {
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (nameContains is { Length: > 1024 }) throw new ArgumentOutOfRangeException(nameof(nameContains));
        if (startedFrom > startedBefore) throw new ArgumentException("History time range is reversed.");
        if (state is { } value && !Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(state));
        if (maximumTextCharacters is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumTextCharacters));
        PageSize = pageSize; NameContains = nameContains; SampleId = sampleId; StartedFrom = startedFrom; StartedBefore = startedBefore;
        State = state; FinalizedOnly = finalizedOnly; After = after; MaximumTextCharacters = maximumTextCharacters;
    }
    public int PageSize { get; }
    public string? NameContains { get; }
    public SampleId? SampleId { get; }
    public DateTimeOffset? StartedFrom { get; }
    public DateTimeOffset? StartedBefore { get; }
    public MeasurementRunState? State { get; }
    public bool FinalizedOnly { get; }
    public RunArchiveCatalogCursor? After { get; }
    // Retained summary text and issue details each have this independent budget.
    public int MaximumTextCharacters { get; }
}

public sealed record RunArchiveReadVersion
{
    public RunArchiveReadVersion(RunId id, string headerSha256, long dataBytes, string dataSha256, string? finalSha256)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (dataBytes < 0) throw new ArgumentOutOfRangeException(nameof(dataBytes));
        Id = id; HeaderSha256 = NormalizeHash(headerSha256); DataBytes = dataBytes;
        DataSha256 = NormalizeHash(dataSha256); FinalSha256 = finalSha256 is null ? null : NormalizeHash(finalSha256);
    }
    public RunId Id { get; }
    public string HeaderSha256 { get; }
    public long DataBytes { get; }
    public string DataSha256 { get; }
    public string? FinalSha256 { get; }
    private static string NormalizeHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Archive version requires SHA-256 hexadecimal fingerprints.", nameof(value));
        return value.ToUpperInvariant();
    }
}

public sealed record RunArchiveRowQuery
{
    public RunArchiveRowQuery(int offset = 0, int pageSize = 500, RunArchiveReadVersion? expectedVersion = null, int maximumDataBytes = 16 * 1024 * 1024)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maximumDataBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumDataBytes));
        Offset = offset; PageSize = pageSize; ExpectedVersion = expectedVersion; MaximumDataBytes = maximumDataBytes;
    }
    public int Offset { get; }
    public int PageSize { get; }
    public RunArchiveReadVersion? ExpectedVersion { get; }
    // UTF-8 source record bytes; managed object overhead is additional.
    public int MaximumDataBytes { get; }
}
