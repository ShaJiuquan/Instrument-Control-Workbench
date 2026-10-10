using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class MatrixModelTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, -1)]
    public void NonpositiveCrosspointsAreRejected(int slot, int row, int column)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MatrixCrossPoint(slot, row, column));

    [Fact]
    public void DefinitionsRequireDistinctCardsAndDeclaredExcitationSources()
    {
        var d = new DeviceDescriptor(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []);
        Assert.Throws<ArgumentException>(() => new MatrixSwitchDefinition(d, [new(1, 4, 6), new(1, 8, 8)], [new("source")]));
        Assert.Throws<ArgumentException>(() => new MatrixSwitchDefinition(d, [new(1, 4, 6)], []));
        Assert.Throws<ArgumentException>(() => new MatrixSwitchDefinition(d, [new(1, 4, 6)], [d.Id]));
        Assert.Throws<ArgumentException>(() => new MatrixSwitchDefinition(d, [new(1, 4, 6)], [new("source"), new("source")]));
    }

    [Fact]
    public void RouteAndSnapshotDefensivelyCopyCrosspointsAndRejectDuplicatesOrUnknownCachedState()
    {
        var d = new MatrixSwitchDefinition(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []),
            [new(1, 4, 6)], [new("source")]);
        var points = new[] { new MatrixCrossPoint(1, 2, 3) };
        var step = new RouteMatrixStep(new("route"), d.Device.Id, points, TimeSpan.Zero);
        var snapshot = new MatrixSwitchSnapshot(d, true, true, points, DateTimeOffset.UtcNow);
        points[0] = new(1, 4, 6);
        Assert.Equal(new(1, 2, 3), Assert.Single(step.Closed)); Assert.Equal(step.Closed, snapshot.Closed);
        Assert.Throws<ArgumentException>(() => new RouteMatrixStep(new("route"), d.Device.Id, [points[0], points[0]], TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new MatrixSwitchSnapshot(d, true, false, points, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RouteMatrixStep(new("route"), d.Device.Id, [], TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatrixSwitchSnapshot(d, true, true, [new(2, 1, 1)], DateTimeOffset.UtcNow));
    }
}
