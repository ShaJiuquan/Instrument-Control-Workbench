using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class RunRowRetentionTests
{
    [Theory]
    [InlineData(0, 8388608)]
    [InlineData(100001, 8388608)]
    [InlineData(1, 8388607)]
    [InlineData(1, 536870913)]
    public void RetentionPolicyRequiresBoundedPositiveLimits(int rows, long bytes) => Assert.Throws<ArgumentOutOfRangeException>(() => new RunRowRetention(rows, bytes));
    [Fact]
    public void RowWindowRetainsCorrectIndicesAndImmutableEarlierCaptures()
    {
        var encoder = new FakeEncoding(10); var data = Data(encoder, new(2)); data.Append(Row(1)); var early = data.Capture(); data.Append(Row(2)); data.Append(Row(3)); var latest = data.Capture();
        Assert.Equal(1, early.TotalRowCount); Assert.Equal(1, Assert.Single(early.Rows).Values[new("value")].Value); Assert.Equal(0, early.RowOffset); Assert.Equal(3, latest.TotalRowCount); Assert.Equal(1, latest.RowOffset); Assert.Equal(new[] { 2.0, 3.0 }, latest.Rows.Select(row => row.Values[new("value")].Value)); Assert.Equal(20, data.RetainedEncodedBytes); Assert.False(latest.HasCompleteRows); Assert.Equal(RunRowWindowProof.AppendHash(early.RowWindow!.FullSha256, encoder.EncodeRow(2, Row(2))), RunRowWindowProof.AppendHash(latest.RowWindow!.PrefixSha256, encoder.EncodeRow(2, Row(2))));
    }
    [Fact]
    public void EncodedByteLimitEvictsRowsBeforeCountLimitAndRejectsOversizedRows()
    {
        var data = Data(new FakeEncoding(5 * 1024 * 1024), new(100, 8 * 1024 * 1024)); data.Append(Row(1)); data.Append(Row(2)); Assert.Single(data.Rows); Assert.Equal(1, data.RowOffset); Assert.Equal(5 * 1024 * 1024, data.RetainedEncodedBytes);
        var rejected = Data(new FakeEncoding(9 * 1024 * 1024), new(100, 8 * 1024 * 1024)); Assert.Throws<InvalidOperationException>(() => rejected.Append(Row(1))); Assert.Equal(0, rejected.TotalRowCount); Assert.Empty(rejected.Rows);
    }
    [Fact]
    public void NonDurableLimitPreservesAcceptedRowsAndNeverDropsOldData()
    {
        var data = Data(null, new(2)); data.Append(Row(1)); data.Append(Row(2)); Assert.Throws<InvalidOperationException>(() => data.Append(Row(3))); Assert.Equal(2, data.TotalRowCount); Assert.Equal(0, data.RowOffset); Assert.True(data.Capture().HasCompleteRows); Assert.Equal(new[] { 1.0, 2.0 }, data.Rows.Select(row => row.Values[new("value")].Value));
    }
    [Theory]
    [InlineData(-1, 1, "zeros")]
    [InlineData(2, 1, "zeros")]
    [InlineData(0, 1, "invalid")]
    public void WindowProofRejectsInvalidCountsOrHashes(int offset, int total, string hash) => Assert.Throws<ArgumentException>(() => new RunRowWindowProof("encoding", offset, total, hash == "zeros" ? RunRowWindowProof.EmptyHash : "invalid", RunRowWindowProof.EmptyHash));
    private static RunDataSet Data(IRunRowEncoding? encoder, RunRowRetention retention) => new(RunId.New(), "retention", DateTimeOffset.UtcNow, new([new DataColumn(new(new("value"), "Value", PhysicalUnit.Volt, ParameterAccess.Read))]), new Dictionary<string, Quantity>(), [], rowEncoding: encoder, retention: retention);
    private static MeasurementDataRow Row(double value) => new(DateTimeOffset.UnixEpoch, new Dictionary<ParameterId, Quantity> { [new("value")] = new(value, PhysicalUnit.Volt) });
    private sealed class FakeEncoding(int bytes) : IRunRowEncoding
    {
        public string RowEncodingId => "test-encoding";
        public byte[] EncodeRow(int index, MeasurementDataRow row) { var buffer = new byte[bytes]; buffer[0] = (byte)index; buffer[^1] = (byte)row.Values[new("value")].Value; return buffer; }
    }
}
