using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Keithley6221ModeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EachArmedFactMustBeObservedFalseEvenWhenIdle(int index)
    {
        foreach (bool? fact in new bool?[] { null, true })
        {
            bool?[] flags = [false, false, false, false, false]; flags[index] = fact;
            Assert.False(new Keithley6221ModeObservation(flags[0], flags[1], flags[2], flags[3], flags[4], 1024, DateTimeOffset.UtcNow).IsDcReady);
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void ActivityErrorsAndUndefinedBitsCannotBeHiddenByIdle(int bit)
        => Assert.False(new Keithley6221ModeObservation(false, false, false, false, false, (ushort)(1024 | 1 << bit), DateTimeOffset.UtcNow).IsDcReady);
    [Fact]
    public void IdleMustBeObservedAndTerminalConditionBitsAreAllowed()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(new Keithley6221ModeObservation(false, false, false, false, false, null, now).IsDcReady);
        Assert.False(new Keithley6221ModeObservation(false, false, false, false, false, 0, now).IsDcReady);
        Assert.True(new Keithley6221ModeObservation(false, false, false, false, false, 1024, now).IsDcReady);
        // Sweep done/aborted, wave stopped and filter settled do not indicate activity.
        Assert.True(new Keithley6221ModeObservation(false, false, false, false, false, 0x0586, now).IsDcReady);
    }
    [Fact]
    public void ModeEvidenceCannotBeAttachedToAnotherCaptureOrDisconnectedSource()
    {
        var now = DateTimeOffset.UtcNow; var mode = new Keithley6221ModeObservation(false, false, false, false, false, 1024, now);
        var settings = new Keithley6221Settings(new(0, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt));
        Assert.Throws<ArgumentException>(() => new K6221CurrentSourceSnapshot(true, false, settings, now.AddTicks(1), mode));
        Assert.Throws<ArgumentException>(() => new K6221CurrentSourceSnapshot(false, false, null, now, mode));
        Assert.Same(mode, new K6221CurrentSourceSnapshot(true, false, settings, now, mode).Mode);
        Assert.Null(new K6221CurrentSourceSnapshot(true, false, settings, now).Mode);
    }
}
