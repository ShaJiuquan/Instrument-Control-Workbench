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

public sealed record PlanSr830AcIsolatorChoice(MatrixSwitchDefinition Definition)
{
    public string Label => $"{Definition.Device.DisplayName} · {Definition.Device.Id} · {Definition.Device.DriverKind}";
}

public partial class PlanSr830AcFormViewModel : ObservableObject
{
    private readonly ReadSr830AcFrameStep original;
    private readonly Action changed;
    private readonly bool template;
    private bool loading = true;

    public PlanSr830AcFormViewModel(ReadSr830AcFrameStep step, IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<MatrixSwitchDefinition> isolations, Action changed, bool template = false)
    {
        original = step; this.changed = changed; this.template = template;
        Isolators = Array.AsReadOnly(isolations.Where(matrix => matrix.AcIsolation is not null).Select(matrix => new PlanSr830AcIsolatorChoice(matrix)).ToArray());
        Generators = Array.AsReadOnly(devices.Where(device => Isolators.Any(matrix => matrix.Definition.AcIsolation!.Generator == device.Id))
            .Select(device => new PlanDeviceChoice(device)).ToArray());
        selectedGenerator = template ? null : Generators.SingleOrDefault(generator => generator.Descriptor.Id == step.Device);
        selectedIsolator = template ? null : Isolators.SingleOrDefault(matrix => matrix.Definition.Device.Id == step.Isolator);
        frequency = Number(step.Excitation.Frequency.Value); sineAmplitude = Number(step.Excitation.SineAmplitude.Value);
        harmonic = step.Excitation.Harmonic.ToString(CultureInfo.InvariantCulture); selectedInput = template ? null : step.Input;
        prefix = template ? "" : step.Prefix.Value; settleSeconds = ((decimal)step.Settle.Ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
        loading = false;
    }

    public IReadOnlyList<PlanDeviceChoice> Generators { get; }
    public IReadOnlyList<PlanSr830AcIsolatorChoice> Isolators { get; }
    public Sr830InputMode[] Inputs { get; } = Enum.GetValues<Sr830InputMode>();
    private DeviceDescriptor? RegisteredGenerator => Generators.SingleOrDefault(generator => generator.Descriptor.Id == SelectedGenerator?.Descriptor.Id)?.Descriptor;
    private MatrixSwitchDefinition? RegisteredIsolator => Isolators.SingleOrDefault(matrix => matrix.Definition.Device.Id == SelectedIsolator?.Definition.Device.Id)?.Definition;
    private bool OriginalBinding => !template && (RegisteredGenerator is null || RegisteredIsolator is null ||
        RegisteredGenerator.Id == original.Device && RegisteredIsolator.Device.Id == original.Isolator);
    public IReadOnlyList<MatrixCrossPoint> Connections => OriginalBinding ? original.Connections : RegisteredIsolator?.AcIsolation?.Connections ?? Array.Empty<MatrixCrossPoint>();
    public string ConnectionsText => string.Join(", ", Connections);
    public string ConnectionsSummary => OriginalBinding
        ? $"保存的通路 {original.Device} → {original.Isolator}，{original.Connections.Count} 个交叉点；保持原连接集，应用时必须与注册声明匹配。"
        : RegisteredIsolator is { AcIsolation: { } capability } matrix
            ? $"注册通路 {capability.Generator} → {matrix.Device.Id}，{capability.Connections.Count} 个交叉点；连接集只读。"
            : "未选择隔离通路；不会自动绑定第一条可用路径。";
    public string SelectionSummary => RegisteredGenerator is { } generator
        ? $"{generator.DisplayName} · {generator.DriverKind}；隔离声明与发生器绑定必须匹配。"
        : !template ? $"保存的发生器 {original.Device}、隔离设备 {original.Isolator}；缺失的注册能力不能由配方补授，应用前须选择匹配声明。" : "显式选择发生器和隔离设备；编辑不会连接或操作设备。";
    public bool IsTemplate => template;
    public string SafetySummary => "SR830 最小振幅非零，停机依靠声明样品通路的断开确认；控制器读回不证明样品已电气隔离。采集后确认断开才保存数据。";
    public string InputSummary => "预期输入模式在运行时须与设备设置一致；此选项不配置输入。X/Y/R 为原始电压域，θ为相位，不直接给出样品电流或微分电阻。";

    [ObservableProperty] private PlanDeviceChoice? selectedGenerator;
    [ObservableProperty] private PlanSr830AcIsolatorChoice? selectedIsolator;
    [ObservableProperty] private string frequency;
    [ObservableProperty] private string sineAmplitude;
    [ObservableProperty] private string harmonic;
    [ObservableProperty] private Sr830InputMode? selectedInput;
    [ObservableProperty] private string prefix;
    [ObservableProperty] private string settleSeconds;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedGeneratorChanged(PlanDeviceChoice? value) => SelectionChanged();
    partial void OnSelectedIsolatorChanged(PlanSr830AcIsolatorChoice? value) => SelectionChanged();
    partial void OnFrequencyChanged(string value) => MarkDirty();
    partial void OnSineAmplitudeChanged(string value) => MarkDirty();
    partial void OnHarmonicChanged(string value) => MarkDirty();
    partial void OnSelectedInputChanged(Sr830InputMode? value) => MarkDirty();
    partial void OnPrefixChanged(string value) => MarkDirty();
    partial void OnSettleSecondsChanged(string value) => MarkDirty();
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(Connections)); OnPropertyChanged(nameof(ConnectionsText));
        OnPropertyChanged(nameof(ConnectionsSummary)); OnPropertyChanged(nameof(SelectionSummary)); MarkDirty();
    }
    private void MarkDirty() { if (!loading) changed(); }

    public ReadSr830AcFrameStep Build()
    {
        var generator = RegisteredGenerator ?? throw new InvalidOperationException("请选择已注册的主动 SR830 发生器。");
        var matrix = RegisteredIsolator ?? throw new InvalidOperationException("请选择已注册的样品隔离通路。");
        var capability = matrix.AcIsolation!;
        if (capability.Generator != generator.Id) throw new InvalidOperationException("所选隔离声明不属于该发生器；请明确选择匹配通路。");
        if (OriginalBinding && !original.Connections.ToHashSet().SetEquals(capability.Connections))
            throw new InvalidOperationException("保存的连接集与注册隔离声明不匹配；不会自动替换原通路。");
        var input = SelectedInput ?? throw new InvalidOperationException("请选择预期 SR830 输入模式。");
        var excitation = new Sr830ExcitationSettings(new(Scalar(Frequency), PhysicalUnit.Hertz), new(Scalar(SineAmplitude), PhysicalUnit.Volt), Integer(Harmonic));
        if (excitation.Frequency.Value > capability.MaximumFrequency.Value || excitation.SineAmplitude.Value > capability.MaximumAmplitude.Value)
            throw new ArgumentOutOfRangeException(nameof(excitation), "发生器设置超出注册隔离能力。");
        return new(original.Id, generator.Id, matrix.Device.Id, Connections, excitation, input, new(Prefix), Duration(SettleSeconds));
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double Scalar(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("请输入有限数值，使用小数点。");
    private static int Integer(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException("谐波必须是整数。");
    private static TimeSpan Duration(string text)
    {
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (!decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out var seconds)) throw new FormatException("等待时间使用十进制秒，不使用指数或分组符号。");
        var fraction = text.Trim().Split('.');
        if (fraction.Length == 2 && fraction[1].Length > 7 && fraction[1].Skip(7).Any(character => character != '0'))
            throw new FormatException("等待时间精度不能超过运行时的 0.0000001 秒。");
        if (seconds < 0 || seconds > (decimal)(uint.MaxValue - 1) / 1000)
            throw new FormatException("等待时间须非负且在支持的计时范围内。");
        var ticks = seconds * TimeSpan.TicksPerSecond;
        if (ticks != decimal.Truncate(ticks))
            throw new FormatException("等待时间须非负、在支持的计时范围内，且精确至 0.0000001 秒。");
        return TimeSpan.FromTicks((long)ticks);
    }
}
