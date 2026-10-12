using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PlanMatrixChoice(MatrixSwitchDefinition Definition)
{
    public string Label => Definition.Device.DisplayName + " · " + Definition.Device.Id + " · " + Definition.Device.DriverKind;
}
public partial class PlanRoutingInitializationFormViewModel : ObservableObject
{
    private readonly Action changed;
    private bool loading = true;
    public PlanRoutingInitializationFormViewModel(IReadOnlyList<DeviceDescriptor> devices, IReadOnlyList<MatrixSwitchDefinition> matrices, Action changed)
    {
        this.changed = changed; Matrices = Array.AsReadOnly(matrices.Select(matrix => new PlanMatrixChoice(matrix)).ToArray());
        Sources = Array.AsReadOnly(devices.Where(device => device.StopPolicy == DeviceStopPolicy.OutputOff && device.Model is "K2450" or "K6221").Select(device => new PlanDeviceChoice(device)).ToArray());
        Meters = Array.AsReadOnly(devices.Where(device => device.StopPolicy == DeviceStopPolicy.AbortAcquisition && device.Model == "K2182").Select(device => new PlanDeviceChoice(device)).ToArray());
        SourcePlus = new("源正端", Changed); SourceMinus = new("源负端", Changed); SensePlus = new("测量正端", Changed); SenseMinus = new("测量负端", Changed); Ports = Array.AsReadOnly(new[] { SourcePlus, SourceMinus, SensePlus, SenseMinus });
        var placeholder = new SampleProfile(new("draft"), "draft", SampleTopology.TwoWire, SampleProfile.RequiredLabels(SampleTopology.TwoWire).Select(label => new SampleElectrode(new(label), new("unselected"), new("unselected"))));
        Details = new(placeholder, Changed, showIdentity: false); Details.SampleName = ""; loading = false;
    }
    public IReadOnlyList<PlanMatrixChoice> Matrices { get; }
    public IReadOnlyList<PlanDeviceChoice> Sources { get; }
    public IReadOnlyList<PlanDeviceChoice> Meters { get; }
    public PlanSampleDetailsFormViewModel Details { get; }
    public PlanMatrixPortFormViewModel SourcePlus { get; }
    public PlanMatrixPortFormViewModel SourceMinus { get; }
    public PlanMatrixPortFormViewModel SensePlus { get; }
    public PlanMatrixPortFormViewModel SenseMinus { get; }
    public IReadOnlyList<PlanMatrixPortFormViewModel> Ports { get; }
    public SampleTopology[] Topologies { get; } = Enum.GetValues<SampleTopology>();
    public string MatrixSummary => Matrices.FirstOrDefault(matrix => matrix.Definition.Device.Id == SelectedMatrix?.Definition.Device.Id)?.Definition is { } definition
        ? string.Join(" · ", definition.Cards.Select(card => $"卡 {card.Slot}: {card.Rows} 行 × {card.Columns} 列")) + "\n保护源: " + string.Join(", ", definition.GuardedSources) : "请选择已注册矩阵；规格由运行目录确定。";
    public bool CanChooseMeter => UseIndependentMeter && !IsReadOnly;
    [ObservableProperty] private PlanMatrixChoice? selectedMatrix;
    [ObservableProperty] private PlanDeviceChoice? selectedSource;
    [ObservableProperty] private PlanDeviceChoice? selectedMeter;
    [ObservableProperty] private bool useIndependentMeter;
    [ObservableProperty] private string wiringIdText = "";
    [ObservableProperty] private string wiringLabel = "";
    [ObservableProperty] private string channelIdText = "";
    [ObservableProperty] private string channelName = "";
    [ObservableProperty] private string sampleIdText = "";
    [ObservableProperty] private SampleTopology? topology;
    [ObservableProperty] private IReadOnlyList<PlanInitialConnectionFormViewModel> connections = Array.Empty<PlanInitialConnectionFormViewModel>();
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedMatrixChanged(PlanMatrixChoice? value) { foreach (var port in Ports) port.Clear(); foreach (var connection in Connections) connection.ClearAddress(); OnPropertyChanged(nameof(MatrixSummary)); Changed(); }
    partial void OnSelectedSourceChanged(PlanDeviceChoice? value) { foreach (var port in Ports) port.Clear(); Changed(); }
    partial void OnSelectedMeterChanged(PlanDeviceChoice? value) { SensePlus.Clear(); SenseMinus.Clear(); Changed(); }
    partial void OnUseIndependentMeterChanged(bool value) { foreach (var port in Ports) port.Clear(); OnPropertyChanged(nameof(CanChooseMeter)); Changed(); }
    partial void OnWiringIdTextChanged(string value) => Changed();
    partial void OnWiringLabelChanged(string value) => Changed();
    partial void OnChannelIdTextChanged(string value) => Changed();
    partial void OnChannelNameChanged(string value) => Changed();
    partial void OnSampleIdTextChanged(string value) => Changed();
    partial void OnTopologyChanged(SampleTopology? value)
    {
        Connections = Array.Empty<PlanInitialConnectionFormViewModel>();
        if (value is { } selected && Enum.IsDefined(selected)) Connections = Array.AsReadOnly(SampleProfile.RequiredLabels(selected).Select(label =>
        { PlanInitialConnectionFormViewModel? row = null; row = new(label, () => { if (Connections.Contains(row!)) Changed(); }); row.IsReadOnly = IsReadOnly; return row; }).ToArray());
        Changed();
    }
    partial void OnIsReadOnlyChanged(bool value) { foreach (var port in Ports) port.IsReadOnly = value; foreach (var connection in Connections) connection.IsReadOnly = value; Details.IsReadOnly = value; OnPropertyChanged(nameof(CanChooseMeter)); }
    private void Changed() { if (!loading) changed(); }
    public RoutingConfiguration Build()
    {
        var matrix = Matrices.FirstOrDefault(choice => choice.Definition.Device.Id == SelectedMatrix?.Definition.Device.Id)?.Definition ?? throw new InvalidOperationException("请选择已注册矩阵。");
        var source = Sources.FirstOrDefault(choice => choice.Descriptor.Id == SelectedSource?.Descriptor.Id)?.Descriptor ?? throw new InvalidOperationException("请选择已注册的支持源。");
        var meter = UseIndependentMeter ? Meters.FirstOrDefault(choice => choice.Descriptor.Id == SelectedMeter?.Descriptor.Id)?.Descriptor ?? throw new InvalidOperationException("请选择已注册 K2182 电压表。") : null;
        if (source.Model == "K6221" && meter is null) throw new InvalidOperationException("K6221 电流源需要独立 K2182 电压表。");
        var selected = Topology ?? throw new InvalidOperationException("请选择首个样品拓扑。");
        var channel = new ChannelProfile(new(ChannelIdText), ChannelName, Connections.Select(connection => connection.Build()));
        var wiring = new InstrumentWiringProfile(new(WiringIdText), source.Id, SourcePlus.Build(), SourceMinus.Build(), SensePlus.Build(), SenseMinus.Build(), WiringLabel, meter?.Id);
        var details = Details.Build(); var sample = new SampleProfile(new SampleId(SampleIdText), details.Name, selected, Connections.Select(connection => new SampleElectrode(connection.Electrode, channel.Id, new(connection.KeyText))), details.Geometry, details.OriginalId, details.Material, details.Substrate, details.Notes);
        return new(matrix.Device.Id, [channel], [wiring], [sample]);
    }
}
