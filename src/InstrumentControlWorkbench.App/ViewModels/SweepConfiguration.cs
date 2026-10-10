using System;
using System.Globalization;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.App.ViewModels;

public static class SweepConfiguration
{
    public static bool TryCreate(string start, string stop, string points, string dwell, string complianceMicroAmps,
        out IvSweepRecipe recipe, out string error)
    {
        recipe = null!;
        error = "";
        if (!Parse(start, out var startValue) || !Parse(stop, out var stopValue))
            error = "起点与终点必须是有效电压数值，例如 -0.2。";
        else if (!int.TryParse(points, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 2 || count > 10000)
            error = "采集点数必须是 2–10000 的整数。";
        else if (!Parse(dwell, out var delay) || delay < 0.02 || delay > 60)
            error = "采集间隔必须在 0.02–60 s 之间。";
        else if (!Parse(complianceMicroAmps, out var limit))
            error = "电流限制必须是有效数值，单位为 µA。";
        else
        {
            try
            {
                Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, startValue, limit * 1e-6));
                Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, stopValue, limit * 1e-6));
                recipe = new IvSweepRecipe("I–V 扫描", startValue, stopValue, count, limit * 1e-6, delay);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "电压范围为 ±210 V；电流限制范围为 0.001–1050000 µA。";
            }
        }
        return false;
    }
    private static bool Parse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
