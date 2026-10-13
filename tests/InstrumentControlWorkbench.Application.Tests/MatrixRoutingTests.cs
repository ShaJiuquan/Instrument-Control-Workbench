using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class MatrixRoutingTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly MatrixCrossPoint Original = new(1, 1, 1);
    private static readonly MatrixCrossPoint First = new(1, 2, 2);
    private static readonly MatrixCrossPoint Second = new(1, 2, 3);

    [Fact]
    public async Task SavedPlanBreaksBeforeMakingAndAcquiresOnVerifiedRoutesThenCleansOnlyRunAddedConnections()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"icw-matrix-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var routesAtRead = new List<MatrixCrossPoint[]>();
                b.Source.BeforeRead = async () => routesAtRead.Add((await b.Matrix.GetSnapshotAsync(Token)).Closed.ToArray());
                var plan = b.Prepared(new SequenceStep(new("root"), [b.Route("route1", [First]), b.On("on1"), b.Read("read1"),
                    b.Route("route2", [Second]), b.On("on2"), b.Read("read2")]));
                var codec = new JsonExperimentPlanDocument(); var path = Path.Combine(folder, "plan.json");
                await codec.SaveAsync(path, plan, Token); plan = await codec.LoadAsync(path, Token);
                await b.Runs.StartPlanAsync(plan, Token);
                Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count);
                Assert.Equal(new[] { First }, routesAtRead[0]); Assert.Equal(new[] { Second }, routesAtRead[1]);
                Assert.Equal(new[] { "open:1:1:1", "close:1:2:2", "open:1:2:2", "close:1:2:3", "open:1:2:3" }, b.Matrix.Writes);
                Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
                Assert.False(b.Runs.HasActiveRun);
                id = b.Runs.LatestDataSet.Id;
                var matrixMetadata = b.Runs.LatestDataSet.Instruments.Single(i => i.InstrumentId == b.Matrix.Definition.Device.Id);
                Assert.Equal("1:1:1", matrixMetadata.Configuration["initial_closed"]);
                Assert.Equal(b.Source.Id.Value, matrixMetadata.Configuration["guarded_sources"]);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs"));
            var loaded = await reopened.LoadAsync(id, Token);
            Assert.Equal(2, loaded.DataSet.Rows.Count);
            var route = Assert.IsType<RouteMatrixStep>(Assert.IsType<SequenceStep>(loaded.DataSet.Plan!.Root).Children[0]);
            Assert.Equal(new[] { First }, route.Closed);
            await reopened.ExportCsvAsync(id, Path.Combine(folder, "matrix.csv"), Token);
            Assert.Contains("route_matrix", await File.ReadAllTextAsync(Path.Combine(folder, "matrix.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task UnchangedPreexistingConnectionsRemainClosedAfterCleanup()
    {
        var b = new Bench();
        await b.Runs.StartPlanAsync(new("keep original", b.Route("route", [Original, First])), Token);
        Assert.Equal(new[] { Original }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.Equal(new[] { "close:1:2:2", "open:1:2:2" }, b.Matrix.Writes);
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 5, 1)]
    [InlineData(1, 1, 7)]
    public async Task CardBoundsAreCheckedBeforeAdmissionOrDriverIO(int slot, int row, int column)
    {
        var b = new Bench();
        var plan = new ExperimentPlan("invalid", b.Route("route", [new(slot, row, column)]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.Equal(0, b.Matrix.Connects); Assert.Equal(0, b.Source.Connects);
        Assert.Empty(b.Matrix.Writes); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task MatrixOnlyPlanClaimsItsDeclaredExcitationSourceWithoutIOAtAdmission()
    {
        var b = new Bench();
        var plan = new ExperimentPlan("route", b.Route("route", [First]));
        var preview = b.Runs.PreviewPlan(plan);
        Assert.Contains(b.Source.Id, preview.Devices); Assert.Contains(b.Matrix.Definition.Device.Id, preview.Devices);
        var resources = await b.Devices.AcquireRunAsync(preview.Devices, RunId.New(), Token);
        Assert.Equal(0, b.Matrix.Connects); Assert.Equal(0, b.Source.Connects);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConnectAsync(b.Source.Id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Session.ConnectDeviceAsync(Token));
        Assert.Empty(await resources.StopAllAsync(Token)); await resources.ReleaseAsync(Token);
    }

    [Fact]
    public async Task SourceOffFailurePreventsAnyRelayWriteAndKeepsTheGroupReservedUntilRecovery()
    {
        var b = new Bench(); b.Source.FailStop = true;
        var error = await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartPlanAsync(new("guard failed", b.Route("route", [First])), Token));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "excitation stop failed");
        Assert.Empty(b.Matrix.Writes); Assert.True(b.Runs.HasActiveRun);
        b.Source.FailStop = false; await b.Runs.RecoverSafetyAsync(Token);
        Assert.False(b.Runs.HasActiveRun); Assert.Equal(new[] { Original }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task FinalCleanupWaitsForFreshSourceOffBeforeOpeningRelaysAndPreservesFailure()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var task = b.Runs.StartPlanAsync(b.Prepared(new SequenceStep(new("root"), [b.Route("route", [First]), b.On("on"),
            new DelayStep(new("wait"), TimeSpan.FromSeconds(30))])), Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, "wait");
        b.Source.FailStop = true; b.Runs.Abort();
        var error = await Assert.ThrowsAsync<AggregateException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "excitation stop failed");
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("cleanup deferred"));
        Assert.Equal(new[] { First }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.Equal(2, b.Matrix.Writes.Count); Assert.True(b.Runs.HasActiveRun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConnectAsync(b.Source.Id, Token));
        b.Source.FailStop = false; await b.Runs.RecoverSafetyAsync(Token);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task OpenReadbackMismatchNeverClosesNewConnections()
    {
        var b = new Bench(); b.Matrix.IgnoreOpen = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(new("open mismatch", b.Route("route", [First])), Token));
        Assert.Contains("no new connections", error.Message);
        Assert.Equal(new[] { "open:1:1:1" }, b.Matrix.Writes);
        Assert.Equal(new[] { Original }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task PartialCloseFaultIsPreservedAndAddedConnectionIsSafelyOpened()
    {
        var b = new Bench(); b.Matrix.FailCloseAfterMutation = true;
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(new("partial close", b.Route("route", [First])), Token));
        Assert.Equal("partial matrix close failed", error.Message);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.Equal(new[] { "open:1:1:1", "close:1:2:2", "open:1:2:2" }, b.Matrix.Writes);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task CleanupReadbackMismatchRetainsOwnershipAndExplicitRecoveryRetries()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var task = b.Runs.StartPlanAsync(new("cleanup mismatch", new SequenceStep(new("root"), [b.Route("route", [First]),
            new DelayStep(new("wait"), TimeSpan.FromSeconds(30))])), Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, "wait"); b.Matrix.IgnoreOpen = true; b.Runs.Abort();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(b.Runs.HasActiveRun); Assert.Equal(new[] { First }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        b.Matrix.IgnoreOpen = false; await b.Runs.RecoverSafetyAsync(Token);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task PauseKeepsRelaysAndStopsExcitationThenResumeChecksRouteBeforeReenabling()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var task = b.Runs.StartPlanAsync(b.Prepared(new SequenceStep(new("root"), [b.Route("route", [First]), b.On("on"),
            new DelayStep(new("wait"), TimeSpan.FromSeconds(30))])), Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, "wait"); b.Runs.Pause();
        await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Equal(new[] { First }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.Equal(2, b.Matrix.Writes.Count);
        b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed);
        Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ChangedRouteDuringPausePreventsResumeOutputAndFaultsWithCleanup()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var task = b.Runs.StartPlanAsync(b.Prepared(new SequenceStep(new("root"), [b.Route("route", [First]), b.On("on"),
            new DelayStep(new("wait"), TimeSpan.FromSeconds(30))])), Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, "wait"); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        await b.Matrix.ExternalChangeAsync([Second]); b.Runs.Resume();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("route changed", error.Message); Assert.Equal(1, b.Source.Enables);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task AbortingPausedRunPerformsFinalCleanupRatherThanReusingHeldRouteProof()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var task = b.Runs.StartPlanAsync(new("pause abort", new SequenceStep(new("root"), [b.Route("route", [First]),
            new DelayStep(new("wait"), TimeSpan.FromSeconds(30))])), Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, "wait"); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.Equal(2, b.Matrix.Writes.Count); b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, b.Matrix.Writes.Count); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task PublicSessionStopCannotBypassExcitationGuardAndHeldProofCannotReleaseGroup()
    {
        var b = new Bench(); var owner = RunId.New();
        var resources = await b.Devices.AcquireRunAsync([b.Source.Id, b.Matrix.Definition.Device.Id], owner, Token);
        await resources.ConnectAllAsync(Token); await resources.RouteMatrixAsync(b.Matrix.Definition.Device.Id, [First], Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Session.StopDeviceAsync(Token, owner));
        Assert.Contains("excitation-off", error.Message); Assert.Equal(2, b.Matrix.Writes.Count);
        Assert.Empty(await resources.StopAllAsync(Token, preserveRouting: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
        Assert.Empty(await resources.StopAllAsync(Token)); await resources.ReleaseAsync(Token);
    }

    [Fact]
    public async Task UnknownRoutingCannotAuthorizeOutputOrReleaseUntilItIsKnownAgain()
    {
        var b = new Bench(); var owner = RunId.New();
        var resources = await b.Devices.AcquireRunAsync([b.Source.Id, b.Matrix.Definition.Device.Id], owner, Token);
        await resources.ConnectAllAsync(Token); await resources.ConfigureSourceMeterAsync(b.Source.Id, new(SourceFunction.Voltage, 0.1, 0.001),
            new(SenseFunction.Current, SensingMode.TwoWire, 1, true), Token);
        await resources.RouteMatrixAsync(b.Matrix.Definition.Device.Id, [First], Token); b.Matrix.Unknown = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.EnableSourceOutputAsync(b.Source.Id, Token));
        Assert.Equal(0, b.Source.Enables); Assert.NotEmpty(await resources.StopAllAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
        b.Matrix.Unknown = false; Assert.Empty(await resources.StopAllAsync(Token, retryFailed: true)); await resources.ReleaseAsync(Token);
    }

    [Fact]
    public async Task TimedOutUncooperativeMatrixStopIsNotOverlappedAndKeepsResourcesUntilCompletion()
    {
        var b = new Bench(safetyTimeout: TimeSpan.FromSeconds(1));
        var resources = await b.Devices.AcquireRunAsync([b.Source.Id, b.Matrix.Definition.Device.Id], RunId.New(), Token, TimeSpan.FromSeconds(1));
        await resources.ConnectAllAsync(Token); await resources.RouteMatrixAsync(b.Matrix.Definition.Device.Id, [First], Token);
        b.Matrix.OpenGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialAttempts = b.Matrix.OpenCalls; var stopping = resources.StopAllAsync(Token);
        await b.Matrix.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(await stopping.WaitAsync(TimeSpan.FromSeconds(5))); var attempts = b.Matrix.OpenCalls;
        Assert.Equal(initialAttempts + 1, attempts);
        Assert.NotEmpty(await resources.StopAllAsync(Token, retryFailed: true)); Assert.Equal(attempts, b.Matrix.OpenCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.RouteMatrixAsync(b.Matrix.Definition.Device.Id, [Second], Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
        b.Matrix.OpenGate.SetResult(); await b.Matrix.OpenFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The old attempt must finish its cancelled readback before a fresh
        // recovery attempt may confirm the already-open relays.
        Assert.NotEmpty(await resources.StopAllAsync(Token));
        Assert.Empty(await resources.StopAllAsync(Token, retryFailed: true)); await resources.ReleaseAsync(Token);
    }

    [Fact]
    public async Task DisconnectedHardwarePolicyCannotClaimRoutingConfirmation()
    {
        var b = new Bench(hardwarePolicy: true);
        var resources = await b.Devices.AcquireRunAsync([b.Source.Id, b.Matrix.Definition.Device.Id], RunId.New(), Token);
        await resources.ConnectAllAsync(Token); await resources.RouteMatrixAsync(b.Matrix.Definition.Device.Id, [First], Token);
        await b.Matrix.DisconnectAsync(Token);
        Assert.NotEmpty(await resources.StopAllAsync(Token));
        Assert.Equal(new[] { First }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        await resources.ConnectAllAsync(Token); Assert.Empty(await resources.StopAllAsync(Token, retryFailed: true));
        await resources.ReleaseAsync(Token);
    }

    [Fact]
    public async Task RepositoryMatrixExampleRunsTwoChannelsFromDisk()
    {
        var b = new Bench();
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "matrix-resistance.plan.json"), Token);
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count);
        Assert.All(b.Runs.LatestDataSet.Rows, row =>
        {
            b.Runs.LatestDataSet.Schema.ValidateRow(row);
            Assert.Equal(row.Values[new("voltage")].Value / row.Values[new("current")].Value, row.Values[new("resistance")].Value);
        });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task MissingOrNonSourceGuardIsRejectedBeforeConnecting()
    {
        var hub = new NotificationHub(); var source = new FaultSource(); var manager = new InstrumentManager(source, hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var definition = new MatrixSwitchDefinition(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []),
            [new(1, 4, 6)], [new("missing")]);
        var matrix = new FaultMatrix(definition, source);
        var runs = new MeasurementRunService(manager, leases, hub, deviceRegistry: new([manager, new MatrixSwitchSession(matrix, hub)], leases));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => runs.StartPlanAsync(new("bad guard", new RouteMatrixStep(new("route"), definition.Device.Id, [First], TimeSpan.Zero)), Token));
        Assert.Equal(0, matrix.Connects); Assert.Equal(0, source.Connects);
        var axis = MockEnvironmentCatalog.Field();
        definition = new(definition.Device, definition.Cards, [axis.Definition.Device.Id]); matrix = new(definition, source);
        runs = new(manager, leases, hub, deviceRegistry: new([manager, new MatrixSwitchSession(matrix, hub), new AxisControllerSession(axis, hub)], leases));
        await Assert.ThrowsAsync<ArgumentException>(() => runs.StartPlanAsync(new("bad guard", new RouteMatrixStep(new("route"), definition.Device.Id, [First], TimeSpan.Zero)), Token));
        Assert.Equal(0, matrix.Connects); Assert.Equal(0, source.Connects);
    }

    [Fact]
    public async Task CancellationAfterPartialCloseStillOpensAddedConnectionsWithIndependentCleanupToken()
    {
        var b = new Bench(); using var cancelled = new CancellationTokenSource();
        b.Matrix.AfterClose = () => { cancelled.Cancel(); return Task.CompletedTask; };
        await b.Runs.StartPlanAsync(new("cancel close", b.Route("route", [First])), cancelled.Token);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task UncooperativeSourceStopTimeoutNeverReachesRelaysAndCannotOverlapRecovery()
    {
        var b = new Bench();
        var resources = await b.Devices.AcquireRunAsync([b.Source.Id, b.Matrix.Definition.Device.Id], RunId.New(), Token, TimeSpan.FromMilliseconds(40));
        await resources.ConnectAllAsync(Token);
        b.Source.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<AggregateException>(() => resources.RouteMatrixAsync(b.Matrix.Definition.Device.Id, [First], Token));
        Assert.Empty(b.Matrix.Writes); var calls = b.Source.StopCalls;
        Assert.NotEmpty(await resources.StopAllAsync(Token, retryFailed: true));
        Assert.Equal(calls, b.Source.StopCalls); Assert.Empty(b.Matrix.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
        b.Source.StopGate.SetResult(); await b.Source.StopFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Drain the old cancelled attempt, then make a fresh confirmation.
        Assert.NotEmpty(await resources.StopAllAsync(Token));
        Assert.Empty(await resources.StopAllAsync(Token, retryFailed: true)); await resources.ReleaseAsync(Token);
    }

    [Fact]
    public async Task FailedInitialSnapshotNeverFallsBackToOpeningPreexistingConnections()
    {
        var b = new Bench(); b.Matrix.Unknown = true;
        await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartPlanAsync(new("unknown initial", b.Route("route", [First])), Token));
        Assert.Empty(b.Matrix.Writes); Assert.True(b.Runs.HasActiveRun);
        b.Matrix.Unknown = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.Equal(new[] { Original }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.Empty(b.Matrix.Writes); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task PausedMatrixPlanVerifiesChangedRouteEvenWhenExcitationWasAlreadyOff()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var task = b.Runs.StartPlanAsync(new("route hold", new SequenceStep(new("root"), [b.Route("route", [First]),
            new DelayStep(new("wait"), TimeSpan.FromSeconds(30))])), Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, "wait"); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        await b.Matrix.ExternalChangeAsync([Second]); b.Runs.Resume();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("route changed", error.Message); Assert.Equal(0, b.Source.Enables);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task EveryDeclaredExcitationSourceMustConfirmOffBeforeAnyMatrixWrite()
    {
        var hub = new NotificationHub(); var source = new FaultSource(); var secondSource = new FaultSource("source2") { FailStop = true };
        var first = new InstrumentManager(source, hub); var second = new InstrumentManager(secondSource, hub);
        var definition = new MatrixSwitchDefinition(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []),
            [new(1, 4, 6)], [source.Id, secondSource.Id]);
        var matrix = new FaultMatrix(definition, source); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var devices = new DeviceRegistry([first, second, new MatrixSwitchSession(matrix, hub)], leases);
        var runs = new MeasurementRunService(first, leases, hub, deviceRegistry: devices);
        var plan = new ExperimentPlan("two sources", new RouteMatrixStep(new("route"), definition.Device.Id, [First], TimeSpan.Zero));
        Assert.Equal(3, runs.PreviewPlan(plan).Devices.Count);
        await Assert.ThrowsAsync<AggregateException>(() => runs.StartPlanAsync(plan, Token));
        Assert.True(source.StopCalls > 0); Assert.True(secondSource.StopCalls > 0); Assert.Empty(matrix.Writes);
        Assert.True(runs.HasActiveRun); secondSource.FailStop = false; await runs.RecoverSafetyAsync(Token); Assert.False(runs.HasActiveRun);
    }

    [Fact]
    public async Task PartialCloseAndExcitationCleanupFailuresAreBothPreservedUntilRecovery()
    {
        var b = new Bench(); b.Matrix.FailCloseAfterMutation = true;
        b.Matrix.AfterClose = () => { b.Source.FailStop = true; return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartPlanAsync(new("multiple faults", b.Route("route", [First])), Token));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "partial matrix close failed");
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "excitation stop failed");
        Assert.Equal(new[] { First }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.True(b.Runs.HasActiveRun);
        b.Source.FailStop = false; await b.Runs.RecoverSafetyAsync(Token);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ResourceRoutingFreezesCallerListBeforeWaitingForExcitationOff()
    {
        var b = new Bench();
        var resources = await b.Devices.AcquireRunAsync([b.Source.Id, b.Matrix.Definition.Device.Id], RunId.New(), Token);
        await resources.ConnectAllAsync(Token); b.Source.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var desired = new List<MatrixCrossPoint> { First };
        var routing = resources.RouteMatrixAsync(b.Matrix.Definition.Device.Id, desired, Token);
        desired[0] = Second; b.Source.StopGate.SetResult(); await routing;
        Assert.Equal(new[] { First }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.Empty(await resources.StopAllAsync(Token)); await resources.ReleaseAsync(Token);
    }

    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind, string? step = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var fact = await events.Reader.ReadAsync(deadline.Token);
            if (fact.Kind == kind && (step is null || fact.Step?.Value == step)) return;
        }
    }
    private sealed class Bench
    {
        public FaultSource Source { get; } = new();
        public NotificationHub Hub { get; } = new();
        public FaultMatrix Matrix { get; }
        public MatrixSwitchSession Session { get; }
        public DeviceRegistry Devices { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? archive = null, TimeSpan? safetyTimeout = null, bool hardwarePolicy = false)
        {
            var descriptor = new DeviceDescriptor(new("K3706:SIM01"), "K3706", "Matrix", hardwarePolicy ? DriverKind.Hardware : DriverKind.Simulation,
                DeviceStopPolicy.OpenRunAddedConnections, []);
            Matrix = new(new(descriptor, [new(1, 4, 6)], [Source.Id]), Source); Session = new(Matrix, Hub);
            var manager = new InstrumentManager(Source, Hub); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            Devices = new([manager, Session], leases); Runs = new(manager, leases, Hub, archive is null ? [] : [archive], safetyTimeout, Devices);
        }
        public RouteMatrixStep Route(string id, MatrixCrossPoint[] points) => new(new(id), Matrix.Definition.Device.Id, points, TimeSpan.Zero);
        public SourceOutputStep On(string id) => new(new(id), Source.Id, true);
        public ReadResistanceStep Read(string id) => new(new(id), Source.Id, SourceFunction.Voltage, new("voltage"), new("current"), new("resistance"), new(1e-15, PhysicalUnit.Ampere));
        public ExperimentPlan Prepared(ExperimentStep root) => new("matrix R", root, [new(Source.Id, SourceFunction.Voltage,
            new(0.1, PhysicalUnit.Volt), new(0.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
    }
    private sealed class FaultMatrix : IMatrixSwitchPort
    {
        private readonly MockMatrixSwitch inner;
        private readonly FaultSource source;
        public FaultMatrix(MatrixSwitchDefinition definition, FaultSource source)
        {
            Definition = definition; this.source = source;
            inner = new(new(new(definition.Device.Id, "K3706", "Matrix simulation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), definition.Cards, definition.GuardedSources), [Original]);
        }
        public MatrixSwitchDefinition Definition { get; }
        public List<string> Writes { get; } = [];
        public int Connects { get; private set; }
        public int OpenCalls { get; private set; }
        public bool IgnoreOpen { get; set; }
        public bool FailCloseAfterMutation { get; set; }
        public bool Unknown { get; set; }
        public Func<Task>? AfterClose { get; set; }
        public TaskCompletionSource? OpenGate { get; set; }
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token) { Connects++; await inner.ConnectAsync(token); return await GetSnapshotAsync(token); }
        public async Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            var snapshot = await inner.GetSnapshotAsync(token);
            return new(Definition, snapshot.Connected, !Unknown, Unknown ? [] : snapshot.Closed, snapshot.Timestamp);
        }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public async Task OpenAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token)
        {
            OpenCalls++; await RequireSourceOff(); Writes.Add("open:" + string.Join(",", points));
            if (OpenGate is not null) { OpenStarted.TrySetResult(); await OpenGate.Task; token = Token; }
            if (!IgnoreOpen) await inner.OpenAsync(points, token);
            if (OpenGate is not null) OpenFinished.TrySetResult();
        }
        public async Task CloseAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token)
        {
            await RequireSourceOff(); Writes.Add("close:" + string.Join(",", points)); await inner.CloseAsync(points, token);
            if (AfterClose is not null) await AfterClose();
            if (FailCloseAfterMutation) throw new IOException("partial matrix close failed");
        }
        public async Task ExternalChangeAsync(MatrixCrossPoint[] points)
        { await inner.OpenAsync((await inner.GetSnapshotAsync(Token)).Closed, Token); await inner.CloseAsync(points, Token); }
        private async Task RequireSourceOff()
        { if ((await source.GetSnapshotAsync(Token)).OutputEnabled) throw new InvalidOperationException("A relay write was attempted while excitation was on."); }
    }
    private sealed class FaultSource : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner;
        public FaultSource(string id = "k2450") => inner = new(new InstrumentId(id));
        public bool FailStop { get; set; }
        public int StopCalls { get; private set; }
        public TaskCompletionSource? StopGate { get; set; }
        public TaskCompletionSource StopFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Connects { get; private set; }
        public int Enables { get; private set; }
        public Func<Task>? BeforeRead { get; set; }
        public InstrumentId Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) { Connects++; return inner.ConnectAsync(token); }
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint source, CancellationToken token) => inner.ConfigureSourceAsync(source, token);
        public Task ConfigureSenseAsync(SenseConfiguration sense, CancellationToken token) => inner.ConfigureSenseAsync(sense, token);
        public Task EnableOutputAsync(CancellationToken token) { Enables++; return inner.EnableOutputAsync(token); }
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token)
        { if (BeforeRead is not null) await BeforeRead(); return await inner.ReadSourceSenseAsync(token); }
        public async Task SafeStopAsync(CancellationToken token)
        {
            StopCalls++;
            if (FailStop) throw new IOException("excitation stop failed");
            if (StopGate is not null) await StopGate.Task;
            try { await inner.SafeStopAsync(token); }
            finally { StopFinished.TrySetResult(); }
        }
    }
}
