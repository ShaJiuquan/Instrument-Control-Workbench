using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Infrastructure.Data;

public sealed partial class FileRunArchive
{
    public async Task ExportCsvAsync(RunId id, string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var metadata = await ReadMetadataAsync(id, token);
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var archivePrefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(archivePrefix, comparison) || string.Equals(fullPath, directory, comparison))
            throw new ArgumentException("Export destination cannot replace files inside the source archive.", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        Exception? error = null;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                var header = metadata.Header;
                var provenance = new RunDataRowValidator(header.Plan, header.Schema);
                var hasCalculations = provenance.HasCalculations;
                var hasMeasurementGroups = provenance.HasMeasurementGroups;
                var hasEnvironment = provenance.HasEnvironment;
                var hasAcquisitionFailures = provenance.HasRecoverableAcquisitions;
                var hasIndependentResistance = provenance.HasIndependentResistance;
                var hasAnalysis = new SampleAnalysisRowValidator(header.Plan, header.Schema).HasGroups;
                await WriteCsvAsync(writer, ["# format", "instrument-workbench-csv", "1"], token);
                await WriteCsvAsync(writer, ["# run_id", id.ToString()], token);
                await WriteCsvAsync(writer, ["# name", header.Name], token);
                await WriteCsvAsync(writer, ["# state", metadata.IsFinalized ? header.State.ToString() : "Unfinalized"], token);
                await WriteCsvAsync(writer, ["# started_at", header.StartedAt.ToString("O", CultureInfo.InvariantCulture)], token);
                await WriteCsvAsync(writer, ["# finished_at", header.FinishedAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""], token);
                await WriteCsvAsync(writer, ["# metadata_json", JsonSerializer.Serialize(RunArchiveJson.MetadataDto.From(header.Metadata ?? RunMetadata.Empty), RunArchiveJson.Options)], token);
                await WriteCsvAsync(writer, ["# recipe_json", JsonSerializer.Serialize(header.Recipe.ToDictionary(p => p.Key, p => RunArchiveJson.QuantityDto.From(p.Value)), RunArchiveJson.Options)], token);
                await WriteCsvAsync(writer, ["# instruments_json", JsonSerializer.Serialize(header.Instruments.Select(RunArchiveJson.InstrumentDto.From), RunArchiveJson.Options)], token);
                await WriteCsvAsync(writer, ["# columns_json", JsonSerializer.Serialize(header.Schema.Columns.Select(RunArchiveJson.ColumnDto.From), RunArchiveJson.Options)], token);
                await WriteCsvAsync(writer, ["# plan_json", header.Plan is null ? "" : new JsonExperimentPlanDocument().Serialize(header.Plan)], token);
                await WriteCsvAsync(writer, ["# blackboard_json", header.Blackboard is null ? "" : JsonSerializer.Serialize(RunArchiveJson.BlackboardDto.From(header.Blackboard), RunArchiveJson.Options)], token);
                await WriteCsvAsync(writer, ["row", "timestamp", .. header.Schema.Columns.Select(c => $"{c.Parameter.Id} [{c.Parameter.Unit}]"),
                    .. header.Plan?.Routing is null ? Array.Empty<string>() : new[] { "routing_json" },
                    .. hasCalculations ? new[] { "calculation_json" } : Array.Empty<string>(),
                    .. hasAnalysis ? new[] { "analysis_json" } : Array.Empty<string>(),
                    .. hasMeasurementGroups ? new[] { "measurement_group_json" } : Array.Empty<string>(),
                    .. hasEnvironment ? new[] { "environment_json" } : Array.Empty<string>(),
                    .. header.Plan is not null ? new[] { "quality_json" } : Array.Empty<string>(),
                    .. hasIndependentResistance ? new[] { "independent_resistance_json" } : Array.Empty<string>(),
                    .. hasAcquisitionFailures ? new[] { "acquisition_failures_json" } : Array.Empty<string>()], token);
                var count = 0;
                var cursor = new ReadCursor();
                await foreach (var row in ReadRowsAsync(metadata, cursor, token))
                    await WriteCsvAsync(writer, [(++count).ToString(CultureInfo.InvariantCulture), row.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                        .. header.Schema.Columns.Select(c => row.Values.TryGetValue(c.Parameter.Id, out var value)
                            ? value.Value.ToString("R", CultureInfo.InvariantCulture) : ""),
                        .. header.Plan?.Routing is null ? Array.Empty<string>() : new[] { row.Routing is null ? "" : JsonSerializer.Serialize(RunArchiveJson.SampleRouteDto.From(row.Routing), RunArchiveJson.Options) },
                        .. hasCalculations ? new[] { row.Calculation is null ? "" : JsonSerializer.Serialize(RunArchiveJson.CalculationDto.From(row.Calculation), RunArchiveJson.Options) } : Array.Empty<string>(),
                        .. hasAnalysis ? new[] { row.Analysis is null ? "" : JsonSerializer.Serialize(RunArchiveJson.SampleAnalysisDto.From(row.Analysis), RunArchiveJson.Options) } : Array.Empty<string>(),
                        .. hasMeasurementGroups ? new[] { row.MeasurementGroup is null ? "" : JsonSerializer.Serialize(RunArchiveJson.MeasurementGroupDto.From(row.MeasurementGroup), RunArchiveJson.Options) } : Array.Empty<string>(),
                        .. hasEnvironment ? new[] { row.Environment.Count == 0 ? "" : JsonSerializer.Serialize(row.Environment.Select(RunArchiveJson.EnvironmentObservationDto.From), RunArchiveJson.Options) } : Array.Empty<string>(),
                        .. header.Plan is not null ? new[] { row.Quality.Count == 0 ? "" : JsonSerializer.Serialize(row.Quality.Select(RunArchiveJson.AcquisitionQualityDto.From), RunArchiveJson.Options) } : Array.Empty<string>(),
                        .. hasIndependentResistance ? new[] { row.IndependentResistance.Count == 0 ? "" : JsonSerializer.Serialize(row.IndependentResistance.Select(RunArchiveJson.IndependentResistanceDto.From), RunArchiveJson.Options) } : Array.Empty<string>(),
                        .. hasAcquisitionFailures ? new[] { row.AcquisitionFailures.Count == 0 ? "" : JsonSerializer.Serialize(row.AcquisitionFailures.Select(RunArchiveJson.IndependentFailureDto.From), RunArchiveJson.Options) } : Array.Empty<string>()], token);
                if (cursor.HasIncompleteTail) await WriteCsvAsync(writer, ["# incomplete_tail", "The unfinished last record was excluded."], token);
                await writer.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { error = error is null ? ex : new AggregateException(error, ex); }
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static Task WriteCsvAsync(StreamWriter writer, IEnumerable<string> fields, CancellationToken token)
        => writer.WriteLineAsync(string.Join(',', fields.Select(field => $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"")).AsMemory(), token);
}
