using System;
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
        Unit = step.Values.Unit; ValuesForm = new(step.Values, () => Timing == IterationTiming.DelayBeforePoint ? PhysicalUnit.Second : Unit, ValidateValues, Changed, template); loading = false;
    }
    public PlanScanValuesFormViewModel ValuesForm { get; }
    public PhysicalUnit[] Units { get; } = Enum.GetValues<PhysicalUnit>();
    public IterationTiming[] Timings { get; } = Enum.GetValues<IterationTiming>();
    public IterationIndexBase[] IndexBases { get; } = Enum.GetValues<IterationIndexBase>();
    public bool CanChooseUnit => !IsReadOnly && Timing != IterationTiming.DelayBeforePoint;
    public string UnitLabel => Timing == IterationTiming.DelayBeforePoint ? "每点执行前等待 / Second" : "迭代值 / " + Unit;
    [ObservableProperty] private string variable = "";
    [ObservableProperty] private IterationTiming timing;
    [ObservableProperty] private IterationIndexBase indexBase;
    [ObservableProperty] private PhysicalUnit unit;
    [ObservableProperty] private bool isReadOnly;
    partial void OnVariableChanged(string value) => Changed();
    partial void OnTimingChanged(IterationTiming value) { if (!loading) ValuesForm.ContextChanged(); Refresh(); Changed(); }
    partial void OnIndexBaseChanged(IterationIndexBase value) => Changed();
    partial void OnUnitChanged(PhysicalUnit value) { if (!loading) ValuesForm.ContextChanged(); Refresh(); Changed(); }
    partial void OnIsReadOnlyChanged(bool value) { ValuesForm.IsReadOnly = value; Refresh(); }
    private void Refresh() { OnPropertyChanged(nameof(CanChooseUnit)); OnPropertyChanged(nameof(UnitLabel)); }
    private void Changed() { if (!loading) changed(); }
    private void ValidateValues(ScanValues values) => _ = new ForEachStep(original.Id, original.Variable, values, original.Body, Timing, IndexBase);
    public ForEachStep Build() => new(original.Id, new(Variable), ValuesForm.Build(), original.Body, Timing, IndexBase);
}
