using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Data;

// Controller route readback, not a measurement of electrical continuity.
public sealed record Sr830AcRouteObservation
{
    public Sr830AcRouteObservation(InstrumentId isolator, DateTimeOffset observedAt, IEnumerable<MatrixCrossPoint> closed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolator.Value); ArgumentNullException.ThrowIfNull(closed);
        if (observedAt == default) throw new ArgumentException("AC route observation requires a capture timestamp.");
        var points = closed.ToArray();
        if (points.Length > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length) throw new ArgumentException("AC route evidence requires unique bounded connections.");
        Isolator = isolator; ObservedAt = observedAt; Closed = Array.AsReadOnly(points);
    }
    public InstrumentId Isolator { get; }
    public DateTimeOffset ObservedAt { get; }
    public IReadOnlyList<MatrixCrossPoint> Closed { get; }
    public static Sr830AcRouteObservation From(MatrixSwitchSnapshot snapshot)
    {
        if (!snapshot.Connected || !snapshot.RouteKnown) throw new ArgumentException("AC route evidence requires connected, known readback.");
        return new(snapshot.Definition.Device.Id, snapshot.Timestamp, snapshot.Closed);
    }
}

public sealed record Sr830AcEvidence
{
    public Sr830AcEvidence(StepId step, InstrumentId device, InstrumentId isolator, IEnumerable<MatrixCrossPoint> connections,
        Sr830ReferenceReadback reference, DateTimeOffset frameCapturedAt, Sr830AcRouteObservation beforeConfigurationOpen,
        Sr830AcRouteObservation beforeFrameClosed, Sr830AcRouteObservation afterFrameClosed, Sr830AcRouteObservation afterAcquisitionOpen)
    {
        ArgumentNullException.ThrowIfNull(step); ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentException.ThrowIfNullOrWhiteSpace(isolator.Value);
        ArgumentNullException.ThrowIfNull(connections); ArgumentNullException.ThrowIfNull(reference);
        if (frameCapturedAt == default) throw new ArgumentException("AC evidence requires a frame timestamp.");
        var points = connections.ToArray();
        if (points.Length is < 1 or > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length || device == isolator) throw new ArgumentException("AC evidence requires a distinct generator and complete unique isolation set.");
        _ = new Sr830ExcitationSettings(reference.Frequency, reference.SineAmplitude, reference.Harmonic);
        if (reference.Source != Sr830ReferenceSource.Internal) throw new ArgumentException("Active AC evidence requires internal reference.");
        var observations = new[] { beforeConfigurationOpen, beforeFrameClosed, afterFrameClosed, afterAcquisitionOpen };
        if (observations.Any(o => o is null || o.Isolator != isolator) || beforeConfigurationOpen.ObservedAt > beforeFrameClosed.ObservedAt || beforeFrameClosed.ObservedAt > frameCapturedAt || frameCapturedAt > afterFrameClosed.ObservedAt || afterFrameClosed.ObservedAt > afterAcquisitionOpen.ObservedAt) throw new ArgumentException("AC route evidence requires the declared isolator and ordered acquisition boundaries.");
        if (points.Any(beforeConfigurationOpen.Closed.Contains) || points.Any(afterAcquisitionOpen.Closed.Contains) || points.Any(p => !beforeFrameClosed.Closed.Contains(p) || !afterFrameClosed.Closed.Contains(p))) throw new ArgumentException("AC evidence must prove open/closed/closed/open for every declared connection.");
        if (!beforeConfigurationOpen.Closed.ToHashSet().SetEquals(afterAcquisitionOpen.Closed) || !beforeFrameClosed.Closed.ToHashSet().SetEquals(afterFrameClosed.Closed) || !beforeFrameClosed.Closed.ToHashSet().SetEquals(beforeConfigurationOpen.Closed.Concat(points))) throw new ArgumentException("AC evidence cannot hide unrelated route changes.");
        Step = step; Device = device; Isolator = isolator; Connections = Array.AsReadOnly(points); Reference = reference; FrameCapturedAt = frameCapturedAt;
        BeforeConfigurationOpen = beforeConfigurationOpen; BeforeFrameClosed = beforeFrameClosed; AfterFrameClosed = afterFrameClosed; AfterAcquisitionOpen = afterAcquisitionOpen;
    }
    public bool ContentEquals(Sr830AcEvidence other) => Step == other.Step && Device == other.Device && Isolator == other.Isolator && Connections.ToHashSet().SetEquals(other.Connections) && Reference == other.Reference && FrameCapturedAt == other.FrameCapturedAt && Same(BeforeConfigurationOpen, other.BeforeConfigurationOpen) && Same(BeforeFrameClosed, other.BeforeFrameClosed) && Same(AfterFrameClosed, other.AfterFrameClosed) && Same(AfterAcquisitionOpen, other.AfterAcquisitionOpen);
    private static bool Same(Sr830AcRouteObservation a, Sr830AcRouteObservation b) => a.Isolator == b.Isolator && a.ObservedAt == b.ObservedAt && a.Closed.ToHashSet().SetEquals(b.Closed);
    public StepId Step { get; }
    public InstrumentId Device { get; }
    public InstrumentId Isolator { get; }
    public IReadOnlyList<MatrixCrossPoint> Connections { get; }
    public Sr830ReferenceReadback Reference { get; }
    public DateTimeOffset FrameCapturedAt { get; }
    public Sr830AcRouteObservation BeforeConfigurationOpen { get; }
    public Sr830AcRouteObservation BeforeFrameClosed { get; }
    public Sr830AcRouteObservation AfterFrameClosed { get; }
    public Sr830AcRouteObservation AfterAcquisitionOpen { get; }
}
