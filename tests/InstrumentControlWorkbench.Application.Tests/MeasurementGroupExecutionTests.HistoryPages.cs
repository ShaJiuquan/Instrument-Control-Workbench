using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class MeasurementGroupExecutionTests
{
    [Fact]
    public async Task SummaryOnlyPageValidatesSkippedInputsAndRetainsFullTypedEvidence()
    {
        var folder = Path.Combine(Path.GetTempPath(), "group-page-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id; MeasurementDataRow expected;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive); await b.Runs.StartPlanAsync(NativePlan(b), Token); id = b.Runs.LatestDataSet!.Id; expected = b.Runs.LatestDataSet.Rows[2];
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
            }
            await using var reader = new FileRunArchive(folder); var page = await reader.ReadPageAsync(id, new(2, 1), Token); var row = Assert.Single(page.Rows);
            Assert.Equal(3, page.ObservedRowCount); Assert.Equal(3, page.FirstRowIndex); Assert.False(page.HasMore);
            Assert.Equal(expected.Timestamp, row.Timestamp); Assert.Equal(expected.Values, row.Values); Assert.Equal(expected.Quality, row.Quality); Assert.Equal(expected.Environment, row.Environment);
            Assert.Equal("CH1#2", row.MeasurementGroup!.Inputs[1].Label); Assert.Equal(1, row.MeasurementGroup.Inputs[0].FirstInputRow);
            Assert.Equal(PhysicalUnit.Volt, row.Values[new("first.V")].Unit); Assert.NotNull(page.Metadata.Header.Plan); Assert.NotNull(page.Metadata.Header.Blackboard);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("input")]
    [InlineData("summary")]
    public async Task PagedGroupReadRejectsRehashedEvidenceMismatchOutsideRequestedRows(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "group-page-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder)) { var b = new Bench(archive); await b.Runs.StartPlanAsync(NativePlan(b), Token); id = b.Runs.LatestDataSet!.Id; }
            var run = Path.Combine(folder, id.Value.ToString("N")); var path = Path.Combine(run, "rows.ndjson"); var lines = await File.ReadAllLinesAsync(path);
            var index = damage == "input" ? 0 : 2; var json = JsonNode.Parse(lines[index])!; json["values"]!["first.V"]!["value"] = 0.3; lines[index] = json.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"); await File.WriteAllBytesAsync(path, bytes);
            var finalPath = Path.Combine(run, "final.json"); var final = JsonNode.Parse(await File.ReadAllTextAsync(finalPath))!; final["dataBytes"] = bytes.Length; final["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)); await File.WriteAllTextAsync(finalPath, final.ToJsonString());
            await using var reader = new FileRunArchive(folder);
            Assert.NotNull(await Record.ExceptionAsync(() => reader.ReadPageAsync(id, new(damage == "input" ? 2 : 0, 1), Token)));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
