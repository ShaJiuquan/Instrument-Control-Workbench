using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PlanTemperatureControllerChoice(AxisControllerDefinition Definition)
{
    public string Label => $"{Definition.Device.DisplayName} · {Definition.Device.Id}";
}

public sealed record PlanTemperatureSamplingChoice(TemperatureRampSampling Mode, string Label);

public partial class PlanTemperatureScanFormViewModel : ObservableObject
{
    private readonly TemperatureScanStep original;
    private readonly Action changed;
    private readonly bool template;
    private bool loading = true;
    private bool valuesEdited;
    private bool controllerEdited;

    public PlanTemperatureScanFormViewModel(TemperatureScanStep step, IReadOnlyList<AxisControllerDefinition> axes, Action changed, bool template = false)
    {
        original = step; this.changed = changed; this.template = template;
        // Application supplies the independently configurable controller catalog.
        // A Kelvin parameter alone does not advertise temperature scan capability.
        Controllers = Array.AsReadOnly(axes.Where(a => a.Target.Unit == PhysicalUnit.Kelvin)
            .Select(a => new PlanTemperatureControllerChoice(a)).ToArray());
        SamplingChoices = Array.AsReadOnly(SamplingModes.Select(mode => new PlanTemperatureSamplingChoice(mode, mode switch
        {
            TemperatureRampSampling.Threshold => "按实际温度",
            TemperatureRampSampling.EstimatedSetpoint => IsSoftware ? "按确认设定值" : "按估算设定值",
            TemperatureRampSampling.Timed => "定时间隔",
            TemperatureRampSampling.Immediate => "连续采样",
            _ => throw new InvalidOperationException("Unsupported temperature sampling mode."),
        })).ToArray());
        selectedController = template ? null : Controllers.SingleOrDefault(c => c.Definition.Device.Id == step.Device);
        column = template ? "" : step.Column.Value;
        rate = Number(step.Rate.Value); waitStableFirst = step.WaitStableFirst; tolerance = Number(step.Tolerance.Value);
        stableCount = step.StableCount.ToString(CultureInfo.InvariantCulture); stableTimeoutSeconds = Number(step.StableTimeout.TotalSeconds);
        stepTimeoutSeconds = Number(step.StepTimeout.TotalSeconds); pollSeconds = Number(step.PollInterval.TotalSeconds);
        sampling = step.Sampling; intervalSeconds = Number(step.Interval.TotalSeconds);
        ValuesForm = new(step.Points, () => PhysicalUnit.Kelvin, ValidateValues, () => { valuesEdited = true; MarkDirty(); }, template);
        loading = false;
    }

    public IReadOnlyList<PlanTemperatureControllerChoice> Controllers { get; }
    public PlanScanValuesFormViewModel ValuesForm { get; }
    public TemperatureRampSampling[] SamplingModes { get; } = Enum.GetValues<TemperatureRampSampling>();
    public IReadOnlyList<PlanTemperatureSamplingChoice> SamplingChoices { get; }
    public PlanTemperatureSamplingChoice? SelectedSamplingChoice
    {
        get => SamplingChoices.SingleOrDefault(choice => choice.Mode == Sampling);
        set { if (value is not null) Sampling = value.Mode; }
    }
    public bool IsSoftware => original is SoftwareTemperatureScanStep;
    public string Title => IsSoftware ? "软件温度斜坡扫描" : "硬件温度斜坡扫描";
    public string RateSummary => IsSoftware
        ? "设定值变化速率 / K·min⁻¹。采样和等待时更新设定值；子测量期间不更新，不改变控制器硬件斜坡速率。"
        : "控制器斜坡速率 / K·min⁻¹。由控制器按此速率推进温度斜坡。";
    public string ControllerSummary => ResolveController() is { } controller
        ? $"目标范围 {controller.Target.Minimum?.Value} … {controller.Target.Maximum?.Value} K；实际温度来自 {controller.Readback.Id}，确认设定值来自 {controller.Target.Id}。"
        : !template && !controllerEdited ? $"保留原控制器 {original.Device}；当前可用目录未列出，完整计划验证仍须通过。" : "请选择支持独立温控配置的已注册温度控制器；编辑不会连接设备。";
    public bool HasInterval => Sampling == TemperatureRampSampling.Timed;
    public string SamplingSummary => Sampling switch
    {
        TemperatureRampSampling.Threshold => "按实际温度跨越声明阈值采样；点索引从 0 开始。",
        TemperatureRampSampling.EstimatedSetpoint => IsSoftware ? "按已确认的程序设定值跨越阈值采样；仍独立记录实际温度。" : "按有效运行时间估算设定轨迹触发采样；温度列仍记录实际读回。",
        TemperatureRampSampling.Timed => "按两次采样开始之间的最小实际时间间隔采样（包含子测量和暂停）；total 为名义估算，尾段可能增加点数。",
        TemperatureRampSampling.Immediate => "每次子测量后继续采样；总点数未知，导出 total=0 表示未知。",
        _ => "请选择有效采样模式。",
    };
    public string TimeoutSummary => "启用首点稳定时，稳定与终点等待上限限制首点连续稳定等待；定时间隔和连续采样还使用它限制实际温度到达终点的等待。"
        + (IsSoftware ? "软件尾段从确认程序设定值到达终点起计。" : "硬件尾段从名义斜坡结束起计。")
        + "阈值等待上限只用于按实际温度和按设定值两种方式。暂停时间不计入这些活动期限。";
    public string EvidenceSummary => IsSoftware
        ? "结果列记录实际 K；同名前缀 .setpoint 记录确认设定值，.index/.total 记录进度。计划终点是配方目标，不是实际读回或停止证明。软件点模式尾段可能重复最后索引。"
        : "结果列记录实际 K，.index/.total 记录进度。估算轨迹不是确认设定值；计划终点不是实际读回或停止证明。";
    public string PrecisionSummary => IsSoftware && (Sampling is TemperatureRampSampling.Threshold or TemperatureRampSampling.EstimatedSetpoint)
        ? "软件推进设定值按 0.001 K 舍入；声明阈值保持原精度。非毫开尔文终点可能使设定值阈值无法跨越并超时，请核对原始阈值，建议使用 0.001 K 的整数倍，或明确选择定时间隔并设置合理温度容差；不会自动改写温度点或切换采样方式。"
        : IsSoftware ? "软件推进设定值按 0.001 K 舍入；原始终点和实际温度容差保持声明精度。"
        : "声明温度点保持原精度；适配器和完整计划验证负责控制器能力与范围。";

