using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanChannelConnectionFormViewModel : ObservableObject
{
    private readonly ChannelConnection? original;
    private readonly Action changed;
    private bool edited;
    public PlanChannelConnectionFormViewModel(ChannelConnection? connection, Action changed)
    {
        original = connection; this.changed = changed; keyText = connection?.Key.Value ?? "";
        slotText = connection?.Slot?.ToString(CultureInfo.InvariantCulture) ?? ""; columnText = connection?.Column?.ToString(CultureInfo.InvariantCulture) ?? "";
        enabled = connection?.Enabled ?? true; label = connection?.Label ?? ""; notes = connection?.Notes ?? "";
    }
    public bool IsIdentityReadOnly => IsReadOnly || original is not null;
    [ObservableProperty] private string keyText;
    [ObservableProperty] private string slotText;
    [ObservableProperty] private string columnText;
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string label;
    [ObservableProperty] private string notes;
    [ObservableProperty] private bool isReadOnly;
    partial void OnKeyTextChanged(string value) => Changed();
    partial void OnSlotTextChanged(string value) => Changed();
    partial void OnColumnTextChanged(string value) => Changed();
    partial void OnEnabledChanged(bool value) => Changed();
    partial void OnLabelChanged(string value) => Changed();
    partial void OnNotesChanged(string value) => Changed();
    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(IsIdentityReadOnly));
    private void Changed() { edited = true; changed(); }
    public ChannelConnection Build() => !edited && original is not null ? original : new(original?.Key ?? new(KeyText),
        string.IsNullOrWhiteSpace(SlotText) ? null : PlanMatrixPortFormViewModel.PositiveInteger(SlotText),
        string.IsNullOrWhiteSpace(ColumnText) ? null : PlanMatrixPortFormViewModel.PositiveInteger(ColumnText), Enabled, original?.Index, Label, original?.DisplayRow, original?.Raw ?? "", Notes);
}
public partial class PlanChannelProfileFormViewModel : ObservableObject
{
    private readonly ChannelProfile? original;
    private readonly Action changed;
    private readonly ObservableCollection<PlanChannelConnectionFormViewModel> connections = new();
    private bool edited;
    public PlanChannelProfileFormViewModel(ChannelProfile? channel, Action changed)
    {
        original = channel; this.changed = changed; idText = channel?.Id.Value ?? ""; channelName = channel?.Name ?? ""; alias = channel?.Alias ?? ""; notes = channel?.Notes ?? "";
        Connections = new(connections); foreach (var row in channel?.Connections ?? []) AddRow(row);
    }
    public string Label => ChannelName + " · " + (original?.Id.Value ?? IdText);
    public bool IsIdentityReadOnly => IsReadOnly || original is not null;
    public ReadOnlyObservableCollection<PlanChannelConnectionFormViewModel> Connections { get; }
    [ObservableProperty] private string idText;
    [ObservableProperty] private string channelName;
    [ObservableProperty] private string alias;
    [ObservableProperty] private string notes;
    [ObservableProperty] private bool isReadOnly;
    partial void OnIdTextChanged(string value) { OnPropertyChanged(nameof(Label)); Changed(); }
    partial void OnChannelNameChanged(string value) { OnPropertyChanged(nameof(Label)); Changed(); }
    partial void OnAliasChanged(string value) => Changed();
    partial void OnNotesChanged(string value) => Changed();
    partial void OnIsReadOnlyChanged(bool value) { OnPropertyChanged(nameof(IsIdentityReadOnly)); foreach (var row in Connections) row.IsReadOnly = value; RefreshCommands(); }
    private void Changed() { edited = true; changed(); }
    private void AddRow(ChannelConnection? connection)
    {
        PlanChannelConnectionFormViewModel? row = null; row = new(connection, () => { if (connections.Contains(row!)) Changed(); }); row.IsReadOnly = IsReadOnly; connections.Add(row);
    }
    private bool CanAddConnection() => !IsReadOnly && Connections.Count < 64;
    private bool CanRemoveConnection(PlanChannelConnectionFormViewModel? row) => !IsReadOnly && Connections.Count > 1 && row is not null && connections.Contains(row);
    [RelayCommand(CanExecute = nameof(CanAddConnection))]
    private void AddConnection() { if (!CanAddConnection()) return; AddRow(null); Changed(); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanRemoveConnection))]
    private void RemoveConnection(PlanChannelConnectionFormViewModel? row) { if (!CanRemoveConnection(row)) return; connections.Remove(row!); Changed(); RefreshCommands(); }
    private void RefreshCommands() { AddConnectionCommand.NotifyCanExecuteChanged(); RemoveConnectionCommand.NotifyCanExecuteChanged(); }
    public ChannelProfile Build() => !edited && original is not null ? original : new(original?.Id ?? new(IdText), ChannelName, Connections.Select(row => row.Build()), original?.OriginalId ?? "", original?.GroupIndex, Alias, Notes);
}
