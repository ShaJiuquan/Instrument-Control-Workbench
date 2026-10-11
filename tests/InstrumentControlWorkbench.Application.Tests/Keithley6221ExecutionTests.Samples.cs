using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Theory]
    [InlineData(SampleTopology.TwoWire, 1)]
    [InlineData(SampleTopology.FourWire, 1)]
    [InlineData(SampleTopology.HallBar, 3)]
    [InlineData(SampleTopology.VanDerPauw, 9)]
    public async Task NativeIndependentSampleUsesActualTopologyAndSeparateTimedAcquisitionEvidence(SampleTopology topology, int count)
    {
        await using var b = new Bench(columns: 6); var codec = new JsonExperimentPlanDocument();
        var plan = codec.Parse(codec.Serialize(IndependentSamplePlan(b, topology)));
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(count, rows.Count);
        Assert.All(rows.Where(r => r.Analysis is null), row =>
        {
            var e = Assert.Single(row.IndependentResistance); Assert.Equal(b.Meter.Descriptor.Id, row.Routing!.VoltageMeter);
            Assert.Equal(topology, row.Routing.Topology); Assert.Equal(2, row.Quality.Count); Assert.Null(row.Quality[0].Status);
            Assert.Equal(CurrentReference.ProgrammedSetpoint, e.CurrentReference); Assert.Equal(100, e.Resistance.Value, 8);
            Assert.Equal(CurrentSourceModel.Keithley6221, e.SourceConfiguration.Model); Assert.Equal(1, row.Quality[1].MeterConfiguration!.Channel);
            Assert.True(e.SourceConfirmedBefore <= e.SourceCapturedAt && e.SourceCapturedAt <= e.VoltageCapturedAt && e.VoltageCapturedAt <= e.SourceConfirmedAfter);
            Assert.Equal(new MatrixRouteExpectation(row.Routing.Matrix, row.Routing.Closed), e.Route); Assert.Equal(3, row.Environment.Count);
        });
        if (count > 1)
        {
            var summary = rows[^1]; Assert.Equal(count - 1, summary.IndependentResistance.Count); Assert.Equal(2 * (count - 1), summary.Quality.Count);
            Assert.Equal(rows.Take(count - 1).SelectMany(r => r.IndependentResistance), summary.IndependentResistance);
            Assert.Equal(SampleAnalysisIssues.None, summary.Analysis!.Issues); Assert.Equal(rows[0].Environment, summary.Environment);
            var expected = topology == SampleTopology.HallBar ? 50 : 100 * Math.PI / Math.Log(2);
            Assert.Equal(expected, summary.Values[new("sample.SheetResistance")].Value, 8);
        }
        Assert.Equal(count == 1 ? 1 : count - 1, b.Source.Enables); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("noRoute")]
    [InlineData("wrongRoute")]
    [InlineData("noMeterWiring")]
    [InlineData("noPreparation")]
    public async Task NativeSampleMisbindingsRejectBeforeInstrumentConnection(string fault)
    {
        await using var b = new Bench(columns: 6); var original = IndependentSamplePlan(b, SampleTopology.FourWire);
        var wrapper = Assert.IsType<ReadSampleResistanceStep>(original.Root); var old = Assert.IsType<ReadCurrentResistanceStep>(wrapper.Acquisition);
        var route = fault == "noRoute" ? null : fault == "wrongRoute" ? new MatrixRouteExpectation(old.RequiredRoute!.Device, old.RequiredRoute.Closed.Take(2)) : old.RequiredRoute;
        var read = new ReadCurrentResistanceStep(old.Id, old.Source, old.Meter, old.CurrentReference, old.VoltageColumn, old.CurrentColumn, old.ResistanceColumn, old.MinimumCurrent, old.ExpectedProgrammedCurrent, route, old.EnvironmentReads);
        var routing = original.Routing!;
        if (fault == "noMeterWiring") routing = new(routing.Matrix, routing.Channels, routing.Wirings.Select(w => new InstrumentWiringProfile(w.Id, w.SourceMeter, w.SourcePlus, w.SourceMinus, w.SensePlus, w.SenseMinus)), routing.Samples);
        var plan = new ExperimentPlan(original.Name, new ReadSampleResistanceStep(read, wrapper.Binding, wrapper.Settle), routing: routing,
            currentSourcePreparations: fault == "noPreparation" ? [] : original.CurrentSourcePreparations);
        Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(plan)); Assert.Equal(0, b.Source.Connections); Assert.False((await b.Meter.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task SampleWiredInputIsCheckedBeforeTriggeringExternalRead()
    {
        await using var b = new Bench(columns: 6); await b.Registry.ConnectAsync(b.Meter.Descriptor.Id, Token);
        var settings = (await b.Meter.GetSnapshotAsync(Token)).Settings!;
        await b.Meter.ConfigureAsync(settings.WithParameter(Keithley2182Parameters.Channel.Id, new(2, PhysicalUnit.Dimensionless)), Token);
        var triggered = false; b.Meter.OnRead = _ => { triggered = true; return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(IndependentSamplePlan(b, SampleTopology.FourWire), Token));
        Assert.False(triggered); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialVdpAbortOrListenerFaultRetainsRawEvidenceWithoutAnalysis(bool fault)
    {
        var listener = new SampleRowListener(); await using var b = new Bench(listener, 6);
        listener.Row = _ => { if (listener.Count == 3) { if (fault) throw new IOException("listener failed"); b.Runs.Abort(); } return Task.CompletedTask; };
        var run = b.Runs.StartPlanAsync(IndependentSamplePlan(b, SampleTopology.VanDerPauw), Token);
        if (fault) await Assert.ThrowsAsync<IOException>(() => run); else await run;
        Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, row => { Assert.Null(row.Analysis); Assert.Single(row.IndependentResistance); });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Source.LastStopTokenLive);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedIndependentSampleRejectsSourceDriftOrResumesItsHeldTopology(bool drift)
    {
        var listener = new SampleRowListener(); await using var b = new Bench(listener, 6); using var events = b.Hub.Subscribe();
        listener.Row = _ => { if (listener.Count == 1) b.Runs.Pause(); return Task.CompletedTask; };
        var run = b.Runs.StartPlanAsync(IndependentSamplePlan(b, SampleTopology.HallBar), Token); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); var enables = b.Source.Enables;
        if (drift) await b.Source.ConfigureAsync(new(new(2e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt), new(20e-6, PhysicalUnit.Ampere)), Token);
        b.Runs.Resume();
        if (drift) { await Assert.ThrowsAsync<InvalidOperationException>(() => run); Assert.Equal(enables, b.Source.Enables); Assert.Single(b.Runs.LatestDataSet!.Rows); }
        else { await run; Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.NotNull(b.Runs.LatestDataSet.Rows[^1].Analysis); }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task FailedSampleStopRetainsAllClaimsUntilFreshRecovery()
    {
        await using var b = new Bench(columns: 6); var reads = 0;
        b.Meter.OnRead = _ => { if (++reads == 4) b.Source.RefuseStop = true; return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(IndependentSamplePlan(b, SampleTopology.VanDerPauw), Token));
        Assert.Equal(4, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Null(row.Analysis)); Assert.True(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        b.Source.RefuseStop = false; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task SourceConfigurationChangeBetweenInputsCannotProduceACombinedAnalysis()
    {
        var listener = new SampleRowListener(); await using var b = new Bench(listener, 6);
        listener.Row = async _ => { if (listener.Count == 1) await b.Source.ConfigureAsync(new(new(1e-5, PhysicalUnit.Ampere), new(50, PhysicalUnit.Volt), new(20e-6, PhysicalUnit.Ampere)), Token); };
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(IndependentSamplePlan(b, SampleTopology.HallBar), Token));
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, r => Assert.Null(r.Analysis));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task MeterConfigurationChangeBetweenInputsCannotProduceACombinedAnalysis()
    {
        var listener = new SampleRowListener(); await using var b = new Bench(listener, 6);
        listener.Row = async _ =>
        {
            if (listener.Count == 1)
            {
                var settings = (await b.Meter.GetSnapshotAsync(Token)).Settings!;
                await b.Meter.ConfigureAsync(settings.WithParameter(Keithley2182Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless)), Token);
            }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(IndependentSamplePlan(b, SampleTopology.HallBar), Token));
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Null(row.Analysis));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    private static RoutingProfileCatalog IndependentSampleCatalog(SampleTopology topology, bool geometry = true)
    {
        var labels = SampleProfile.RequiredLabels(topology);
        var channel = new ChannelProfile(new("channel1"), "Sample contacts", Enumerable.Range(1, labels.Count).Select(i => new ChannelConnection(new("contact" + i), 1, i)));
        var sample = new SampleProfile(new("sample1"), "Independent " + topology, topology, labels.Select((label, i) => new SampleElectrode(new(label), channel.Id, new("contact" + (i + 1)))),
            geometry ? new(new(0.002, PhysicalUnit.Meter), new(0.004, PhysicalUnit.Meter), new(1e-7, PhysicalUnit.Meter)) : null);
        var wiring = new MatrixWiringLayout(new("circuit"), "Independent physical source/meter", new(1, 1), new(1, 2), new(1, 3), new(1, 4));
        return new(channel.Id, sample.Id, [channel], [sample], [wiring], [KeyValuePair.Create("curr_dc", wiring.Id)]);
    }
    private static ExperimentPlan IndependentSamplePlan(Bench b, SampleTopology topology, bool geometry = true, string prefix = "sample")
    {
        var routing = BindCurrentChannels(b, IndependentSampleCatalog(topology, geometry)).Samples!.Routing;
        var sample = routing.Samples[0];
        var selections = topology switch { SampleTopology.TwoWire => new[] { SampleRouteSelection.TwoWireResistance }, SampleTopology.FourWire => new[] { SampleRouteSelection.FourWireResistance }, _ => SampleResistanceAnalysis.Selections(topology).ToArray() };
        var reads = selections.Select((selection, index) =>
        {
            var binding = new SampleRoutingBinding(sample.Id, new("circuit"), selection); var route = routing.Resolve(binding);
            var acquisition = new ReadCurrentResistanceStep(new(prefix + ".read." + index), b.Source.Descriptor.Id, b.Meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint,
                new(prefix + ".V"), new(prefix + ".I"), new(prefix + ".R"), new(2.220446049250313e-16, PhysicalUnit.Ampere), new ConstantValue(b.Preparation.Settings.Current),
                new(route.Matrix, route.Closed), Enum.GetValues<EnvironmentRole>().Select(role => new EnvironmentRead(role, new(prefix + "." + role))));
            return new ReadSampleResistanceStep(acquisition, binding, TimeSpan.Zero);
        }).ToArray();
        ExperimentStep body = reads.Length == 1 ? reads[0] : new SampleAnalysisStep(new(prefix + ".analysis"), reads, SampleResistanceAnalysis.Kinds(topology).Select(kind => new SampleDerivedColumn(kind, new(prefix + "." + kind))));
        return new("Independent " + topology, body, routing: routing, currentSourcePreparations: [b.Preparation]);
    }
    private sealed class SampleRowListener : IRunResultListener
    {
        public int Count { get; private set; }
        public Func<MeasurementDataRow, Task>? Row { get; set; }
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token) { Count = 0; return Task.CompletedTask; }
        public async Task OnRowAsync(RunId run, MeasurementDataRow row, CancellationToken token) { Count++; if (Row is { } action) await action(row); }
        public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => Task.CompletedTask;
    }
}
