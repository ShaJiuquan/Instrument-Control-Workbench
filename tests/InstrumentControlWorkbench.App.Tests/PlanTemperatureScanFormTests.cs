using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanTemperatureScanFormTests
{
    [Theory]
    [InlineData(false, TemperatureRampSampling.Threshold)]
    [InlineData(false, TemperatureRampSampling.EstimatedSetpoint)]
    [InlineData(false, TemperatureRampSampling.Timed)]
    [InlineData(false, TemperatureRampSampling.Immediate)]
    [InlineData(true, TemperatureRampSampling.Threshold)]
    [InlineData(true, TemperatureRampSampling.EstimatedSetpoint)]
    [InlineData(true, TemperatureRampSampling.Timed)]
    [InlineData(true, TemperatureRampSampling.Immediate)]
    public void UneditedModelRetainsExactCodecBodyPointsAndTiming(bool software, TemperatureRampSampling mode)
    {
        var step = Step(software, mode); var changes = 0; var axis = Axis();
        var outer = new PlanStepFormViewModel(step, [axis.Device], () => changes++, temperatureScanAxes: [axis]);
        Assert.True(outer.HasTemperatureScan); Assert.True(outer.IsSupported); Assert.Equal(0, changes); Assert.False(outer.IsDirty);
        var rebuilt = Assert.IsAssignableFrom<TemperatureScanStep>(outer.Build());
        Assert.Equal(step.GetType(), rebuilt.GetType()); Assert.Same(step.Body, rebuilt.Body); Assert.Same(step.Points, rebuilt.Points);
        Assert.Equal(step.StableTimeout.Ticks, rebuilt.StableTimeout.Ticks); Assert.Equal(step.Interval.Ticks, rebuilt.Interval.Ticks);
        var codec = new JsonExperimentPlanDocument();
        Assert.Equal(codec.Serialize(new("temperature", step)), codec.Serialize(new("temperature", rebuilt)));
    }

    [Fact]
    public void ChangesPropagateAndReadonlyDoesNotDirtyOrPermitScanCommands()
    {
        var axis = Axis(); var outer = new PlanStepFormViewModel(Step(true), [axis.Device], () => { }, temperatureScanAxes: [axis]);
        var form = outer.TemperatureScan!; outer.IsReadOnly = true;
        Assert.True(form.IsReadOnly); Assert.True(form.ValuesForm.IsReadOnly); Assert.True(form.ValuesForm.Definition.IsReadOnly);
        Assert.False(form.ValuesForm.PreviewCommand.CanExecute(null)); Assert.False(outer.IsDirty);
        outer.IsReadOnly = false; Assert.False(form.IsReadOnly); Assert.False(outer.IsDirty);
        form.Tolerance = ".02"; Assert.True(outer.IsDirty);
        Assert.Equal(new(.02, PhysicalUnit.Kelvin), form.Build().Tolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemplatesRequireExplicitControllerColumnAndValues(bool software)
    {
        var axis = Axis(); var form = new PlanTemperatureScanFormViewModel(Step(software), [axis], () => { }, template: true);
        Assert.Null(form.SelectedController); Assert.Empty(form.Column); Assert.Empty(form.ValuesForm.Definition.ValuesText);
        Assert.Throws<InvalidOperationException>(() => form.Build());
        form.SelectedController = Assert.Single(form.Controllers); Assert.Throws<FormatException>(() => form.Build());
        form.ValuesForm.Definition.ValuesText = "4, 4, 5"; Assert.Throws<ArgumentException>(() => form.Build());
        form.Column = "actual_temperature"; var built = form.Build();
        Assert.Equal(software, built is SoftwareTemperatureScanStep); Assert.Equal(new[] { 4d, 4, 5 }, built.Points.Values.Select(q => q.Value));
    }

    [Fact]
    public void NullCapabilityCatalogDoesNotFallBackToAllAxes()
    {
        var axis = Axis(); var outer = new PlanStepFormViewModel(Step(true), [axis.Device], () => { }, template: true, axes: [axis]);
        Assert.Empty(outer.TemperatureScan!.Controllers); Assert.Throws<InvalidOperationException>(() => outer.Build());
    }

    [Fact]
    public void MissingUneditedControllerRetainsBindingWithoutInventingCapability()
    {
        var step = Step(true); var form = new PlanTemperatureScanFormViewModel(step, [], () => { });
        Assert.Empty(form.Controllers); Assert.Null(form.SelectedController); Assert.Equal(step.Device, form.Build().Device);
        Assert.Contains("完整计划验证", form.ControllerSummary);
    }

    [Fact]
    public void ControllerChangeKeepsInputsAndRejectsNewBounds()
    {
        var first = Axis(); var narrow = Axis("narrow", 4.2); var form = new PlanTemperatureScanFormViewModel(Step(true), [first, narrow], () => { });
        var values = form.ValuesForm.Definition.ValuesText; var rate = form.Rate; var tolerance = form.Tolerance;
        form.SelectedController = form.Controllers.Single(c => c.Definition.Device.Id == narrow.Device.Id);
        Assert.Equal(values, form.ValuesForm.Definition.ValuesText); Assert.Equal(rate, form.Rate); Assert.Equal(tolerance, form.Tolerance);
        Assert.Throws<ArgumentOutOfRangeException>(() => form.Build());
        form.SelectedController = form.Controllers.Single(c => c.Definition.Device.Id == first.Device.Id); Assert.Equal(Step(true).Points.Values, form.Build().Points.Values);
    }

    [Theory]
    [InlineData("rate", "NaN")]
    [InlineData("rate", "Infinity")]
    [InlineData("tolerance", "")]
    [InlineData("count", "1.5")]
    [InlineData("stable", "-1")]
    [InlineData("step", "1e30")]
    [InlineData("poll", "NaN")]
    [InlineData("interval", "Infinity")]
    public void MalformedInputsAreRejected(string field, string value)
    {
        var form = Form(); Set(form, field, value); Assert.Throws<FormatException>(() => form.Build());
    }

    [Theory]
    [InlineData("rate", "0")]
    [InlineData("rate", "21")]
    [InlineData("tolerance", "0")]
    [InlineData("count", "0")]
    [InlineData("count", "10001")]
    [InlineData("stable", "0")]
    [InlineData("step", "0")]
    [InlineData("poll", ".049")]
    [InlineData("interval", ".0009")]
    public void DomainLimitsAreRejected(string field, string value)
    {
        var form = Form(); Set(form, field, value); Assert.ThrowsAny<ArgumentException>(() => form.Build());
    }

    [Theory]
    [InlineData("4")]
    [InlineData("4, 4")]
    [InlineData("4, 5, 4.5")]
    [InlineData("5, 4, 4.5")]
    [InlineData(".9, 4")]
    public void InvalidPointPathsAreRejected(string text)
    {
        var form = Form(); form.ValuesForm.Definition.ValuesText = text; Assert.ThrowsAny<ArgumentException>(() => form.Build());
    }

    [Fact]
    public void SoftwareNominalRateDoesNotUseHardwareRegisterBounds()
    {
        var axis = Axis(rateMaximum: .5); var software = new PlanTemperatureScanFormViewModel(Step(true), [axis], () => { });
        Assert.Equal(new(1.234567890123, PhysicalUnit.KelvinPerMinute), software.Build().Rate);
        var hardware = new PlanTemperatureScanFormViewModel(Step(false), [axis], () => { }); Assert.Throws<ArgumentOutOfRangeException>(() => hardware.Build());
    }

    [Fact]
    public void SamplingDescriptionsSeparateActualConfirmedAndEstimatedEvidence()
    {
        var software = Form(); software.Sampling = TemperatureRampSampling.EstimatedSetpoint;
        Assert.Contains("已确认", software.SamplingSummary); Assert.Contains(".setpoint", software.EvidenceSummary); Assert.Contains("0.001 K", software.PrecisionSummary);
        software.ValuesForm.Definition.ValuesText = "4, 4.0004"; Assert.Equal(4.0004, software.Build().Points.Values[^1].Value);
        software.Sampling = TemperatureRampSampling.Timed; Assert.True(software.HasInterval); Assert.Contains("估算", software.SamplingSummary);
        software.Sampling = TemperatureRampSampling.Immediate; Assert.False(software.HasInterval); Assert.Contains("未知", software.SamplingSummary);
        var hardware = new PlanTemperatureScanFormViewModel(Step(false, TemperatureRampSampling.EstimatedSetpoint), [Axis()], () => { });
        Assert.Contains("估算", hardware.SamplingSummary); Assert.DoesNotContain(".setpoint", hardware.EvidenceSummary);
        software.Sampling = (TemperatureRampSampling)999; Assert.Throws<ArgumentException>(() => software.Build());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChineseSamplingChoicesMapTypedModesAndKeepHiddenTiming(bool software)
    {
        var original = Step(software); var form = new PlanTemperatureScanFormViewModel(original, [Axis()], () => { });
        Assert.Equal(new[] { "按实际温度", software ? "按确认设定值" : "按估算设定值", "定时间隔", "连续采样" }, form.SamplingChoices.Select(choice => choice.Label));
        var interval = form.IntervalSeconds; var stable = form.StableTimeoutSeconds; var step = form.StepTimeoutSeconds; var poll = form.PollSeconds;
        foreach (var choice in form.SamplingChoices)
        {
            form.SelectedSamplingChoice = choice;
            Assert.Equal(choice.Mode, form.Sampling); Assert.Same(choice, form.SelectedSamplingChoice);
            var built = form.Build(); Assert.Equal(choice.Mode, built.Sampling);
            Assert.Equal(original.Interval, built.Interval); Assert.Equal(original.StableTimeout, built.StableTimeout);
            Assert.Equal(original.StepTimeout, built.StepTimeout); Assert.Equal(original.PollInterval, built.PollInterval);
        }
        form.Sampling = TemperatureRampSampling.Threshold;
        Assert.Equal(TemperatureRampSampling.Threshold, form.SelectedSamplingChoice!.Mode);
        Assert.Equal(interval, form.IntervalSeconds); Assert.Equal(stable, form.StableTimeoutSeconds); Assert.Equal(step, form.StepTimeoutSeconds); Assert.Equal(poll, form.PollSeconds);
        Assert.Contains(software ? "确认程序设定值到达终点" : "名义斜坡结束", form.TimeoutSummary);
        Assert.DoesNotContain("墙钟", form.SamplingSummary);
        var codec = new JsonExperimentPlanDocument(); Assert.Equal(codec.Serialize(new("temperature", original)), codec.Serialize(new("temperature", form.Build())));
    }

    private static PlanTemperatureScanFormViewModel Form() => new(Step(true), [Axis()], () => { });
    private static void Set(PlanTemperatureScanFormViewModel form, string field, string value)
    {
        switch (field)
        {
            case "rate": form.Rate = value; break;
            case "tolerance": form.Tolerance = value; break;
            case "count": form.StableCount = value; break;
            case "stable": form.StableTimeoutSeconds = value; break;
            case "step": form.StepTimeoutSeconds = value; break;
            case "poll": form.PollSeconds = value; break;
            case "interval": form.IntervalSeconds = value; break;
        }
    }
    private static TemperatureScanStep Step(bool software, TemperatureRampSampling sampling = TemperatureRampSampling.Threshold)
    {
        var points = new ScanValues([new(4, PhysicalUnit.Kelvin), new(4, PhysicalUnit.Kelvin), new(4.5, PhysicalUnit.Kelvin)]);
        var body = new SequenceStep(new("body"), [new DelayStep(new("child"), TimeSpan.FromMilliseconds(1))]);
        return software
            ? new SoftwareTemperatureScanStep(new("scan"), new("temperature"), new("actual"), points, new(1.234567890123, PhysicalUnit.KelvinPerMinute), true, new(.01234567890123, PhysicalUnit.Kelvin), 7, TimeSpan.FromTicks(100000123), TimeSpan.FromTicks(200000127), TimeSpan.FromTicks(500001), sampling, TimeSpan.FromTicks(300000123), body)
            : new HardwareTemperatureScanStep(new("scan"), new("temperature"), new("actual"), points, new(1.234567890123, PhysicalUnit.KelvinPerMinute), true, new(.01234567890123, PhysicalUnit.Kelvin), 7, TimeSpan.FromTicks(100000123), TimeSpan.FromTicks(200000127), TimeSpan.FromTicks(500001), sampling, TimeSpan.FromTicks(300000123), body);
    }
    private static AxisControllerDefinition Axis(string id = "temperature", double maximum = 10, double rateMaximum = 20)
    {
        var target = new ParameterDescriptor(new("target"), "Target", PhysicalUnit.Kelvin, ParameterAccess.ReadWrite, new(1, PhysicalUnit.Kelvin), new(maximum, PhysicalUnit.Kelvin));
        var readback = new ParameterDescriptor(new("readback"), "Actual", PhysicalUnit.Kelvin, ParameterAccess.Read, new(1, PhysicalUnit.Kelvin), new(maximum, PhysicalUnit.Kelvin));
        var rate = new ParameterDescriptor(new("rate"), "Rate", PhysicalUnit.KelvinPerMinute, ParameterAccess.ReadWrite, new(.01, PhysicalUnit.KelvinPerMinute), new(rateMaximum, PhysicalUnit.KelvinPerMinute));
        var device = new DeviceDescriptor(new(id), "SIM-TEMPERATURE", id, DriverKind.Simulation, DeviceStopPolicy.HoldCurrentPosition, [target, readback, rate]);
        return new(device, target, readback, rate, supportsSoftwareTemperatureRamp: true);
    }
}