    [ObservableProperty] private PlanTemperatureControllerChoice? selectedController;
    [ObservableProperty] private string column;
    [ObservableProperty] private string rate;
    [ObservableProperty] private bool waitStableFirst;
    [ObservableProperty] private string tolerance;
    [ObservableProperty] private string stableCount;
    [ObservableProperty] private string stableTimeoutSeconds;
    [ObservableProperty] private string stepTimeoutSeconds;
    [ObservableProperty] private string pollSeconds;
    [ObservableProperty] private TemperatureRampSampling sampling;
    [ObservableProperty] private string intervalSeconds;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedControllerChanged(PlanTemperatureControllerChoice? value)
    {
        if (!loading) controllerEdited = true;
        OnPropertyChanged(nameof(ControllerSummary)); MarkDirty();
    }
    partial void OnColumnChanged(string value) => MarkDirty();
    partial void OnRateChanged(string value) => MarkDirty();
    partial void OnWaitStableFirstChanged(bool value) => MarkDirty();
    partial void OnToleranceChanged(string value) => MarkDirty();
    partial void OnStableCountChanged(string value) => MarkDirty();
    partial void OnStableTimeoutSecondsChanged(string value) => MarkDirty();
    partial void OnStepTimeoutSecondsChanged(string value) => MarkDirty();
    partial void OnPollSecondsChanged(string value) => MarkDirty();
    partial void OnSamplingChanged(TemperatureRampSampling value) { OnPropertyChanged(nameof(HasInterval)); OnPropertyChanged(nameof(SelectedSamplingChoice)); OnPropertyChanged(nameof(SamplingSummary)); OnPropertyChanged(nameof(PrecisionSummary)); MarkDirty(); }
    partial void OnIntervalSecondsChanged(string value) => MarkDirty();
    partial void OnIsReadOnlyChanged(bool value) => ValuesForm.IsReadOnly = value;
    private void MarkDirty() { if (!loading) changed(); }
    private AxisControllerDefinition? ResolveController() => Controllers.FirstOrDefault(c => c.Definition.Device.Id == SelectedController?.Definition.Device.Id)?.Definition;

    private void ValidateValues(ScanValues points)
    {
        _ = Create(original.Device, original.Column, points, original.Rate, original.Tolerance, original.StableCount,
            original.StableTimeout, original.StepTimeout, original.PollInterval, original.Sampling, original.Interval);
        if (ResolveController() is { } controller)
        {
            foreach (var point in points.Values) controller.Target.Validate(point);
            if (IsSoftware)
            {
                controller.Target.Validate(SoftwareTemperatureTrajectory.Round(points.Values[0]));
                controller.Target.Validate(SoftwareTemperatureTrajectory.Round(points.Values[^1]));
            }
        }
    }

    public TemperatureScanStep Build()
    {
        var controller = ResolveController();
        var device = controller?.Device.Id ?? (!template && !controllerEdited ? original.Device
            : throw new InvalidOperationException("请选择支持独立温控配置的温度控制器。"));
        var points = !template && !valuesEdited ? original.Points : ValuesForm.Build();
        ValidateValues(points);
        var requestedRate = new Quantity(Scalar(Rate), PhysicalUnit.KelvinPerMinute);
        if (!IsSoftware && controller is not null) controller.Rate.Validate(requestedRate);
        return Create(device, new(Column), points, requestedRate, new(Scalar(Tolerance), PhysicalUnit.Kelvin), Integer(StableCount),
            Duration(StableTimeoutSeconds, original.StableTimeout), Duration(StepTimeoutSeconds, original.StepTimeout), Duration(PollSeconds, original.PollInterval), Sampling, Duration(IntervalSeconds, original.Interval));
    }

    private TemperatureScanStep Create(InstrumentId device, ParameterId resultColumn, ScanValues points, Quantity requestedRate,
        Quantity requestedTolerance, int count, TimeSpan stableTimeout, TimeSpan stepTimeout, TimeSpan poll, TemperatureRampSampling mode, TimeSpan interval)
        => IsSoftware
            ? new SoftwareTemperatureScanStep(original.Id, device, resultColumn, points, requestedRate, WaitStableFirst, requestedTolerance, count, stableTimeout, stepTimeout, poll, mode, interval, original.Body)
            : new HardwareTemperatureScanStep(original.Id, device, resultColumn, points, requestedRate, WaitStableFirst, requestedTolerance, count, stableTimeout, stepTimeout, poll, mode, interval, original.Body);
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double Scalar(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
        ? number : throw new FormatException("请输入有限数值，使用小数点。");
    private static int Integer(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? number : throw new FormatException("连续稳定次数必须是整数。");
    private static TimeSpan Duration(string value, TimeSpan previous)
    {
        if (value == Number(previous.TotalSeconds)) return previous;
        var seconds = Scalar(value);
        if (seconds < 0 || seconds > TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalSeconds) throw new FormatException("请输入有效的秒数。");
        return TimeSpan.FromSeconds(seconds);
    }
}
