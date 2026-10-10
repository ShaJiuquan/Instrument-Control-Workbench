using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum EnvironmentRole { Temperature, Field, Angle }

// An absent address records an explicitly unbound controller, never a target fallback.
public sealed record EnvironmentRead
{
    public EnvironmentRead(EnvironmentRole role, ParameterId column, ParameterAddress? address = null)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(column);
        Role = role; Column = column; Address = address;
    }
    public EnvironmentRole Role { get; }
    public ParameterId Column { get; }
    public ParameterAddress? Address { get; }
    public static PhysicalUnit Unit(EnvironmentRole role) => role switch
    {
        EnvironmentRole.Temperature => PhysicalUnit.Kelvin,
        EnvironmentRole.Field => PhysicalUnit.Tesla,
        EnvironmentRole.Angle => PhysicalUnit.Degree,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
    internal static IReadOnlyList<EnvironmentRead> Freeze(IEnumerable<EnvironmentRead>? reads, IEnumerable<ParameterId> reserved)
    {
        var items = reads?.ToArray() ?? [];
        if (items.Length > 3 || items.Any(r => r is null) || items.Select(r => r.Role).Distinct().Count() != items.Length ||
            items.Select(r => r.Column).Distinct().Count() != items.Length || items.Any(r => reserved.Contains(r.Column) || r.Column.Value == "elapsed"))
            throw new ArgumentException("Environment reads need up to three distinct roles/columns separate from acquisition columns.");
        return Array.AsReadOnly(items);
    }
}
