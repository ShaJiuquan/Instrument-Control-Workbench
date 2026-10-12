using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Constructs ordinary typed calculations over one verified raw frame. This is
// nominal input referral, not a calibrated sample conductance or DC difference.
public static class Sr830InputReferredAnalysis
{
    public static CalculatedMeasurementStep Create(StepId id, ReadSr830FrameStep frame, ParameterId resultPrefix)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(resultPrefix);
        var current = frame.Input is Sr830InputMode.Current1M or Sr830InputMode.Current100M;
        var gain = frame.Input == Sr830InputMode.Current100M ? 1e8 : 1e6;
        PlanValue Input(string suffix)
        {
            var raw = new VariableValue(new(frame.Prefix.Value + suffix));
            return current ? new ArithmeticValue(ArithmeticOperator.Divide, raw, new ConstantValue(new(gain, PhysicalUnit.Ohm))) : raw;
        }
        var columns = new List<CalculatedColumn>
        {
            new(new(resultPrefix.Value + ".input_x"), Input(".x_v")),
            new(new(resultPrefix.Value + ".input_y"), Input(".y_v")),
            new(new(resultPrefix.Value + ".input_r"), Input(".r_v")),
            new(new(resultPrefix.Value + ".nominal_input_sensitivity"), Input(".sensitivity_v")),
        };
        if (current) columns.Add(new(new(resultPrefix.Value + ".nominal_transimpedance_ohm"), new ConstantValue(new(gain, PhysicalUnit.Ohm))));
        return new(id, frame, columns);
    }
}
