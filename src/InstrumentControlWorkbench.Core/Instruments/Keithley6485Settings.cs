using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

// Observed configuration only; zero-correct enabled does not certify calibration.
public sealed record Keithley6485Settings
{
    public static IReadOnlyList<double> Ranges { get; } = Array.AsReadOnly(new[] { 2e-9, 2e-8, 2e-7, 2e-6, 2e-5, 2e-4, 2e-3, 2e-2 });
    public Keithley6485Settings(Quantity? manualRange, double nplc, bool zeroCheck, bool zeroCorrect, bool averagingEnabled, int averagingCount, AveragingMode averagingMode)
    {
        if (manualRange is { } range && (range.Unit != PhysicalUnit.Ampere || !Ranges.Contains(range.Value))) throw new ArgumentException("K6485 requires an exact nominal current range.", nameof(manualRange));
        if (!double.IsFinite(nplc) || nplc is < 0.01 or > 60) throw new ArgumentOutOfRangeException(nameof(nplc));
        if (averagingCount is < 1 or > 100 || !Enum.IsDefined(averagingMode)) throw new ArgumentOutOfRangeException(nameof(averagingCount));
        ManualRange = manualRange; Nplc = nplc; ZeroCheck = zeroCheck; ZeroCorrect = zeroCorrect; AveragingEnabled = averagingEnabled; AveragingCount = averagingCount; AveragingMode = averagingMode;
    }
    public Quantity? ManualRange { get; }
    public bool AutoRange => ManualRange is null;
    public double Nplc { get; }
    public bool ZeroCheck { get; }
    public bool ZeroCorrect { get; }
    public bool AveragingEnabled { get; }
    public int AveragingCount { get; }
    public AveragingMode AveragingMode { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureParameters()
    {
        var values = new Dictionary<ParameterId, Quantity>
        {
            [Keithley6485Parameters.AutoRange.Id] = new(AutoRange ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley6485Parameters.Nplc.Id] = new(Nplc, PhysicalUnit.Dimensionless),
            [Keithley6485Parameters.ZeroCheck.Id] = new(ZeroCheck ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley6485Parameters.ZeroCorrect.Id] = new(ZeroCorrect ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley6485Parameters.AveragingEnabled.Id] = new(AveragingEnabled ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley6485Parameters.AveragingCount.Id] = new(AveragingCount, PhysicalUnit.Dimensionless),
            [Keithley6485Parameters.AveragingMode.Id] = new((int)AveragingMode, PhysicalUnit.Dimensionless),
        };
        if (ManualRange is { } range) values[Keithley6485Parameters.ManualRange.Id] = range;
        return values;
    }
    public Keithley6485Settings WithParameter(ParameterId parameter, Quantity value)
    {
        Keithley6485Parameters.ValidateWrite(parameter, value);
        return new(parameter == Keithley6485Parameters.EnableAutoRange.Id ? null : parameter == Keithley6485Parameters.ManualRange.Id ? value : ManualRange,
            parameter == Keithley6485Parameters.Nplc.Id ? value.Value : Nplc,
            parameter == Keithley6485Parameters.ZeroCheck.Id ? value.Value == 1 : ZeroCheck,
            parameter == Keithley6485Parameters.ZeroCorrect.Id ? value.Value == 1 : ZeroCorrect,
            parameter == Keithley6485Parameters.AveragingEnabled.Id ? value.Value == 1 : AveragingEnabled,
            parameter == Keithley6485Parameters.AveragingCount.Id ? (int)value.Value : AveragingCount,
            parameter == Keithley6485Parameters.AveragingMode.Id ? (AveragingMode)(int)value.Value : AveragingMode);
    }
}
