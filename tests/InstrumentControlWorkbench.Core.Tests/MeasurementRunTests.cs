using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class MeasurementRunTests
{
    [Fact]
    public void LatestPointReturnsMostRecentPoint()
    {
        var startedAt = DateTimeOffset.UnixEpoch;
        var points = new[]
        {
            new MeasurementPoint(startedAt, TimeSpan.Zero, 0.0, 1.0, "V", "A"),
            new MeasurementPoint(startedAt.AddSeconds(1), TimeSpan.FromSeconds(1), 0.1, 1.2, "V", "A"),
        };

        var run = new MeasurementRun("run-1", "Timed read", MeasurementRunState.Running, startedAt, points);

        Assert.Equal(1.2, run.LatestPoint?.MeasuredValue);
    }
}
