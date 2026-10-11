using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Infrastructure.Data;

internal static partial class RunArchiveJson
{
    internal sealed record MatrixPointDto(int Slot, int Row, int Column);
    internal sealed record CurrentRouteDto(string Device, MatrixPointDto[] Closed)
    {
        public MatrixRouteExpectation ToDomain() => new(new(Device), Required(Closed).Select(p => new MatrixCrossPoint(Required(p).Slot, p.Row, p.Column)));
        public static CurrentRouteDto From(MatrixRouteExpectation route) => new(route.Device.Value, route.Closed.Select(p => new MatrixPointDto(p.Slot, p.Row, p.Column)).ToArray());
    }
    internal sealed record CurrentSourceConfigurationDto(double Level, double Compliance, QuantityDto? ManualRange)
    {
        public SourceSetpoint ToDomain() => new(SourceFunction.Current, Level, Compliance, ManualRange?.ToDomain());
        public static CurrentSourceConfigurationDto From(SourceSetpoint source) => new(source.Level, source.Compliance, source.ManualRange is { } span ? QuantityDto.From(span) : null);
    }
    internal sealed record IndependentResistanceDto(string Step, string Source, string Meter, string CurrentReference,
        string VoltageColumn, string CurrentColumn, string ResistanceColumn, QuantityDto MinimumCurrent, CurrentRouteDto? Route,
        CurrentSourceConfigurationDto SourceConfiguration, QuantityDto Current, QuantityDto Voltage,
        DateTimeOffset SourceCapturedAt, DateTimeOffset VoltageCapturedAt, DateTimeOffset SourceConfirmedBefore, DateTimeOffset SourceConfirmedAfter)
    {
        public IndependentResistanceEvidence ToDomain() => new(new ReadCurrentResistanceStep(new(Step), new(Source), new(Meter), CurrentReference switch
        {
            "ProgrammedSetpoint" => Core.Experiments.CurrentReference.ProgrammedSetpoint,
            "MeasuredSourceReadback" => Core.Experiments.CurrentReference.MeasuredSourceReadback,
            _ => throw new FormatException("Independent current reference must be a named acquisition mode.")
        },
            new(VoltageColumn), new(CurrentColumn), new(ResistanceColumn), Required(MinimumCurrent).ToDomain(), requiredRoute: Route?.ToDomain()),
            Required(SourceConfiguration).ToDomain(), Required(Current).ToDomain(), Required(Voltage).ToDomain(), SourceCapturedAt, VoltageCapturedAt, SourceConfirmedBefore, SourceConfirmedAfter);
        public static IndependentResistanceDto From(IndependentResistanceEvidence evidence) => new(evidence.Step.Value, evidence.Source.Value, evidence.Meter.Value, evidence.CurrentReference.ToString(),
            evidence.VoltageColumn.Value, evidence.CurrentColumn.Value, evidence.ResistanceColumn.Value, QuantityDto.From(evidence.MinimumCurrent), evidence.Route is { } route ? CurrentRouteDto.From(route) : null,
            CurrentSourceConfigurationDto.From(evidence.SourceConfiguration), QuantityDto.From(evidence.Current), QuantityDto.From(evidence.Voltage), evidence.SourceCapturedAt, evidence.VoltageCapturedAt, evidence.SourceConfirmedBefore, evidence.SourceConfirmedAfter);
    }
}
