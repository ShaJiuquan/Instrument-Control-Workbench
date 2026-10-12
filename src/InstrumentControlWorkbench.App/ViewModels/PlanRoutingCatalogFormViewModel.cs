using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanRoutingCatalogFormViewModel : ObservableObject
{
    private readonly ObservableCollection<PlanChannelProfileFormViewModel> channels = new();
    private readonly ObservableCollection<PlanWiringProfileFormViewModel> wirings = new();
    private readonly IReadOnlyList<DeviceDescriptor> devices;
    private readonly Action changed;
    public PlanRoutingCatalogFormViewModel(RoutingConfiguration routing, IReadOnlyList<DeviceDescriptor> devices, IReadOnlyList<MatrixSwitchDefinition> matrices, bool allowGuardCoordination, Action changed)
    {
        this.devices = devices; this.changed = changed; AllowGuardCoordination = allowGuardCoordination; Channels = new(channels); Wirings = new(wirings);
        foreach (var channel in routing.Channels) AddChannelForm(channel); foreach (var wiring in routing.Wirings) AddWiringForm(wiring);
        SelectedChannel = channels[0]; SelectedWiring = wirings[0];
        var matrix = matrices.SingleOrDefault(definition => definition.Device.Id == routing.Matrix);
        MatrixSummary = routing.Matrix + (matrix is null ? " · 未注册" : " · " + string.Join(" · ", matrix.Cards.Select(card => $"卡 {card.Slot}: {card.Rows} 行 × {card.Columns} 列")));
    }
    public string MatrixSummary { get; }
    public ReadOnlyObservableCollection<PlanChannelProfileFormViewModel> Channels { get; }
    public ReadOnlyObservableCollection<PlanWiringProfileFormViewModel> Wirings { get; }
    public bool AllowGuardCoordination { get; }
    public bool CanCoordinateGuards => AllowGuardCoordination && !IsReadOnly;
    [ObservableProperty] private PlanChannelProfileFormViewModel? selectedChannel;
    [ObservableProperty] private PlanWiringProfileFormViewModel? selectedWiring;
    [ObservableProperty] private bool coordinateIndependentRoutes;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedChannelChanged(PlanChannelProfileFormViewModel? value) => RefreshCommands();
    partial void OnSelectedWiringChanged(PlanWiringProfileFormViewModel? value) => RefreshCommands();
    partial void OnCoordinateIndependentRoutesChanged(bool value) => changed();
    partial void OnIsReadOnlyChanged(bool value) { foreach (var channel in Channels) channel.IsReadOnly = value; foreach (var wiring in Wirings) wiring.IsReadOnly = value; OnPropertyChanged(nameof(CanCoordinateGuards)); RefreshCommands(); }
    private void AddChannelForm(ChannelProfile? original)
    {
        PlanChannelProfileFormViewModel? form = null; form = new(original, () => { if (channels.Contains(form!)) changed(); }); form.IsReadOnly = IsReadOnly; channels.Add(form);
    }
    private void AddWiringForm(InstrumentWiringProfile? original)
    {
        PlanWiringProfileFormViewModel? form = null; form = new(original, devices, () => { if (wirings.Contains(form!)) changed(); }); form.IsReadOnly = IsReadOnly; wirings.Add(form);
    }
    private bool CanAddChannel() => !IsReadOnly && channels.Count < 64;
    private bool CanRemoveChannel() => !IsReadOnly && channels.Count > 1 && SelectedChannel is not null && channels.Contains(SelectedChannel);
    private bool CanAddWiring() => !IsReadOnly && wirings.Count < 64;
    private bool CanRemoveWiring() => !IsReadOnly && wirings.Count > 1 && SelectedWiring is not null && wirings.Contains(SelectedWiring);
    [RelayCommand(CanExecute = nameof(CanAddChannel))]
    private void AddChannel() { if (!CanAddChannel()) return; AddChannelForm(null); SelectedChannel = channels[^1]; SelectedChannel.AddConnectionCommand.Execute(null); changed(); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanRemoveChannel))]
    private void RemoveChannel() { if (!CanRemoveChannel()) return; channels.Remove(SelectedChannel!); SelectedChannel = channels[0]; changed(); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanAddWiring))]
    private void AddWiring() { if (!CanAddWiring()) return; AddWiringForm(null); SelectedWiring = wirings[^1]; changed(); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanRemoveWiring))]
    private void RemoveWiring() { if (!CanRemoveWiring()) return; wirings.Remove(SelectedWiring!); SelectedWiring = wirings[0]; changed(); RefreshCommands(); }
    private void RefreshCommands() { AddChannelCommand.NotifyCanExecuteChanged(); RemoveChannelCommand.NotifyCanExecuteChanged(); AddWiringCommand.NotifyCanExecuteChanged(); RemoveWiringCommand.NotifyCanExecuteChanged(); }
    public (ChannelProfile[] Channels, InstrumentWiringProfile[] Wirings) Build() => (channels.Select(channel => channel.Build()).ToArray(), wirings.Select(wiring => wiring.Build()).ToArray());
}
