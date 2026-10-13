using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Sr830AcModelTests
{
    private static Sr830ExcitationSettings Excitation() => new(new(13.7, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt));
    private static MatrixAcIsolationCapability Capability(IEnumerable<MatrixCrossPoint>? points = null) => new(new("generator"), points ?? [new(1, 1, 1)], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz));
    private static DeviceDescriptor Matrix() => new(new("isolator"), "matrix", "AC isolation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []);

    [Theory]
    [InlineData(.001, .004, 1)]
    [InlineData(102000, 5, 1)]
    [InlineData(5, 1, 19999)]
    public void ExcitationUsesInternalReferenceAndPreservesSupportedNonzeroValues(double frequency, double amplitude, int harmonic)
    {
        var settings = new Sr830ExcitationSettings(new(frequency, PhysicalUnit.Hertz), new(amplitude, PhysicalUnit.Volt), harmonic);
        Assert.Equal(Sr830ReferenceSource.Internal, settings.ExpectedReference.Source);
        Assert.Equal(settings.Frequency, settings.ExpectedReference.Frequency);
        Assert.Equal(settings.SineAmplitude, settings.ExpectedReference.SineAmplitude);
        Assert.Equal(settings.Harmonic, settings.ExpectedReference.Harmonic);
    }

    [Theory]
    [InlineData("zero-amplitude")]
    [InlineData("low-amplitude")]
    [InlineData("high-amplitude")]
    [InlineData("amplitude-unit")]
    [InlineData("zero-frequency")]
    [InlineData("low-frequency")]
    [InlineData("high-frequency")]
    [InlineData("frequency-unit")]
    [InlineData("zero-harmonic")]
    [InlineData("high-harmonic")]
    [InlineData("harmonic-product")]
    public void UnsupportedExcitationCannotRepresentOffOrAnInvalidGeneratorState(string damage)
    {
        var frequency = damage switch { "zero-frequency" => 0, "low-frequency" => .0009, "high-frequency" => 102001, "harmonic-product" => 102000, _ => 13.7 };
        var amplitude = damage switch { "zero-amplitude" => 0, "low-amplitude" => .003, "high-amplitude" => 5.001, _ => .004 };
        var harmonic = damage switch { "zero-harmonic" => 0, "high-harmonic" => 20000, "harmonic-product" => 2, _ => 1 };
        Assert.ThrowsAny<ArgumentException>(() => new Sr830ExcitationSettings(new(frequency, damage == "frequency-unit" ? PhysicalUnit.Second : PhysicalUnit.Hertz),
            new(amplitude, damage == "amplitude-unit" ? PhysicalUnit.Ampere : PhysicalUnit.Volt), harmonic));
    }

    [Theory]
    [InlineData(5, PhysicalUnit.Volt, 102000, PhysicalUnit.Hertz)]
    [InlineData(10, PhysicalUnit.Volt, 200000, PhysicalUnit.Hertz)]
    public void IsolationEnvelopeMustCoverEveryPossibleDeclaredSr830Output(double amplitude, PhysicalUnit amplitudeUnit, double frequency, PhysicalUnit frequencyUnit)
    {
        var capability = new MatrixAcIsolationCapability(new("generator"), [new(1, 1, 1)], new(amplitude, amplitudeUnit), new(frequency, frequencyUnit));
        Assert.Equal(new(amplitude, amplitudeUnit), capability.MaximumAmplitude);
        Assert.Equal(new(frequency, frequencyUnit), capability.MaximumFrequency);
    }

    [Theory]
    [InlineData(4.999, PhysicalUnit.Volt, 102000, PhysicalUnit.Hertz)]
    [InlineData(0, PhysicalUnit.Volt, 102000, PhysicalUnit.Hertz)]
    [InlineData(5, PhysicalUnit.Ampere, 102000, PhysicalUnit.Hertz)]
    [InlineData(5, PhysicalUnit.Volt, 101999, PhysicalUnit.Hertz)]
    [InlineData(5, PhysicalUnit.Volt, 0, PhysicalUnit.Hertz)]
    [InlineData(5, PhysicalUnit.Volt, 102000, PhysicalUnit.Second)]
    public void NarrowOrUntypedRelayEnvelopeCannotAuthorizeEmergencyHotOpen(double amplitude, PhysicalUnit amplitudeUnit, double frequency, PhysicalUnit frequencyUnit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MatrixAcIsolationCapability(new("generator"), [new(1, 1, 1)], new(amplitude, amplitudeUnit), new(frequency, frequencyUnit)));

    [Fact]
    public void RegisteredCapabilityAndAtomicRecipeFreezeTheirCompleteConnectionSets()
    {
        var points = new[] { new MatrixCrossPoint(1, 1, 1), new MatrixCrossPoint(1, 1, 2) };
        var capability = Capability(points);
        var step = new ReadSr830AcFrameStep(new("ac"), new("generator"), new("isolator"), points, Excitation(), Sr830InputMode.Current1M, new("raw"), TimeSpan.FromMilliseconds(10));
        points[0] = new(2, 4, 5);
        Assert.Equal(new MatrixCrossPoint(1, 1, 1), capability.Connections[0]);
        Assert.Equal(capability.Connections, step.Connections);
        Assert.Throws<NotSupportedException>(() => ((IList<MatrixCrossPoint>)capability.Connections).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MatrixCrossPoint>)step.Connections).Clear());
        Assert.Equal(step.Id, step.Frame.Id);
        Assert.Equal(step.Device, step.Frame.Device);
        Assert.Equal(step.Input, step.Frame.Input);
        Assert.Equal(step.Prefix, step.Frame.Prefix);
        Assert.True(step.Frame.IncludeReference);
        Assert.Equal(12, step.Frame.Channels.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(10), step.Settle);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("budget")]
    public void EmptyDuplicateNullOrOversizedDisconnectSetsAreRejectedByBothModels(string damage)
    {
        var point = new MatrixCrossPoint(1, 1, 1);
        IEnumerable<MatrixCrossPoint> points = damage switch
        {
            "empty" => [],
            "duplicate" => [point, point],
            "null" => [null!],
            _ => Enumerable.Range(1, 4097).Select(column => new MatrixCrossPoint(1, 1, column))
        };
        Assert.Throws<ArgumentException>(() => Capability(points));
        Assert.Throws<ArgumentException>(() => new ReadSr830AcFrameStep(new("ac"), new("generator"), new("isolator"), points, Excitation(), Sr830InputMode.VoltageAB, new("raw"), TimeSpan.Zero));
    }

    [Fact]
    public void IndependentAcCapabilityAllowsNoDcGuardWithoutWeakeningLegacyMatrixValidation()
    {
        var capability = Capability();
        var definition = new MatrixSwitchDefinition(Matrix(), [new(1, 2, 4)], [], capability);
        Assert.Same(capability, definition.AcIsolation);
        Assert.Empty(definition.GuardedSources);
        Assert.Throws<ArgumentException>(() => new MatrixSwitchDefinition(Matrix(), [new(1, 2, 4)], []));
        var legacy = new MatrixSwitchDefinition(Matrix(), [new(1, 2, 4)], [new("dc-source")]);
        Assert.Null(legacy.AcIsolation);
        Assert.Single(legacy.GuardedSources);
    }

    [Theory]
    [InlineData("outside-card")]
    [InlineData("self-generator")]
    [InlineData("duplicate-guards")]
    [InlineData("self-guard")]
    public void InvalidMatrixQualificationCannotBeHiddenBehindAcCapability(string damage)
    {
        var capability = damage == "self-generator" ? new MatrixAcIsolationCapability(Matrix().Id, [new(1, 1, 1)], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)) : Capability(damage == "outside-card" ? [new(2, 1, 1)] : null);
        var guards = damage switch { "duplicate-guards" => new[] { new InstrumentId("source"), new InstrumentId("source") }, "self-guard" => new[] { Matrix().Id }, _ => [] };
        Assert.ThrowsAny<ArgumentException>(() => new MatrixSwitchDefinition(Matrix(), [new(1, 2, 4)], guards, capability));
    }

    [Theory]
    [InlineData("self-isolator")]
    [InlineData("null-excitation")]
    [InlineData("input")]
    [InlineData("negative-settle")]
    [InlineData("oversized-settle")]
    public void AtomicRecipeRejectsInvalidIdentitySettingsInputAndTimer(string damage)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ReadSr830AcFrameStep(new("ac"), new("generator"), new(damage == "self-isolator" ? "generator" : "isolator"), [new(1, 1, 1)],
            damage == "null-excitation" ? null! : Excitation(), damage == "input" ? (Sr830InputMode)99 : Sr830InputMode.VoltageAB, new("raw"),
            damage == "negative-settle" ? TimeSpan.FromTicks(-1) : damage == "oversized-settle" ? TimeSpan.MaxValue : TimeSpan.Zero));
    }
}
