namespace InstrumentControlWorkbench.Core.Routing;

// Physical ports remain usable as configuration before a device family is implemented.
public sealed record MatrixWiringLayout
{
    public MatrixWiringLayout(WiringProfileId id, string label, MatrixPort sourcePlus, MatrixPort sourceMinus, MatrixPort sensePlus, MatrixPort senseMinus)
    {
        ArgumentNullException.ThrowIfNull(id); ProfileText.Validate(label);
        ArgumentNullException.ThrowIfNull(sourcePlus); ArgumentNullException.ThrowIfNull(sourceMinus);
        ArgumentNullException.ThrowIfNull(sensePlus); ArgumentNullException.ThrowIfNull(senseMinus);
        Id = id; Label = label; SourcePlus = sourcePlus; SourceMinus = sourceMinus; SensePlus = sensePlus; SenseMinus = senseMinus;
    }
    public WiringProfileId Id { get; }
    public string Label { get; }
    public MatrixPort SourcePlus { get; }
    public MatrixPort SourceMinus { get; }
    public MatrixPort SensePlus { get; }
    public MatrixPort SenseMinus { get; }
}
