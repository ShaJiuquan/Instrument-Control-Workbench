using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class IndependentSampleModelTests
{
    [Theory]
    [InlineData("source", false)]
    [InlineData("", false)]
    [InlineData("meter", true)]
    public void InvalidIndependentPhysicalWiringRejects(string meter, bool shared)
    {
        Assert.Throws<ArgumentException>(() => new InstrumentWiringProfile(new("wiring"), new("source"), new(1, 1), new(1, 2), new(1, shared ? 1 : 3), new(1, shared ? 2 : 4), voltageMeter: new(meter)));
    }
    [Fact]
    public void SampleContextKeepsDistinctVoltageMeterAndRejectsChangedArchivedIdentity()
    {
        var routing = Routing(); var context = routing.Resolve(new(new("sample"), new("wiring"), SampleRouteSelection.HallLongitudinal));
        Assert.Equal(new InstrumentId("meter"), context.VoltageMeter); routing.ValidateContext(context);
        var changed = new ResolvedSampleRoute(context.Binding, context.SampleName, context.Topology, context.Matrix, context.SourceMeter, context.Roles, new("other-meter"));
        Assert.Throws<ArgumentException>(() => routing.ValidateContext(changed));
        Assert.Throws<ArgumentException>(() => new ResolvedSampleRoute(context.Binding, context.SampleName, context.Topology, context.Matrix, context.SourceMeter, context.Roles, context.Matrix));
    }
    [Fact]
    public void IndependentSampleRejectsExternallyManagedCleanup()
    {
        var read = Group(Routing()).Readings[0];
        Assert.Throws<ArgumentException>(() => new ReadSampleResistanceStep(read.Acquisition, read.Binding, TimeSpan.Zero, routingManagedExternally: true));
    }
    [Theory]
    [InlineData("meter")]
    [InlineData("route")]
    [InlineData("policy")]
    [InlineData("expected")]
    [InlineData("integrated")]
    public void AnalysisRequiresTheSameAcquisitionFamilyAndCurrentPolicy(string fault)
    {
        var routing = Routing(); var original = Group(routing); var first = original.Readings[0]; var second = original.Readings[1]; var old = Assert.IsType<ReadCurrentResistanceStep>(second.Acquisition);
        ResistanceAcquisitionStep changed = fault == "integrated"
            ? new ReadResistanceStep(old.Id, old.Source, SourceFunction.Current, old.VoltageColumn, old.CurrentColumn, old.ResistanceColumn, old.MinimumCurrent)
            : new ReadCurrentResistanceStep(old.Id, old.Source, fault == "meter" ? new("other") : old.Meter,
                fault == "policy" ? CurrentReference.MeasuredSourceReadback : old.CurrentReference, old.VoltageColumn, old.CurrentColumn, old.ResistanceColumn, old.MinimumCurrent,
                fault == "expected" ? new ConstantValue(new(2e-5, PhysicalUnit.Ampere)) : old.ExpectedProgrammedCurrent, fault == "route" ? null : old.RequiredRoute);
        var damaged = new SampleAnalysisStep(original.Id, [first, new(changed, second.Binding, TimeSpan.Zero)], original.Columns);
        Assert.Throws<ArgumentException>(() => damaged.ValidateRouting(routing));
    }
    [Fact]
    public void SharedSampleContractKeepsIndependentDeviceRolesWithoutSmuSenseConfiguration()
    {
        var group = Group(Routing()); var list = group.Readings.ToList(); var copy = new SampleAnalysisStep(group.Id, list, group.Columns); list.Clear();
        Assert.Equal(2, copy.Readings.Count); copy.ValidateRouting(Routing());
        var acquisition = Assert.IsType<ReadCurrentResistanceStep>(copy.Readings[0].Acquisition);
        Assert.Equal(acquisition.Source, acquisition.Device); Assert.Equal(SourceFunction.Current, acquisition.Function); Assert.NotEqual(acquisition.Device, acquisition.Meter);
    }
    private static SampleAnalysisStep Group(RoutingConfiguration routing)
    {
        var readings = SampleResistanceAnalysis.Selections(SampleTopology.HallBar).Select((selection, index) =>
        {
            var binding = new SampleRoutingBinding(new("sample"), new("wiring"), selection); var route = routing.Resolve(binding);
            return new ReadSampleResistanceStep(new ReadCurrentResistanceStep(new("read" + index), route.SourceMeter, route.VoltageMeter!.Value, CurrentReference.ProgrammedSetpoint,
                new("V"), new("I"), new("R"), new(1e-16, PhysicalUnit.Ampere), new ConstantValue(new(1e-5, PhysicalUnit.Ampere)), new(route.Matrix, route.Closed)), binding, TimeSpan.Zero);
        });
        return new(new("analysis"), readings, SampleResistanceAnalysis.Kinds(SampleTopology.HallBar).Select(k => new SampleDerivedColumn(k, new(k.ToString()))));
    }
    private static RoutingConfiguration Routing()
    {
        var labels = SampleProfile.RequiredLabels(SampleTopology.HallBar);
        var channel = new ChannelProfile(new("contacts"), "Contacts", Enumerable.Range(1, 6).Select(i => new ChannelConnection(new("contact" + i), 1, i)));
        var sample = new SampleProfile(new("sample"), "Hall sample", SampleTopology.HallBar, labels.Select((l, i) => new SampleElectrode(new(l), channel.Id, new("contact" + (i + 1)))));
        var wiring = new InstrumentWiringProfile(new("wiring"), new("source"), new(1, 1), new(1, 2), new(1, 3), new(1, 4), voltageMeter: new("meter"));
        return new(new("matrix"), [channel], [wiring], [sample]);
    }
}
