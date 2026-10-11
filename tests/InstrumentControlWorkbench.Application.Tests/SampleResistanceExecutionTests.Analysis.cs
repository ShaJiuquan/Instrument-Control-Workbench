using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class SampleResistanceExecutionTests
{
    private static SampleGeometry Geometry => new(new(0.002, PhysicalUnit.Meter), new(0.004, PhysicalUnit.Meter), new(1e-7, PhysicalUnit.Meter));

    [Theory]
    [InlineData(SampleTopology.HallBar, 2, SourceFunction.Voltage)]
    [InlineData(SampleTopology.HallBar, 2, SourceFunction.Current)]
    [InlineData(SampleTopology.VanDerPauw, 8, SourceFunction.Voltage)]
    public async Task AnalyzedRecipesKeepEveryRawRowThenPublishAnOffSourceSummary(SampleTopology topology, int rawCount, SourceFunction function)
    {
        var b = new Bench();
        b.Listener.Row = async row => { if (row.Analysis is not null) Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); };
        await b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(topology, Geometry), function), Token);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(rawCount + 1, rows.Count); Assert.Equal(rawCount + 1, b.Listener.Rows);
        Assert.All(rows.Take(rawCount), r => { Assert.NotNull(r.Routing); Assert.Null(r.Analysis); });
        var summary = rows[^1]; Assert.Null(summary.Routing);
        Assert.Equal(1, summary.Analysis!.FirstInputRow); Assert.Equal(rawCount, summary.Analysis.InputCount); Assert.Equal(rawCount * 3, summary.Quality.Count); Assert.Equal(0, summary.ObservedQualityColumns); Assert.Equal(SampleAnalysisIssues.None, summary.Analysis.Issues);
        var expected = topology == SampleTopology.HallBar ? rows[0].Values[new("resistance")].Value * 0.5 : Math.PI * rows[0].Values[new("resistance")].Value / Math.Log(2);
        Assert.True(Math.Abs(summary.Values[new("r_sheet")].Value / expected - 1) < 1e-12);
        Assert.Equal(PhysicalUnit.OhmPerSquare, summary.Values[new("r_sheet")].Unit); Assert.Equal(PhysicalUnit.OhmMeter, summary.Values[new("rho_xx")].Unit);
        Assert.False(b.Runs.HasActiveRun); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task NestedScansKeepSeparateInputRangesAndSummaryDependencies()
    {
        var b = new Bench(); var plan = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.VanDerPauw, Geometry)));
        var scan = new ScanStep(new("bias_scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("bias"),
            new([new(0.01, PhysicalUnit.Volt), new(0.02, PhysicalUnit.Volt)]), TimeSpan.Zero, plan.Root);
        await b.Runs.StartPlanAsync(new("bias x analyzed vdP", scan, plan.Preparations, routing: plan.Routing), Token);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(18, rows.Count);
        Assert.Equal(new[] { 1, 10 }, rows.Where(r => r.Analysis is not null).Select(r => r.Analysis!.FirstInputRow));
        foreach (var summary in rows.Where(r => r.Analysis is not null))
            Assert.All(rows.Skip(summary.Analysis!.FirstInputRow - 1).Take(8), raw => Assert.Equal(summary.Values[new("bias")], raw.Values[new("bias")]));
        Assert.Contains(new ParameterId("bias"), b.Runs.LatestDataSet.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("r_sheet")).DependsOn);
    }
    [Fact]
    public async Task CancellationMidGroupKeepsPartialRawDataWithoutSummary()
    {
        var b = new Bench(); using var cancellation = new CancellationTokenSource();
        b.Listener.Row = _ => { if (b.Listener.Rows == 3) cancellation.Cancel(); return Task.CompletedTask; };
        await b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.VanDerPauw, Geometry)), cancellation.Token);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count);
        Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Null(row.Analysis));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task PauseDuringGroupHoldsOffSourceAndAbortDoesNotPublishSummary()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        b.Listener.Row = _ => { if (b.Listener.Rows == 4) b.Runs.Pause(); return Task.CompletedTask; };
        var run = b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.VanDerPauw, Geometry)), Token);
        await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Runs.HasActiveRun);
        b.Runs.Abort(); await run; Assert.Equal(4, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, r => Assert.Null(r.Analysis));
    }
    [Fact]
    public async Task MissingGeometryIsExplicitAndOnlyAvailableDerivedValuesArePublished()
    {
        var b = new Bench(); await b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.HallBar)), Token);
        var summary = b.Runs.LatestDataSet!.Rows[^1]; Assert.Equal(SampleAnalysisIssues.MissingGeometry, summary.Analysis!.Issues);
        Assert.Contains(new ParameterId("r_xx"), summary.Values.Keys); Assert.Contains(new ParameterId("r_xy"), summary.Values.Keys);
        Assert.DoesNotContain(new ParameterId("r_sheet"), summary.Values.Keys); Assert.DoesNotContain(new ParameterId("rho_xx"), summary.Values.Keys);
    }
    [Theory]
    [InlineData("plain")]
    [InlineData("timeout")]
    [InlineData("subtree")]
    public async Task GeometryAndGroupPlanArchiveRoundTripWithStreamingCsvEvidence(string scope)
    {
        using var folder = new TemporaryFolder(); RunId id;
        var codec = new JsonExperimentPlanDocument(); var path = Path.Combine(folder.Path, "analysis.plan.json");
        var original = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.VanDerPauw, Geometry)));
        if (scope != "plain") original = new(original.Name, scope == "timeout" ? new TimeoutStep(new("bounded.analysis"), TimeSpan.FromSeconds(20), original.Root) : new SubtreeStep(new("scoped.analysis"), [original.Root]), original.Preparations, original.Metadata, original.Routing);
        await codec.SaveAsync(path, original, Token); var plan = await codec.LoadAsync(path, Token);
        Assert.Equal(Geometry, plan.Routing!.Samples[0].Geometry);
        Assert.IsType<SampleAnalysisStep>(plan.Root switch { TimeoutStep bounded => bounded.Body, SubtreeStep local => local.Children.Single(), _ => plan.Root });
        await using (var archive = new FileRunArchive(Path.Combine(folder.Path, "runs")))
        {
            var b = new Bench(archive); await b.Runs.StartPlanAsync(plan, Token); id = b.Runs.LatestDataSet!.Id;
        }
        await using var reopened = new FileRunArchive(Path.Combine(folder.Path, "runs")); var loaded = await reopened.LoadAsync(id, Token);
        Assert.True(loaded.IsFinalized); Assert.Equal(9, loaded.DataSet.Rows.Count); Assert.Equal(1, loaded.DataSet.Rows[^1].Analysis!.FirstInputRow);
        Assert.Equal(Geometry, loaded.DataSet.Plan!.Routing!.Samples[0].Geometry);
        if (scope != "plain")
        {
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(loaded.DataSet.Plan, Token);
            Assert.Equal(9, fresh.Runs.LatestDataSet!.Rows.Count);
        }
        var csv = Path.Combine(folder.Path, "analysis.csv"); await reopened.ExportCsvAsync(id, csv, Token);
        var text = await File.ReadAllTextAsync(csv); Assert.Contains("analysis_json", text); Assert.Contains("r_sheet [OhmPerSquare]", text);
        Assert.Contains("rho_xx [OhmMeter]", text); Assert.Contains("\"\"firstInputRow\"\":1", text);
    }
    [Fact]
    public async Task CancellationBeforeSummaryArchiveDeliveryReconcilesTheCompleteCommittedGroup()
    {
        using var folder = new TemporaryFolder(); using var cancellation = new CancellationTokenSource();
        await using var archive = new FileRunArchive(folder.Path); var b = new Bench(archive);
        b.Listener.Row = row => { if (row.Analysis is not null) cancellation.Cancel(); return Task.CompletedTask; };
        await b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.HallBar, Geometry)), cancellation.Token);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        var loaded = await archive.LoadAsync(b.Runs.LatestDataSet!.Id, Token); Assert.True(loaded.IsFinalized); Assert.Equal(3, loaded.DataSet.Rows.Count);
        Assert.NotNull(loaded.DataSet.Rows[^1].Analysis); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData("value")]
    [InlineData("first_row")]
    [InlineData("count")]
    [InlineData("issues")]
    [InlineData("missing_context")]
    [InlineData("raw_pair")]
    [InlineData("algorithm")]
    [InlineData("quality_drop")]
    [InlineData("quality_word")]
    public async Task UnfinishedArchiveRejectsAlteredSummaryEvidenceAndPreservesExportDestination(string damage)
    {
        using var folder = new TemporaryFolder(); var b = new Bench(); await b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.HallBar, Geometry)), Token);
        var snapshot = b.Runs.LatestDataSet!.Capture();
        await using (var archive = new FileRunArchive(folder.Path))
        {
            await archive.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = Array.Empty<MeasurementDataRow>() }, Token);
            foreach (var row in snapshot.Rows) await archive.OnRowAsync(snapshot.Id, row, Token);
        }
        var rowPath = Path.Combine(folder.Path, snapshot.Id.Value.ToString("N"), "rows.ndjson"); var lines = await File.ReadAllLinesAsync(rowPath);
        var index = damage == "raw_pair" ? 0 : 2; var node = JsonNode.Parse(lines[index])!.AsObject();
        switch (damage)
        {
            case "value": node["values"]!["r_sheet"]!["value"] = 1; break;
            case "first_row": node["analysis"]!["firstInputRow"] = 2; break;
            case "count": node["analysis"]!["inputCount"] = 8; break;
            case "issues": node["analysis"]!["issues"] = "MissingGeometry"; break;
            case "missing_context": node.Remove("analysis"); break;
            case "raw_pair": node["values"]!["voltage"]!["value"] = 0.123; break;
            case "algorithm": node["analysis"]!["algorithmVersion"] = 2; break;
            case "quality_drop": node.Remove("quality"); break;
            case "quality_word": node["quality"]![0]!["sourceStatus"] = 232; node["quality"]![0]!["measurementStatus"] = 1; break;
        }
        lines[index] = node.ToJsonString(); await File.WriteAllTextAsync(rowPath, string.Join('\n', lines) + "\n");
        await using var reopened = new FileRunArchive(folder.Path);
        await Assert.ThrowsAnyAsync<Exception>(() => reopened.LoadAsync(snapshot.Id, Token));
        var csv = Path.Combine(folder.Path, "existing.csv"); await File.WriteAllTextAsync(csv, "preserve");
        await Assert.ThrowsAnyAsync<Exception>(() => reopened.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("preserve", await File.ReadAllTextAsync(csv));
    }
    [Fact]
    public async Task IncompleteOrMismatchedGroupIsRejectedBeforeDeviceAdmission()
    {
        var b = new Bench(); var plan = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.VanDerPauw, Geometry)));
        var group = (SampleAnalysisStep)plan.Root; var broken = new SampleAnalysisStep(group.Id, group.Readings.Take(7), group.Columns);
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(new(plan.Name, broken, plan.Preparations, routing: plan.Routing), Token));
        Assert.Equal(0, b.Matrix.Connects); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        var duplicateSelection = new SampleAnalysisStep(group.Id, group.Readings.Select((r, i) => i == 7 ? new(r.Acquisition, group.Readings[0].Binding, r.Settle) : r), group.Columns);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new(plan.Name, duplicateSelection, plan.Preparations, routing: plan.Routing)));
    }
    [Fact]
    public void CodecRejectsWrongGeometryAndNonMeasurementAnalysisChildren()
    {
        var codec = new JsonExperimentPlanDocument(); var text = codec.Serialize(SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.HallBar, Geometry))));
        var node = JsonNode.Parse(text)!; node["routing"]!["samples"]![0]!["geometry"]!["thickness"]!["unit"] = "Volt";
        Assert.Throws<ArgumentException>(() => codec.Parse(node.ToJsonString()));
        node = JsonNode.Parse(text)!; node["root"]!["readings"]![0] = new JsonObject { ["id"] = "bad", ["kind"] = "delay", ["seconds"] = 0 };
        Assert.Throws<FormatException>(() => codec.Parse(node.ToJsonString()));
    }

    [Fact]
    public async Task RepositoryHallAnalysisFileProducesThreeSeparatelyReferencedGroups()
    {
        var b = new Bench(); var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "sample-hall-analysis.plan.json"), Token);
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(9, rows.Count); Assert.Equal(new[] { 1, 4, 7 }, rows.Where(r => r.Analysis is not null).Select(r => r.Analysis!.FirstInputRow));
        Assert.All(rows.Where(r => r.Analysis is not null), row =>
        {
            Assert.Equal(SampleAnalysisIssues.None, row.Analysis!.Issues);
            Assert.Equal(PhysicalUnit.OhmMeter, row.Values[new("rho_xx")].Unit);
        });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task LiveAppendAndArchiveWriterRejectBadSummaryBeforeRecordingIt()
    {
        using var folder = new TemporaryFolder(); var b = new Bench(); await b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.HallBar, Geometry)), Token);
        var snapshot = b.Runs.LatestDataSet!.Capture(); var summary = snapshot.Rows[^1];
        var changed = new Dictionary<ParameterId, Quantity>(summary.Values) { [new("r_sheet")] = new(1, PhysicalUnit.OhmPerSquare) };
        var bad = new MeasurementDataRow(summary.Timestamp, changed, analysis: summary.Analysis);
        var data = new RunDataSet(RunId.New(), snapshot.Name, snapshot.StartedAt, snapshot.Schema, snapshot.Recipe, snapshot.Instruments, snapshot.Plan);
        foreach (var row in snapshot.Rows.Take(2)) data.Append(row);
        Assert.Throws<ArgumentException>(() => data.Append(bad)); Assert.Equal(2, data.Rows.Count);
        await using var archive = new FileRunArchive(folder.Path);
        await archive.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = Array.Empty<MeasurementDataRow>() }, Token);
        foreach (var row in snapshot.Rows.Take(2)) await archive.OnRowAsync(snapshot.Id, row, Token);
        await Assert.ThrowsAsync<ArgumentException>(() => archive.OnRowAsync(snapshot.Id, bad, Token));
        data.Append(summary); await archive.OnRowAsync(snapshot.Id, summary, Token); await archive.OnFinalizedAsync(snapshot, Token);
        Assert.Equal(3, (await archive.LoadAsync(snapshot.Id, Token)).DataSet.Rows.Count);
    }

    [Fact]
    public async Task SummaryCannotCombineRawReadingsWithDifferentScanCoordinates()
    {
        var b = new Bench(); var plan = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.HallBar, Geometry)));
        var scan = new ScanStep(new("bias_scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("bias"), new([new(0.01, PhysicalUnit.Volt)]), TimeSpan.Zero, plan.Root);
        await b.Runs.StartPlanAsync(new("scan", scan, plan.Preparations, routing: plan.Routing), Token); var snapshot = b.Runs.LatestDataSet!.Capture();
        var data = new RunDataSet(RunId.New(), snapshot.Name, snapshot.StartedAt, snapshot.Schema, snapshot.Recipe, snapshot.Instruments, snapshot.Plan);
        var wrongAxis = new Dictionary<ParameterId, Quantity>(snapshot.Rows[0].Values) { [new("bias")] = new(0.02, PhysicalUnit.Volt) };
        data.Append(new(snapshot.Rows[0].Timestamp, wrongAxis, snapshot.Rows[0].Routing)); data.Append(snapshot.Rows[1]);
        Assert.Throws<ArgumentException>(() => data.Append(snapshot.Rows[2])); Assert.Equal(2, data.Rows.Count);
    }

    [Fact]
    public async Task ResumeCompletesTheSameGroupWithoutRepeatingItsPriorPermutations()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        b.Listener.Row = _ => { if (b.Listener.Rows == 4) b.Runs.Pause(); return Task.CompletedTask; };
        var run = b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.VanDerPauw, Geometry)), Token);
        await Until(events, WorkbenchNotificationKind.RunPaused); b.Runs.Resume(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(9, rows.Count);
        Assert.Equal(8, rows.Take(8).Select(r => r.Routing!.Binding.Selection).Distinct().Count());
        Assert.Equal(1, rows[^1].Analysis!.FirstInputRow); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }
    [Fact]
    public async Task SummaryListenerFailurePreservesTheResultAndStillCleansHardware()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var b = new Bench(archive);
        b.Listener.Row = row => row.Analysis is null ? Task.CompletedTask : throw new IOException("summary delivery failed");
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.HallBar, Geometry)), Token));
        Assert.Equal("summary delivery failed", error.Message); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        var loaded = await archive.LoadAsync(b.Runs.LatestDataSet!.Id, Token); Assert.True(loaded.IsFinalized); Assert.NotNull(loaded.DataSet.Rows[^1].Analysis);
    }
    [Fact]
    public void AnalysisGroupRejectsExternalRoutingBeforePreviewAdmission()
    {
        var b = new Bench(); var plan = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.HallBar, Geometry))); var group = (SampleAnalysisStep)plan.Root;
        var external = new SampleAnalysisStep(group.Id, group.Readings.Select(r => new ReadSampleResistanceStep(r.Acquisition, r.Binding, r.Settle, true)), group.Columns);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new(plan.Name, external, plan.Preparations, routing: plan.Routing)));
        Assert.Equal(0, b.Matrix.Connects); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public void OptionalDerivedColumnsDoNotPreserveAStaleGuaranteedVariable()
    {
        var b = new Bench(); var plan = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.HallBar)));
        var root = new SequenceStep(new("sequence"), [new AssignValueStep(new("before"), new("r_sheet"), new ConstantValue(new(1, PhysicalUnit.OhmPerSquare))),
            plan.Root, new AssignValueStep(new("after"), new("downstream"), new VariableValue(new("r_sheet")))]);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new(plan.Name, root, plan.Preparations, routing: plan.Routing))); Assert.Equal(0, b.Matrix.Connects);
    }
}
