using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanSampleCreationFormViewModel : ObservableObject
{
    private readonly RoutingConfiguration routing;
    private readonly Action changed;
    public PlanSampleCreationFormViewModel(RoutingConfiguration routing, Action changed)
    {
        this.routing = routing; this.changed = changed;
        var placeholder = new SampleProfile(new("draft"), "draft", SampleTopology.TwoWire, SampleProfile.RequiredLabels(SampleTopology.TwoWire).Select(label => new SampleElectrode(new(label), new("unselected"), new("unselected"))));
        Details = new(placeholder, changed, showIdentity: false); Details.SampleName = "";
    }
    public SampleTopology[] Topologies { get; } = Enum.GetValues<SampleTopology>();
    public PlanSampleDetailsFormViewModel Details { get; }
    public bool HasMapping => Mapping is not null;
    [ObservableProperty] private string sampleIdText = "";
    [ObservableProperty] private SampleTopology? topology;
    [ObservableProperty] private PlanSampleElectrodesFormViewModel? mapping;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSampleIdTextChanged(string value) => changed();
    partial void OnTopologyChanged(SampleTopology? value)
    {
        Mapping = null;
        if (value is { } selected && Enum.IsDefined(selected))
        {
            PlanSampleElectrodesFormViewModel? current = null;
            current = new(routing, null, selected, () => { if (ReferenceEquals(Mapping, current)) changed(); }); current.IsReadOnly = IsReadOnly; Mapping = current;
        }
        OnPropertyChanged(nameof(HasMapping)); changed();
    }
    partial void OnIsReadOnlyChanged(bool value) { Details.IsReadOnly = value; if (Mapping is { } model) model.IsReadOnly = value; }
    public SampleProfile Build()
    {
        var selected = Topology ?? throw new InvalidOperationException("请选择样品拓扑。"); var details = Details.Build();
        return new(new SampleId(SampleIdText), details.Name, selected, (Mapping ?? throw new InvalidOperationException("请选择有效拓扑。")).Build(), details.Geometry, details.OriginalId, details.Material, details.Substrate, details.Notes);
    }
}
