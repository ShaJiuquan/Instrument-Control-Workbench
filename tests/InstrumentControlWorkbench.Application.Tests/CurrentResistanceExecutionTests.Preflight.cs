using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class CurrentResistanceExecutionTests
{
    [Theory]
    [InlineData("wrongSourceFunction")]
    [InlineData("missingPreparation")]
    [InlineData("wrongMeter")]
    [InlineData("unknownExpectedAxis")]
    [InlineData("invalidRoute")]
    public async Task InvalidNativeIndependentPlanRejectsBeforeConnectingOrClaiming(string damage)
    {
        await using var b = new Bench(); var read = b.Read(); SourcePreparation[] preparations = [b.Preparation];
        switch (damage)
        {
            case "wrongSourceFunction": preparations = [new(b.Source.Id, SourceFunction.Voltage, new(0.01, PhysicalUnit.Volt), new(0.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]; break;
            case "missingPreparation": preparations = []; break;
            case "wrongMeter": read = new(read.Id, read.Source, b.Matrix.Definition.Device.Id, read.CurrentReference, read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn, read.MinimumCurrent); break;
            case "unknownExpectedAxis": read = new(read.Id, read.Source, read.Meter, read.CurrentReference, read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn, read.MinimumCurrent, new VariableValue(new("unknown"))); break;
            case "invalidRoute": read = new(read.Id, read.Source, read.Meter, read.CurrentReference, read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn, read.MinimumCurrent, requiredRoute: new(b.Route.Device, [new(1, 5, 1)])); break;
        }
        var plan = new ExperimentPlan("Invalid independent acquisition", read, preparations);
        Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(plan)); Assert.Empty(b.Peer.Server.Commands); Assert.Equal(0, b.Meter.Reads);
        Assert.False(b.Runs.HasActiveRun); Assert.All(b.Registry.Devices, d => Assert.False(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow)));
    }
    [Theory]
    [InlineData((ushort)32, false)]
    [InlineData((ushort)4, true)]
    public async Task MeasuredSourceWarningsRemainSourceEvidenceAndProtectionDiscardsExternalReading(ushort status, bool reject)
    {
        await using var b = new Bench(); b.Peer.ExtraSourceStatus = status; var plan = b.Plan(b.Read(CurrentReference.MeasuredSourceReadback));
        if (reject)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(0, b.Meter.Reads);
        }
        else
        {
            await b.Runs.StartPlanAsync(plan, Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
            Assert.Equal(Core.Data.ReadingQualityState.PartiallyObserved, row.QualityState);
            Assert.True(row.QualityIssues.HasFlag(ReadingQualityIssues.SourceLimited));
            Assert.True(row.Quality.Single(q => q.Device == b.Source.Id).Status!.Issues.HasFlag(ReadingQualityIssues.SourceLimited));
            Assert.Null(row.Quality.Single(q => q.Device == b.Meter.Descriptor.Id).Status);
        }
        Assert.False(b.Peer.Output); Assert.False(b.Runs.HasActiveRun);
    }
}
