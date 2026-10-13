using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TcpKeithley2450Tests
{
    [Theory]
    [InlineData("overrange")]
    [InlineData("malformed")]
    [InlineData("protection")]
    [InlineData("scpi")]
    public async Task ContinuityActualTcpOverrangeContinuesButMalformedReadStillFaults(string scenario)
    {
        await using var peer = new Keithley2450ScpiPeer(); var calls = 0;
        peer.Override = (command, _) =>
        {
            if (command != Pair || ++calls != 1) return null;
            if (scenario == "scpi") peer.Errors.Enqueue("-222,\"Rejected\"");
            return Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(".1," + (scenario == "malformed" ? "NaN" : "9.9e37") + (scenario == "protection" ? ",140,0" : ",136,0")));
        };
        var source = new TcpKeithley2450SourceMeter(new("tcp"), "TCP source", peer.Server.Configuration()); var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var matrix = new MockMatrixSwitch(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 8, 10)], [source.Id]));
        var registry = new DeviceRegistry([new InstrumentManager(source, hub), new MatrixSwitchSession(matrix, hub)], leases); var runs = new MeasurementRunService(registry, hub);
        var preparation = new SourcePreparation(source.Id, SourceFunction.Voltage, new(.1, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true));
        var route = new MatrixRouteExpectation(matrix.Definition.Device.Id, [new(1, 1, 1), new(1, 2, 2)]);
        ReadElectrodeContinuityStep Probe(string id) => new(new(id), preparation, new("channel1"), ElectrodePair.Source, route, TimeSpan.Zero, new(1e6, PhysicalUnit.Ohm), new(id + ".status"), new(id + ".reason"), new(id + ".v"), new(id + ".i"), new(id + ".r"));
        var plan = new ExperimentPlan("TCP diagnostics", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), route.Device, route.Closed, TimeSpan.Zero), Probe("first"), Probe("second")]), [preparation]);
        if (scenario != "overrange") { if (scenario == "protection") await Assert.ThrowsAsync<InvalidOperationException>(() => runs.StartPlanAsync(plan, Token)); else await Assert.ThrowsAsync<IOException>(() => runs.StartPlanAsync(plan, Token)); Assert.Empty(runs.LatestDataSet!.Rows); }
        else { await runs.StartPlanAsync(plan, Token); Assert.Equal(2, runs.LatestDataSet!.Rows.Count); Assert.Equal(2, runs.LatestDataSet.Rows[0].Values[new("first.reason")].Value); Assert.Equal(0, runs.LatestDataSet.Rows[1].Values[new("second.status")].Value); }
        Assert.False(peer.Output); Assert.False(runs.HasActiveRun); Assert.Empty((await matrix.GetSnapshotAsync(Token)).Closed); await source.DisconnectAsync(Token);
    }
}
