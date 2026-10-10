using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Devices;

public sealed record AxisControllerDefinition
{
    public AxisControllerDefinition(DeviceDescriptor device, ParameterDescriptor target, ParameterDescriptor readback,
        ParameterDescriptor rate)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(readback);
        ArgumentNullException.ThrowIfNull(rate);
        if (device.StopPolicy != DeviceStopPolicy.HoldCurrentPosition)
            throw new ArgumentException("Axis controllers must hold their current position when stopped.", nameof(device));
        if (target.Unit != readback.Unit || target.Access != ParameterAccess.ReadWrite ||
            readback.Access != ParameterAccess.Read || rate.Access != ParameterAccess.ReadWrite)
            throw new ArgumentException("An axis requires a writable target, a read-only matching readback and a writable rate.");
        var expectedRate = target.Unit switch
        {
            PhysicalUnit.Kelvin => PhysicalUnit.KelvinPerMinute,
            PhysicalUnit.Tesla => PhysicalUnit.TeslaPerSecond,
            PhysicalUnit.Degree => PhysicalUnit.DegreePerSecond,
            _ => throw new ArgumentException("Unsupported axis unit.", nameof(target)),
        };
        if (rate.Unit != expectedRate || rate.Minimum is not { Value: > 0 })
            throw new ArgumentException("Axis rate must have the matching unit and a strictly positive minimum.", nameof(rate));
        foreach (var parameter in new[] { target, readback, rate })
            if (device.GetParameter(parameter.Id) != parameter) throw new ArgumentException("Axis parameters must match its device descriptor.");
        Device = device;
        Target = target;
        Readback = readback;
        Rate = rate;
    }

    public DeviceDescriptor Device { get; }
    public ParameterDescriptor Target { get; }
    public ParameterDescriptor Readback { get; }
    public ParameterDescriptor Rate { get; }
}

public sealed record AxisControllerSnapshot(bool Connected, Quantity Target, Quantity Readback,
    Quantity Rate, bool Moving, DateTimeOffset Timestamp);

public interface IAxisControllerPort
{
    AxisControllerDefinition Definition { get; }
    Task<AxisControllerSnapshot> ConnectAsync(CancellationToken cancellationToken);
    Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task SetTargetAsync(Quantity target, CancellationToken cancellationToken);
    Task SetRateAsync(Quantity rate, CancellationToken cancellationToken);
    Task HoldAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}
