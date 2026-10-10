using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record ScanValues
{
    public ScanValues(IEnumerable<Quantity> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.ToArray();
        if (items.Length is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(values), "A scan needs 1 to 1,000,000 values.");
        if (items.Any(v => v.Unit != items[0].Unit)) throw new ArgumentException("All scan values must use the same unit.", nameof(values));
        Values = Array.AsReadOnly(items);
    }

    public IReadOnlyList<Quantity> Values { get; }
    public PhysicalUnit Unit => Values[0].Unit;

    public static ScanValues Linear(Quantity start, Quantity end, int points)
    {
        if (start.Unit != end.Unit) throw new ArgumentException("Scan endpoints need matching units.");
        if (points is < 2 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(points));
        return new(Enumerable.Range(0, points).Select(i => new Quantity(
            i == points - 1 ? end.Value : start.Value + (end.Value - start.Value) * i / (points - 1), start.Unit)));
    }

    public static ScanValues Stepped(Quantity start, Quantity end, Quantity step)
    {
        if (start.Unit != end.Unit || start.Unit != step.Unit || step.Value <= 0)
            throw new ArgumentException("Step size must be positive with matching endpoint units.", nameof(step));
        var count = Math.Ceiling(Math.Abs(end.Value - start.Value) / step.Value);
        if (!double.IsFinite(count) || count > 999_999) throw new ArgumentOutOfRangeException(nameof(step), "Too many scan values.");
        var direction = Math.Sign(end.Value - start.Value);
        var values = Enumerable.Range(0, (int)count).Select(i => new Quantity(start.Value + direction * step.Value * i, start.Unit)).ToList();
        values.Add(end);
        return new(values);
    }

    public static ScanValues Logarithmic(Quantity start, Quantity end, int points)
    {
        if (start.Value <= 0 || end.Value <= 0 || start.Unit != end.Unit)
            throw new ArgumentException("Logarithmic endpoints must be positive with matching units.");
        if (points is < 2 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(points));
        var first = Math.Log(start.Value);
        var last = Math.Log(end.Value);
        return new(Enumerable.Range(0, points).Select(i => i == 0 ? start : i == points - 1 ? end :
            new Quantity(Math.Exp(first + (last - first) * i / (points - 1)), start.Unit)));
    }

    public static ScanValues Segmented(IEnumerable<ScanValues> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var values = new List<Quantity>();
        foreach (var segment in segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            foreach (var value in segment.Values)
            {
                if (values.Count == 0 || values[^1] != value) values.Add(value);
                if (values.Count > 1_000_000) throw new ArgumentOutOfRangeException(nameof(segments));
            }
        }
        return new(values);
    }
}
