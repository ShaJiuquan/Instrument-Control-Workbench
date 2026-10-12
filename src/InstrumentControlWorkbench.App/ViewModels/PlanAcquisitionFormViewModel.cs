using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanAcquisitionFormViewModel : ObservableObject
{
    private readonly ExperimentStep original;
    private readonly ExperimentStep acquisition;
    private readonly Action changed;
    private bool loading = true;
    private InstrumentId? routingSource;
    public PlanAcquisitionFormViewModel(ExperimentStep step, IReadOnlyList<DeviceDescriptor> devices, Action changed, bool template = false, PlanEnvironmentFormViewModel? environment = null, RoutingConfiguration? routing = null)
    {
        original = step; acquisition = step is ReadSampleResistanceStep sample ? sample.Acquisition : step; this.changed = changed;
        if (acquisition is not (ReadSourceSenseStep or ReadResistanceStep or ReadCurrentResistanceStep)) throw new ArgumentException("Unsupported acquisition form.", nameof(step));
        Sources = devices.Where(d => d.StopPolicy == DeviceStopPolicy.OutputOff && (d.Model == "K2450" || IsIndependent && d.Model == "K6221"))
            .Select(d => new PlanDeviceChoice(d)).ToList().AsReadOnly();
        Meters = devices.Where(d => d.Model == "K2182" && d.StopPolicy == DeviceStopPolicy.AbortAcquisition)
            .Select(d => new PlanDeviceChoice(d)).ToList().AsReadOnly();
        switch (acquisition)
        {
            case ReadSourceSenseStep s:
                SelectedSource = Sources.FirstOrDefault(d => !template && d.Descriptor.Id == s.Device);
                SourceFunction = s.Function; VoltageColumn = s.VoltageColumn.Value; CurrentColumn = s.CurrentColumn.Value; break;
            case ResistanceAcquisitionStep s:
                SelectedSource = Sources.FirstOrDefault(d => !template && d.Descriptor.Id == s.Device);
                SourceFunction = s.Function; VoltageColumn = s.VoltageColumn.Value; CurrentColumn = s.CurrentColumn.Value;
                ResistanceColumn = s.ResistanceColumn.Value; MinimumCurrent = Number(s.MinimumCurrent.Value); break;
        }
        if (acquisition is ReadCurrentResistanceStep independent)
        {
            SelectedMeter = Meters.FirstOrDefault(d => !template && d.Descriptor.Id == independent.Meter);
            SelectedReference = template ? null : independent.CurrentReference;
            UseExpectedCurrent = independent.ExpectedProgrammedCurrent is ConstantValue;
            ExpectedCurrent = independent.ExpectedProgrammedCurrent is ConstantValue constant ? Number(constant.Value.Value) : "";
            UseRetry = independent.Retry is not null;
            if (independent.Retry is { } retry)
            {
                OwnsSourceOutput = retry.OwnsSourceOutput; RetrySettle = Number(retry.Settle.TotalSeconds); ContinueOnFailure = retry.ContinueOnAcquisitionFailure;
            }
        }
        if (step is ReadSampleResistanceStep routed)
        {
            SampleSettle = Number(routed.Settle.TotalSeconds); AcquisitionSettle = Number(routed.AcquisitionSettle.TotalSeconds);
            RoutingManagedExternally = routed.RoutingManagedExternally;
            if (template) MinimumCurrent = "";
        }
        Environment = environment ?? new(EnvironmentReads, Array.Empty<AxisControllerDefinition>(), changed);
        if (step is ReadSampleResistanceStep sampleStep)
        {
            routingSource = sampleStep.Acquisition.Device;
            SampleRouting = new(sampleStep.Binding, routing, IsIndependent, RoutingChanged);
        }
        loading = false;
    }
    public bool IsIndependent => acquisition is ReadCurrentResistanceStep;
    public bool IsSample => original is ReadSampleResistanceStep;
    public bool HasResistance => acquisition is ResistanceAcquisitionStep;
    public bool HasSourceFunction => !IsIndependent;
    public bool CanChooseSource => !IsReadOnly && !IsSample;
    public PlanEnvironmentFormViewModel Environment { get; }
    public PlanSampleRoutingFormViewModel? SampleRouting { get; }
    private InstrumentId? CurrentSourceId => IsSample ? SampleRouting?.WiringSource ?? routingSource : SelectedSource?.Descriptor.Id;
    public bool HasExpectedExpression => acquisition is ReadCurrentResistanceStep { ExpectedProgrammedCurrent: not null and not ConstantValue };
    public IReadOnlyList<PlanDeviceChoice> Sources { get; }
    public IReadOnlyList<PlanDeviceChoice> Meters { get; }
    public SourceFunction[] SourceFunctions { get; } = Enum.GetValues<SourceFunction>();
    public CurrentReference[] CurrentReferences => Sources.FirstOrDefault(d => d.Descriptor.Id == CurrentSourceId)?.Descriptor.Model == "K6221" ? [CurrentReference.ProgrammedSetpoint] : Enum.GetValues<CurrentReference>();
    public bool CanEditRetry => !IsReadOnly && UseRetry;
    public bool CanChooseRetryOwnership => CanEditRetry && !IsSample;
    public bool CanEditRetrySettle => CanEditRetry;
    public bool CanChooseExternalRouting => !IsReadOnly && !IsIndependent;
    public bool CanEditExpectedCurrent => !IsReadOnly && UseExpectedCurrent && !HasExpectedExpression;
    public string ExpectedExpressionLabel => acquisition is ReadCurrentResistanceStep { ExpectedProgrammedCurrent: { } expected } ? expected switch
    {
        VariableValue variable => "保持预期电流变量：" + variable.Variable.Value,
        QuantityFromNumberValue value => "保持预期电流表达式：" + value.Variable.Value + " / " + value.Unit,
        _ => "保持原预期电流表达式；高级 JSON 可编辑。",
    } : "";
    public string PreservedContext =>
        (IsSample ? "样品、接线和矩阵保护由上方计划目录选择解析。\n" :
            acquisition is ReadCurrentResistanceStep { RequiredRoute: { } route } ? $"矩阵保护 {route.Device} · {route.Closed.Count} 个交叉点保持原值\n" : "") +
        "源准备与完整路由仍由计划验证；环境读数可在下方单独绑定。";
    private IReadOnlyList<EnvironmentRead> EnvironmentReads => acquisition switch { ReadSourceSenseStep s => s.EnvironmentReads, ResistanceAcquisitionStep s => s.EnvironmentReads, _ => Array.Empty<EnvironmentRead>() };
    [ObservableProperty] private PlanDeviceChoice? selectedSource;
    [ObservableProperty] private PlanDeviceChoice? selectedMeter;
    [ObservableProperty] private SourceFunction sourceFunction;
    [ObservableProperty] private CurrentReference? selectedReference;
    [ObservableProperty] private string voltageColumn = "voltage_read";
    [ObservableProperty] private string currentColumn = "current_read";
    [ObservableProperty] private string resistanceColumn = "resistance";
    [ObservableProperty] private string minimumCurrent = "0";
    [ObservableProperty] private bool useExpectedCurrent;
    [ObservableProperty] private string expectedCurrent = "";
    [ObservableProperty] private bool useRetry;
    [ObservableProperty] private bool ownsSourceOutput = true;
    [ObservableProperty] private string retrySettle = "0";
    [ObservableProperty] private bool continueOnFailure;
    [ObservableProperty] private string sampleSettle = "0";
    [ObservableProperty] private string acquisitionSettle = "0";
    [ObservableProperty] private bool routingManagedExternally;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedSourceChanged(PlanDeviceChoice? value)
    {
        if (!loading && IsIndependent) { SelectedReference = null; if (!HasExpectedExpression) ExpectedCurrent = ""; }
        OnPropertyChanged(nameof(CurrentReferences)); MarkDirty();
    }
    partial void OnSelectedMeterChanged(PlanDeviceChoice? value) => MarkDirty();
    partial void OnSourceFunctionChanged(SourceFunction value) => MarkDirty();
    partial void OnSelectedReferenceChanged(CurrentReference? value) => MarkDirty();
    partial void OnVoltageColumnChanged(string value) => MarkDirty();
    partial void OnCurrentColumnChanged(string value) => MarkDirty();
    partial void OnResistanceColumnChanged(string value) => MarkDirty();
    partial void OnMinimumCurrentChanged(string value) => MarkDirty();
    partial void OnUseExpectedCurrentChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnExpectedCurrentChanged(string value) => MarkDirty();
    partial void OnUseRetryChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnOwnsSourceOutputChanged(bool value) { RefreshControls(); MarkDirty(); }
    partial void OnRetrySettleChanged(string value) => MarkDirty();
    partial void OnContinueOnFailureChanged(bool value) => MarkDirty();
    partial void OnSampleSettleChanged(string value) => MarkDirty();
    partial void OnAcquisitionSettleChanged(string value) => MarkDirty();
    partial void OnRoutingManagedExternallyChanged(bool value) => MarkDirty();
    partial void OnIsReadOnlyChanged(bool value)
    {
        Environment.IsReadOnly = value; if (SampleRouting is { } routing) routing.IsReadOnly = value; RefreshControls();
    }
    private void RefreshControls()
    {
        OnPropertyChanged(nameof(CanChooseSource)); OnPropertyChanged(nameof(CanChooseExternalRouting)); OnPropertyChanged(nameof(CanEditRetry)); OnPropertyChanged(nameof(CanChooseRetryOwnership)); OnPropertyChanged(nameof(CanEditRetrySettle)); OnPropertyChanged(nameof(CanEditExpectedCurrent));
    }
    private void RoutingChanged()
    {
        var source = SampleRouting?.WiringSource;
        if (!loading && source != routingSource && IsIndependent) { SelectedReference = null; if (!HasExpectedExpression) ExpectedCurrent = ""; }
        routingSource = source; OnPropertyChanged(nameof(CurrentReferences)); MarkDirty();
    }
    private void MarkDirty() { if (!loading) changed(); }
    private InstrumentId SourceId() => Sources.FirstOrDefault(d => d.Descriptor.Id == SelectedSource?.Descriptor.Id)?.Descriptor.Id
        ?? throw new InvalidOperationException("请选择已注册、支持该测量架构的源设备。");
    private InstrumentId MeterId() => Meters.FirstOrDefault(d => d.Descriptor.Id == SelectedMeter?.Descriptor.Id)?.Descriptor.Id
        ?? throw new InvalidOperationException("请选择已注册的 K2182 电压表。");
    private CurrentReference Reference(InstrumentId source)
    {
        var reference = SelectedReference ?? throw new InvalidOperationException("请选择电流参考来源。");
        if (!Enum.IsDefined(reference) || Sources.Single(d => d.Descriptor.Id == source).Descriptor.Model == "K6221" && reference != CurrentReference.ProgrammedSetpoint)
            throw new InvalidOperationException("该源设备不支持所选电流参考。");
        return reference;
    }
    public ExperimentStep Build()
    {
        var context = IsSample && SampleRouting?.CanResolveContext == true ? SampleRouting.BuildContext() : null;
        var source = context?.SourceMeter ?? SourceId();
        if (!Sources.Any(s => s.Descriptor.Id == source)) throw new InvalidOperationException("样品接线的源设备不在该测量架构的运行目录中。");
        var voltage = new ParameterId(VoltageColumn); var current = new ParameterId(CurrentColumn);
        ExperimentStep result = acquisition switch
        {
            ReadSourceSenseStep s => new ReadSourceSenseStep(s.Id, source, SourceFunction, voltage, current, Environment.Build()),
            ReadResistanceStep s => new ReadResistanceStep(s.Id, source, SourceFunction, voltage, current, new(ResistanceColumn), new(Scalar(MinimumCurrent), PhysicalUnit.Ampere), Environment.Build()),
            ReadCurrentResistanceStep s => new ReadCurrentResistanceStep(s.Id, source, context?.VoltageMeter ?? MeterId(), Reference(source), voltage, current, new(ResistanceColumn), new(Scalar(MinimumCurrent), PhysicalUnit.Ampere),
                HasExpectedExpression ? s.ExpectedProgrammedCurrent : UseExpectedCurrent ? new ConstantValue(new(Scalar(ExpectedCurrent), PhysicalUnit.Ampere)) : null,
                context is not null && SampleRouting!.IsEdited ? new MatrixRouteExpectation(context.Matrix, context.Closed) : s.RequiredRoute, Environment.Build(), UseRetry ? new(OwnsSourceOutput, Duration(RetrySettle), ContinueOnFailure) : null),
            _ => throw new InvalidOperationException("Unsupported acquisition form."),
        };
        return original is ReadSampleResistanceStep sample
            ? new ReadSampleResistanceStep((ResistanceAcquisitionStep)result, SampleRouting!.BuildBinding(), Duration(SampleSettle), RoutingManagedExternally, Duration(AcquisitionSettle)) : result;
    }
    private static double Scalar(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("请输入有限数值，使用小数点和可选科学计数法。");
    private static TimeSpan Duration(string text)
    {
        var seconds = Scalar(text);
        if (seconds < 0 || seconds > (uint.MaxValue - 1) / 1000.0) throw new FormatException("等待时间必须非负且在计时器范围内。");
        return TimeSpan.FromSeconds(seconds);
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
