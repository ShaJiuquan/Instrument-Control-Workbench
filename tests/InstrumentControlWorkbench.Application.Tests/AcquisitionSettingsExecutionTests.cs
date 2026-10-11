using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
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
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class AcquisitionSettingsExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(SenseFunction.Current, AveragingMode.Repeat)]
    [InlineData(SenseFunction.Current, AveragingMode.Moving)]
    [InlineData(SenseFunction.Voltage, AveragingMode.Repeat)]
    [InlineData(SenseFunction.Voltage, AveragingMode.Moving)]
    [InlineData(SenseFunction.Resistance, AveragingMode.Repeat)]
    [InlineData(SenseFunction.Resistance, AveragingMode.Moving)]
    public async Task ProfileAndPerReadingConfigurationSurviveNativePlanFreshArchiveAndCsv(SenseFunction function, AveragingMode mode)
    {
        var folder = Path.Combine(Path.GetTempPath(), "acquisition-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(peer, archive);
            var settings = new Keithley2450AcquisitionSettings(false, function != SenseFunction.Resistance, true, 3, mode);
            var prep = b.Preparation(function, settings); var plan = b.Plan(prep, b.Read(function)); var doc = new JsonExperimentPlanDocument();
            await doc.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); var loaded = await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token); Assert.Equal(settings, loaded.Preparations[0].Sense.Acquisition);
            await b.Runs.StartPlanAsync(loaded, Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
            Assert.Equal(settings, (await b.Source.GetSnapshotAsync(Token)).Sense.Acquisition); Assert.Equal(settings, Assert.Single(row.Quality).AcquisitionConfiguration); Assert.Equal(ReadingQualityState.ObservedNormal, row.QualityState);
            var snapshot = Assert.Single(b.Runs.LatestDataSet.Instruments); Assert.Equal(new(3, PhysicalUnit.Dimensionless), Assert.Single(snapshot.Parameters, p => p.Descriptor.Id == Keithley2450Parameters.AveragingCount.Id).Value);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token); Assert.Equal(row.Quality.ToArray(), Assert.Single(saved.DataSet.Rows).Quality.ToArray());
            await fresh.ExportCsvAsync(saved.DataSet.Id, Path.Combine(folder, "data.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")); Assert.Contains("acquisitionConfiguration", csv); Assert.Contains(mode.ToString(), csv);
            if (function == SenseFunction.Resistance) Assert.DoesNotContain(peer.Server.Commands, c => c.Contains(":RES:RANG:AUTO:REB", StringComparison.Ordinal));
            Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task MovingFilterIsNotReconfiguredBetweenUnchangedSingleReads()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var settings = new Keithley2450AcquisitionSettings(false, true, true, 4, AveragingMode.Moving);
        await b.Runs.StartPlanAsync(b.Plan(b.Preparation(SenseFunction.Current, settings), b.Read(SenseFunction.Current), b.Read(SenseFunction.Current, "read2")), Token);
        var commands = peer.Server.Commands.ToArray(); var first = Array.FindIndex(commands, c => c.StartsWith(":MEAS?", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Skip(first + 1), c => !c.EndsWith("?", StringComparison.Ordinal) && (c.StartsWith(":SENS:FUNC", StringComparison.Ordinal) || c.Contains(":AVER:", StringComparison.Ordinal) || c.Contains(":NPLC", StringComparison.Ordinal)));
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, r => Assert.Equal(settings, Assert.Single(r.Quality).AcquisitionConfiguration)); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task DynamicOwnedCountChangesKeepActualPerRowConfigurationSeparateFromInitialSnapshot()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var initial = new Keithley2450AcquisitionSettings(true, true, true, 1, AveragingMode.Repeat);
        var scan = new ScanStep(new("counts"), new(b.Source.Id, Keithley2450Parameters.AveragingCount.Id), new("count_axis"), new([new(2, PhysicalUnit.Dimensionless), new(3, PhysicalUnit.Dimensionless)]), TimeSpan.Zero, b.Read(SenseFunction.Current));
        await b.Runs.StartPlanAsync(b.Plan(b.Preparation(SenseFunction.Current, initial), scan), Token);
        Assert.Equal(new[] { 2, 3 }, b.Runs.LatestDataSet!.Rows.Select(r => Assert.Single(r.Quality).AcquisitionConfiguration!.AveragingCount));
        Assert.Equal(new(1, PhysicalUnit.Dimensionless), Assert.Single(b.Runs.LatestDataSet.Instruments[0].Parameters, p => p.Descriptor.Id == Keithley2450Parameters.AveragingCount.Id).Value);
        Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(0.5, false)]
    [InlineData(0.5, true)]
    [InlineData(100.5, false)]
    [InlineData(100.5, true)]
    public async Task InvalidLiteralOrDynamicCountCannotReachTheFilterWrite(double value, bool dynamic)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var steps = new List<ExperimentStep>();
        if (dynamic) steps.Add(new AssignValueStep(new("assign"), new("count"), new ConstantValue(new(value, PhysicalUnit.Dimensionless))));
        steps.Add(new SetParameterStep(new("invalid"), new(b.Source.Id, Keithley2450Parameters.AveragingCount.Id), dynamic ? new VariableValue(new("count")) : new ConstantValue(new(value, PhysicalUnit.Dimensionless))));
        var plan = b.Plan(b.Preparation(SenseFunction.Current), steps.ToArray());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.DoesNotContain(peer.Server.Commands, c => c.Contains(":AVER:COUN ", StringComparison.Ordinal));
        if (!dynamic) Assert.Equal(0, peer.Server.Connections); Assert.False(peer.Output); if (peer.Server.Connections != 0) await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(":SENS:CURR:AVER:COUN?", "0")]
    [InlineData(":SENS:CURR:AVER:COUN?", "10.5")]
    [InlineData(":SENS:CURR:AVER:COUN?", "101")]
    [InlineData(":SENS:CURR:AVER:TCON?", "bad")]
    [InlineData(":SENS:CURR:AVER:STAT?", "2")]
    [InlineData(":SENS:CURR:AZER?", "2")]
    [InlineData(":SENS:CURR:RANG:AUTO:REB?", "2")]
    public async Task InvalidObservedSettingsCannotBecomeDefaultConfiguration(string query, string reply)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); peer.Override = (command, _) => command == query ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(reply)) : null;
        await Assert.ThrowsAsync<IOException>(() => b.Source.ConnectAsync(Token)); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.All(peer.Server.Commands, c => Assert.EndsWith("?", c));
    }
    [Fact]
    public async Task UnspecifiedProfileRetainsTheTargetFunctionsActualRegisters()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var voltage = new Keithley2450AcquisitionSettings(false, true, true, 17, AveragingMode.Moving); peer.SetAcquisition(SenseFunction.Voltage, voltage);
        await b.Runs.StartPlanAsync(b.Plan(b.Preparation(SenseFunction.Current), b.Read(SenseFunction.Voltage)), Token);
        Assert.Equal(voltage, Assert.Single(Assert.Single(b.Runs.LatestDataSet!.Rows).Quality).AcquisitionConfiguration);
        Assert.DoesNotContain(peer.Server.Commands, c => c.StartsWith(":SENS:VOLT:AVER:", StringComparison.Ordinal) && !c.EndsWith("?", StringComparison.Ordinal)); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task FailedLiveSettingAndRefusedOffRetainOwnershipUntilExplicitRecovery()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        peer.Override = (command, _) => { if (command == ":OUTP ON") { peer.IgnoreAcquisitionWrites = true; peer.RefuseOff = true; } return null; };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Preparation(SenseFunction.Current), new SetParameterStep(new("change"), new(b.Source.Id, Keithley2450Parameters.AveragingCount.Id), new ConstantValue(new(20, PhysicalUnit.Dimensionless)))), Token));
        Assert.True(peer.Output); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow.AddYears(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Manager.WriteParameterAsync(Keithley2450Parameters.AutoZero.Id, new(0, PhysicalUnit.Dimensionless), Token));
        peer.RefuseOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task DriftDuringReadCannotMasqueradeAsThePreparedProfile()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var changed = new Keithley2450AcquisitionSettings(false, false, false, 10, AveragingMode.Repeat);
        peer.Override = (command, _) => { if (command.StartsWith(":MEAS?", StringComparison.Ordinal)) peer.SetAcquisition(SenseFunction.Current, changed); return null; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Preparation(SenseFunction.Current), b.Read(SenseFunction.Current)), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task PausedProfileDriftDeniesOutputRestoration()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(b.Preparation(SenseFunction.Current), b.Read(SenseFunction.Current), new DelayStep(new("wait"), TimeSpan.FromMinutes(1))), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); var outputCommands = peer.Server.Commands.Count(c => c == ":OUTP ON");
        peer.SetAcquisition(SenseFunction.Current, new(false, false, false, 10, AveragingMode.Repeat)); b.Runs.Resume(); await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        Assert.Equal(outputCommands, peer.Server.Commands.Count(c => c == ":OUTP ON")); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroRefreshIsAnOffOnlyCompletedActionAndNeverAPersistentMode(bool live)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(SenseFunction.Current, new(false, true, false, 10, AveragingMode.Repeat));
        var refresh = new SetParameterStep(new("zero"), new(b.Source.Id, Keithley2450Parameters.RefreshZero.Id), new ConstantValue(new(1, PhysicalUnit.Dimensionless)));
        var plan = live ? b.Plan(prep, refresh) : new ExperimentPlan("refresh", new SequenceStep(new("root"), [refresh, new SourceOutputStep(new("on"), b.Source.Id, true), b.Read(SenseFunction.Current)]), [prep]);
        if (live) { await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(0, peer.ZeroRefreshes); }
        else { await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(1, peer.ZeroRefreshes); Assert.Contains("*OPC?", peer.Server.Commands); Assert.False((await b.Source.GetSnapshotAsync(Token)).Sense.Acquisition!.AutoZero); }
        Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task ZeroRefreshWithoutCompletionProofFaultsBeforeOutputEnable()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); peer.Override = (command, _) => command == "*OPC?" ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("0")) : null;
        var refresh = new SetParameterStep(new("zero"), new(b.Source.Id, Keithley2450Parameters.RefreshZero.Id), new ConstantValue(new(1, PhysicalUnit.Dimensionless)));
        var plan = new ExperimentPlan("refresh failure", new SequenceStep(new("root"), [refresh, new SourceOutputStep(new("on"), b.Source.Id, true)]), [b.Preparation(SenseFunction.Current)]);
        await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.DoesNotContain(":OUTP ON", peer.Server.Commands); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != kind) { }
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public TcpKeithley2450SourceMeter Source { get; }
        public InstrumentManager Manager { get; }
        public MeasurementRunService Runs { get; }
        public DeviceRegistry Registry { get; }
        public Bench(Keithley2450ScpiPeer peer, IRunResultListener? listener = null)
        {
            Source = new(new("acquisition-smu"), "TCP acquisition", peer.Server.Configuration()); Manager = new(Source, Hub); Registry = new([Manager], Leases);
            Runs = new(Manager, Leases, Hub, listener is null ? null : [listener], deviceRegistry: Registry);
        }
        public SourcePreparation Preparation(SenseFunction function, Keithley2450AcquisitionSettings? settings = null) => new(Source.Id, function == SenseFunction.Voltage ? SourceFunction.Current : SourceFunction.Voltage,
            new(0.02, function == SenseFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(function == SenseFunction.Voltage ? 21 : 105e-6, function == SenseFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere), new(function, SensingMode.FourWire, 1, true, Acquisition: settings));
        public ReadStep Read(SenseFunction function, string id = "read") => new(new(id), [new(new(id + ".value"), new(Source.Id, function switch { SenseFunction.Voltage => Keithley2450Parameters.MeasuredVoltage.Id, SenseFunction.Current => Keithley2450Parameters.MeasuredCurrent.Id, _ => Keithley2450Parameters.MeasuredResistance.Id }))]);
        public ExperimentPlan Plan(SourcePreparation preparation, params ExperimentStep[] steps) => new("acquisition", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), .. steps]), [preparation]);
    }
}
