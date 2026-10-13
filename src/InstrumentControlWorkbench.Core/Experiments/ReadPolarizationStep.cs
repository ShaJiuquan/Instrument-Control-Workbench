using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Software-timed voltage pulse/readout; paused attempts restart, never stitch.
public sealed record ReadPolarizationStep : ExperimentStep
{
    public ReadPolarizationStep(StepId id, InstrumentId device, Quantity polarizationVoltage, TimeSpan polarizationDuration,
        Quantity interPulseVoltage, TimeSpan interPulseDuration, Quantity readVoltage, TimeSpan readDelay, Quantity compliance,
        SenseConfiguration sense, ParameterId voltageColumn, ParameterId currentColumn, ParameterId attemptsColumn,
        int maximumAttempts = 8, MatrixRouteExpectation? requiredRoute = null, IEnumerable<EnvironmentRead>? environmentReads = null, ParameterId? requestedPolarizationColumn = null, PlanValue? polarizationTarget = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(sense);
        ArgumentNullException.ThrowIfNull(voltageColumn); ArgumentNullException.ThrowIfNull(currentColumn); ArgumentNullException.ThrowIfNull(attemptsColumn);
        if (new[] { voltageColumn, currentColumn, attemptsColumn }.Distinct().Count() != 3 || maximumAttempts is < 1 or > 100 ||
            compliance.Unit != PhysicalUnit.Ampere || sense.Function != SenseFunction.Current) throw new ArgumentException("Polarization requires distinct columns, current sensing, Ampere compliance and 1..100 attempts.");
        ExperimentDurations.Validate(polarizationDuration); ExperimentDurations.Validate(interPulseDuration); ExperimentDurations.Validate(readDelay);
        if (polarizationVoltage.Unit != PhysicalUnit.Volt) throw new ArgumentException("Polarization configuration requires Volt.");
        Keithley2450Limits.ValidateConfiguration(new(SourceFunction.Voltage, polarizationVoltage.Value, compliance.Value), sense);
        polarizationTarget ??= new ConstantValue(polarizationVoltage); NumericPlanValue.Variables(polarizationTarget);
        if (requestedPolarizationColumn is not null && new[] { voltageColumn, currentColumn, attemptsColumn }.Contains(requestedPolarizationColumn)) throw new ArgumentException("Requested polarization column must be distinct.");
        if (NumericPlanValue.TryConstant(polarizationTarget, out var constantTarget))
        {
            if (constantTarget != polarizationVoltage) throw new ArgumentException("Constant polarization target conflicts with its configuration voltage.");
            if (constantTarget.Unit != PhysicalUnit.Volt) throw new ArgumentException("Polarization target requires Volt.");
            Keithley2450Limits.ValidateConfiguration(new(SourceFunction.Voltage, constantTarget.Value, compliance.Value), sense);
        }
        else if (requestedPolarizationColumn is null) throw new ArgumentException("Dynamic polarization requires a per-row requested voltage column.");
        foreach (var voltage in new[] { interPulseVoltage, readVoltage })
        {
            if (voltage.Unit != PhysicalUnit.Volt) throw new ArgumentException("Polarization stages require Volt quantities.");
            Keithley2450Limits.ValidateConfiguration(new(SourceFunction.Voltage, voltage.Value, compliance.Value), sense);
        }
        if (requiredRoute is { } route && route.Device == device) throw new ArgumentException("Polarization route requires a separate matrix device.");
        Device = device; PolarizationTarget = polarizationTarget; RequestedPolarizationColumn = requestedPolarizationColumn; PolarizationDuration = polarizationDuration;
        InterPulseVoltage = interPulseVoltage; InterPulseDuration = interPulseDuration; ReadVoltage = readVoltage; ReadDelay = readDelay;
        Compliance = compliance; Sense = sense; VoltageColumn = voltageColumn; CurrentColumn = currentColumn; AttemptsColumn = attemptsColumn;
        MaximumAttempts = maximumAttempts; RequiredRoute = requiredRoute; EnvironmentReads = EnvironmentRead.Freeze(environmentReads, [voltageColumn, currentColumn, attemptsColumn, .. requestedPolarizationColumn is { } requested ? new[] { requested } : []]);
    }
    public InstrumentId Device { get; }
    public PlanValue PolarizationTarget { get; }
    // Compatibility accessor for existing fixed recipes; dynamic recipes use Target.
    public Quantity PolarizationVoltage => NumericPlanValue.TryConstant(PolarizationTarget, out var value) ? value : throw new InvalidOperationException("Polarization target is dynamic.");
    public ParameterId? RequestedPolarizationColumn { get; }
    public TimeSpan PolarizationDuration { get; }
    public Quantity InterPulseVoltage { get; }
    public TimeSpan InterPulseDuration { get; }
    public Quantity ReadVoltage { get; }
    public TimeSpan ReadDelay { get; }
    public Quantity Compliance { get; }
    public SenseConfiguration Sense { get; }
    public ParameterId VoltageColumn { get; }
    public ParameterId CurrentColumn { get; }
    public ParameterId AttemptsColumn { get; }
    public int MaximumAttempts { get; }
    public MatrixRouteExpectation? RequiredRoute { get; }
    public IReadOnlyList<EnvironmentRead> EnvironmentReads { get; }
    public SourceSetpoint Setpoint(Quantity voltage)
    {
        if (voltage.Unit != PhysicalUnit.Volt) throw new ArgumentException("Polarization setpoint requires Volt.", nameof(voltage));
        var source = new SourceSetpoint(SourceFunction.Voltage, voltage.Value, Compliance.Value);
        Keithley2450Limits.ValidateConfiguration(source, Sense); return source;
    }
}
