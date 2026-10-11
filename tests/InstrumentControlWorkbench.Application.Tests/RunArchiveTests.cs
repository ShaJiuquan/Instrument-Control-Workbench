using System.Globalization;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class RunArchiveTests
{
    [Fact]
    public async Task FreshArchiveInstanceRestoresTypedPlanMetadataSnapshotsAndRows()
    {
        using var folder = new TemporaryFolder();
        RunId id;
        await using (var archive = new FileRunArchive(folder.Path))
        {
            var (runs, driver, _) = Create(archive);
            var metadata = new RunMetadata(new("sample_01"), "样品 A, \"quoted\"", "line 1\nline 2", new Dictionary<string, string> { ["operator"] = "test operator" });
            await runs.StartIvSweepAsync(new("persistent IV", -0.02, 0.02, 3, 105e-6, 0), CancellationToken.None, metadata);
            id = runs.LatestDataSet!.Id;
            Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        }
        await using var reopened = new FileRunArchive(folder.Path);
        var history = new RunHistoryService(reopened);
        var catalog = await history.RefreshAsync(CancellationToken.None);
        Assert.Empty(catalog.Issues);
        Assert.Single(catalog.Runs);
        Assert.Equal(3, catalog.Runs[0].RowCount);
        var saved = await history.OpenAsync(id, CancellationToken.None);
        Assert.True(saved.IsFinalized);
        Assert.False(saved.HasIncompleteTail);
        Assert.Equal(MeasurementRunState.Completed, saved.DataSet.State);
        Assert.Equal(3, saved.DataSet.Rows.Count);
        Assert.Equal("sample_01", saved.DataSet.Metadata!.SampleId!.Value);
        Assert.Equal("样品 A, \"quoted\"", saved.DataSet.Metadata.SampleName);
        Assert.Equal("line 1\nline 2", saved.DataSet.Metadata.Notes);
        Assert.Equal("test operator", saved.DataSet.Metadata.Attributes["operator"]);
        Assert.IsType<SequenceStep>(saved.DataSet.Plan!.Root);
        Assert.Equal("Simulation", saved.DataSet.Instruments[0].Configuration["driver_kind"]);
        Assert.Equal("False", saved.DataSet.Instruments[0].Configuration["output_enabled"]);
        Assert.All(saved.DataSet.Rows, saved.DataSet.Schema.ValidateRow);
    }

    [Fact]
    public async Task AbortedRunPersistsPartialResultsBeforeTerminalNotification()
    {
        using var folder = new TemporaryFolder();
        await using var archive = new FileRunArchive(folder.Path);
        var (runs, driver, hub) = Create(archive);
        var task = runs.StartIvSweepAsync(new("abort", 0, 0.1, 3, 105e-6, 30), CancellationToken.None);
        await Until(hub, WorkbenchNotificationKind.MeasurementPointReceived);
        runs.Abort();
        await task;
        await Until(hub, WorkbenchNotificationKind.RunAborted);
        var saved = await archive.LoadAsync(runs.LatestDataSet!.Id, CancellationToken.None);
        Assert.Equal(MeasurementRunState.Aborted, saved.DataSet.State);
        Assert.Single(saved.DataSet.Rows);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.False(runs.HasActiveRun);
    }

    [Fact]
    public async Task StorageAdmissionFailureStopsResourcesBeforeAnyOutputEnable()
    {
        using var folder = new TemporaryFolder();
        var blocked = System.IO.Path.Combine(folder.Path, "file");
        await File.WriteAllTextAsync(blocked, "existing data");
        await using var archive = new FileRunArchive(blocked);
        var (runs, driver, _) = Create(archive);
        Assert.NotNull(await Record.ExceptionAsync(() => runs.StartIvSweepAsync(new("blocked", 0, 0.1, 2, 105e-6, 0), CancellationToken.None)));
        Assert.Equal(MeasurementRunState.Faulted, runs.LatestOutcome!.State);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.False(runs.HasActiveRun);
        Assert.Equal("existing data", await File.ReadAllTextAsync(blocked));
    }

    [Fact]
    public async Task FinalSnapshotReconcilesCommittedRowsNotDeliveredAfterCancellation()
    {
        using var folder = new TemporaryFolder();
        await using var archive = new FileRunArchive(folder.Path);
        var dataSet = DataSet();
        await archive.OnStartedAsync(dataSet.Capture(), CancellationToken.None);
        dataSet.Append(Row(1));
        await archive.OnRowAsync(dataSet.Id, dataSet.Rows[0], CancellationToken.None);
        dataSet.Append(Row(2));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.OnRowAsync(dataSet.Id, dataSet.Rows[1], cancelled.Token));
        dataSet.Complete(MeasurementRunState.Aborted, DateTimeOffset.UtcNow);
        await archive.OnFinalizedAsync(dataSet.Capture(), CancellationToken.None);
        var restored = await archive.LoadAsync(dataSet.Id, CancellationToken.None);
        Assert.Equal(new[] { 1.0, 2.0 }, restored.DataSet.Rows.Select(r => r.Values[new("x")].Value));
        Assert.Equal(MeasurementRunState.Aborted, restored.DataSet.State);
    }

    [Fact]
    public async Task UnfinalizedArchiveSurvivesRestartAndExcludesOnlyUnterminatedTail()
    {
        using var folder = new TemporaryFolder();
        var dataSet = DataSet();
        await using (var archive = new FileRunArchive(folder.Path))
        {
            await archive.OnStartedAsync(dataSet.Capture(), CancellationToken.None);
            await archive.OnRowAsync(dataSet.Id, Row(1), CancellationToken.None);
        }
        var rowsPath = System.IO.Path.Combine(folder.Path, dataSet.Id.Value.ToString("N"), "rows.ndjson");
        await File.AppendAllTextAsync(rowsPath, "{\"index\":2,\"timestamp\":");
        await using var reopened = new FileRunArchive(folder.Path);
        var restored = await reopened.LoadAsync(dataSet.Id, CancellationToken.None);
        Assert.False(restored.IsFinalized);
        Assert.True(restored.HasIncompleteTail);
        Assert.Null(restored.DataSet.FinishedAt);
        Assert.Equal(MeasurementRunState.Running, restored.DataSet.State);
        Assert.Single(restored.DataSet.Rows);
        Assert.Null((await reopened.ReadMetadataAsync(dataSet.Id, CancellationToken.None)).RowCount);
        await reopened.ExportCsvAsync(dataSet.Id, System.IO.Path.Combine(folder.Path, "..", $"partial-{dataSet.Id.Value:N}.csv"), CancellationToken.None);
        File.Delete(System.IO.Path.Combine(folder.Path, "..", $"partial-{dataSet.Id.Value:N}.csv"));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("sequence")]
    [InlineData("missing_timestamp")]
    [InlineData("bad_unit")]
    [InlineData("duplicate_field")]
    [InlineData("truncated_finalized")]
    [InlineData("unknown_version")]
    public async Task CorruptRecordsAreRejectedAndNeverExportedOverExistingData(string corruption)
    {
        using var folder = new TemporaryFolder();
        await using var archive = new FileRunArchive(folder.Path);
        var dataSet = DataSet(); dataSet.Append(Row(1));
        await archive.OnStartedAsync(dataSet.Capture() with { Rows = [] }, CancellationToken.None);
        await archive.OnRowAsync(dataSet.Id, dataSet.Rows[0], CancellationToken.None);
        dataSet.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
        await archive.OnFinalizedAsync(dataSet.Capture(), CancellationToken.None);
        var runFolder = System.IO.Path.Combine(folder.Path, dataSet.Id.Value.ToString("N"));
        var rowsPath = System.IO.Path.Combine(runFolder, "rows.ndjson");
        var text = await File.ReadAllTextAsync(rowsPath);
        var row = JsonNode.Parse(text)!;
        switch (corruption)
        {
            case "checksum": row["values"]!["x"]!["value"] = 2; text = row.ToJsonString() + "\n"; break;
            case "sequence": row["index"] = 2; text = row.ToJsonString() + "\n"; break;
            case "missing_timestamp": row.AsObject().Remove("timestamp"); text = row.ToJsonString() + "\n"; break;
            case "bad_unit": row["values"]!["x"]!["unit"] = "Tesla"; text = row.ToJsonString() + "\n"; break;
            case "duplicate_field": text = text.Replace("\"index\":1", "\"index\":1,\"index\":2", StringComparison.Ordinal); break;
            case "truncated_finalized": text = text[..^1]; break;
            case "unknown_version":
                var headerPath = System.IO.Path.Combine(runFolder, "metadata.json");
                var header = JsonNode.Parse(await File.ReadAllTextAsync(headerPath))!; header["version"] = 999;
                await File.WriteAllTextAsync(headerPath, header.ToJsonString()); break;
        }
        await File.WriteAllTextAsync(rowsPath, text);
        Assert.NotNull(await Record.ExceptionAsync(() => archive.LoadAsync(dataSet.Id, CancellationToken.None)));
        var export = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icw-export-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(export, "previous export");
            Assert.NotNull(await Record.ExceptionAsync(() => archive.ExportCsvAsync(dataSet.Id, export, CancellationToken.None)));
            Assert.Equal("previous export", await File.ReadAllTextAsync(export));
            Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(export)!, System.IO.Path.GetFileName(export) + ".*.tmp"));
        }
        finally { File.Delete(export); }
    }

    [Fact]
    public async Task CsvStreamsHeterogeneousRowsWithUnitsProvenanceAndInvariantCulture()
    {
        using var folder = new TemporaryFolder();
        await using var archive = new FileRunArchive(folder.Path);
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var axis = MockEnvironmentCatalog.Field();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([manager, new AxisControllerSession(axis, hub)], leases);
        var runs = new MeasurementRunService(manager, leases, hub, [archive], deviceRegistry: registry);
        var plan = new ExperimentPlan("heterogeneous", new SequenceStep(new("root"),
            [new ReadStep(new("voltage"), [new(new("voltage"), new(driver.Id, Keithley2450Parameters.SourceVoltage.Id))]),
             new ReadStep(new("field"), [new(new("field"), new(axis.Definition.Device.Id, axis.Definition.Readback.Id))])]),
            [new(driver.Id, SourceFunction.Voltage, new(0.125, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere),
                new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        await runs.StartPlanAsync(plan, CancellationToken.None, new(sampleName: "sample, \"A\"", notes: "two\nlines"));
        var export = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icw-export-{Guid.NewGuid():N}.csv");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            await archive.ExportCsvAsync(runs.LatestDataSet!.Id, export, CancellationToken.None);
            var text = await File.ReadAllTextAsync(export);
            Assert.Contains("\"voltage [Volt]\"", text);
            Assert.Contains("\"field [Tesla]\"", text);
            Assert.Contains("\"0.125\"", text);
            Assert.DoesNotContain("\"0,125\"", text);
            Assert.Contains("# metadata_json", text);
            Assert.Contains("# instruments_json", text);
            Assert.Contains("k2450", text);
            Assert.Contains("# plan_json", text);
            Assert.Contains("# recipe_json", text);
            Assert.Contains("\"\"", text);
            Assert.DoesNotContain("NaN", text);
            await Assert.ThrowsAsync<IOException>(() => archive.LoadAsync(runs.LatestDataSet.Id, CancellationToken.None, maximumRows: 1));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ExportCsvAsync(runs.LatestDataSet.Id, export, cancelled.Token));
            Assert.Equal(text, await File.ReadAllTextAsync(export));
            await Assert.ThrowsAsync<ArgumentException>(() => archive.ExportCsvAsync(runs.LatestDataSet.Id,
                System.IO.Path.Combine(folder.Path, "overwrite.csv"), CancellationToken.None));
        }
        finally { CultureInfo.CurrentCulture = culture; File.Delete(export); }
    }

    [Fact]
    public async Task DuplicateRunIdAndAlteredCommittedRowsCannotOverwriteHistory()
    {
        using var folder = new TemporaryFolder();
        await using var archive = new FileRunArchive(folder.Path);
        var dataSet = DataSet();
        await archive.OnStartedAsync(dataSet.Capture(), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => archive.OnStartedAsync(dataSet.Capture(), CancellationToken.None));
        await archive.OnRowAsync(dataSet.Id, Row(1), CancellationToken.None);
        dataSet.Append(Row(2)); dataSet.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => archive.OnFinalizedAsync(dataSet.Capture(), CancellationToken.None));
        var loaded = await archive.LoadAsync(dataSet.Id, CancellationToken.None);
        Assert.False(loaded.IsFinalized);
        Assert.Equal(1, loaded.DataSet.Rows[0].Values[new("x")].Value);
    }

    [Fact]
    public async Task WideRowsAndMetadataCrossMultipleReadBuffersWithoutLoss()
    {
        using var folder = new TemporaryFolder();
        await using var archive = new FileRunArchive(folder.Path);
        var parameters = Enumerable.Range(0, 300).Select(i => new ParameterDescriptor(new($"channel_{i}"),
            $"Measurement channel {i}", PhysicalUnit.Volt, ParameterAccess.Read)).ToArray();
        var dataSet = new RunDataSet(RunId.New(), "wide", DateTimeOffset.UtcNow,
            new(parameters.Select(p => new DataColumn(p))), new Dictionary<string, Quantity>(), [],
            metadata: new(notes: new string('测', 10_000)));
        await archive.OnStartedAsync(dataSet.Capture(), CancellationToken.None);
        for (var index = 0; index < 3; index++)
        {
            var row = new MeasurementDataRow(DateTimeOffset.UtcNow,
                parameters.ToDictionary(p => p.Id, _ => new Quantity(index + 0.125, PhysicalUnit.Volt)));
            dataSet.Append(row); await archive.OnRowAsync(dataSet.Id, row, CancellationToken.None);
        }
        dataSet.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
        await archive.OnFinalizedAsync(dataSet.Capture(), CancellationToken.None);
        var restored = await archive.LoadAsync(dataSet.Id, CancellationToken.None);
        Assert.Equal(3, restored.DataSet.Rows.Count);
        Assert.Equal(10_000, restored.DataSet.Metadata!.Notes!.Length);
        Assert.All(restored.DataSet.Rows, row => { Assert.Equal(300, row.Values.Count); restored.DataSet.Schema.ValidateRow(row); });
        Assert.Equal(2.125, restored.DataSet.Rows[2].Values[new("channel_299")].Value);
    }

    [Fact]
    public async Task CatalogReportsDamagedDirectoriesAlongsideHealthyRuns()
    {
        using var folder = new TemporaryFolder();
        await using var archive = new FileRunArchive(folder.Path);
        var good = DataSet();
        await archive.OnStartedAsync(good.Capture(), CancellationToken.None);
        good.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
        await archive.OnFinalizedAsync(good.Capture(), CancellationToken.None);
        var bad = RunId.New();
        Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, bad.Value.ToString("N")));
        var catalog = await archive.ListAsync(CancellationToken.None);
        Assert.Single(catalog.Runs);
        Assert.Equal(good.Id, catalog.Runs[0].Header.Id);
        Assert.Equal(bad, Assert.Single(catalog.Issues).Id);
        Assert.False(string.IsNullOrWhiteSpace(catalog.Issues[0].Message));
    }

    private static (MeasurementRunService, MockKeithley2450SourceMeter, NotificationHub) Create(FileRunArchive archive)
    {
        var hub = new NotificationHub(); var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        return (new(manager, new(TimeSpan.FromSeconds(5)), hub, [archive]), driver, hub);
    }
    private static RunDataSet DataSet() => new(RunId.New(), "journal", DateTimeOffset.UtcNow,
        new([new(new ParameterDescriptor(new("x"), "Voltage", PhysicalUnit.Volt, ParameterAccess.Read))]),
        new Dictionary<string, Quantity>(), []);
    private static MeasurementDataRow Row(double value) => new(DateTimeOffset.UnixEpoch,
        new Dictionary<ParameterId, Quantity> { [new("x")] = new(value, PhysicalUnit.Volt) });
    private static async Task Until(NotificationHub hub, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await hub.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icw-archive-{Guid.NewGuid():N}");
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
