using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanPolarizationFormViewModel : ObservableObject
{
    private readonly ReadPolarizationStep original;
    private readonly Action changed;
    private bool loading = true;
    public PlanPolarizationFormViewModel(ReadPolarizationStep step, IReadOnlyList<DeviceDescriptor> devices, Action changed,
        PlanEnvironmentFormViewModel environment, bool template = false)
    {
        original = step; this.changed = changed; Environment = environment;
        Sources = Array.AsReadOnly(devices.Where(d => d.Model == "K2450" && d.StopPolicy == DeviceStopPolicy.OutputOff).Select(d => new PlanDeviceChoice(d)).ToArray());
        selectedSource = template ? null : Sources.SingleOrDefault(d => d.Descriptor.Id == step.Device);
        Target = new(step.PolarizationTarget, MarkDirty, () => PhysicalUnit.Volt);
        if (template) Target.NumberText = "";
        selectedMode = template ? null : step.Sense.Mode;
        polarizationSeconds = Number(step.PolarizationDuration.TotalSeconds); interPulseVoltage = Number(step.InterPulseVoltage.Value); interPulseSeconds = Number(step.InterPulseDuration.TotalSeconds);
        readVoltage = template ? "" : Number(step.ReadVoltage.Value); readDelaySeconds = Number(step.ReadDelay.TotalSeconds);
        compliance = template ? "" : Number(step.Compliance.Value); nplc = Number(step.Sense.Nplc); useManualRange = !step.Sense.AutoRange;
        manualRange = step.Sense.ManualRange is { } span ? Number(span.Value) : "";
        voltageColumn = template ? "" : step.VoltageColumn.Value; currentColumn = template ? "" : step.CurrentColumn.Value;
        attemptsColumn = template ? "" : step.AttemptsColumn.Value; recordRequestedVoltage = template || step.RequestedPolarizationColumn is not null;
        requestedVoltageColumn = template ? "" : step.RequestedPolarizationColumn?.Value ?? ""; maximumAttempts = step.MaximumAttempts.ToString(CultureInfo.InvariantCulture);
        loading = false;
    }
    public IReadOnlyList<PlanDeviceChoice> Sources { get; }
    public SensingMode[] Modes { get; } = Enum.GetValues<SensingMode>();
    public PlanValueFormViewModel Target { get; }
    public PlanEnvironmentFormViewModel Environment { get; }
    public bool CanEditRange => UseManualRange && !IsReadOnly;
    public bool CanEditRequestedColumn => RecordRequestedVoltage && !IsReadOnly;
    public string RouteSummary => original.RequiredRoute is { } route ? $"保留矩阵 {route.Device} 的 {route.Closed.Count} 个交叉点；修改接线前不能更换脉冲源。" : "使用计划的已配置接线；完整计划验证会检查源准备和感测模式。";
    public string AcquisitionSummary => original.Sense.Acquisition is { } settings
        ? $"保留采样设置：自动归零 {(settings.AutoZero ? "开" : "关")}，平均 {(settings.AveragingEnabled ? "开" : "关")}，{settings.AveragingCount} 次。"
        : "沿用源设备的采样设置；每次读出保留实际配置证据。";
    public void SetAvailableVariables(IReadOnlyList<PlanVariableAvailability>? variables) => Target.SetAvailableVariables(variables);
    [ObservableProperty] private PlanDeviceChoice? selectedSource;
    [ObservableProperty] private SensingMode? selectedMode;
    [ObservableProperty] private string polarizationSeconds;
    [ObservableProperty] private string interPulseVoltage;
    [ObservableProperty] private string interPulseSeconds;
    [ObservableProperty] private string readVoltage;
    [ObservableProperty] private string readDelaySeconds;
    [ObservableProperty] private string compliance;
    [ObservableProperty] private string nplc;
    [ObservableProperty] private bool useManualRange;
    [ObservableProperty] private string manualRange;
    [ObservableProperty] private string voltageColumn;
    [ObservableProperty] private string currentColumn;
    [ObservableProperty] private string attemptsColumn;
    [ObservableProperty] private bool recordRequestedVoltage;
    [ObservableProperty] private string requestedVoltageColumn;
    [ObservableProperty] private string maximumAttempts;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedSourceChanged(PlanDeviceChoice? value) => MarkDirty();
    partial void OnSelectedModeChanged(SensingMode? value) => MarkDirty();
    partial void OnPolarizationSecondsChanged(string value) => MarkDirty();
    partial void OnInterPulseVoltageChanged(string value) => MarkDirty();
    partial void OnInterPulseSecondsChanged(string value) => MarkDirty();
    partial void OnReadVoltageChanged(string value) => MarkDirty();
    partial void OnReadDelaySecondsChanged(string value) => MarkDirty();
    partial void OnComplianceChanged(string value) => MarkDirty();
    partial void OnNplcChanged(string value) => MarkDirty();
    partial void OnUseManualRangeChanged(bool value) { OnPropertyChanged(nameof(CanEditRange)); MarkDirty(); }
    partial void OnManualRangeChanged(string value) => MarkDirty();
    partial void OnVoltageColumnChanged(string value) => MarkDirty();
    partial void OnCurrentColumnChanged(string value) => MarkDirty();
    partial void OnAttemptsColumnChanged(string value) => MarkDirty();
    partial void OnRecordRequestedVoltageChanged(bool value) { OnPropertyChanged(nameof(CanEditRequestedColumn)); MarkDirty(); }
    partial void OnRequestedVoltageColumnChanged(string value) => MarkDirty();
    partial void OnMaximumAttemptsChanged(string value) => MarkDirty();
    partial void OnIsReadOnlyChanged(bool value)
    {
        Target.IsReadOnly = value; Environment.IsReadOnly = value;
        OnPropertyChanged(nameof(CanEditRange)); OnPropertyChanged(nameof(CanEditRequestedColumn));
    }
    private void MarkDirty() { if (!loading) changed(); }
    public ReadPolarizationStep Build()
    {
        var source = Sources.SingleOrDefault(d => d.Descriptor.Id == SelectedSource?.Descriptor.Id)?.Descriptor ?? throw new InvalidOperationException("请选择已注册的 K2450 脉冲源。");
        if (original.RequiredRoute is not null && source.Id != original.Device) throw new InvalidOperationException("该脉冲保留矩阵接线，请先调整完整接线再更换源。");
        var mode = SelectedMode ?? throw new InvalidOperationException("请选择两线或四线感测模式。");
        if (!Enum.IsDefined(mode)) throw new ArgumentException("感测模式无效。");
        var read = new Quantity(Scalar(ReadVoltage), PhysicalUnit.Volt); var target = Target.Build();
        var seed = NumericPlanValue.TryConstant(target, out var constant) ? constant : read;
        if (!int.TryParse(MaximumAttempts, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts)) throw new FormatException("最大尝试次数须为 1..100 的整数。");
        return new(original.Id, source.Id, seed, Duration(PolarizationSeconds), new(Scalar(InterPulseVoltage), PhysicalUnit.Volt), Duration(InterPulseSeconds), read, Duration(ReadDelaySeconds), new(Scalar(Compliance), PhysicalUnit.Ampere),
            new(SenseFunction.Current, mode, Scalar(Nplc), !UseManualRange, UseManualRange ? new(Scalar(ManualRange), PhysicalUnit.Ampere) : null, original.Sense.Acquisition),
            new(VoltageColumn), new(CurrentColumn), new(AttemptsColumn), attempts, original.RequiredRoute, Environment.Build(), RecordRequestedVoltage ? new ParameterId(RequestedVoltageColumn) : null, target);
    }
    private static double Scalar(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("请输入有限数值，使用小数点和可选科学计数法。");
    private static TimeSpan Duration(string text)
    {
        var seconds = Scalar(text); if (seconds < 0 || seconds > (uint.MaxValue - 1) / 1000.0) throw new FormatException("时长必须非负且在计时器范围内。");
        return TimeSpan.FromSeconds(seconds);
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
