using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PreparationRangeChoice(Quantity Quantity)
{
    public string Label => Quantity.Value.ToString("R", CultureInfo.InvariantCulture) + " " + Quantity.Unit;
}

public partial class SourcePreparationFormViewModel : ObservableObject
{
    private readonly Action changed;
    private bool loading = true;
    public SourcePreparationFormViewModel(DeviceDescriptor device, SourcePreparation? source, CurrentSourcePreparation? current, Action changed)
    {
        if (device.Model is not ("K2450" or "K6221")) throw new NotSupportedException("This source family uses the native JSON editor.");
        if (source is not null && (source.Device != device.Id || device.Model != "K2450") || current is not null && (current.Device != device.Id || device.Model != "K6221"))
            throw new ArgumentException("Preparation family or device differs from the selected source.");
        Device = device; this.changed = changed; HasExistingPreparation = source is not null || current is not null;
        if (source is not null)
        {
            SourceFunction = source.Function; LevelText = Number(source.Level.Value); ComplianceText = Number(source.Compliance.Value);
            SourceAutoRange = source.ManualSourceRange is null; SourceRange = Choice(SourceRanges, source.ManualSourceRange);
            SenseFunction = source.Sense.Function; SensingMode = source.Sense.Mode; Nplc = Number(source.Sense.Nplc);
            SenseAutoRange = source.Sense.AutoRange; UseSenseRange = source.Sense.ManualRange is not null; SenseRange = Choice(SenseRanges, source.Sense.ManualRange);
            UseAcquisition = source.Sense.Acquisition is not null;
            if (source.Sense.Acquisition is { } profile)
            {
                AutoZero = profile.AutoZero; AutorangeRebound = profile.AutorangeRebound; AveragingEnabled = profile.AveragingEnabled;
                AveragingCount = profile.AveragingCount.ToString(CultureInfo.InvariantCulture); AveragingMode = profile.AveragingMode;
            }
        }
        if (current is not null)
        {
            LevelText = Number(current.Settings.Current.Value); ComplianceText = Number(current.Settings.Compliance.Value);
            SourceAutoRange = current.Settings.ManualRange is null; SourceRange = Choice(SourceRanges, current.Settings.ManualRange);
            UseSourceDelay = current.Settings.SourceDelay is not null; SourceDelay = current.Settings.SourceDelay is { } delay ? Number(delay.Value) : "";
        }
        loading = false;
    }
    public DeviceDescriptor Device { get; }
    public bool IsCurrentSource => Device.Model == "K6221";
    public bool IsSourceMeter => !IsCurrentSource;
    public bool HasExistingPreparation { get; }
    public string DeviceLabel => $"{Device.DisplayName} · {Device.Id} · {Device.DriverKind}";
    public SourceFunction[] SourceFunctions { get; } = Enum.GetValues<SourceFunction>();
    public SenseFunction[] SenseFunctions { get; } = Enum.GetValues<SenseFunction>();
    public SensingMode[] SensingModes { get; } = Enum.GetValues<SensingMode>();
    public AveragingMode[] AveragingModes { get; } = Enum.GetValues<AveragingMode>();
    [ObservableProperty] private SourceFunction sourceFunction = SourceFunction.Voltage;
    [ObservableProperty] private SenseFunction senseFunction = SenseFunction.Current;
    [ObservableProperty] private SensingMode sensingMode = SensingMode.FourWire;
    [ObservableProperty] private string levelText = "";
    [ObservableProperty] private string complianceText = "";
    [ObservableProperty] private bool sourceAutoRange = true;
    [ObservableProperty] private PreparationRangeChoice? sourceRange;
    [ObservableProperty] private string nplc = "1";
    [ObservableProperty] private bool senseAutoRange = true;
    [ObservableProperty] private PreparationRangeChoice? senseRange;
    [ObservableProperty] private bool useSenseRange;
    [ObservableProperty] private bool useAcquisition;
    [ObservableProperty] private bool autoZero = true;
    [ObservableProperty] private bool autorangeRebound;
    [ObservableProperty] private bool averagingEnabled;
    [ObservableProperty] private string averagingCount = "10";
    [ObservableProperty] private AveragingMode averagingMode = AveragingMode.Repeat;
    [ObservableProperty] private bool useSourceDelay;
    [ObservableProperty] private string sourceDelay = "";
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isReadOnly;
    public string LevelUnit => IsCurrentSource || SourceFunction == SourceFunction.Current ? "Ampere" : "Volt";
    public string ComplianceUnit => IsCurrentSource || SourceFunction == SourceFunction.Current ? "Volt" : "Ampere";
    public IReadOnlyList<PreparationRangeChoice> SourceRanges => IsCurrentSource ? Keithley6221Settings.NominalRanges.Select(v => new PreparationRangeChoice(new(v, PhysicalUnit.Ampere))).ToArray()
        : Enum.IsDefined(SourceFunction) ? Keithley2450SourceRanges.For(SourceFunction).Select(q => new PreparationRangeChoice(q)).ToArray() : Array.Empty<PreparationRangeChoice>();
    public IReadOnlyList<PreparationRangeChoice> SenseRanges => Enum.IsDefined(SenseFunction) ? Keithley2450SenseRanges.For(SenseFunction).Select(q => new PreparationRangeChoice(q)).ToArray() : Array.Empty<PreparationRangeChoice>();
    public bool CanSetSourceRange => !IsReadOnly && !SourceAutoRange;
    public bool CanChooseSenseRange => !IsReadOnly && !SenseAutoRange;
    public bool CanSetSenseRange => CanChooseSenseRange && UseSenseRange;
    public bool CanSetAcquisition => !IsReadOnly && UseAcquisition;
    public bool CanSetSourceDelay => !IsReadOnly && UseSourceDelay;
    partial void OnSourceFunctionChanged(SourceFunction value)
    {
        if (!loading) { LevelText = ""; ComplianceText = ""; SourceRange = null; }
        OnPropertyChanged(nameof(LevelUnit)); OnPropertyChanged(nameof(ComplianceUnit)); OnPropertyChanged(nameof(SourceRanges)); MarkDirty();
    }
    partial void OnSenseFunctionChanged(SenseFunction value) { if (!loading) SenseRange = null; OnPropertyChanged(nameof(SenseRanges)); MarkDirty(); }
    partial void OnSensingModeChanged(SensingMode value) => MarkDirty();
    partial void OnLevelTextChanged(string value) => MarkDirty();
    partial void OnComplianceTextChanged(string value) => MarkDirty();
    partial void OnSourceAutoRangeChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnSourceRangeChanged(PreparationRangeChoice? value) => MarkDirty();
    partial void OnNplcChanged(string value) => MarkDirty();
    partial void OnSenseAutoRangeChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnUseSenseRangeChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnSenseRangeChanged(PreparationRangeChoice? value) => MarkDirty();
    partial void OnUseAcquisitionChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnAutoZeroChanged(bool value) => MarkDirty();
    partial void OnAutorangeReboundChanged(bool value) => MarkDirty();
    partial void OnAveragingEnabledChanged(bool value) => MarkDirty();
    partial void OnAveragingCountChanged(string value) => MarkDirty();
    partial void OnAveragingModeChanged(AveragingMode value) => MarkDirty();
    partial void OnUseSourceDelayChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnSourceDelayChanged(string value) => MarkDirty();
    partial void OnIsReadOnlyChanged(bool value) => RefreshControls();
    private void RefreshControls()
    { OnPropertyChanged(nameof(CanSetSourceRange)); OnPropertyChanged(nameof(CanChooseSenseRange)); OnPropertyChanged(nameof(CanSetSenseRange)); OnPropertyChanged(nameof(CanSetAcquisition)); OnPropertyChanged(nameof(CanSetSourceDelay)); }
    private void MarkDirty() { if (loading) return; IsDirty = true; changed(); }
    private static PreparationRangeChoice? Choice(IReadOnlyList<PreparationRangeChoice> choices, Quantity? range) => range is { } value ? choices.SingleOrDefault(c => c.Quantity == value) : null;
    private static Quantity Range(IReadOnlyList<PreparationRangeChoice> choices, PreparationRangeChoice? selection) => choices.FirstOrDefault(c => c.Quantity == selection?.Quantity)?.Quantity
        ?? throw new InvalidOperationException("请选择该功能支持的标称量程。");
    public SourcePreparation BuildSourceMeter()
    {
        if (!IsSourceMeter) throw new InvalidOperationException("A pure current source has no source-meter preparation.");
        var sourceUnit = SourceFunction switch { SourceFunction.Voltage => PhysicalUnit.Volt, SourceFunction.Current => PhysicalUnit.Ampere, _ => throw new ArgumentOutOfRangeException(nameof(SourceFunction)) };
        var complianceUnit = sourceUnit == PhysicalUnit.Volt ? PhysicalUnit.Ampere : PhysicalUnit.Volt;
        var acquisition = UseAcquisition ? new Keithley2450AcquisitionSettings(AutoZero, AutorangeRebound, AveragingEnabled, Integer(AveragingCount), AveragingMode) : null;
        return new(Device.Id, SourceFunction, new(Scalar(LevelText), sourceUnit), new(Scalar(ComplianceText), complianceUnit),
            new(SenseFunction, SensingMode, Scalar(Nplc), SenseAutoRange, SenseAutoRange || !UseSenseRange ? null : Range(SenseRanges, SenseRange), acquisition),
            SourceAutoRange ? null : Range(SourceRanges, SourceRange));
    }
    public CurrentSourcePreparation BuildCurrentSource()
    {
        if (!IsCurrentSource) throw new InvalidOperationException("A source meter has no pure current-source preparation.");
        return new(Device.Id, new(new(Scalar(LevelText), PhysicalUnit.Ampere), new(Scalar(ComplianceText), PhysicalUnit.Volt),
            SourceAutoRange ? null : Range(SourceRanges, SourceRange), UseSourceDelay ? new(Scalar(SourceDelay), PhysicalUnit.Second) : null));
    }
    private static double Scalar(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("请输入有限数值，使用小数点和可选科学计数法。");
    private static int Integer(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException("平均次数必须是整数。");
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
