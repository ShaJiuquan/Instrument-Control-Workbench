using System;
using System.Linq;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

// Named simulation fixture; the editor and executor accept arbitrary supported plans.
public static class DemoExperimentPlans
{
    public static ExperimentPlan Initial(ConfiguredDeviceRuntime runtime)
    {
        if (runtime.Configuration.PrimarySource is { } source && runtime.Configuration.Aliases.TryGetValue("field", out var field) && runtime.Registry.Devices.Any(d => d.Id == field))
            return FieldVoltage(source, runtime.Registry.GetAxisDefinition(field));
        var axes = runtime.Configuration.Devices.Where(d => d.Enabled && d.Category is ConfiguredDeviceCategory.Temperature or ConfiguredDeviceCategory.Field or ConfiguredDeviceCategory.Rotation)
            .Select(d => runtime.Registry.GetAxisDefinition(d.Id)).ToArray();
        return axes.Length > 0 ? new("控制器读数", new ReadStep(new("read_axes"), axes.Select(axis => new ReadChannel(new(axis.Device.Id.Value + ".actual"), new(axis.Device.Id, axis.Readback.Id)))))
            : new("新实验：请编辑或载入计划", new SequenceStep(new("root"), []));
    }
    public static ExperimentPlan FieldVoltage(InstrumentId source, AxisControllerDefinition field) => new("磁场 × 电压联合测量",
        new SequenceStep(new("experiment"),
        [
            new SourceOutputStep(new("source_on"), source, true),
            new ScanStep(new("field_scan"), new(field.Device.Id, field.Target.Id), new("field_axis"),
                ScanValues.Linear(new(0, PhysicalUnit.Tesla), new(0.02, PhysicalUnit.Tesla), 3), TimeSpan.Zero,
                new SequenceStep(new("field_body"),
                [
                    new WaitStableStep(new("field_stable"), new(field.Device.Id, field.Readback.Id), new VariableValue(new("field_axis")),
                        new(0.0001, PhysicalUnit.Tesla), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5)),
                    new ScanStep(new("voltage_scan"), new(source, Keithley2450Parameters.SourceVoltage.Id), new("voltage_axis"),
                        ScanValues.Linear(new(-0.02, PhysicalUnit.Volt), new(0.02, PhysicalUnit.Volt), 5), TimeSpan.FromSeconds(0.3),
                        new ReadStep(new("joint_read"), [new(new("current"), new(source, Keithley2450Parameters.MeasuredCurrent.Id)),
                            new(new("field_readback"), new(field.Device.Id, field.Readback.Id))])),
                ])),
        ]), [new(source, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere),
            new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
}
