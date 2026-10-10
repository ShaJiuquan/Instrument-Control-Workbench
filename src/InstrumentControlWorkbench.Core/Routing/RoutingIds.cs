namespace InstrumentControlWorkbench.Core.Routing;

public sealed record ChannelProfileId
{
    public ChannelProfileId(string value) { RoutingIdentifier.Validate(value); Value = value; }
    public string Value { get; }
    public override string ToString() => Value;
}
public sealed record ConnectionKey
{
    public ConnectionKey(string value) { RoutingIdentifier.Validate(value); Value = value; }
    public string Value { get; }
    public override string ToString() => Value;
}
public sealed record WiringProfileId
{
    public WiringProfileId(string value) { RoutingIdentifier.Validate(value); Value = value; }
    public string Value { get; }
    public override string ToString() => Value;
}
public sealed record ElectrodeLabel
{
    public ElectrodeLabel(string value) { RoutingIdentifier.Validate(value); Value = value; }
    public string Value { get; }
    public override string ToString() => Value;
}
internal static class RoutingIdentifier
{
    public static void Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("Routing identifiers must be at most 256 characters without control characters.", nameof(value));
    }
}
