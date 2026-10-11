using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class CurrentResistanceExecutionTests
{
    [Theory]
    [InlineData(CurrentReference.ProgrammedSetpoint, true)]
    [InlineData(CurrentReference.MeasuredSourceReadback, true)]
    [InlineData(CurrentReference.MeasuredSourceReadback, false)]
    public async Task K2450InvalidPointRetainsReferenceAndConfigurationWithoutClaimingMeasuredValues(CurrentReference reference, bool owns)
    {
        await using var b = new Bench(); b.Peer.ActualSource = 8e-6;
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.BufferTimeout, "No voltage frame");
        var r = b.Read(reference, route: b.Route); var read = new ReadCurrentResistanceStep(r.Id, r.Source, r.Meter, r.CurrentReference, r.VoltageColumn, r.CurrentColumn, r.ResistanceColumn, r.MinimumCurrent, r.ExpectedProgrammedCurrent, r.RequiredRoute, retry: new(owns, TimeSpan.Zero, true));
        await b.Runs.StartPlanAsync(b.Plan(read), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var failure = Assert.Single(row.AcquisitionFailures);
        Assert.Equal(CurrentSourceModel.Keithley2450, failure.SourceConfiguration.Model); Assert.Equal(reference, failure.CurrentReference); Assert.Equal(1e-5, failure.SourceConfiguration.Level);
        Assert.Equal(2, failure.Attempts); Assert.Equal(2, b.Meter.Reads); Assert.Empty(row.Quality); Assert.Empty(row.IndependentResistance); Assert.Single(row.Values);
        Assert.Equal(owns, failure.SourceOffConfirmedAt is not null); Assert.False(b.Peer.Output); Assert.False(b.Runs.HasActiveRun);
    }
}
