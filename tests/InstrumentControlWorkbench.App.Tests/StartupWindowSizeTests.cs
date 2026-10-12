using InstrumentControlWorkbench.App;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class StartupWindowSizeTests
{
    [Theory]
    [InlineData("--window-size", "1060x720")]
    [InlineData("--window-size=1060x720", null)]
    public void MinimumAcceptanceGeometryIsInvariantAndExplicit(string option, string? value)
    {
        var size = StartupWindowSize.Parse(value is null ? [option] : [option, value]); Assert.NotNull(size); Assert.Equal(1060, size.Width); Assert.Equal(720, size.Height);
    }
    [Fact]
    public void DefaultAndOtherAvaloniaArgumentsDoNotChangeViewGeometry()
    { Assert.Null(StartupWindowSize.Parse([])); Assert.Null(StartupWindowSize.Parse(["--trace", "sample"])); }
    [Theory]
    [InlineData("--window-size", null)]
    [InlineData("--window-size=", null)]
    [InlineData("--window-size", "1060")]
    [InlineData("--window-size", "1060x720x1")]
    [InlineData("--window-size", "1060.5x720")]
    [InlineData("--window-size", "-1060x720")]
    [InlineData("--window-size", "0x720")]
    [InlineData("--window-size", "1060x0")]
    [InlineData("--window-size", "16385x720")]
    [InlineData("--window-size", "1060x16385")]
    public void InvalidExplicitGeometryIsRejectedInsteadOfSilentlyIgnoringIt(string option, string? value)
    { Assert.Throws<ArgumentException>(() => StartupWindowSize.Parse(value is null ? [option] : [option, value])); }
    [Fact]
    public void DuplicateGeometryHasNoHiddenPrecedence()
    { Assert.Throws<ArgumentException>(() => StartupWindowSize.Parse(["--window-size=1060x720", "--window-size", "1440x900"])); }
}
