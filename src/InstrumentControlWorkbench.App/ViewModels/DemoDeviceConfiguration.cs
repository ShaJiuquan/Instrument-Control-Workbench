using System.Collections.Generic;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.App.ViewModels;

// Explicit first-start simulation profile. An existing settings file always wins.
public static class DemoDeviceConfiguration
{
    public static DeviceConfigurationCatalog Create() => new(new InstrumentId("demo-smu"),
    [
        new(new("demo-smu"), "K2450", "Keithley 2450 simulator", ConfiguredDeviceCategory.SourceMeter, new SimulationDeviceConnection()),
        new(new("demo-field"), "SIM-FIELD", "Field simulator", ConfiguredDeviceCategory.Field, new SimulationDeviceConnection()),
        new(new("demo-temperature"), "SIM-TEMPERATURE", "Temperature simulator", ConfiguredDeviceCategory.Temperature, new SimulationDeviceConnection()),
        new(new("demo-angle"), "ROTATOR", "Angle simulator", ConfiguredDeviceCategory.Rotation, new SimulationDeviceConnection()),
    ], new Dictionary<string, InstrumentId> { ["k2450"] = new("demo-smu"), ["field"] = new("demo-field"), ["temperature"] = new("demo-temperature"), ["angle"] = new("demo-angle") });
}
