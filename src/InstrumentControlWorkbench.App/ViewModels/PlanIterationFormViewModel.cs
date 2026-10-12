using System;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanIterationFormViewModel : ObservableObject
{
    private readonly ForEachStep original;
    private readonly Action changed;
    private bool loading = true;
    public PlanIterationFormViewModel(ForEachStep step, Action changed, bool template = false)
    {
        original = step; this.changed = changed; Variable = template ? "" : step.Variable.Value; Timing = step.Timing; IndexBase = step.IndexBase;
        Unit = step.Values.Unit; ValuesText = template ? "" : string.Join(", ", step.Values.Values.Select(q => q.Value.ToString("R", CultureInfo.InvariantCulture))); loading = false;
    }
    public PhysicalUnit[] Units { get; } = Enum.GetValues<PhysicalUnit>();
    public IterationTiming[] Timings { get; } = Enum.GetValues<IterationTiming>();
    public IterationIndexBase[] IndexBases { get; } = Enum.GetValues<IterationIndexBase>();
    public bool CanChooseUnit => !IsReadOnly && Timing != IterationTiming.DelayBeforePoint;
    public string UnitLabel => Timing == IterationTiming.DelayBeforePoint ? "每点执行前等待 / Second" : "迭代值 / " + Unit;
    [ObservableProperty] private string variable = "";
    [ObservableProperty] private IterationTiming timing;
    [ObservableProperty] private IterationIndexBase indexBase;
    [ObservableProperty] private PhysicalUnit unit;
    [ObservableProperty] private string valuesText = "";
    [ObservableProperty] private bool isReadOnly;
    partial void OnVariableChanged(string value) => Changed();
    partial void OnTimingChanged(IterationTiming value) { if (!loading) ValuesText = ""; Refresh(); Changed(); }
    partial void OnIndexBaseChanged(IterationIndexBase value) => Changed();
    partial void OnUnitChanged(PhysicalUnit value) { if (!loading) ValuesText = ""; Refresh(); Changed(); }
    partial void OnValuesTextChanged(string value) => Changed();
    partial void OnIsReadOnlyChanged(bool value) => Refresh();
    private void Refresh() { OnPropertyChanged(nameof(CanChooseUnit)); OnPropertyChanged(nameof(UnitLabel)); }
    private void Changed() { if (!loading) changed(); }
    public ForEachStep Build()
    {
        if (Encoding.UTF8.GetByteCount(ValuesText) > 4 * 1024 * 1024) throw new FormatException("迭代数值超过计划字节预算。");
        var parts = ValuesText.Split(',', StringSplitOptions.TrimEntries); if (parts.Length > 1_000_000) throw new FormatException("迭代点数超过计划预算。");
        var unit = Timing == IterationTiming.DelayBeforePoint ? PhysicalUnit.Second : Unit;
        var values = new ScanValues(parts.Select(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? new Quantity(number, unit) : throw new FormatException("请输入逗号分隔的有限数值，使用小数点。")));
        return new(original.Id, new(Variable), values, original.Body, Timing, IndexBase);
    }
}
