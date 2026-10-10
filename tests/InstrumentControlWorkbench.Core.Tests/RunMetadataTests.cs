using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class RunMetadataTests
{
    [Fact]
    public void MetadataCopiesAttributesAndBoundsUserText()
    {
        var attributes = new Dictionary<string, string> { ["operator"] = "A" };
        var metadata = new RunMetadata(new("sample01"), "name", "notes", attributes);
        attributes["operator"] = "B";
        Assert.Equal("A", metadata.Attributes["operator"]);
        Assert.Equal("sample01", metadata.SampleId!.Value);
        Assert.Throws<ArgumentException>(() => new RunMetadata(notes: new string('x', 65537)));
        Assert.Throws<ArgumentException>(() => new RunMetadata(attributes: new Dictionary<string, string> { [""] = "bad" }));
        Assert.Throws<ArgumentException>(() => new SampleId("bad\nID"));
    }
}
