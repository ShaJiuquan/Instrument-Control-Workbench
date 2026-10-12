using System;
using System.Collections.Generic;
using System.Globalization;

namespace InstrumentControlWorkbench.App;

// Optional launch geometry for repeatable native layout checks. Window minimums
// still belong to the view; this never changes device or execution settings.
public sealed record StartupWindowSize
{
    private StartupWindowSize(int width, int height) { Width = width; Height = height; }
    public int Width { get; }
    public int Height { get; }
    public static StartupWindowSize? Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? value = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument != "--window-size" && !argument.StartsWith("--window-size=", StringComparison.Ordinal)) continue;
            if (value is not null) throw new ArgumentException("Specify --window-size once.", nameof(arguments));
            if (argument == "--window-size")
            {
                if (++i == arguments.Count) throw new ArgumentException("--window-size requires WIDTHxHEIGHT.", nameof(arguments));
                value = arguments[i];
            }
            else value = argument["--window-size=".Length..];
        }
        if (value is null) return null;
        var parts = value.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) || width is < 320 or > 16384 || height is < 240 or > 16384)
            throw new ArgumentException("--window-size requires WIDTHxHEIGHT within 320..16384 by 240..16384 logical pixels; the view minimums still apply.", nameof(arguments));
        return new(width, height);
    }
}
