using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Data;

internal static partial class RunArchiveJson
{
    internal sealed record ElectrodeRouteDto(RoutingRole Role, string Electrode, string Channel, string Connection, int Slot, int Row, int Column)
    {
        public ResolvedElectrodeRoute ToDomain() => new(Role, new(Electrode), new(Channel), new(Connection), new(Slot, Row, Column));
        public static ElectrodeRouteDto From(ResolvedElectrodeRoute role) => new(role.Role, role.Electrode.Value, role.Channel.Value,
            role.Connection.Value, role.Point.Slot, role.Point.Row, role.Point.Column);
    }
    internal sealed record SampleRouteDto(string Sample, string SampleName, SampleTopology Topology, string Wiring,
        SampleRouteSelection Selection, string Matrix, string SourceMeter, ElectrodeRouteDto[] Roles, string? VoltageMeter = null)
    {
        public ResolvedSampleRoute ToDomain() => new(new(new(Sample), new(Wiring), Selection), SampleName, Topology,
            new(Matrix), new(SourceMeter), Required(Roles).Select(r => Required(r).ToDomain()), VoltageMeter is null ? null : new Core.Instruments.InstrumentId(VoltageMeter));
        public static SampleRouteDto From(ResolvedSampleRoute context) => new(context.Binding.Sample.Value, context.SampleName, context.Topology,
            context.Binding.Wiring.Value, context.Binding.Selection, context.Matrix.Value, context.SourceMeter.Value,
            context.Roles.Select(ElectrodeRouteDto.From).ToArray(), context.VoltageMeter?.Value);
    }
}
