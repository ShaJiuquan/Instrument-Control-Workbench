using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class IndependentChannelRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogBindingPreservesSourceFamiliesAndMetersWithoutExpandingChannelAliases(bool smu)
    {
        var catalog = Catalog(); var devices = Devices(smu); var bindings = Bind(catalog, devices);
        Assert.Empty(bindings.Channels); Assert.Equal(devices.CurrentSourcePreparations, bindings.CurrentSourcePreparations);
        Assert.Equal(devices.Preparations, bindings.Preparations); Assert.Equal(devices.StandaloneVoltageMeters, bindings.StandaloneVoltageMeters);
        Assert.Same(bindings.Samples!.Routing, bindings.ChannelProfiles!.Routing);
        var route = bindings.ChannelProfiles.Resolve("contacts", SensingMode.TwoWire, "curr_dc");
        Assert.Equal(new("source"), route.Source); Assert.Equal(new InstrumentId("meter"), route.VoltageMeter);
        Assert.Equal(new[] { new MatrixCrossPoint(1, 1, 1), new(1, 2, 2), new(1, 3, 1), new(1, 4, 2) }, route.Route.Closed);
        Assert.Throws<ArgumentException>(() => bindings.ChannelProfiles.Resolve("contacts", SensingMode.FourWire, "curr_dc"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("matrix")]
    [InlineData("unknown")]
    [InlineData("")]
    public void InvalidIndependentMeterCannotBind(string meter)
    {
        Assert.Throws<ArgumentException>(() => Bind(Catalog(), Devices(), new(meter)));
    }

    [Theory]
    [InlineData("missingCurrent")]
    [InlineData("missingMeter")]
    [InlineData("voltageSmu")]
    [InlineData("matrixSource")]
    [InlineData("sharedPorts")]
    public void InvalidIndependentElectricalRelationshipsReject(string fault)
    {
        var devices = Devices(fault == "voltageSmu", fault == "missingCurrent", fault == "missingMeter", fault == "voltageSmu");
        var source = fault == "matrixSource" ? new InstrumentId("matrix") : new("source");
        Assert.Throws<ArgumentException>(() => Catalog(fault == "sharedPorts").Bind(devices, new("matrix"),
            [KeyValuePair.Create(new WiringProfileId("circuit"), source)], new("circuit"), channelVoltageMeter: new("meter")));
    }

    [Theory]
    [InlineData("volt_dc")]
    [InlineData("curr_delta")]
    public void IndependentCatalogDoesNotSilentlyUseAnotherMeasurementFamily(string mode)
    {
        var catalog = Catalog(); catalog = new(catalog.ActiveChannel, catalog.ActiveSample, catalog.Channels, catalog.Samples, catalog.Wirings,
            [KeyValuePair.Create(mode, new WiringProfileId("circuit"))]);
        var profiles = Bind(catalog, Devices()).ChannelProfiles!;
        Assert.Throws<ArgumentException>(() => profiles.Resolve("contacts", SensingMode.TwoWire, mode));
        Assert.Throws<ArgumentException>(() => profiles.Resolve("contacts", SensingMode.TwoWire, "curr_dc"));
    }

    [Fact]
    public void MeterDeclarationCannotMutateExistingDeviceBindings()
    {
        var meters = new List<InstrumentId> { new("meter") }; var prepared = new List<CurrentSourcePreparation> { new(new("source"), new(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt))) };
        var devices = new ExperimentImportBindings(Devices().Instruments, [], standaloneVoltageMeters: meters, currentSourcePreparations: prepared);
        var bindings = Bind(Catalog(), devices); meters.Clear(); prepared.Clear();
        Assert.Single(bindings.StandaloneVoltageMeters); Assert.Single(bindings.CurrentSourcePreparations);
    }

    private static ExperimentImportBindings Bind(RoutingProfileCatalog catalog, ExperimentImportBindings devices, InstrumentId? meter = null) =>
        catalog.Bind(devices, new("matrix"), [KeyValuePair.Create(new WiringProfileId("circuit"), new InstrumentId("source"))], new("circuit"), channelVoltageMeter: meter ?? new("meter"));
    private static ExperimentImportBindings Devices(bool smu = false, bool missingCurrent = false, bool missingMeter = false, bool voltage = false) =>
        new(new Dictionary<string, InstrumentId> { [smu ? "k2450" : "k6221"] = new("source"), ["k2182"] = new("meter"), ["k3706"] = new("matrix") }, [],
            preparations: smu ? [new(new("source"), voltage ? SourceFunction.Voltage : SourceFunction.Current, new(1e-5, voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere), new(10, voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(voltage ? SenseFunction.Current : SenseFunction.Voltage, SensingMode.TwoWire, 1, true))] : [],
            standaloneVoltageMeters: missingMeter ? [] : [new("meter")],
            currentSourcePreparations: smu || missingCurrent ? [] : [new(new("source"), new(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))]);
    private static RoutingProfileCatalog Catalog(bool sharedPorts = false)
    {
        var channel = new ChannelProfile(new("channel1"), "CH1", [new(new("i_plus"), 1, 1), new(new("i_minus"), 1, 2), new(new("v_plus"), 1, 1), new(new("v_minus"), 1, 2)], alias: "contacts");
        var sample = new SampleProfile(new("sample1"), "Sample", SampleTopology.TwoWire, [new(new("A"), channel.Id, new("i_plus")), new(new("B"), channel.Id, new("i_minus"))]);
        var wiring = new MatrixWiringLayout(new("circuit"), "Independent wiring", new(1, 1), new(1, 2), new(1, sharedPorts ? 1 : 3), new(1, sharedPorts ? 2 : 4));
        return new(channel.Id, sample.Id, [channel], [sample], [wiring], [KeyValuePair.Create("curr_dc", wiring.Id)]);
    }
}
