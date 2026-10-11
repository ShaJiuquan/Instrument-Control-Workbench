using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class SubtreeStepTests
{
    [Fact]
    public void ScopeFreezesChildrenAndMappingsAndAllowsEmptyChildren()
    {
        var body = new DelayStep(new("child"), TimeSpan.Zero);
        var children = new List<ExperimentStep> { body };
        var inputs = new List<VariableBinding> { new(new("parent"), new("local")) };
        var outputs = new List<VariableBinding> { new(new("local"), new("selected")) };
        var scope = new SubtreeStep(new("scope"), children, inputs, outputs);
        children.Clear(); inputs.Clear(); outputs.Clear();
        Assert.Same(body, Assert.Single(scope.Children)); Assert.Equal(new ParameterId("parent"), Assert.Single(scope.Inputs).Source);
        Assert.Equal(new ParameterId("selected"), Assert.Single(scope.Outputs).Destination);
        Assert.Empty(new SubtreeStep(new("empty"), []).Children);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingRejectsNullTypedIdentity(bool source)
        => Assert.Throws<ArgumentNullException>(() => new VariableBinding(source ? null! : new("source"), source ? new("target") : null!));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InputsAndOutputsRejectDuplicateSourcesOrAmbiguousDestinations(bool outputs, bool destination)
    {
        VariableBinding[] map = [new(new("one"), new("target")), new(new(destination ? "two" : "one"), new(destination ? "target" : "other"))];
        Assert.Throws<ArgumentException>(() => new SubtreeStep(new("scope"), [], outputs ? null : map, outputs ? map : null));
    }

    [Fact]
    public void NullAndOversizedScopeReferencesRejectAtDomainBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => new SubtreeStep(new("scope"), null!));
        Assert.Throws<ArgumentException>(() => new SubtreeStep(new("scope"), [null!]));
        Assert.Throws<ArgumentException>(() => new SubtreeStep(new("scope"), [], [null!]));
        Assert.Throws<ArgumentException>(() => new SubtreeStep(new("scope"), [], outputs: [null!]));
        var body = new DelayStep(new("child"), TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => new SubtreeStep(new("scope"), Enumerable.Repeat(body, 10001)));
        var map = Enumerable.Range(0, 10001).Select(i => new VariableBinding(new("source." + i), new("target." + i))).ToArray();
        Assert.Throws<ArgumentException>(() => new SubtreeStep(new("scope"), [], map));
        Assert.Throws<ArgumentException>(() => new SubtreeStep(new("scope"), [], outputs: map));
    }
}
