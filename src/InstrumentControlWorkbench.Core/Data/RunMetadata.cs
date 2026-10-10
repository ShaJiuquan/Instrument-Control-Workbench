using System.Collections.ObjectModel;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record SampleId
{
    public SampleId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("Sample ID must be at most 256 characters without control characters.", nameof(value));
        Value = value;
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record RunMetadata
{
    public RunMetadata(SampleId? sampleId = null, string? sampleName = null, string? notes = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        if (sampleName is { Length: > 1024 } || notes is { Length: > 65536 }) throw new ArgumentException("Run metadata text exceeds its limit.");
        var items = new Dictionary<string, string>(attributes ?? new Dictionary<string, string>());
        if (items.Count > 128 || items.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 256 || p.Value is null || p.Value.Length > 8192))
            throw new ArgumentException("Metadata attributes need non-empty keys and bounded non-null values.", nameof(attributes));
        SampleId = sampleId; SampleName = sampleName; Notes = notes;
        Attributes = new ReadOnlyDictionary<string, string>(items);
    }
    public static RunMetadata Empty { get; } = new();
    public SampleId? SampleId { get; }
    public string? SampleName { get; }
    public string? Notes { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
}
