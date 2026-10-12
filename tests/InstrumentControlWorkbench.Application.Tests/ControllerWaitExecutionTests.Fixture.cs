using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControllerWaitExecutionTests
{
    [Fact]
    public async Task PublishedTemperatureAndAngleWaitFixtureRunsAndReopensForReplay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "controller-waits-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "workbench-devices.json"), Token);
            var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var runs = new MeasurementRunService(runtime.Registry, hub, [archive]);
            var plan = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-controller-waits-import.json"), runtime.CreateImportBindings(), Token);
            var codec = new JsonExperimentPlanDocument(); await codec.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); await runs.StartPlanAsync(await codec.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            Assert.Equal(2, runs.LatestDataSet!.Rows.Count); Assert.Equal(300, runs.LatestDataSet.Rows[0].Values[new("ppms.root.5.value")].Value); Assert.Equal(0, runs.LatestDataSet.Rows[1].Values[new("ppms.root.6.value")].Value);
            Assert.Equal(5, runs.LatestDataSet.Plan!.Metadata["ppms.root.4.source_line"].Value); Assert.False(runs.HasActiveRun);
            Assert.False((await runtime.Registry.GetStatusAsync(new("smu-primary"), Token)).Connected); Assert.False((await runtime.Registry.GetStatusAsync(new("matrix-1"), Token)).Connected);
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await reopened.LoadAsync(runs.LatestDataSet.Id, Token); Assert.Equal(2, saved.DataSet.Rows.Count);
            await reopened.ExportCsvAsync(saved.DataSet.Id, Path.Combine(folder, "waits.csv"), Token); Assert.Contains("ppms.root.6.value", await File.ReadAllTextAsync(Path.Combine(folder, "waits.csv")));
            var fresh = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new()); var replay = new MeasurementRunService(fresh.Registry, new());
            await replay.StartPlanAsync(saved.DataSet.Plan!, Token); Assert.Equal(2, replay.LatestDataSet!.Rows.Count); Assert.False(replay.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task TemperatureStateWaitCannotUseAngleOnlyPort()
    {
        var b = new Bench(); var plan = new Core.Experiments.ExperimentPlan("Wrong capability", new Core.Experiments.WaitTemperatureStateStep(new("wait"), b.Id, Core.Experiments.TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<NotSupportedException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.False((await b.Angle.GetSnapshotAsync(Token)).Connected);
    }
}
