using System.Text.Json;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Cli;

// Console composition; every control and acquisition uses Application ownership.
public sealed class CommandLineRunner(IConfiguredDeviceFactory? factory = null, TimeSpan? safetyTimeout = null)
{
    public async Task<int> ExecuteAsync(CliOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(output);
        using var writeGate = new SemaphoreSlim(1, 1);
        MeasurementRunService? runs = null; Exception? reportingError = null;
        async Task Report(object value)
        {
            await writeGate.WaitAsync(CancellationToken.None);
            try { if (reportingError is null) { await output.WriteLineAsync(JsonSerializer.Serialize(value)); await output.FlushAsync(); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { reportingError ??= ex; runs?.Abort(); }
            finally { writeGate.Release(); }
        }
        var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        await using var archive = options.Archive is null ? null : new FileRunArchive(options.Archive);
        var startup = await new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), factory ?? new ConfiguredDeviceFactory(), leases, hub)
            .StartAsync(options.Devices, null, cancellationToken, archive is null ? null : [archive], safetyTimeout);
        runs = startup.Runs; var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs, new PpmsExperimentPlanImporter(), new PpmsRoutingProfileImporter(), startup.Devices);
        if (options.Command == "import")
        {
            var imported = await ImportPlanCommand.ExecuteAsync(options, workspace, cancellationToken);
            await Report(new { version = 1, kind = "imported", name = imported.Plan.Name, input = options.Ppms, output = options.Output, routing = imported.Plan.Routing is not null, maximum_steps = imported.Plan.MaximumStepExecutions, devices = imported.Preview.Devices.Select(id => id.Value) });
            if (reportingError is not null) throw new IOException("Import succeeded but CLI reporting failed.", reportingError);
            return 0;
        }
        var document = await workspace.LoadAsync(options.Plan, cancellationToken);
        await Report(new { version = 1, kind = "validated", name = document.Plan.Name, devices = startup.Devices.Registry.Devices.Where(device => document.Preview.Devices.Contains(device.Id)).Select(device => new { id = device.Id.Value, model = device.Model, driver = device.DriverKind.ToString() }), maximum_steps = document.Preview.MaximumStepExecutions, dynamic_loops = document.Preview.HasDynamicLoops });
        if (reportingError is not null) throw new IOException("CLI output failed before execution.", reportingError);
        if (options.Command == "validate") return 0;
        if (archive is null) throw new ArgumentException("Run requires an archive.");
        using var controlsLifetime = new CancellationTokenSource();
        var controls = options.ControlFile is null ? Task.CompletedTask : new CommandFileControl(options.ControlFile, runs, Report).PollAsync(controlsLifetime.Token);
        Exception? executionError = null;
        var execution = runs.StartPlanAsync(document.Plan, cancellationToken, new RunMetadata(sampleName: options.Sample ?? ""));
        try
        {
            while (!execution.IsCompleted && (runs.LatestDataSet is null || runs.Phase == RunExecutionPhase.Starting)) await Task.Delay(10, CancellationToken.None);
            if (runs.LatestDataSet is { } started)
            {
                try { await archive.ReadMetadataAsync(started.Id, CancellationToken.None); await Report(new { version = 1, kind = "started", run_id = started.Id.ToString(), archive = options.Archive }); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            try { await execution; }
            catch (Exception ex) { executionError = ex; }
            await runs.WaitForFinalizationAsync(CancellationToken.None);
            if (runs.HasActiveRun)
                await Report(new { version = 1, kind = "safety_recovery_required", run_id = runs.LatestOutcome?.Id.ToString(), ownership_retained = true, phase = runs.Phase.ToString(), error = executionError?.Message });
            // Never dispose storage/exit while unconfirmed stop tasks and leases are owned.
            // Cancellation already aborts the engine; recovery uses the independently live control file.
            while (runs.HasActiveRun) await Task.Delay(100, CancellationToken.None);
        }
        finally
        {
            if (runs.HasActiveRun) runs.Abort();
            // Reporting failures must still drain execution and retain the process if cleanup is unknown.
            try { await execution; } catch (Exception ex) { executionError ??= ex; }
            while (runs.HasActiveRun) await Task.Delay(100, CancellationToken.None);
            controlsLifetime.Cancel();
            try { await controls; } catch (OperationCanceledException) when (controlsLifetime.IsCancellationRequested) { }
            foreach (var id in document.Preview.Devices)
            {
                try { await startup.Devices.Registry.DisconnectAsync(id, CancellationToken.None); }
                catch (Exception ex) { executionError = executionError is null ? ex : new AggregateException(executionError, ex); }
            }
        }
        var outcome = runs.LatestOutcome; var data = runs.LatestDataSet;
        if (options.Csv is not null && data is not null) await archive.ExportCsvAsync(data.Id, options.Csv, CancellationToken.None);
        await Report(new { version = 1, kind = "result", run_id = outcome?.Id.ToString(), state = outcome?.State.ToString(), rows = data?.TotalRowCount ?? 0, ownership_retained = runs.HasActiveRun, archive = options.Archive, csv = options.Csv, error = executionError?.Message });
        return outcome?.State == MeasurementRunState.Aborted && reportingError is null ? 130 : executionError is not null || reportingError is not null || outcome?.State != MeasurementRunState.Completed ? 1 : 0;
    }
}
