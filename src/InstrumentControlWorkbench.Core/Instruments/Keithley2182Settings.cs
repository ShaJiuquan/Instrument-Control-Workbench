using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

// DC voltage settings only. These are configuration observations, not buffer
// status, constituent filter samples, or the range actually used for a reading.
public sealed record Keithley2182Settings
{
    public const double MinimumNplc = 0.01;
    public const double MaximumNplc = 50;
    public const int MinimumFilterCount = 1;
    public const int MaximumFilterCount = 100;
    public const double MaximumFilterWindowPercent = 10;
    private static readonly IReadOnlyList<double> ChannelOneRanges = Array.AsReadOnly(new[] { 0.01, 0.1, 1, 10, 100 });
    private static readonly IReadOnlyList<double> ChannelTwoRanges = Array.AsReadOnly(new[] { 0.1, 1, 10 });
    public Keithley2182Settings(int channel, Quantity? manualRange, double nplc,
        bool filterEnabled, int filterCount, AveragingMode filterMode, double filterWindowPercent)
    {
        if (channel is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channel));
        if (!double.IsFinite(nplc) || nplc is < MinimumNplc or > MaximumNplc) throw new ArgumentOutOfRangeException(nameof(nplc));
        if (filterCount is < MinimumFilterCount or > MaximumFilterCount || !Enum.IsDefined(filterMode)) throw new ArgumentOutOfRangeException(nameof(filterCount));
        if (!double.IsFinite(filterWindowPercent) || filterWindowPercent is < 0 or > MaximumFilterWindowPercent) throw new ArgumentOutOfRangeException(nameof(filterWindowPercent));
        if (manualRange is { } range && (range.Unit != PhysicalUnit.Volt || !Ranges(channel).Contains(range.Value)))
            throw new ArgumentException("K2182 manual range must be an exact nominal voltage span for the selected input.", nameof(manualRange));
        Channel = channel; ManualRange = manualRange; Nplc = nplc; FilterEnabled = filterEnabled;
        FilterCount = filterCount; FilterMode = filterMode; FilterWindowPercent = filterWindowPercent;
    }
    public int Channel { get; }
    public Quantity? ManualRange { get; }
    public bool AutoRange => ManualRange is null;
    public double Nplc { get; }
    public bool FilterEnabled { get; }
    public int FilterCount { get; }
    public AveragingMode FilterMode { get; }
    public double FilterWindowPercent { get; }
    public static IReadOnlyList<double> Ranges(int channel) => channel switch
    {
        1 => ChannelOneRanges,
        2 => ChannelTwoRanges,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
    public static double MaximumInputVoltage(int channel) => channel switch { 1 => 120, 2 => 12, _ => throw new ArgumentOutOfRangeException(nameof(channel)) };
    public static Quantity ResolveRange(int channel, Quantity selector)
    {
        if (selector.Unit != PhysicalUnit.Volt || selector.Value <= 0 || selector.Value > MaximumInputVoltage(channel))
            throw new ArgumentException("K2182 range selector must be a positive voltage within the input limit.");
        return new(Ranges(channel).FirstOrDefault(r => r >= selector.Value, Ranges(channel)[^1]), PhysicalUnit.Volt);
    }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureParameters()
    {
        var result = new Dictionary<ParameterId, Quantity>
        {
            [Keithley2182Parameters.Channel.Id] = new(Channel, PhysicalUnit.Dimensionless),
            [Keithley2182Parameters.AutoRange.Id] = new(AutoRange ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley2182Parameters.Nplc.Id] = new(Nplc, PhysicalUnit.Dimensionless),
            [Keithley2182Parameters.FilterEnabled.Id] = new(FilterEnabled ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley2182Parameters.FilterCount.Id] = new(FilterCount, PhysicalUnit.Dimensionless),
            [Keithley2182Parameters.FilterMode.Id] = new((int)FilterMode, PhysicalUnit.Dimensionless),
            [Keithley2182Parameters.FilterWindowPercent.Id] = new(FilterWindowPercent, PhysicalUnit.Dimensionless),
        };
        if (ManualRange is { } range) result[Keithley2182Parameters.ManualRange.Id] = range;
        return result;
    }
    public Keithley2182Settings WithParameter(ParameterId parameter, Quantity value)
    {
        Keithley2182Parameters.ValidateWrite(parameter, value);
        if (parameter == Keithley2182Parameters.Channel.Id && value.Value != Channel)
            throw new InvalidOperationException("Observe the target input through the port before changing its settings.");
        var channel = Channel;
        var range = ManualRange;
        if (parameter == Keithley2182Parameters.RangeSelector.Id) range = ResolveRange(Channel, value);
        else if (parameter == Keithley2182Parameters.ManualRange.Id) range = value;
        else if (parameter == Keithley2182Parameters.EnableAutoRange.Id) range = null;
        return new(channel, range,
            parameter == Keithley2182Parameters.Nplc.Id ? value.Value : Nplc,
            parameter == Keithley2182Parameters.FilterEnabled.Id ? value.Value == 1 : FilterEnabled,
            parameter == Keithley2182Parameters.FilterCount.Id ? (int)value.Value : FilterCount,
            parameter == Keithley2182Parameters.FilterMode.Id ? (AveragingMode)(int)value.Value : FilterMode,
            parameter == Keithley2182Parameters.FilterWindowPercent.Id ? value.Value : FilterWindowPercent);
    }
}
