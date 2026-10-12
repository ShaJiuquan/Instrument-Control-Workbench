using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanGeometryDimensionFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanGeometryDimensionFormViewModel(string label, Quantity? value, Action changed)
    { Label = label + " / Meter"; this.changed = changed; included = value is not null; valueText = value is { } q ? q.Value.ToString("R", CultureInfo.InvariantCulture) : ""; }
    public string Label { get; }
    public bool CanEditValue => Included && !IsReadOnly;
    [ObservableProperty] private bool included;
    [ObservableProperty] private string valueText;
    [ObservableProperty] private bool isReadOnly;
    partial void OnIncludedChanged(bool value) { OnPropertyChanged(nameof(CanEditValue)); changed(); }
    partial void OnValueTextChanged(string value) => changed();
    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(CanEditValue));
    public Quantity? Build() => !Included ? null : double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number > 0
        ? new(number, PhysicalUnit.Meter) : throw new FormatException("已提供的样品几何必须是正的有限米值，使用小数点；未提供请取消勾选。");
}

public partial class PlanSampleDetailsFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanSampleDetailsFormViewModel(SampleProfile sample, Action changed, bool showIdentity = true)
    {
        ShowIdentity = showIdentity; Id = sample.Id; Topology = sample.Topology; this.changed = changed;
        sampleName = sample.Name; originalId = sample.OriginalId; material = sample.Material; substrate = sample.Substrate; notes = sample.Notes;
        Width = new("宽度", sample.Geometry.Width, changed); Length = new("长度", sample.Geometry.Length, changed);
        Thickness = new("厚度", sample.Geometry.Thickness, changed); Spacing = new("间距", sample.Geometry.Spacing, changed);
        Dimensions = Array.AsReadOnly(new[] { Width, Length, Thickness, Spacing });
    }
    public bool ShowIdentity { get; }
    public SampleId Id { get; }
    public SampleTopology Topology { get; }
    public string Identity => Id + " · " + Topology;
    public PlanGeometryDimensionFormViewModel Width { get; }
    public PlanGeometryDimensionFormViewModel Length { get; }
    public PlanGeometryDimensionFormViewModel Thickness { get; }
    public PlanGeometryDimensionFormViewModel Spacing { get; }
    public IReadOnlyList<PlanGeometryDimensionFormViewModel> Dimensions { get; }
    [ObservableProperty] private string sampleName;
    [ObservableProperty] private string originalId;
    [ObservableProperty] private string material;
    [ObservableProperty] private string substrate;
    [ObservableProperty] private string notes;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSampleNameChanged(string value) => changed();
    partial void OnOriginalIdChanged(string value) => changed();
    partial void OnMaterialChanged(string value) => changed();
    partial void OnSubstrateChanged(string value) => changed();
    partial void OnNotesChanged(string value) => changed();
    partial void OnIsReadOnlyChanged(bool value) { foreach (var dimension in Dimensions) dimension.IsReadOnly = value; }
    public SampleProfileDetails Build() => new(SampleName, OriginalId, Material, Substrate, Notes, new(Width.Build(), Length.Build(), Thickness.Build(), Spacing.Build()));
}
