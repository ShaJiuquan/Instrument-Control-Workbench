using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Infrastructure.Data;

internal static partial class RunArchiveJson
{
    internal sealed record AcCrossPointDto(int Slot, int Row, int Column)
    {
        public MatrixCrossPoint ToDomain() => new(Slot, Row, Column);
        public static AcCrossPointDto From(MatrixCrossPoint point) => new(point.Slot, point.Row, point.Column);
    }
    internal sealed record AcRouteDto(string Isolator, DateTimeOffset ObservedAt, AcCrossPointDto[] Closed)
    {
        public Sr830AcRouteObservation ToDomain() => new(new(Isolator), ObservedAt, Required(Closed).Select(p => Required(p).ToDomain()));
        public static AcRouteDto From(Sr830AcRouteObservation route) => new(route.Isolator.Value, route.ObservedAt, route.Closed.Select(AcCrossPointDto.From).ToArray());
    }
    internal sealed record AcReferenceDto(string Source, QuantityDto Frequency, QuantityDto SineAmplitude, int Harmonic)
    {
        public Sr830ReferenceReadback ToDomain() => new(Source == nameof(Sr830ReferenceSource.Internal) ? Sr830ReferenceSource.Internal : throw new ArgumentException("Active AC requires an internal reference."), Required(Frequency).ToDomain(), Required(SineAmplitude).ToDomain(), Harmonic);
        public static AcReferenceDto From(Sr830ReferenceReadback reference) => new(reference.Source.ToString(), QuantityDto.From(reference.Frequency), QuantityDto.From(reference.SineAmplitude), reference.Harmonic);
    }
    internal sealed record Sr830AcDto(string Step, string Device, string Isolator, AcCrossPointDto[] Connections, AcReferenceDto Reference,
        DateTimeOffset FrameCapturedAt, AcRouteDto BeforeConfigurationOpen, AcRouteDto BeforeFrameClosed, AcRouteDto AfterFrameClosed, AcRouteDto AfterAcquisitionOpen)
    {
        public Sr830AcEvidence ToDomain() => new(new(Step), new(Device), new(Isolator), Required(Connections).Select(p => Required(p).ToDomain()), Required(Reference).ToDomain(), FrameCapturedAt,
            Required(BeforeConfigurationOpen).ToDomain(), Required(BeforeFrameClosed).ToDomain(), Required(AfterFrameClosed).ToDomain(), Required(AfterAcquisitionOpen).ToDomain());
        public static Sr830AcDto From(Sr830AcEvidence evidence) => new(evidence.Step.Value, evidence.Device.Value, evidence.Isolator.Value, evidence.Connections.Select(AcCrossPointDto.From).ToArray(), AcReferenceDto.From(evidence.Reference), evidence.FrameCapturedAt,
            AcRouteDto.From(evidence.BeforeConfigurationOpen), AcRouteDto.From(evidence.BeforeFrameClosed), AcRouteDto.From(evidence.AfterFrameClosed), AcRouteDto.From(evidence.AfterAcquisitionOpen));
    }
}
