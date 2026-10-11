using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public enum HistoryPlotRowKind { Reading, RoutedReading, SampleAnalysis, MeasurementGroup }

// One entry per source record, including missing coordinates. Renderers must
// keep gaps and distinguish provenance; neither sorting nor interpolation occurs.
public sealed record HistoryPlotSample(long RowIndex, DateTimeOffset Timestamp,
    Quantity? X, Quantity? Y, HistoryPlotRowKind Kind, ReadingQualityState QualityState,
    ReadingQualityIssues QualityIssues, int ObservedQualityColumns)
{
    public bool HasCoordinates => X is not null && Y is not null;
}

public sealed record HistoryPlotPage(RunArchiveMetadata Metadata, ParameterDescriptor XAxis,
    ParameterDescriptor YAxis, IReadOnlyList<HistoryPlotSample> Samples, int Offset,
    int ObservedRowCount, int? NextOffset, bool HasIncompleteTail, RunArchiveReadVersion Version,
    int ReturnedDataBytes)
{
    public bool HasMore => NextOffset is not null;
}
