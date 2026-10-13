using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TcpKeithley2450Tests
{
    [Theory]
    [InlineData(SensingMode.TwoWire)]
    [InlineData(SensingMode.FourWire)]
    public async Task PolarizationRunsThroughRealTcpAdapterWithFreshRegistersAndOffConfirmation(SensingMode mode)
    {
        await using var peer = new Keithley2450ScpiPeer { ActualSource = 1.005, ActualSense = 21e-6 }; var b = new Bench(peer);
        var sense = new SenseConfiguration(SenseFunction.Current, mode, 2, true);
        var pulse = new ReadPolarizationStep(new("pulse"), b.Source.Id, new(5, PhysicalUnit.Volt), TimeSpan.Zero, new(0, PhysicalUnit.Volt), TimeSpan.FromMilliseconds(1), new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(.001, PhysicalUnit.Ampere), sense, new("v"), new("i"), new("attempts"));
        var plan = new ExperimentPlan("TCP polarization", pulse, [new(b.Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), sense)]);
        await b.Runs.StartPlanAsync(plan, Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(new(1.005, PhysicalUnit.Volt), row.Values[pulse.VoltageColumn]); Assert.Equal(new(21e-6, PhysicalUnit.Ampere), row.Values[pulse.CurrentColumn]);
        Assert.Equal(ReadingQualityState.ObservedNormal, row.QualityState); Assert.All(row.Quality, q => Assert.NotNull(q.Status));
        var commands = peer.Server.Commands.ToArray(); var on = Array.IndexOf(commands, ":OUTP ON"); var measure = Array.IndexOf(commands, Pair);
        Assert.True(Array.IndexOf(commands, ":SOUR:VOLT:LEV 5") < on); Assert.True(on >= 0 && measure > on);
        Assert.Contains(":SOUR:VOLT:LEV 0", commands.Skip(on)); Assert.Contains(":SOUR:VOLT:LEV 1", commands.Skip(on)); Assert.Contains(":OUTP OFF", commands.Skip(measure + 1)); Assert.Contains(":OUTP?", commands.Skip(measure + 1));
        Assert.Single(commands, c => c == Pair); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task PolarizationTcpRefusedFinalOffSuppressesSampleAndRetainsSafetyOwnership()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        peer.Override = (command, _) => { if (command == Pair) peer.RefuseOff = true; return null; };
        var sense = new SenseConfiguration(SenseFunction.Current, SensingMode.TwoWire, 1, true);
        var pulse = new ReadPolarizationStep(new("pulse"), b.Source.Id, new(5, PhysicalUnit.Volt), TimeSpan.Zero, new(0, PhysicalUnit.Volt), TimeSpan.Zero, new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(.001, PhysicalUnit.Ampere), sense, new("v"), new("i"), new("attempts"));
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(new("Refused OFF", pulse, [new(b.Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), sense)]), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun); Assert.True(peer.Output);
        peer.RefuseOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
}
