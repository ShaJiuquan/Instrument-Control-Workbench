using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TcpKeithley2450Tests
{
    [Theory]
    [InlineData(false, SourceFunction.Voltage)]
    [InlineData(false, SourceFunction.Current)]
    [InlineData(true, SourceFunction.Voltage)]
    [InlineData(true, SourceFunction.Current)]
    public async Task OneReplyQualitySurvivesSingleAndPairedResistanceArchives(bool single, SourceFunction function)
    {
        var folder = Path.Combine(Path.GetTempPath(), "reading-quality-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer { ExtraSourceStatus = 32, MeasurementStatus = 1 };
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(peer, listener: archive);
            var preparation = b.Plan().Preparations[0];
            preparation = function == SourceFunction.Voltage ? preparation : new(b.Source.Id, function, new(21e-6, PhysicalUnit.Ampere), new(21, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.FourWire, 1, true));
            ExperimentStep read = single ? new ReadStep(new("single"), [new(new("measured"), new(b.Source.Id, function == SourceFunction.Voltage ? Keithley2450Parameters.MeasuredCurrent.Id : Keithley2450Parameters.MeasuredVoltage.Id))]) :
                new ReadResistanceStep(new("resistance"), b.Source.Id, function, new("voltage"), new("current"), new("R"), new(0, PhysicalUnit.Ampere));
            await b.Runs.StartPlanAsync(new("quality", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), read]), [preparation]), Token);
            var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
            Assert.Equal(single ? 1 : 3, row.Quality.Count); Assert.Equal(row.Quality.Count, row.ObservedQualityColumns);
            Assert.Equal(ReadingQualityIssues.SourceLimited | ReadingQualityIssues.Questionable, row.QualityIssues); Assert.Equal(ReadingQualityState.ObservedWithIssues, row.QualityState);
            Assert.All(row.Quality, q => { Assert.Equal(read.Id, q.Step); Assert.Equal(b.Source.Id, q.Device); Assert.Equal(row.Quality[0].CapturedAt, q.CapturedAt); Assert.NotNull(q.Status); });
            var measurements = peer.Server.Commands.Where(c => c.StartsWith(":MEAS?", StringComparison.Ordinal)).ToArray(); Assert.Single(measurements);
            Assert.EndsWith("SOURSTAT, STAT", measurements[0]); Assert.DoesNotContain(peer.Server.Commands, c => c.StartsWith(":FETC", StringComparison.Ordinal));
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token);
            Assert.Equal(row.Quality.ToArray(), Assert.Single(saved.DataSet.Rows).Quality.ToArray());
            var csv = Path.Combine(folder, "quality.csv"); await fresh.ExportCsvAsync(saved.DataSet.Id, csv, Token); var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("quality_json", text); Assert.Contains("sourceStatus", text); Assert.Contains("measurementStatus", text);
            Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("-1", false)]
    [InlineData("0.5", false)]
    [InlineData("65536", false)]
    [InlineData("NaN", false)]
    [InlineData("bad", false)]
    [InlineData("-1", true)]
    [InlineData("0.5", true)]
    [InlineData("65536", true)]
    [InlineData("NaN", true)]
    [InlineData("bad", true)]
    public async Task MalformedEitherStatusWordFaultsWithoutCommittingAFrame(string word, bool measurement)
    {
        await using var peer = new Keithley2450ScpiPeer();
        peer.Override = (command, _) => command == Pair ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply($"0.0197,0.000021,{(measurement ? "200" : word)},{(measurement ? word : "0")}")) : null;
        var b = new Bench(peer); await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(72, 0)]
    [InlineData(192, 0)]
    [InlineData(136, 0)]
    [InlineData(200, 8)]
    [InlineData(204, 0)]
    [InlineData(216, 0)]
    public async Task InFrameSafetyOrCaptureMismatchStopsEvenWhenLaterRegistersLookNormal(ushort source, ushort measurement)
    {
        await using var peer = new Keithley2450ScpiPeer();
        peer.Override = (command, _) => command == Pair ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply($"0.0197,0.000021,{source},{measurement}")) : null;
        var b = new Bench(peer); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task InvalidStatusAndRefusedStopRetainOwnershipUntilReconnectedSafeProof()
    {
        await using var peer = new Keithley2450ScpiPeer { RefuseOff = true, ExtraSourceStatus = 4 }; var b = new Bench(peer);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(), Token)); Assert.True(b.Runs.HasActiveRun); Assert.True(peer.Output);
        Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SourceManager.ReadParameterAsync(Keithley2450Parameters.Nplc.Id, Token));
        peer.RefuseOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun);
        await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData("device")]
    [InlineData("step")]
    [InlineData("column")]
    [InlineData("timestamp")]
    [InlineData("half_status")]
    [InlineData("missing_column")]
    public async Task UnfinishedArchiveRejectsQualityProvenanceDamageAndPreservesExport(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "quality-tamper-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); await b.Runs.StartPlanAsync(b.Plan(), Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                await archive.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [], Blackboard = null }, Token);
                await archive.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token);
            }
            var path = Path.Combine(folder, "runs", snapshot.Id.Value.ToString("N"), "rows.ndjson"); var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var quality = node["quality"]!.AsArray();
            if (damage == "missing_column") quality.RemoveAt(1);
            else if (damage == "half_status") quality[0]!["sourceStatus"] = null;
            else if (damage == "timestamp") quality[0]!["capturedAt"] = snapshot.Rows[0].Timestamp.AddHours(1).ToString("O");
            else quality[0]![damage] = "wrong";
            await File.WriteAllTextAsync(path, node.ToJsonString() + "\n"); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs"));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(snapshot.Id, Token));
            var csv = Path.Combine(folder, "old.csv"); await File.WriteAllTextAsync(csv, "keep"); await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("keep", await File.ReadAllTextAsync(csv));
            await b.Source.DisconnectAsync(Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("0.000021,-1,0")]
    [InlineData("0.000021,200,0.5")]
    [InlineData("0.000021,200,65536")]
    [InlineData("0.000021,200")]
    [InlineData("0.000021,200,0,0")]
    public async Task SingleChannelAlsoRejectsMalformedAndIncompleteStatusReplies(string reply)
    {
        await using var peer = new Keithley2450ScpiPeer();
        peer.Override = (command, _) => command == ":MEAS? \"defbuffer1\", READ, SOURSTAT, STAT" ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(reply)) : null;
        var b = new Bench(peer); var basePlan = b.Plan();
        var single = new ReadStep(new("single"), [new(new("measured"), new(b.Source.Id, Keithley2450Parameters.MeasuredCurrent.Id))]);
        var plan = new ExperimentPlan("single", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), single]), basePlan.Preparations);
        await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(1, 0)]
    [InlineData(256, 0)]
    [InlineData(0, 512)]
    public async Task UnknownStatusBitsArePreservedAndNeverBecomeGoodQuality(ushort source, ushort measurement)
    {
        await using var peer = new Keithley2450ScpiPeer { ExtraSourceStatus = source, MeasurementStatus = measurement }; var b = new Bench(peer);
        await b.Runs.StartPlanAsync(b.Plan(), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(ReadingQualityIssues.UnknownStatusBits, row.QualityIssues); Assert.Equal((ushort)(200 | source), row.Quality[0].Status!.Source); Assert.Equal(measurement, row.Quality[0].Status!.Measurement);
        Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task IntegralScientificNotationStatusIsAcceptedWithoutSeparateStatusQuery()
    {
        await using var peer = new Keithley2450ScpiPeer();
        peer.Override = (command, _) => command == Pair ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("0.0197,0.000021,2.00E2,0.0E0")) : null;
        var b = new Bench(peer); await b.Runs.StartPlanAsync(b.Plan(), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(new Keithley2450ReadingStatus(200, 0), row.Quality[0].Status); Assert.Equal(ReadingQualityState.ObservedNormal, row.QualityState); await b.Source.DisconnectAsync(Token);
    }

}
