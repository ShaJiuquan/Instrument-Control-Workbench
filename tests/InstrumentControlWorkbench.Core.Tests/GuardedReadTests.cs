using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class GuardedReadTests
{
    [Fact]
    public void RequiredReadRouteRetainsImmutableDistinctSortedCrosspoints()
    {
        var points = new[] { new MatrixCrossPoint(1, 2, 2), new(1, 1, 1) }; var route = new MatrixRouteExpectation(new("matrix"), points);
        var step = new ReadStep(new("read"), [new(new("value"), new(new("meter"), new("voltage")))], requiredRoute: route);
        points[0] = new(1, 4, 4); Assert.Same(route, step.RequiredRoute); Assert.Equal(new MatrixCrossPoint(1, 2, 2), step.RequiredRoute!.Closed[1]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcitationGuardRequiresAnIndependentMatrixRoute(bool sameAsMatrix)
    {
        Assert.Throws<ArgumentException>(() => new ReadStep(new("read"), [new(new("value"), new(new("meter"), new("voltage")))],
            requiredRoute: sameAsMatrix ? new(new("matrix"), [new(1, 1, 1)]) : null, excitationSource: new("matrix")));
    }
    [Fact]
    public void MeasurementAndRouteDeviceMustDiffer()
    {
        Assert.Throws<ArgumentException>(() => new ReadStep(new("read"), [new(new("value"), new(new("matrix"), new("voltage")))], requiredRoute: new(new("matrix"), [new(1, 1, 1)])));
    }
}
