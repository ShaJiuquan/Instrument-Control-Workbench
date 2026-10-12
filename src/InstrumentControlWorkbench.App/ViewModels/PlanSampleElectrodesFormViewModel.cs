using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PlanChannelChoice(ChannelProfile Profile) { public string Label => Profile.Name + " · " + Profile.Id; }
public sealed record PlanConnectionChoice(ChannelConnection Connection)
{
    public string Label => $"{Connection.Key} · 卡 {Connection.Slot?.ToString() ?? "由接线指定"} / 列 {Connection.Column?.ToString() ?? "未指定"}" + (Connection.Enabled ? "" : " · 禁用");
}
public partial class PlanElectrodeFormViewModel : ObservableObject
{
    private readonly SampleElectrode? original;
    private readonly Action changed;
    private bool loading = true;
    private bool edited;
    public PlanElectrodeFormViewModel(ElectrodeLabel label, IReadOnlyList<ChannelProfile> channels, SampleElectrode? electrode, Action changed)
    {
        Label = label; original = electrode; this.changed = changed; Channels = Array.AsReadOnly(channels.Select(channel => new PlanChannelChoice(channel)).ToArray());
        enabled = electrode?.Enabled ?? true; notes = electrode?.Notes ?? "";
        SelectedChannel = Channels.FirstOrDefault(channel => StringComparer.OrdinalIgnoreCase.Equals(channel.Profile.Id.Value, electrode?.Channel.Value));
        SelectedConnection = Connections.FirstOrDefault(connection => StringComparer.OrdinalIgnoreCase.Equals(connection.Connection.Key.Value, electrode?.Connection.Value)); loading = false;
    }
    public ElectrodeLabel Label { get; }
    public IReadOnlyList<PlanChannelChoice> Channels { get; }
    [ObservableProperty] private IReadOnlyList<PlanConnectionChoice> connections = Array.Empty<PlanConnectionChoice>();
    [ObservableProperty] private PlanChannelChoice? selectedChannel;
    [ObservableProperty] private PlanConnectionChoice? selectedConnection;
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string notes;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedChannelChanged(PlanChannelChoice? value)
    {
        var channel = Channels.FirstOrDefault(choice => StringComparer.OrdinalIgnoreCase.Equals(choice.Profile.Id.Value, value?.Profile.Id.Value));
        Connections = Array.AsReadOnly((channel?.Profile.Connections ?? []).Select(connection => new PlanConnectionChoice(connection)).ToArray()); SelectedConnection = null; Changed();
    }
    partial void OnSelectedConnectionChanged(PlanConnectionChoice? value) => Changed();
    partial void OnEnabledChanged(bool value) => Changed();
    partial void OnNotesChanged(string value) => Changed();
    private void Changed() { if (!loading) { edited = true; changed(); } }
    public SampleElectrode Build()
    {
        if (!edited && original is not null) return original;
        var channel = Channels.FirstOrDefault(choice => StringComparer.OrdinalIgnoreCase.Equals(choice.Profile.Id.Value, SelectedChannel?.Profile.Id.Value))?.Profile ?? throw new InvalidOperationException("请选择计划目录中的通道。");
        var connection = channel.Connections.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Key.Value, SelectedConnection?.Connection.Key.Value)) ?? throw new InvalidOperationException("请选择该通道中的连接点。");
        return new(Label, channel.Id, connection.Key, Enabled, Notes);
    }
}
public partial class PlanSampleElectrodesFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanSampleElectrodesFormViewModel(RoutingConfiguration routing, IReadOnlyList<SampleElectrode>? electrodes, SampleTopology topology, Action changed)
    {
        this.changed = changed;
        Electrodes = Array.AsReadOnly((electrodes is null ? SampleProfile.RequiredLabels(topology).Select(label => new PlanElectrodeFormViewModel(new(label), routing.Channels, null, changed)) : electrodes.Select(electrode => new PlanElectrodeFormViewModel(electrode.Label, routing.Channels, electrode, changed))).ToArray());
        Wirings = Array.AsReadOnly(routing.Wirings.Select(wiring => new PlanWiringChoice(wiring)).ToArray());
    }
    public IReadOnlyList<PlanElectrodeFormViewModel> Electrodes { get; }
    public IReadOnlyList<PlanWiringChoice> Wirings { get; }
    [ObservableProperty] private PlanWiringChoice? selectedWiring;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedWiringChanged(PlanWiringChoice? value) => changed();
    partial void OnIsReadOnlyChanged(bool value) { foreach (var electrode in Electrodes) electrode.IsReadOnly = value; }
    public WiringProfileId BuildWiring() => Wirings.FirstOrDefault(wiring => wiring.Profile.Id == SelectedWiring?.Profile.Id)?.Profile.Id ?? throw new InvalidOperationException("请选择验证接线；应用时检查全部测量方向。");
    public SampleElectrode[] Build() => Electrodes.Select(electrode => electrode.Build()).ToArray();
}
