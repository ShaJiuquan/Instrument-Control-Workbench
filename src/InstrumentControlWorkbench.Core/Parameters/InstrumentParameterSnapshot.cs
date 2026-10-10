using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Parameters;

public sealed record ParameterSnapshot
{
    public ParameterSnapshot(ParameterDescriptor descriptor, Quantity value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Validate(value);
        Descriptor = descriptor;
        Value = value;
    }

    public ParameterDescriptor Descriptor { get; }
    public Quantity Value { get; }
}

public sealed record InstrumentParameterSnapshot
{
    public InstrumentParameterSnapshot(InstrumentId instrumentId, string identifier, DateTimeOffset capturedAt,
        IEnumerable<ParameterSnapshot> parameters, IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(configuration);
        var items = parameters.ToArray();
        if (items.Any(p => p is null) || items.Select(p => p.Descriptor.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("Snapshot parameters must be non-null and unique.", nameof(parameters));
        InstrumentId = instrumentId;
        Identifier = identifier;
        CapturedAt = capturedAt;
        Parameters = Array.AsReadOnly(items);
        Configuration = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(configuration));
    }

    public InstrumentId InstrumentId { get; }
    public string Identifier { get; }
    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyList<ParameterSnapshot> Parameters { get; }
    public IReadOnlyDictionary<string, string> Configuration { get; }
}
