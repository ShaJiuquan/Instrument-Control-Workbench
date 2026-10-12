using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanDerivedColumnFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanDerivedColumnFormViewModel(SampleDerivedColumn column, Action changed, bool template)
    { Kind = column.Kind; this.changed = changed; columnId = template ? "" : column.Column.Value; }
    public SampleDerivedQuantity Kind { get; }
    public string Label => Kind + " / " + SampleResistanceAnalysis.Unit(Kind);
    [ObservableProperty] private string columnId;
    [ObservableProperty] private bool isReadOnly;
    partial void OnColumnIdChanged(string value) => changed();
    public SampleDerivedColumn Build() => new(Kind, new(ColumnId));
}

public sealed class PlanAnalysisReadingFormViewModel
{
    public PlanAnalysisReadingFormViewModel(ReadSampleResistanceStep reading, IReadOnlyList<DeviceDescriptor> devices, IReadOnlyList<AxisControllerDefinition> axes, RoutingConfiguration? routing, Action changed, bool template)
    {
        Label = reading.Binding.Selection + " · " + reading.Id;
        Environment = new(reading.Acquisition.EnvironmentReads, axes, changed);
        Acquisition = new(reading, devices, changed, template, Environment, routing);
        if (template) Acquisition.MinimumCurrent = "";
    }
    public string Label { get; }
    public PlanEnvironmentFormViewModel Environment { get; }
    public PlanAcquisitionFormViewModel Acquisition { get; }
    public ReadSampleResistanceStep Build() => (ReadSampleResistanceStep)Acquisition.Build();
}

public partial class PlanSampleAnalysisFormViewModel : ObservableObject
{
    private readonly SampleAnalysisStep original;
    private readonly RoutingConfiguration? routing;
    public PlanSampleAnalysisFormViewModel(SampleAnalysisStep step, IReadOnlyList<DeviceDescriptor> devices, IReadOnlyList<AxisControllerDefinition> axes, RoutingConfiguration? routing, Action changed, bool template = false)
    {
        original = step; this.routing = routing;
        void ReadingChanged() { OnPropertyChanged(nameof(GeometrySummary)); changed(); }
        Columns = Array.AsReadOnly(step.Columns.Select(column => new PlanDerivedColumnFormViewModel(column, changed, template)).ToArray());
        Readings = Array.AsReadOnly(step.Readings.Select(reading => new PlanAnalysisReadingFormViewModel(reading, devices, axes, routing, ReadingChanged, template)).ToArray());
    }
    public IReadOnlyList<PlanDerivedColumnFormViewModel> Columns { get; }
    public IReadOnlyList<PlanAnalysisReadingFormViewModel> Readings { get; }
    public string GroupLabel => $"{original.Readings.Count} 个有序方向 · 算法 v{SampleResistanceAnalysis.AlgorithmVersion}";
    public string GeometrySummary
    {
        get
        {
            try
            {
                var binding = Readings[0].Acquisition.SampleRouting!.BuildBinding();
                var sample = routing?.Samples.FirstOrDefault(s => s.Id == binding.Sample);
                return sample is null ? "几何来自计划样品目录；绑定在应用时验证。" : $"样品 {sample.Name} · {sample.Topology}\n宽 {Dimension(sample.Geometry.Width)} · 长 {Dimension(sample.Geometry.Length)} · 厚 {Dimension(sample.Geometry.Thickness)}";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return error.Message; }
        }
    }
    private static string Dimension(Quantity? value) => value is { } q ? q.Value.ToString("G6", CultureInfo.InvariantCulture) + " Meter" : "未提供";
    [ObservableProperty] private bool isReadOnly;
    partial void OnIsReadOnlyChanged(bool value)
    {
        foreach (var column in Columns) column.IsReadOnly = value;
        foreach (var reading in Readings) reading.Acquisition.IsReadOnly = value;
    }
    public SampleAnalysisStep Build()
    {
        var step = new SampleAnalysisStep(original.Id, Readings.Select(reading => reading.Build()), Columns.Select(column => column.Build()));
        if (routing is not null) step.ValidateRouting(routing);
        return step;
    }
}
