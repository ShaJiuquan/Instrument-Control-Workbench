using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Sr830AcExecutionTests
{
    [Fact]
    public async Task PreCancelledAdmissionRollsBackOwnClaimsWithoutIsolationOrOtherIo()
    {
        var b = new Bench(baseline: true); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await b.Runs.StartPlanAsync(b.Plan(b.Read()), cancelled.Token);
        Assert.Empty(b.Events); Assert.Equal(0, b.Meter.Connections); Assert.Equal(0, b.Matrix.Connections);
        Assert.Equal(0, b.Source.Connections); Assert.Equal(0, b.Matrix.Opens); Assert.Equal(0, b.Meter.Aborts); b.AssertReleased();
    }
    [Fact]
    public async Task LateSampleOpenCannotBecomeTerminalProofOrStrandFinalization()
    {
        var b = new Bench(safetyTimeout: TimeSpan.FromSeconds(1)); using var events = b.Hub.Subscribe();
        b.Matrix.OpenGate = new(TaskCreationOptions.RunContinuationsAsynchronously); b.Matrix.IgnoreSnapshotCancellation = true;
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read()), Token);
        await b.Matrix.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(events, WorkbenchNotificationKind.RunFinalizing);
        b.Matrix.OpenGate.SetResult(); await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        await b.Runs.WaitForFinalizationAsync(Token).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        if (b.Runs.HasActiveRun) await b.Runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.True(b.Matrix.Opens >= 2); b.AssertReleased();
    }
    [Fact]
    public async Task DcStopFailureDoesNotSuppressEmergencyAcIsolationButRetainsAllClaims()
    {
        var b = new Bench(baseline: true); b.Source.FailStop = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token));
        Assert.True(b.Matrix.Opens > 0); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.Equal(0, b.Matrix.Closes); Assert.Empty(b.Runs.LatestDataSet!.Rows); b.AssertReserved();
        b.Source.FailStop = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); b.AssertReleased();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonActiveRouteReadAndVerificationCannotUseDedicatedAcMatrix(bool verification)
    {
        var b = new Bench(); var id = b.Matrix.Definition.Device.Id;
        ExperimentStep step = verification ? new VerifyMatrixRouteStep(new("verify"), id, [PathA, PathB])
            : new ReadStep(new("read"), [new(new("amplitude"), new(b.Meter.Descriptor.Id, Sr830ReferenceParameters.SineAmplitude.Id))], requiredRoute: new(id, [PathA, PathB]));
        Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(b.Plan(step))); Assert.Empty(b.Events);
    }
    [Fact]
    public void RegistryRejectsMultipleIsolationMatricesForTheSameGeneratorWithoutIo()
    {
        var b = new Bench(); var definition = b.Matrix.Definition;
        var second = new MatrixSwitchDefinition(new(new("second-isolator"), "MATRIX", "Second", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), definition.Cards, definition.GuardedSources, definition.AcIsolation);
        Assert.Throws<ArgumentException>(() => new DeviceRegistry([new Sr830MeterSession(b.Meter, b.Hub), new MatrixSwitchSession(b.Matrix, b.Hub), new MatrixSwitchSession(new MockMatrixSwitch(second), b.Hub)], b.Leases));
        Assert.Empty(b.Events);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WideAndCalculatedRowsRetainEachAtomicMembersIsolationProofThroughFreshArchive(bool calculate)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ac-compose-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(listener: archive);
            var first = b.Read("first"); var second = b.Read("second");
            ExperimentStep step = new MeasurementGroupStep(new("point"), [new("first", first), new("second", second)]);
            if (calculate) step = new CalculatedMeasurementStep(new("result"), step, [new(new("x_copy"), new VariableValue(first.Frame.Channels[0].Column))]);
            await b.Runs.StartPlanAsync(b.Plan(step), Token); var data = b.Runs.LatestDataSet!; Assert.Equal(calculate ? 4 : 3, data.Rows.Count); var row = data.Rows[^1];
            Assert.Equal(2, row.Sr830Ac.Count); Assert.Equal(8, row.Quality.Count); Assert.Equal(2, b.Meter.Reads); Assert.Equal(2, b.Matrix.Closes);
            Assert.Equal(first.Id, row.Sr830Ac[0].Step); Assert.Equal(second.Id, row.Sr830Ac[1].Step); Assert.NotNull(row.MeasurementGroup);
            Assert.Equal(calculate, row.Calculation is not null); b.AssertReleased();
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var restored = (await fresh.LoadAsync(data.Id, Token)).DataSet.Rows[^1];
            Assert.True(row.Sr830Ac.Zip(restored.Sr830Ac).All(pair => pair.First.ContentEquals(pair.Second)));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); Assert.Contains("sr830_ac_json", await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-generator")]
    [InlineData("wrong-reference")]
    [InlineData("still-connected")]
    [InlineData("unknown-field")]
    public async Task FreshArchiveRejectsForgedOrOmittedActiveIsolationEvidence(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ac-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(listener: archive);
            await b.Runs.StartPlanAsync(b.Plan(b.Read()), Token); var data = b.Runs.LatestDataSet!;
            var path = Assert.Single(Directory.GetFiles(folder, "rows.ndjson", SearchOption.AllDirectories));
            var row = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (damage == "missing") row.AsObject().Remove("sr830Ac");
            else
            {
                var proof = row["sr830Ac"]![0]!;
                switch (damage)
                {
                    case "wrong-generator": proof["device"] = "other"; break;
                    case "wrong-reference": proof["reference"]!["sineAmplitude"]!["value"] = .02; break;
                    case "still-connected": proof["afterAcquisitionOpen"]!["closed"]!.AsArray().Add(proof["connections"]![0]!.DeepClone()); break;
                    default: proof["safe"] = true; break;
                }
            }
            await File.WriteAllTextAsync(path, row.ToJsonString() + "\n");
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs"));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(data.Id, Token));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "invalid.csv"), Token));
            Assert.False(File.Exists(Path.Combine(folder, "invalid.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
