using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PlanSampleChoice(SampleProfile Profile)
{
    public string Label => $"{Profile.Name} · {Profile.Id} · {Profile.Topology}";
}
public sealed record PlanWiringChoice(InstrumentWiringProfile Profile)
{
    public string Label => $"{Profile.Id} · {Profile.SourceMeter}" + (Profile.VoltageMeter is { } meter ? $" + {meter}" : "");
}

public partial class PlanSampleRoutingFormViewModel : ObservableObject
{
    private readonly RoutingConfiguration? catalog;
    private readonly SampleRoutingBinding original;
    private readonly Action changed;
    private bool loading = true;
    public PlanSampleRoutingFormViewModel(SampleRoutingBinding binding, RoutingConfiguration? catalog, bool independent, Action changed)
    {
        original = binding; this.catalog = catalog; this.changed = changed;
        Samples = Array.AsReadOnly((catalog?.Samples ?? Array.Empty<SampleProfile>()).Select(p => new PlanSampleChoice(p)).ToArray());
        Wirings = Array.AsReadOnly((catalog?.Wirings ?? Array.Empty<InstrumentWiringProfile>()).Where(w => (w.VoltageMeter is not null) == independent).Select(p => new PlanWiringChoice(p)).ToArray());
        SelectedSample = Samples.FirstOrDefault(s => Same(s.Profile.Id.Value, binding.Sample.Value));
        SelectedWiring = Wirings.FirstOrDefault(w => Same(w.Profile.Id.Value, binding.Wiring.Value));
        SelectedMeasurement = binding.Selection; loading = false;
    }
    public IReadOnlyList<PlanSampleChoice> Samples { get; }
    public IReadOnlyList<PlanWiringChoice> Wirings { get; }
    public bool CanResolveContext => catalog is not null;
    public bool CanRebind => CanResolveContext && !IsReadOnly;
    public bool IsEdited { get; private set; }
    public IReadOnlyList<SampleRouteSelection> Measurements => ResolveSample()?.Topology switch
    {
        SampleTopology.TwoWire => [SampleRouteSelection.TwoWireResistance],
        SampleTopology.FourWire => [SampleRouteSelection.FourWireResistance],
        SampleTopology.HallBar => [SampleRouteSelection.HallLongitudinal, SampleRouteSelection.HallTransverse],
        SampleTopology.VanDerPauw => [SampleRouteSelection.VdpAbCd, SampleRouteSelection.VdpBaCd, SampleRouteSelection.VdpAbDc, SampleRouteSelection.VdpBaDc,
            SampleRouteSelection.VdpBcDa, SampleRouteSelection.VdpCbDa, SampleRouteSelection.VdpBcAd, SampleRouteSelection.VdpCbAd],
        _ => Array.Empty<SampleRouteSelection>(),
    };
    public InstrumentId? WiringSource => ResolveWiring()?.SourceMeter;
    public string Summary
    {
        get
        {
            if (catalog is null) return $"保持原样品 {original.Sample} / 接线 {original.Wiring} / {original.Selection}；未提供路由目录。";
            try
            {
                var context = BuildContext();
                return $"{context.SampleName} · {context.Topology} · {context.SensingMode}\n源 {context.SourceMeter}" + (context.VoltageMeter is { } meter ? $" · 电压表 {meter}" : "") +
                    $" · 矩阵 {context.Matrix}\n" + string.Join("\n", context.Roles.Select(r => $"{r.Role} → {r.Electrode} · 卡 {r.Point.Slot} / 行 {r.Point.Row} / 列 {r.Point.Column}"));
            }
            catch (ArgumentException error) { return error.Message; }
            catch (InvalidOperationException error) { return error.Message; }
            catch (KeyNotFoundException error) { return error.Message; }
        }
    }
    [ObservableProperty] private PlanSampleChoice? selectedSample;
    [ObservableProperty] private PlanWiringChoice? selectedWiring;
    [ObservableProperty] private SampleRouteSelection? selectedMeasurement;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedSampleChanged(PlanSampleChoice? value)
    {
        if (!loading) SelectedMeasurement = null;
        OnPropertyChanged(nameof(Measurements)); Changed();
    }
    partial void OnSelectedWiringChanged(PlanWiringChoice? value) { OnPropertyChanged(nameof(WiringSource)); Changed(); }
    partial void OnSelectedMeasurementChanged(SampleRouteSelection? value) => Changed();
    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(CanRebind));
    private void Changed()
    {
        if (!loading) { IsEdited = true; changed(); }
        OnPropertyChanged(nameof(Summary));
    }
    private static bool Same(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(a, b);
    private SampleProfile? ResolveSample() => Samples.FirstOrDefault(s => SelectedSample is { } selected && Same(s.Profile.Id.Value, selected.Profile.Id.Value))?.Profile;
    private InstrumentWiringProfile? ResolveWiring() => Wirings.FirstOrDefault(w => SelectedWiring is { } selected && Same(w.Profile.Id.Value, selected.Profile.Id.Value))?.Profile;
    public SampleRoutingBinding BuildBinding()
    {
        if (!IsEdited) return original;
        if (catalog is null) throw new InvalidOperationException("先提供计划路由目录再选择样品与接线。");
        var sample = ResolveSample() ?? throw new InvalidOperationException("请选择计划目录中的样品。");
        var wiring = ResolveWiring() ?? throw new InvalidOperationException("请选择与测量架构匹配的计划接线。");
        var selection = SelectedMeasurement ?? throw new InvalidOperationException("请选择样品测量方向。");
        if (!Measurements.Contains(selection)) throw new ArgumentException("测量方向与样品拓扑不匹配。");
        return new(sample.Id, wiring.Id, selection);
    }
    public ResolvedSampleRoute BuildContext() => (catalog ?? throw new InvalidOperationException("当前表单没有路由目录。")).Resolve(BuildBinding());
}
