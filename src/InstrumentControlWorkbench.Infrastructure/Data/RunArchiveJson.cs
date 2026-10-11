using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Infrastructure.Data;

// Versioned storage DTOs never become mutable domain or UI state.
internal static partial class RunArchiveJson
{
    internal const int Version = 1;
    internal const int MaximumFrameBytes = 8 * 1024 * 1024;
    internal const string Format = "instrument-workbench-run";
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 512,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<PhysicalUnit>(allowIntegerValues: false),
            new JsonStringEnumConverter<ParameterAccess>(allowIntegerValues: false),
            new JsonStringEnumConverter<MeasurementRunState>(allowIntegerValues: false),
            new JsonStringEnumConverter<SampleTopology>(allowIntegerValues: false),
            new JsonStringEnumConverter<SampleRouteSelection>(allowIntegerValues: false),
            new JsonStringEnumConverter<SampleAnalysisIssues>(allowIntegerValues: false),
            new JsonStringEnumConverter<RoutingRole>(allowIntegerValues: false),
            new JsonStringEnumConverter<EnvironmentRole>(allowIntegerValues: false) },
    };
    private static readonly JsonExperimentPlanDocument Plans = new();

    internal sealed record QuantityDto(double Value, PhysicalUnit Unit)
    {
        public Quantity ToDomain() => new(Value, Unit);
        public static QuantityDto From(Quantity q) => new(q.Value, q.Unit);
    }
    internal sealed record ParameterDto(string Id, string Label, PhysicalUnit Unit, ParameterAccess Access, QuantityDto? Minimum, QuantityDto? Maximum)
    {
        public ParameterDescriptor ToDomain() => new(new(Id), Label, Unit, Access, Minimum?.ToDomain(), Maximum?.ToDomain());
        public static ParameterDto From(ParameterDescriptor p) => new(p.Id.Value, p.Label, p.Unit, p.Access,
            p.Minimum is { } min ? QuantityDto.From(min) : null, p.Maximum is { } max ? QuantityDto.From(max) : null);
    }
    internal sealed record ColumnDto(ParameterDto Parameter, string[] DependsOn, bool Optional, string? Device, string? SourceParameter)
    {
        public DataColumn ToDomain()
        {
            if ((Device is null) != (SourceParameter is null)) throw new FormatException("Column source address is incomplete.");
            return new(Required(Parameter).ToDomain(), Required(DependsOn).Select(id => new ParameterId(id)), Optional,
                Device is null ? null : new ParameterAddress(new(Device), new(SourceParameter!)));
        }
        public static ColumnDto From(DataColumn c) => new(ParameterDto.From(c.Parameter), c.DependsOn.Select(id => id.Value).ToArray(),
            c.Optional, c.Source?.Device.Value, c.Source?.Parameter.Value);
    }
    internal sealed record ParameterValueDto(ParameterDto Parameter, QuantityDto Value);
    internal sealed record InstrumentDto(string Id, string Identifier, DateTimeOffset CapturedAt, ParameterValueDto[] Parameters, Dictionary<string, string> Configuration)
    {
        public InstrumentParameterSnapshot ToDomain() => new(new(Id), Identifier, CapturedAt,
            Required(Parameters).Select(p => new ParameterSnapshot(Required(p.Parameter).ToDomain(), Required(p.Value).ToDomain())), Required(Configuration));
        public static InstrumentDto From(InstrumentParameterSnapshot s) => new(s.InstrumentId.Value, s.Identifier, s.CapturedAt,
            s.Parameters.Select(p => new ParameterValueDto(ParameterDto.From(p.Descriptor), QuantityDto.From(p.Value))).ToArray(), new(s.Configuration));
    }
    internal sealed record MetadataDto(string? SampleId, string? SampleName, string? Notes, Dictionary<string, string> Attributes)
    {
        public RunMetadata ToDomain() => new(SampleId is null ? null : new SampleId(SampleId), SampleName, Notes, Required(Attributes));
        public static MetadataDto From(RunMetadata m) => new(m.SampleId?.Value, m.SampleName, m.Notes, new(m.Attributes));
    }
    internal sealed record HeaderDto(string Format, int Version, Guid Id, string Name, DateTimeOffset StartedAt,
        ColumnDto[] Columns, Dictionary<string, QuantityDto> Recipe, InstrumentDto[] Instruments, string? Plan, MetadataDto Metadata)
    {
        public RunDataSetSnapshot ToDomain()
        {
            if (Format != RunArchiveJson.Format || Version != RunArchiveJson.Version) throw new NotSupportedException("Unsupported run archive format/version.");
            var recipe = Required(Recipe).ToDictionary(p => p.Key, p => Required(p.Value).ToDomain());
            var dataSet = new RunDataSet(new(Id), Name, StartedAt, new(Required(Columns).Select(c => c.ToDomain())), recipe,
                Required(Instruments).Select(i => i.ToDomain()), Plan is null ? null : Plans.Parse(Plan), Required(Metadata).ToDomain());
            return dataSet.Capture();
        }
        public static HeaderDto From(RunDataSetSnapshot s) => new(RunArchiveJson.Format, RunArchiveJson.Version, s.Id.Value, s.Name, s.StartedAt,
            s.Schema.Columns.Select(ColumnDto.From).ToArray(), s.Recipe.ToDictionary(p => p.Key, p => QuantityDto.From(p.Value)),
            s.Instruments.Select(InstrumentDto.From).ToArray(), s.Plan is null ? null : Plans.Serialize(s.Plan), MetadataDto.From(s.Metadata ?? RunMetadata.Empty));
    }
    internal sealed record RowDto(int Index, DateTimeOffset Timestamp, Dictionary<string, QuantityDto> Values,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SampleRouteDto? Routing = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SampleAnalysisDto? Analysis = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MeasurementGroupDto? MeasurementGroup = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnvironmentObservationDto[]? Environment = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AcquisitionQualityDto[]? Quality = null)
    {
        public MeasurementDataRow ToDomain() => new(Timestamp, Required(Values).ToDictionary(p => new ParameterId(p.Key), p => Required(p.Value).ToDomain()), Routing?.ToDomain(), Analysis?.ToDomain(), MeasurementGroup?.ToDomain(), Environment?.Select(o => Required(o).ToDomain()), Quality?.Select(q => Required(q).ToDomain()));
        public static RowDto From(int index, MeasurementDataRow row) => new(index, row.Timestamp,
            row.Values.OrderBy(p => p.Key.Value, StringComparer.Ordinal).ToDictionary(p => p.Key.Value, p => QuantityDto.From(p.Value)), row.Routing is null ? null : SampleRouteDto.From(row.Routing), row.Analysis is null ? null : SampleAnalysisDto.From(row.Analysis), row.MeasurementGroup is null ? null : MeasurementGroupDto.From(row.MeasurementGroup), row.Environment.Count == 0 ? null : row.Environment.Select(EnvironmentObservationDto.From).ToArray(), row.Quality.Count == 0 ? null : row.Quality.Select(AcquisitionQualityDto.From).ToArray());
    }
    internal sealed record AcquisitionQualityDto(string Step, string Device, string Column, DateTimeOffset CapturedAt, ushort? SourceStatus, ushort? MeasurementStatus,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AcquisitionSettingsDto? AcquisitionConfiguration = null)
    {
        public AcquisitionQuality ToDomain()
        {
            if ((SourceStatus is null) != (MeasurementStatus is null)) throw new FormatException("Both buffer quality words must be present or absent.");
            return new(new(Step), new(Device), new(Column), CapturedAt, SourceStatus is { } source ? new(source, MeasurementStatus!.Value) : null, AcquisitionConfiguration?.ToDomain());
        }
        public static AcquisitionQualityDto From(AcquisitionQuality quality) => new(quality.Step.Value, quality.Device.Value, quality.Column.Value, quality.CapturedAt, quality.Status?.Source, quality.Status?.Measurement, quality.AcquisitionConfiguration is { } settings ? AcquisitionSettingsDto.From(settings) : null);
    }
    internal sealed record AcquisitionSettingsDto(bool AutoZero, bool AutorangeRebound, bool AveragingEnabled, int AveragingCount, string AveragingMode)
    {
        public Keithley2450AcquisitionSettings ToDomain() => new(AutoZero, AutorangeRebound, AveragingEnabled, AveragingCount, AveragingMode switch { "Repeat" => Core.Instruments.AveragingMode.Repeat, "Moving" => Core.Instruments.AveragingMode.Moving, _ => throw new FormatException("Unknown averaging mode.") });
        public static AcquisitionSettingsDto From(Keithley2450AcquisitionSettings settings) => new(settings.AutoZero, settings.AutorangeRebound, settings.AveragingEnabled, settings.AveragingCount, settings.AveragingMode.ToString());
    }
    internal sealed record EnvironmentObservationDto(EnvironmentRole Role, string Column, string? Device, string? Parameter,
        DateTimeOffset CapturedAt, QuantityDto? Value, string? Error)
    {
        public EnvironmentObservation ToDomain()
        {
            if ((Device is null) != (Parameter is null)) throw new FormatException("Environment source address is incomplete.");
            return new(Role, new(Column), Device is null ? null : new(new(Device), new(Parameter!)), CapturedAt, Value?.ToDomain(), Error);
        }
        public static EnvironmentObservationDto From(EnvironmentObservation observation) => new(observation.Role, observation.Column.Value,
            observation.Source?.Device.Value, observation.Source?.Parameter.Value, observation.CapturedAt,
            observation.Value is { } value ? QuantityDto.From(value) : null, observation.Error);
    }
    internal sealed record SampleAnalysisDto(string Step, int FirstInputRow, int InputCount, SampleAnalysisIssues Issues, int AlgorithmVersion)
    {
        public SampleAnalysisSummary ToDomain() => new(new(Step), FirstInputRow, InputCount, Issues, AlgorithmVersion);
        public static SampleAnalysisDto From(SampleAnalysisSummary summary) => new(summary.Step.Value, summary.FirstInputRow, summary.InputCount, summary.Issues, summary.AlgorithmVersion);
    }
    internal sealed record MeasurementGroupInputDto(string Label, int FirstInputRow, int InputCount);
    internal sealed record MeasurementGroupDto(string Step, MeasurementGroupInputDto[] Inputs)
    {
        public MeasurementGroupSummary ToDomain() => new(new(Step), Required(Inputs).Select(i => new MeasurementGroupInput(Required(i).Label, i.FirstInputRow, i.InputCount)));
        public static MeasurementGroupDto From(MeasurementGroupSummary group) => new(group.Step.Value, group.Inputs.Select(i => new MeasurementGroupInputDto(i.Label, i.FirstInputRow, i.InputCount)).ToArray());
    }
    internal sealed record FinalDto(string Format, int Version, Guid Id, MeasurementRunState State, DateTimeOffset FinishedAt, int RowCount, long DataBytes, string Sha256);

    internal static byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumFrameBytes) throw new IOException("Archive frame exceeds 8 MiB.");
        return bytes;
    }
    internal static T Decode<T>(ReadOnlySpan<byte> bytes) where T : class
    {
        // System.Text.Json DTO binding rejects unknown fields. Explicit duplicate
        // detection prevents ambiguous identity/state fields from being last-wins.
        using var doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 512 });
        CheckDuplicates(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new FormatException("Missing archive object.");
    }
    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw new FormatException($"Duplicate archive field '{p.Name}'.");
                CheckDuplicates(p.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) CheckDuplicates(item);
    }
    private static T Required<T>(T? value) where T : class => value ?? throw new FormatException("Missing required archive field.");
}
