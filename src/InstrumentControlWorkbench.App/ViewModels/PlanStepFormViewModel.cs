using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanReadChannelFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanReadChannelFormViewModel(IReadOnlyList<DeviceDescriptor> devices, ReadChannel? channel, Action changed)
    {
        this.changed = changed; column = channel?.Column.Value ?? "";
        Address = new(devices, ParameterAccess.Read, channel?.Address, changed);
    }
    public PlanParameterSelectionViewModel Address { get; }
    [ObservableProperty] private string column;
    [ObservableProperty] private bool isReadOnly;
    partial void OnColumnChanged(string value) => changed();
    partial void OnIsReadOnlyChanged(bool value) => Address.IsReadOnly = value;
    public ReadChannel Build() => new(new(Column), Address.BuildAddress());
}

public partial class PlanStepFormViewModel : ObservableObject
{
    private readonly ExperimentStep original;
    private readonly IReadOnlyList<DeviceDescriptor> devices;
    private readonly Action changed;
    private bool loading = true;
    public PlanStepFormViewModel(ExperimentStep step, IReadOnlyList<DeviceDescriptor> devices, Action changed, bool template = false, IReadOnlyList<AxisControllerDefinition>? axes = null, RoutingConfiguration? routing = null)
    {
        original = step; this.devices = devices; this.changed = changed;
        ParameterAddress? address = step switch { SetParameterStep s => s.Address, ScanStep s => s.Axis, WaitStableStep s => s.Readback, SourceOutputStep s => new(s.Device, new("output")), _ => null };
        Address = new(devices, IsSet || IsScan ? ParameterAccess.Write : ParameterAccess.Read, template ? null : address, AddressChanged, IsOutput);
        switch (step)
        {
            case DelayStep s: seconds = Number(s.Duration.TotalSeconds); break;
            case RepeatStep s: cycles = s.Cycles.ToString(CultureInfo.InvariantCulture); break;
            case TimeoutStep s: seconds = Number(s.Timeout.TotalSeconds); break;
            case RetryStep s: seconds = Number(s.Delay.TotalSeconds); cycles = s.MaxAttempts.ToString(CultureInfo.InvariantCulture); break;
            case WaitStableStep s: seconds = Number(s.Timeout.TotalSeconds); tolerance = Number(s.Tolerance.Value); stableSeconds = Number(s.StableFor.TotalSeconds); pollSeconds = Number(s.PollInterval.TotalSeconds); break;
            case SetParameterStep { Value: ConstantValue value }: valueText = template ? "" : Number(value.Value.Value); break;
            case ScanStep s:
                valuesText = template ? "" : string.Join(", ", s.Values.Values.Select(q => Number(q.Value)));
                column = s.Column.Value; dwell = Number(s.Dwell.TotalSeconds); break;
            case ReadStep s:
                foreach (var channel in s.Channels) Channels.Add(new(devices, template ? null : channel, MarkDirty)); break;
            case SourceOutputStep s: outputEnabled = s.Enabled; break;
        }
        if (HasEnvironment)
        {
            var reads = step switch { ReadStep s => s.EnvironmentReads, ReadSourceSenseStep s => s.EnvironmentReads, ResistanceAcquisitionStep s => s.EnvironmentReads, ReadSampleResistanceStep s => s.Acquisition.EnvironmentReads, _ => Array.Empty<EnvironmentRead>() };
            Environment = new(reads, axes ?? Array.Empty<AxisControllerDefinition>(), MarkDirty);
        }
        if (HasAcquisition) Acquisition = new(step, devices, MarkDirty, template, Environment, routing);
        loading = false;
    }
    public bool IsDelay => original is DelayStep;
    public bool IsRepeat => original is RepeatStep;
    public bool IsTimeout => original is TimeoutStep;
    public bool IsRetry => original is RetryStep;
    public bool IsWaitStable => original is WaitStableStep;
    public bool IsSet => original is SetParameterStep { Value: ConstantValue };
    public bool IsScan => original is ScanStep;
    public bool IsRead => original is ReadStep;
    public bool IsOutput => original is SourceOutputStep;
    public bool HasSeconds => IsDelay || IsTimeout || IsRetry || IsWaitStable;
    public bool HasCycles => IsRepeat || IsRetry;
    public string SecondsLabel => IsRetry ? "重试间隔 / 秒" : IsTimeout || IsWaitStable ? "超时 / 秒" : "等待时间 / 秒";
    public string CyclesLabel => IsRetry ? "最大尝试次数" : "重复次数";
    public string StableTargetLabel => original is WaitStableStep s ? "目标保持原值：" + (s.Target switch { VariableValue v => "变量 " + v.Variable.Value, ConstantValue c => Number(c.Value.Value) + " " + c.Value.Unit, _ => "高级表达式" }) : "";
    public bool HasAddress => IsSet || IsScan || IsWaitStable;
    public bool HasAcquisition => original is ReadSourceSenseStep or ReadResistanceStep or ReadCurrentResistanceStep or ReadSampleResistanceStep;
    public PlanAcquisitionFormViewModel? Acquisition { get; }
    public bool HasEnvironment => IsRead || HasAcquisition;
    public PlanEnvironmentFormViewModel? Environment { get; }
    public bool IsSupported => HasSeconds || IsRepeat || HasAddress || IsRead || IsOutput || HasAcquisition;
    public PlanParameterSelectionViewModel Address { get; }
    public ObservableCollection<PlanReadChannelFormViewModel> Channels { get; } = new();
    [ObservableProperty] private string seconds = "";
    [ObservableProperty] private string cycles = "";
    [ObservableProperty] private string valueText = "";
    [ObservableProperty] private string valuesText = "";
    [ObservableProperty] private string column = "axis";
    [ObservableProperty] private string dwell = "0";
    [ObservableProperty] private bool outputEnabled;
    [ObservableProperty] private string tolerance = "";
    [ObservableProperty] private string stableSeconds = "";
    [ObservableProperty] private string pollSeconds = "";
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSecondsChanged(string value) => MarkDirty();
    partial void OnCyclesChanged(string value) => MarkDirty();
    partial void OnValueTextChanged(string value) => MarkDirty();
    partial void OnValuesTextChanged(string value) => MarkDirty();
    partial void OnColumnChanged(string value) => MarkDirty();
    partial void OnDwellChanged(string value) => MarkDirty();
    partial void OnOutputEnabledChanged(bool value) => MarkDirty();
    partial void OnToleranceChanged(string value) => MarkDirty();
    partial void OnStableSecondsChanged(string value) => MarkDirty();
    partial void OnPollSecondsChanged(string value) => MarkDirty();
    partial void OnIsReadOnlyChanged(bool value)
    {
        Address.IsReadOnly = value; if (Environment is { } environment) environment.IsReadOnly = value; if (Acquisition is { } acquisition) acquisition.IsReadOnly = value; foreach (var channel in Channels) channel.IsReadOnly = value;
        AddChannelCommand.NotifyCanExecuteChanged(); RemoveChannelCommand.NotifyCanExecuteChanged();
    }
    private void AddressChanged()
    {
        if (loading) return;
        // A numeric value must be entered again after the address changes. This
        // prevents silently carrying a voltage across to an angle/temperature.
        ValueText = ""; ValuesText = ""; Tolerance = ""; MarkDirty();
    }
    private void MarkDirty() { if (loading) return; IsDirty = true; changed(); }
    private bool CanAddChannel() => IsRead && !IsReadOnly;
    private bool CanRemoveChannel(PlanReadChannelFormViewModel? channel) => CanAddChannel() && Channels.Count > 1 && channel is not null && Channels.Contains(channel);
    [RelayCommand(CanExecute = nameof(CanAddChannel))]
    private void AddChannel()
    {
        if (!CanAddChannel()) return;
        Channels.Add(new(devices, null, MarkDirty)); RemoveChannelCommand.NotifyCanExecuteChanged(); MarkDirty();
    }
    [RelayCommand(CanExecute = nameof(CanRemoveChannel))]
    private void RemoveChannel(PlanReadChannelFormViewModel? channel)
    {
        if (!CanRemoveChannel(channel)) return;
        Channels.Remove(channel!); RemoveChannelCommand.NotifyCanExecuteChanged(); MarkDirty();
    }
    public ExperimentStep Build()
    {
        if (Acquisition is { } acquisition) return acquisition.Build();
        return original switch
        {
            DelayStep s => new DelayStep(s.Id, Duration(Seconds)),
            RepeatStep s => new RepeatStep(s.Id, Integer(Cycles), s.Body),
            TimeoutStep s => new TimeoutStep(s.Id, Duration(Seconds), s.Body),
            RetryStep s => new RetryStep(s.Id, Integer(Cycles), Duration(Seconds), s.Body),
            WaitStableStep s => new WaitStableStep(s.Id, Address.BuildAddress(), s.Target, new(Scalar(Tolerance), Address.ResolveParameter().Unit), Duration(StableSeconds), Duration(PollSeconds), Duration(Seconds)),
            SetParameterStep { Value: ConstantValue } s => new SetParameterStep(s.Id, Address.BuildAddress(), new ConstantValue(WriteValue(ValueText))),
            ScanStep s => new ScanStep(s.Id, Address.BuildAddress(), new(Column), ScanValues(), Duration(Dwell), s.Body),
            ReadStep s => new ReadStep(s.Id, Channels.Select(c => c.Build()), Environment!.Build(), s.RequiredRoute, s.ExcitationSource),
            SourceOutputStep s => new SourceOutputStep(s.Id, Address.ResolveDevice().Id, OutputEnabled),
            _ => throw new NotSupportedException("该节点使用高级 JSON 编辑。"),
        };
    }
    private Quantity WriteValue(string text)
    {
        var parameter = Address.ResolveParameter(); var value = new Quantity(Scalar(text), parameter.Unit);
        parameter.ValidateWrite(value); return value;
    }
    private ScanValues ScanValues()
    {
        if (Encoding.UTF8.GetByteCount(ValuesText) > 4 * 1024 * 1024) throw new FormatException("扫描数值超过计划字节预算。");
        var parts = ValuesText.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > 1_000_000) throw new FormatException("扫描点数超过计划预算。");
        return new(parts.Select(WriteValue));
    }
    private static double Scalar(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("请输入有限数值，使用小数点和可选科学计数法。");
    private static int Integer(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException("重复次数必须是整数。");
    private static TimeSpan Duration(string text)
    {
        var seconds = Scalar(text);
        if (seconds < 0 || seconds > (uint.MaxValue - 1) / 1000.0) throw new FormatException("时间必须非负且在计时器范围内。");
        return TimeSpan.FromSeconds(seconds);
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
