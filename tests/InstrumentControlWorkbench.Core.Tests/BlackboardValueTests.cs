using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class BlackboardValueTests
{
    [Fact]
    public void TextValuesPreserveEmptyUnicodeWhitespaceAndRejectNullOrOversizedContent()
    {
        foreach (var text in new[] { "", "  sample\n硅片🧪\t", new string('x', BlackboardText.MaximumLength) })
            Assert.Equal(text, new TextValue(text).Value);
        Assert.Throws<ArgumentNullException>(() => new TextValue(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextValue(new string('x', BlackboardText.MaximumLength + 1)));
    }
    [Fact]
    public void ValuesKeepQuantityUnitsDistinctFromBooleanAndText()
    {
        var quantity = new BlackboardQuantity(new(1, PhysicalUnit.Volt));
        Assert.NotEqual(quantity.Type, new BlackboardBoolean(true).Type);
        Assert.NotEqual(quantity.Type, new BlackboardQuantity(new(1, PhysicalUnit.Ampere)).Type);
        Assert.NotEqual(new BlackboardBoolean(true).Type, new BlackboardText("true").Type);
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackboardValueType.Quantity((PhysicalUnit)999));
    }
    [Fact]
    public void ProvenanceRequiresValueSourceNonnegativeElapsedAndKnownWriteKind()
    {
        var value = new BlackboardBoolean(true); var source = new StepId("write"); var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentNullException>(() => new BlackboardEntry(null!, source, now, TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new BlackboardEntry(value, null!, now, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackboardEntry(value, source, now, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackboardEntry(value, source, now, TimeSpan.Zero, (BlackboardWriteKind)999));
    }
}
