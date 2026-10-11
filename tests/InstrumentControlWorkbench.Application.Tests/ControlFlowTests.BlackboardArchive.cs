using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static readonly JsonSerializerOptions BlackboardJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string SnapshotHash(JsonNode node) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString(BlackboardJson))));

    [Fact]
    public async Task BlackboardArchiveRestoresExactTypedEntriesSourcesTimesAndScopedExports()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blackboard-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunBlackboardSnapshot expected; RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive);
                var scope = new SubtreeStep(new("scope"), [TypedWrite("local", "ready", new BooleanValue(false))], outputs: [new(new("ready"), new("finished"))]);
                var loop = new ForEachStep(new("loop"), new("index"), new ScanValues([Number(1), Number(2)]),
                    new SequenceStep(new("point"), [new MutateValueStep(new("increment"), new("count"), VariableMutation.Add, Number(1)), b.ReadSource("observed")]), indexBase: IterationIndexBase.ZeroBased);
                var plan = b.Powered(new SequenceStep(new("body"), [TypedWrite("sample", "sample", new TextValue("晶圆,\"Si\"\n🧪")),
                    TypedWrite("seed_ready", "ready", new BooleanValue(true)), loop, scope]));
                await b.Runs.StartPlanAsync(plan, Token); expected = b.Runs.LatestBlackboardSnapshot!; id = expected.Id;
                Assert.Equal(id, b.Runs.LatestDataSet!.Id); Assert.Same(expected, b.Runs.LatestDataSet.Capture().Blackboard);
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
                Assert.Same(expected, b.Runs.History.Runs.Single().Blackboard);
            }
            await using var reopened = new FileRunArchive(folder); var metadata = await reopened.ReadMetadataAsync(id, Token); var loaded = await reopened.LoadAsync(id, Token);
            Assert.NotNull(metadata.FinalRecordSha256); Assert.Equal(2, loaded.DataSet.Rows.Count);
            var actual = loaded.DataSet.Blackboard!; Assert.Equal(expected.Id, actual.Id); Assert.Equal(expected.CapturedAt, actual.CapturedAt); Assert.Equal(expected.Elapsed, actual.Elapsed);
            Assert.Equal(expected.Entries.Count, actual.Entries.Count);
            foreach (var item in expected.Entries) Assert.Equal(item.Value, actual.Entries[item.Key]);
            Assert.Equal(BlackboardWriteKind.SubtreeOutput, actual.Entries[new("finished")].Kind);
            Assert.False(Assert.IsType<BlackboardBoolean>(actual.Entries[new("finished")].Value).Value); Assert.True(Assert.IsType<BlackboardBoolean>(actual.Entries[new("ready")].Value).Value);
            Assert.Equal(PhysicalUnit.Ampere, Assert.IsType<BlackboardQuantity>(actual.Entries[new("observed.value")].Value).Value.Unit);
            var csv = Path.Combine(folder, "..", "snapshot-" + id.Value.ToString("N") + ".csv");
            try { await reopened.ExportCsvAsync(id, csv, Token); var text = await File.ReadAllTextAsync(csv); Assert.Contains("# blackboard_json", text); Assert.Contains("SubtreeOutput", text); Assert.Contains("晶圆", text); }
            finally { File.Delete(csv); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("abort")]
    [InlineData("listener")]
    public async Task BlackboardArchivePersistsFaultOrAbortAfterScopeRestoreAndSafeStop(string mode)
    {
        var folder = Path.Combine(Path.GetTempPath(), "blackboard-partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(folder); var b = new Bench(mode == "listener" ? new ArchiveThenRefuse(archive) : archive); using var events = b.Hub.Subscribe();
            ExperimentStep tail = mode == "failure" ? TypedCheck("failure", "ready", new BooleanValue(true)) : new DelayStep(new("pending"), TimeSpan.FromSeconds(30));
            var scope = new SubtreeStep(new("scope"), [TypedWrite("local_label", "sample", new TextValue("local")), TypedWrite("local_flag", "ready", new BooleanValue(false)), b.ReadSource("observed"), tail],
                outputs: [new(new("ready"), new("exported"))]);
            ExperimentStep body = mode == "timeout" ? new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(200), scope) : scope;
            var plan = b.Powered(new SequenceStep(new("body"), [TypedWrite("sample", "sample", new TextValue("parent")), body]));
            var task = b.Runs.StartPlanAsync(plan, Token);
            if (mode == "abort") { await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Abort(); }
            var error = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            if (mode == "abort") Assert.Null(error); else Assert.NotNull(error);
            var loaded = await archive.LoadAsync(b.Runs.LatestDataSet!.Id, Token); var entries = loaded.DataSet.Blackboard!.Entries;
            Assert.Single(loaded.DataSet.Rows); Assert.Equal(mode == "abort" ? MeasurementRunState.Aborted : MeasurementRunState.Faulted, loaded.DataSet.State);
            Assert.Equal("parent", Assert.IsType<BlackboardText>(entries[new("sample")].Value).Value); Assert.Equal(new StepId("sample"), entries[new("sample")].Source);
            Assert.False(entries.ContainsKey(new("ready"))); Assert.False(entries.ContainsKey(new("exported"))); Assert.False(entries.ContainsKey(new("observed.value")));
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class ArchiveThenRefuse(FileRunArchive archive) : IRunResultListener
    {
        public Task OnStartedAsync(RunDataSetSnapshot data, CancellationToken token) => archive.OnStartedAsync(data, token);
        public async Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) { await archive.OnRowAsync(id, row, token); throw new IOException("delivery refused"); }
        public Task OnFinalizedAsync(RunDataSetSnapshot data, CancellationToken token) => archive.OnFinalizedAsync(data, token);
    }
}
