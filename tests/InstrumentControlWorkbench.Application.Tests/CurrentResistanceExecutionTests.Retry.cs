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
    public async Task K2450RetryRestoresOnlyMatchingStateAndRecapturesActualSourceCurrent(CurrentReference reference, bool owns)
    {
        await using var b = new Bench(); b.Peer.ActualSource = 8e-6;
        b.Meter.OnRead = _ => b.Meter.Reads == 1 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "First read timed out")) : Task.CompletedTask;
        var r = b.Read(reference, route: b.Route); var read = new ReadCurrentResistanceStep(r.Id, r.Source, r.Meter, r.CurrentReference, r.VoltageColumn, r.CurrentColumn, r.ResistanceColumn, r.MinimumCurrent, r.ExpectedProgrammedCurrent, r.RequiredRoute, retry: new(owns, TimeSpan.Zero));
        await b.Runs.StartPlanAsync(b.Plan(read), Token);
        var e = Assert.Single(Assert.Single(b.Runs.LatestDataSet!.Rows).IndependentResistance);
        Assert.Equal(2, e.Attempts); Assert.Equal(reference == CurrentReference.ProgrammedSetpoint ? 100 : 125, e.Resistance.Value, 8);
        Assert.Equal(owns, e.Retry!.SourceOffConfirmedAt is not null); Assert.Equal(2, b.Meter.Reads);
        Assert.False(b.Peer.Output); Assert.False(b.Runs.HasActiveRun);
    }
}
