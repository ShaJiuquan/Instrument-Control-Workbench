using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class RunArchiveQueryTests
{
    [Fact]
    public void ReadVersionsRejectMalformedOrOwnerlessFingerprintsAndNormalizeHexCase()
    {
        var id = RunId.New(); var hash = new string('a', 64);
        var lower = new RunArchiveReadVersion(id, hash, 0, hash, hash); var upper = new RunArchiveReadVersion(id, hash.ToUpperInvariant(), 0, hash.ToUpperInvariant(), hash.ToUpperInvariant());
        Assert.Equal(lower, upper); Assert.NotEqual(lower, new(id, hash, 1, hash, hash));
        Assert.Throws<ArgumentNullException>(() => new RunArchiveReadVersion(null!, hash, 0, hash, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveReadVersion(id, hash, -1, hash, null));
        Assert.Throws<ArgumentException>(() => new RunArchiveReadVersion(id, "short", 0, hash, null));
        Assert.Throws<ArgumentException>(() => new RunArchiveReadVersion(id, hash, 0, new string('z', 64), null));
        Assert.Throws<ArgumentException>(() => new RunArchiveReadVersion(id, hash, 0, hash, ""));
    }

    [Fact]
    public void UnboundedPageRequestsAndReversedOrUndefinedFiltersAreRejectedBeforeStorage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveRowQuery(offset: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveRowQuery(pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveRowQuery(pageSize: 2001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveRowQuery(maximumDataBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveRowQuery(maximumDataBytes: 64 * 1024 * 1024 + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveCatalogQuery(pageSize: 201));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveCatalogQuery(pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveCatalogQuery(nameContains: new string('x', 1025)));
        Assert.Throws<ArgumentException>(() => new RunArchiveCatalogQuery(startedFrom: DateTimeOffset.UnixEpoch.AddSeconds(1), startedBefore: DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveCatalogQuery(state: (MeasurementRunState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveCatalogQuery(maximumTextCharacters: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunArchiveCatalogQuery(maximumTextCharacters: 8 * 1024 * 1024 + 1));
        Assert.Throws<ArgumentNullException>(() => new RunArchiveCatalogCursor(DateTimeOffset.UnixEpoch, null!));
    }
}
