using System.Security.Cryptography;

namespace InstrumentControlWorkbench.Core.Data;

// The persistence adapter supplies its canonical frame bytes; Core never owns
// file paths or storage codecs. Both producer and archive verify the same chain.
public interface IRunRowEncoding
{
    string RowEncodingId { get; }
    byte[] EncodeRow(int index, MeasurementDataRow row);
}
public sealed record RunRowRetention
{
    public RunRowRetention(int maximumRows = 4096, long maximumEncodedBytes = 32 * 1024 * 1024)
    {
        if (maximumRows is < 1 or > 100_000 || maximumEncodedBytes is < 8 * 1024 * 1024 or > 512 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        MaximumRows = maximumRows; MaximumEncodedBytes = maximumEncodedBytes;
    }
    public int MaximumRows { get; }
    public long MaximumEncodedBytes { get; }
}
public sealed record RunRowWindowProof
{
    public RunRowWindowProof(string encodingId, int rowOffset, int totalRows, string prefixSha256, string fullSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingId);
        if (rowOffset < 0 || totalRows < rowOffset || !ValidHash(prefixSha256) || !ValidHash(fullSha256)) throw new ArgumentException("Row window proof requires ordered counts and SHA256 chain checkpoints.");
        EncodingId = encodingId; RowOffset = rowOffset; TotalRows = totalRows; PrefixSha256 = prefixSha256; FullSha256 = fullSha256;
    }
    public string EncodingId { get; }
    public int RowOffset { get; }
    public int TotalRows { get; }
    public string PrefixSha256 { get; }
    public string FullSha256 { get; }
    public static string EmptyHash { get; } = new('0', 64);
    private static bool ValidHash(string value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    public static string AppendHash(string previous, ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(Convert.FromHexString(previous)); hash.AppendData(bytes); return Convert.ToHexString(hash.GetHashAndReset());
    }
}
