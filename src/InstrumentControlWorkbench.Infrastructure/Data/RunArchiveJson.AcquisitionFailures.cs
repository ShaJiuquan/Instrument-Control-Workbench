using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Data;

internal static partial class RunArchiveJson
{
    internal sealed record IndependentFailureDto(string Step, string Source, string Meter, string CurrentReference,
        string VoltageColumn, string CurrentColumn, string ResistanceColumn, QuantityDto MinimumCurrent, CurrentRouteDto? Route,
        CurrentSourceConfigurationDto SourceConfiguration, string Kind, int? CommandCode, string Message, string Status, MeterSettingsDto MeterSettings,
        DateTimeOffset SourceConfirmedBefore, DateTimeOffset AttemptStartedAt, DateTimeOffset FailedAt, DateTimeOffset SourceConfirmedAfter,
        DateTimeOffset IdleConfirmedAt, DateTimeOffset? SourceOffConfirmedAt, CurrentRetryDto? Retry = null)
    {
        public IndependentResistanceFailure ToDomain()
        {
            var step = new ReadCurrentResistanceStep(new(Step), new(Source), new(Meter), CurrentReference switch
            {
                "ProgrammedSetpoint" => Core.Experiments.CurrentReference.ProgrammedSetpoint,
                "MeasuredSourceReadback" => Core.Experiments.CurrentReference.MeasuredSourceReadback,
                _ => throw new FormatException("Unknown failed current reference."),
            }, new(VoltageColumn), new(CurrentColumn), new(ResistanceColumn), Required(MinimumCurrent).ToDomain(), requiredRoute: Route?.ToDomain(), retry: new(SourceOffConfirmedAt is not null, TimeSpan.Zero, true));
            var kind = Kind switch
            {
                "ReadTimeout" => InstrumentReadFailureKind.ReadTimeout,
                "BufferTimeout" => InstrumentReadFailureKind.BufferTimeout,
                "CommandRejected" => InstrumentReadFailureKind.CommandRejected,
                "Overrange" => InstrumentReadFailureKind.Overrange,
                "InvalidResponse" => InstrumentReadFailureKind.InvalidResponse,
                "VisaIoRead" => InstrumentReadFailureKind.VisaIoRead,
                _ => throw new FormatException("Unknown failed acquisition kind."),
            };
            var failure = new IndependentResistanceFailure(step, Required(SourceConfiguration).ToDomain(), kind, CommandCode, Message, Required(MeterSettings).ToDomain(),
                SourceConfirmedBefore, AttemptStartedAt, FailedAt, SourceConfirmedAfter, IdleConfirmedAt, SourceOffConfirmedAt, Retry?.ToDomain());
            if (failure.Status != Status) throw new FormatException("Failure status differs from its typed error.");
            return failure;
        }
        public static IndependentFailureDto From(IndependentResistanceFailure failure) => new(failure.Step.Value, failure.Source.Value, failure.Meter.Value, failure.CurrentReference.ToString(),
            failure.VoltageColumn.Value, failure.CurrentColumn.Value, failure.ResistanceColumn.Value, QuantityDto.From(failure.MinimumCurrent), failure.Route is { } route ? CurrentRouteDto.From(route) : null,
            CurrentSourceConfigurationDto.From(failure.SourceConfiguration), failure.Kind.ToString(), failure.CommandCode, failure.Message, failure.Status, MeterSettingsDto.From(failure.MeterSettings),
            failure.SourceConfirmedBefore, failure.AttemptStartedAt, failure.FailedAt, failure.SourceConfirmedAfter, failure.IdleConfirmedAt, failure.SourceOffConfirmedAt, failure.Retry is { } retry ? CurrentRetryDto.From(retry) : null);
    }
}
