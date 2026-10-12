using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class FieldWaitExecutionTests
{
    [Fact]
    public async Task PublishedFieldWaitFixtureMeasuresOnlyAfterNonrampingAndReopensForReplay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "field-wait-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "workbench-devices.json"), Token);
            var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var runs = new MeasurementRunService(runtime.Registry, hub, [archive]);
            var plan = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-wait-import.json"), runtime.CreateImportBindings(), Token);
            var codec = new JsonExperimentPlanDocument(); await codec.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); await runs.StartPlanAsync(await codec.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var row = Assert.Single(runs.LatestDataSet!.Rows); Assert.Equal(new(0.01, PhysicalUnit.Tesla), row.Values[new("ppms.root.2.value")]);
            Assert.Equal(2, runs.LatestDataSet.Plan!.Metadata["ppms.root.1.source_line"].Value); Assert.False(runs.HasActiveRun);
            Assert.False((await runtime.Registry.GetStatusAsync(new("smu-primary"), Token)).Connected); Assert.False((await runtime.Registry.GetStatusAsync(new("matrix-1"), Token)).Connected);
            await using var freshArchive = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await freshArchive.LoadAsync(runs.LatestDataSet.Id, Token); Assert.Single(saved.DataSet.Rows);
            Assert.Equal(runs.LatestBlackboardSnapshot!.Entries[new("ppms.root.2.value")], saved.DataSet.Blackboard!.Entries[new("ppms.root.2.value")]);
            await freshArchive.ExportCsvAsync(saved.DataSet.Id, Path.Combine(folder, "field.csv"), Token); Assert.Contains("ppms.root.2.value", await File.ReadAllTextAsync(Path.Combine(folder, "field.csv")));
            var replayHub = new NotificationHub(); var replayDevices = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), replayHub); var replay = new MeasurementRunService(replayDevices.Registry, replayHub);
            await replay.StartPlanAsync(saved.DataSet.Plan!, Token); Assert.Equal(new(0.01, PhysicalUnit.Tesla), Assert.Single(replay.LatestDataSet!.Rows).Values[new("ppms.root.2.value")]); Assert.False(replay.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task NativeFieldWaitCannotUseAnAngleController()
    {
        var angle = MockEnvironmentCatalog.Angle(); var hub = new NotificationHub(); var registry = new DeviceRegistry([new AxisControllerSession(angle, hub)], new(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub); var plan = new ExperimentPlan("Wrong controller", new WaitFieldStep(new("wait"), angle.Definition.Device.Id, new(0.0005, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<ArgumentException>(() => runs.StartPlanAsync(plan, Token)); Assert.False((await angle.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task FieldDeadlineIncludesPauseWithoutFabricatingSuccessfulHold()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var wait = new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(250), new WaitFieldStep(new("wait"), b.Id, new(0.0005, PhysicalUnit.Tesla), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(20)));
        var run = b.Runs.StartPlanAsync(new("Paused field deadline", wait), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            var error = await Assert.ThrowsAsync<PlanStepTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(new StepId("deadline"), error.Step);
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun); Assert.True(b.Field.Holds > 0);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
}
