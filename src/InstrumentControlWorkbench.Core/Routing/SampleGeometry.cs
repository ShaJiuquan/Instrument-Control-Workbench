using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Routing;

public sealed record SampleGeometry
{
    public SampleGeometry(Quantity? width = null, Quantity? length = null, Quantity? thickness = null, Quantity? spacing = null)
    {
        foreach (var value in new[] { width, length, thickness, spacing })
            if (value is { } dimension && (dimension.Unit != PhysicalUnit.Meter || dimension.Value <= 0))
                throw new ArgumentException("Sample geometry dimensions must be positive meters.");
        Width = width; Length = length; Thickness = thickness; Spacing = spacing;
    }
    public Quantity? Width { get; }
    public Quantity? Length { get; }
    public Quantity? Thickness { get; }
    public Quantity? Spacing { get; }
    public static SampleGeometry Empty { get; } = new();
}
