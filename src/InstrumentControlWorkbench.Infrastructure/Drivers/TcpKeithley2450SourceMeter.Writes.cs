using System.Globalization;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

public sealed partial class TcpKeithley2450SourceMeter
{
    public async Task ConfigureSourceAsync(SourceSetpoint requested, CancellationToken token)
    {
        Keithley2450Limits.Validate(requested);
        await operations.WaitAsync(token);
        try
        {
            var previous = await ReconcileAsync(token);
            if (previous.Source.Function != requested.Function && previous.OutputEnabled) throw new InvalidOperationException("Turn output off before changing 2450 source function.");
            var prefix = SourceToken(requested.Function);
            if (previous.Source.Function != requested.Function)
            {
                await transport.WriteAsync($":SOUR:FUNC {prefix}", token);
                previous = await ReconcileAsync(token);
            }
            await transport.WriteAsync($":SOUR:{prefix}:RANG:AUTO ON", token);
            var level = $":SOUR:{prefix}:LEV {requested.Level.ToString("G17", CultureInfo.InvariantCulture)}";
            var compliance = $":SOUR:{prefix}:{(requested.Function == SourceFunction.Voltage ? "ILIM" : "VLIM")} {requested.Compliance.ToString("G17", CultureInfo.InvariantCulture)}";
            // Avoid a forbidden intermediate quadrant when raising the limit while
            // reducing a high source level. Otherwise lower the limit first.
            var lowerLevelFirst = false;
            try { Keithley2450Limits.Validate(new SourceSetpoint(requested.Function, previous.Source.Level, requested.Compliance)); }
            catch (ArgumentOutOfRangeException) { lowerLevelFirst = true; }
            await transport.WriteAsync(lowerLevelFirst ? level : compliance, token);
            await transport.WriteAsync(lowerLevelFirst ? compliance : level, token);
            await CheckErrorsAsync(token);
            var actual = await ReconcileAsync(token);
            if (actual.Source != requested) throw new IOException("2450 source configuration readback differs from the request; inspect actual settings before proceeding.");
        }
        finally { operations.Release(); }
    }
    public async Task ConfigureSenseAsync(SenseConfiguration requested, CancellationToken token)
    {
        Keithley2450Limits.Validate(requested);
        await operations.WaitAsync(token);
        try
        {
            RequireConnected(); var prefix = SenseToken(requested.Function);
            await transport.WriteAsync($":SENS:FUNC \"{prefix}\"", token);
            await transport.WriteAsync($":SENS:{prefix}:RSEN {(requested.Mode == SensingMode.FourWire ? "ON" : "OFF")}", token);
            await transport.WriteAsync($":SENS:{prefix}:NPLC {requested.Nplc.ToString("G17", CultureInfo.InvariantCulture)}", token);
            await transport.WriteAsync($":SENS:{prefix}:RANG:AUTO {(requested.AutoRange ? "ON" : "OFF")}", token);
            await CheckErrorsAsync(token);
            if ((await ReconcileAsync(token)).Sense != requested) throw new IOException("2450 sense configuration readback differs from the request.");
        }
        finally { operations.Release(); }
    }
    public async Task EnableOutputAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            await ReconcileAsync(token);
            await transport.WriteAsync(OutputOn, token); await CheckErrorsAsync(token);
            if (!Boolean(await transport.QueryAsync(OutputQuery, token))) throw new IOException("2450 output-enable command was not confirmed.");
            snapshot = snapshot with { OutputEnabled = true };
        }
        finally { operations.Release(); }
    }
    public async Task DisableOutputAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            RequireConnected(); await transport.WriteAsync(OutputOff, token); await CheckErrorsAsync(token);
            if (Boolean(await transport.QueryAsync(OutputQuery, token))) throw new IOException("2450 output-off command was not confirmed.");
            snapshot = snapshot with { OutputEnabled = false };
        }
        finally { operations.Release(); }
    }
    private const string OutputOn = ":OUTP ON";
}
