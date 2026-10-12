using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public enum PlanScanValueMode { List, Linear, Stepped, Logarithmic }

public partial class PlanScanRangeFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanScanRangeFormViewModel(Action changed, ScanValues? values = null)
    {
        this.changed = changed;
        valuesText = values is null ? "" : string.Join(", ", values.Values.Select(q => q.Value.ToString("R", CultureInfo.InvariantCulture)));
    }
    public PlanScanValueMode[] Modes { get; } = Enum.GetValues<PlanScanValueMode>();
    public bool IsList => Mode == PlanScanValueMode.List;
    public bool HasEndpoints => !IsList;
    public bool HasPoints => Mode is PlanScanValueMode.Linear or PlanScanValueMode.Logarithmic;
    public bool HasStep => Mode == PlanScanValueMode.Stepped;
    [ObservableProperty] private PlanScanValueMode mode;
    [ObservableProperty] private string valuesText;
    [ObservableProperty] private string startText = "";
    [ObservableProperty] private string endText = "";
    [ObservableProperty] private string pointsText = "";
    [ObservableProperty] private string stepText = "";
    [ObservableProperty] private bool isReadOnly;
    partial void OnModeChanged(PlanScanValueMode value)
    {
        OnPropertyChanged(nameof(IsList)); OnPropertyChanged(nameof(HasEndpoints));
        OnPropertyChanged(nameof(HasPoints)); OnPropertyChanged(nameof(HasStep)); changed();
    }
    partial void OnValuesTextChanged(string value) => changed();
    partial void OnStartTextChanged(string value) => changed();
    partial void OnEndTextChanged(string value) => changed();
    partial void OnPointsTextChanged(string value) => changed();
    partial void OnStepTextChanged(string value) => changed();
    public void Clear() { ValuesText = ""; StartText = ""; EndText = ""; PointsText = ""; StepText = ""; }
    public ScanValues Build(PhysicalUnit unit) => Mode switch
    {
        PlanScanValueMode.List => List(unit),
        PlanScanValueMode.Linear => ScanValues.Linear(Number(StartText, unit), Number(EndText, unit), Points()),
        PlanScanValueMode.Stepped => ScanValues.Stepped(Number(StartText, unit), Number(EndText, unit), Number(StepText, unit)),
        PlanScanValueMode.Logarithmic => ScanValues.Logarithmic(Number(StartText, unit), Number(EndText, unit), Points()),
        _ => throw new FormatException("请选择有效的扫描生成方式。"),
    };
    private ScanValues List(PhysicalUnit unit)
    {
        if (Encoding.UTF8.GetByteCount(ValuesText) > 4 * 1024 * 1024) throw new FormatException("扫描数值超过计划字节预算。");
        var parts = ValuesText.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > 1_000_000) throw new FormatException("扫描点数超过计划预算。");
        return new(parts.Select(text => Number(text, unit)));
    }
    private int Points() => int.TryParse(PointsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException("点数必须是整数，范围为 2 至 1,000,000。");
    private static Quantity Number(string text, PhysicalUnit unit) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? new(value, unit) : throw new FormatException("请输入有限数值，使用小数点。");
}

public partial class PlanScanValuesFormViewModel : ObservableObject
{
    private readonly Func<PhysicalUnit> unit;
    private readonly Action<ScanValues> validate;
    private readonly Action changed;
    private readonly ObservableCollection<PlanScanRangeFormViewModel> segments = new();
    public PlanScanValuesFormViewModel(ScanValues values, Func<PhysicalUnit> unit, Action<ScanValues> validate, Action changed, bool template = false)
    {
        this.unit = unit; this.validate = validate; this.changed = changed;
        Definition = new(Changed, template ? null : values); Segments = new(segments);
    }
    public PlanScanRangeFormViewModel Definition { get; }
    public ReadOnlyObservableCollection<PlanScanRangeFormViewModel> Segments { get; }
    public string UnitLabel
    {
        get { try { return "全部数值单位 / " + unit(); } catch (InvalidOperationException) { return "请选择设备参数以确定单位。"; } }
    }
    [ObservableProperty] private bool useSegments;
    [ObservableProperty] private bool isReadOnly;
    [ObservableProperty] private string previewText = "";
    partial void OnUseSegmentsChanged(bool value) => Changed();
    partial void OnIsReadOnlyChanged(bool value)
    {
        Definition.IsReadOnly = value; foreach (var segment in segments) segment.IsReadOnly = value;
        RefreshCommands();
    }
    private void Changed() { PreviewText = ""; changed(); }
    public void ContextChanged()
    {
        Definition.Clear(); foreach (var segment in segments) segment.Clear();
        OnPropertyChanged(nameof(UnitLabel)); Changed();
    }
    private bool CanPreview() => !IsReadOnly;
    private bool CanAdd() => !IsReadOnly && segments.Count < 10_000;
    private bool CanRemove(PlanScanRangeFormViewModel? segment) => !IsReadOnly && segment is not null && segments.Contains(segment);
    private void RefreshCommands() { PreviewCommand.NotifyCanExecuteChanged(); AddSegmentCommand.NotifyCanExecuteChanged(); RemoveSegmentCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSegment() { if (!CanAdd()) return; segments.Add(new(Changed)); RefreshCommands(); Changed(); }
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveSegment(PlanScanRangeFormViewModel? segment) { if (!CanRemove(segment)) return; segments.Remove(segment!); RefreshCommands(); Changed(); }
    public ScanValues Build()
    {
        var physicalUnit = unit();
        var values = UseSegments ? ScanValues.Segmented(segments.Select(segment => segment.Build(physicalUnit))) : Definition.Build(physicalUnit);
        validate(values); return values;
    }
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private void Preview()
    {
        if (!CanPreview()) return;
        try
        {
            var values = Build();
            PreviewText = $"{values.Values.Count} 点 · {values.Unit} / " + string.Join(", ", values.Values.Take(12).Select(q => q.Value.ToString("G6", CultureInfo.InvariantCulture)))
                + (values.Values.Count > 12 ? " …" : "");
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException) { PreviewText = error.Message; }
    }
}
