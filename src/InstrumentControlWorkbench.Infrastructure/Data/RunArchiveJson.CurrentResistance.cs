using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Data;

internal static partial class RunArchiveJson
{
    internal sealed record MatrixPointDto(int Slot, int Row, int Column);
    internal sealed record CurrentRouteDto(string Device, MatrixPointDto[] Closed)
    {
        public MatrixRouteExpectation ToDomain() => new(new(Device), Required(Closed).Select(p => new MatrixCrossPoint(Required(p).Slot, p.Row, p.Column)));
        public static CurrentRouteDto From(MatrixRouteExpectation route) => new(route.Device.Value, route.Closed.Select(p => new MatrixPointDto(p.Slot, p.Row, p.Column)).ToArray());
    }
    internal sealed record CurrentSourceConfigurationDto(double Level, double Compliance, QuantityDto? ManualRange,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Model = null)
    {
        public CurrentExcitationConfiguration ToDomain() => new(Model switch { null or "Keithley2450" => CurrentSourceModel.Keithley2450, "Keithley6221" => CurrentSourceModel.Keithley6221, _ => throw new FormatException("Unknown current excitation model.") }, new(Level, PhysicalUnit.Ampere), new(Compliance, PhysicalUnit.Volt), ManualRange?.ToDomain());
        public static CurrentSourceConfigurationDto From(CurrentExcitationConfiguration source) => new(source.Level, source.Compliance, source.ManualRange is { } span ? QuantityDto.From(span) : null, source.Model.ToString());
    }
    internal sealed record CurrentRetryDto(string Kind, int? CommandCode, string Message, DateTimeOffset AttemptStartedAt, DateTimeOffset FailedAt, DateTimeOffset IdleConfirmedAt, DateTimeOffset? SourceOffConfirmedAt, MeterSettingsDto MeterSettings)
    {
        public CurrentResistanceRetryEvidence ToDomain() => new(Kind switch
        {
            "ReadTimeout" => InstrumentReadFailureKind.ReadTimeout,
            "BufferTimeout" => InstrumentReadFailureKind.BufferTimeout,
            "CommandRejected" => InstrumentReadFailureKind.CommandRejected,
            _ => throw new FormatException("Unknown or ineligible retry failure kind.")
        }, CommandCode, Message, AttemptStartedAt, FailedAt, IdleConfirmedAt, SourceOffConfirmedAt, Required(MeterSettings).ToDomain());
        public static CurrentRetryDto From(CurrentResistanceRetryEvidence retry) => new(retry.Kind.ToString(), retry.CommandCode, retry.Message, retry.AttemptStartedAt, retry.FailedAt, retry.IdleConfirmedAt, retry.SourceOffConfirmedAt, MeterSettingsDto.From(retry.MeterSettings));
    }
    internal sealed record IndependentResistanceDto(string Step, string Source, string Meter, string CurrentReference,
        string VoltageColumn, string CurrentColumn, string ResistanceColumn, QuantityDto MinimumCurrent, CurrentRouteDto? Route,
        CurrentSourceConfigurationDto SourceConfiguration, QuantityDto Current, QuantityDto Voltage,
        DateTimeOffset SourceCapturedAt, DateTimeOffset VoltageCapturedAt, DateTimeOffset SourceConfirmedBefore, DateTimeOffset SourceConfirmedAfter,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CurrentRetryDto? Retry = null)
    {
        public IndependentResistanceEvidence ToDomain() => new(new ReadCurrentResistanceStep(new(Step), new(Source), new(Meter), CurrentReference switch
        {
            "ProgrammedSetpoint" => Core.Experiments.CurrentReference.ProgrammedSetpoint,
            "MeasuredSourceReadback" => Core.Experiments.CurrentReference.MeasuredSourceReadback,
            _ => throw new FormatException("Independent current reference must be a named acquisition mode.")
        },
            new(VoltageColumn), new(CurrentColumn), new(ResistanceColumn), Required(MinimumCurrent).ToDomain(), requiredRoute: Route?.ToDomain(), retry: Retry is null ? null : new CurrentResistanceRetry(Retry.SourceOffConfirmedAt is not null, TimeSpan.Zero)),
            Required(SourceConfiguration).ToDomain(), Required(Current).ToDomain(), Required(Voltage).ToDomain(), SourceCapturedAt, VoltageCapturedAt, SourceConfirmedBefore, SourceConfirmedAfter, Retry?.ToDomain());
        public static IndependentResistanceDto From(IndependentResistanceEvidence evidence) => new(evidence.Step.Value, evidence.Source.Value, evidence.Meter.Value, evidence.CurrentReference.ToString(),
            evidence.VoltageColumn.Value, evidence.CurrentColumn.Value, evidence.ResistanceColumn.Value, QuantityDto.From(evidence.MinimumCurrent), evidence.Route is { } route ? CurrentRouteDto.From(route) : null,
            CurrentSourceConfigurationDto.From(evidence.SourceConfiguration), QuantityDto.From(evidence.Current), QuantityDto.From(evidence.Voltage), evidence.SourceCapturedAt, evidence.VoltageCapturedAt, evidence.SourceConfirmedBefore, evidence.SourceConfirmedAfter, evidence.Retry is { } retry ? CurrentRetryDto.From(retry) : null);
    }
}
