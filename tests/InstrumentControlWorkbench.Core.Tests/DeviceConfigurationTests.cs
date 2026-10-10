using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class DeviceConfigurationTests
{
    private static DeviceConfiguration Source(string id = "source", DeviceConnection? connection = null, bool enabled = true)
        => new(new(id), "K2450", "SMU", ConfiguredDeviceCategory.SourceMeter, connection ?? new SimulationDeviceConnection(), enabled);
    [Theory]
    [InlineData("port")]
    [InlineData("host")]
    [InlineData("newline")]
    [InlineData("timeout")]
    [InlineData("huge")]
    [InlineData("visa")]
    [InlineData("serial")]
    public void InvalidConnectionsRejectBeforeAnythingCanUseThem(string damage)
    {
        Action create = damage switch
        {
            "port" => () => new TcpDeviceConnection("localhost", 0, "\n", TimeSpan.FromSeconds(1)),
            "host" => () => new TcpDeviceConnection(" ", 5025, "\n", TimeSpan.FromSeconds(1)),
            "newline" => () => new TcpDeviceConnection("localhost\n", 5025, "\n", TimeSpan.FromSeconds(1)),
            "timeout" => () => new TcpDeviceConnection("localhost", 5025, "\n", TimeSpan.Zero),
            "huge" => () => new SerialDeviceConnection("COM3", TimeSpan.MaxValue),
            "visa" => () => new VisaDeviceConnection(" "),
            _ => () => new SerialDeviceConnection("port\n", TimeSpan.FromSeconds(1)),
        };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(create));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("primary")]
    [InlineData("disabled-primary")]
    [InlineData("guard")]
    [InlineData("disabled-guard")]
    [InlineData("alias")]
    [InlineData("role")]
    [InlineData("endpoint")]
    public void InvalidCrossReferencesAndDuplicatePhysicalOwnersReject(string damage)
    {
        var devices = new List<DeviceConfiguration> { Source() }; var aliases = new List<KeyValuePair<string, InstrumentId>>(); var primary = new InstrumentId("source");
        switch (damage)
        {
            case "duplicate": devices.Add(Source("SOURCE")); break;
            case "primary": primary = new("missing"); break;
            case "disabled-primary": devices[0] = Source(enabled: false); break;
            case "disabled-guard": devices.Add(Source("disabled", enabled: false)); goto case "guard";
            case "guard": devices.Add(new(new("matrix"), "K3706", "Matrix", ConfiguredDeviceCategory.Matrix, new SimulationDeviceConnection(), matrix: new([new(1, 8, 10)], [new(damage == "guard" ? "missing" : "disabled")]))); break;
            case "alias": aliases.Add(KeyValuePair.Create("smu", new InstrumentId("missing"))); break;
            case "role": aliases.Add(KeyValuePair.Create("field", primary)); break;
            case "endpoint":
                devices[0] = Source(connection: new TcpDeviceConnection("LOCALHOST", 5025, "\n", TimeSpan.FromSeconds(1)));
                devices.Add(Source("other", new TcpDeviceConnection("localhost", 5025, "\r\n", TimeSpan.FromSeconds(2)))); break;
        }
        Assert.Throws<ArgumentException>(() => new DeviceConfigurationCatalog(primary, devices, aliases));
    }
    [Fact]
    public void CatalogCopiesCollectionsAndDisabledTemplatesDoNotOwnHardwareEndpoints()
    {
        var shared = new VisaDeviceConnection("GPIB0::7::INSTR");
        var devices = new List<DeviceConfiguration> { Source(connection: shared), Source("template", shared, false) };
        var aliases = new Dictionary<string, InstrumentId> { ["smu"] = new("source") };
        var catalog = new DeviceConfigurationCatalog(new("source"), devices, aliases); devices.Clear(); aliases.Clear();
        Assert.Equal(2, catalog.Devices.Count); Assert.Equal(new InstrumentId("source"), catalog.Aliases["SMU"]); Assert.Equal(DriverKind.Hardware, catalog.Devices[0].DriverKind);
        Assert.Throws<NotSupportedException>(() => ((IList<DeviceConfiguration>)catalog.Devices).Clear());
    }
}
