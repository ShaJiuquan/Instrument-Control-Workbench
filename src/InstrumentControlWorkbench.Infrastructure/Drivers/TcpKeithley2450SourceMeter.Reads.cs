using System.Globalization;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

public sealed partial class TcpKeithley2450SourceMeter
{
    private async Task<SourceMeterSnapshot> ReconcileAsync(CancellationToken token)
    {
        RequireConnected();
        var output = Boolean(await transport.QueryAsync(OutputQuery, token));
        var function = Word(await transport.QueryAsync(":SOUR:FUNC?", token)) switch { "VOLT" or "VOLTAGE" => SourceFunction.Voltage, "CURR" or "CURRENT" => SourceFunction.Current, _ => throw new IOException("Unknown 2450 source function readback.") };
        var sourceToken = SourceToken(function);
        var level = Number(await transport.QueryAsync($":SOUR:{sourceToken}:LEV?", token));
        var compliance = Number(await transport.QueryAsync(function == SourceFunction.Voltage ? ":SOUR:VOLT:ILIM?" : ":SOUR:CURR:VLIM?", token));
        Quantity? manualRange = Boolean(await transport.QueryAsync($":SOUR:{sourceToken}:RANG:AUTO?", token)) ? null :
            new(Number(await transport.QueryAsync($":SOUR:{sourceToken}:RANG?", token)), function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere);
        var senseFunction = Word(await transport.QueryAsync(":SENS:FUNC?", token)) switch
        { "VOLT" or "VOLTAGE" or "VOLT:DC" or "VOLTAGE:DC" => SenseFunction.Voltage, "CURR" or "CURRENT" or "CURR:DC" or "CURRENT:DC" => SenseFunction.Current, "RES" or "RESISTANCE" => SenseFunction.Resistance, _ => throw new IOException("Unknown 2450 sense function readback.") };
        var senseToken = SenseToken(senseFunction);
        var mode = Boolean(await transport.QueryAsync($":SENS:{senseToken}:RSEN?", token)) ? SensingMode.FourWire : SensingMode.TwoWire;
        var nplc = Number(await transport.QueryAsync($":SENS:{senseToken}:NPLC?", token));
        var autoRange = Boolean(await transport.QueryAsync($":SENS:{senseToken}:RANG:AUTO?", token));
        Quantity? senseRange = autoRange ? null : new(Number(await transport.QueryAsync($":SENS:{senseToken}:RANG?", token)), Keithley2450SenseRanges.For(senseFunction)[0].Unit);
        var terminals = Word(await transport.QueryAsync(":ROUT:TERM?", token)) switch { "FRON" or "FRONT" => Terminals.Front, "REAR" => Terminals.Rear, _ => throw new IOException("Unknown 2450 terminal readback.") };
        var source = new SourceSetpoint(function, level, compliance, manualRange); var sense = new SenseConfiguration(senseFunction, mode, nplc, autoRange, senseRange, await ReadAcquisitionSettingsAsync(senseFunction, token));
        Keithley2450Limits.Validate(source); Keithley2450Limits.Validate(sense);
        await CheckErrorsAsync(token);
        snapshot = new(Id, snapshot.Identifier, InstrumentLifecycleState.Idle, true, output, terminals, source, sense, DateTimeOffset.UtcNow);
        return snapshot;
    }
    private async Task<Keithley2450AcquisitionSettings> ReadAcquisitionSettingsAsync(SenseFunction function, CancellationToken token)
    {
        var prefix = SenseToken(function);
        var autoZero = Boolean(await transport.QueryAsync($":SENS:{prefix}:AZER?", token));
        var enabled = Boolean(await transport.QueryAsync($":SENS:{prefix}:AVER:STAT?", token));
        var count = Number(await transport.QueryAsync($":SENS:{prefix}:AVER:COUN?", token));
        if (count is < 1 or > 100 || count != Math.Truncate(count)) throw new IOException("2450 averaging count must be an integer from 1 to 100.");
        var mode = Word(await transport.QueryAsync($":SENS:{prefix}:AVER:TCON?", token)) switch
        { "REP" or "REPEAT" => AveragingMode.Repeat, "MOV" or "MOVING" => AveragingMode.Moving, _ => throw new IOException("Unknown 2450 averaging mode.") };
        var rebound = function != SenseFunction.Resistance && Boolean(await transport.QueryAsync($":SENS:{prefix}:RANG:AUTO:REB?", token));
        return new(autoZero, rebound, enabled, (int)count, mode);
    }

    public async Task<MeasurementValue> ReadAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            var state = await ReconcileAsync(token); RequireOutputForAcquisition(state);
            await PrepareAcquisitionAsync(token);
            var raw = (await transport.QueryAsync(Measure, token)).Split(',');
            if (raw.Length != 3) throw new IOException("2450 acquisition requires a value and both buffer status words.");
            var value = Number(raw[0]); var status = new Keithley2450ReadingStatus(StatusWord(raw[1]), StatusWord(raw[2])); await CheckErrorsAsync(token);
            return new(value, state.Sense.Function switch { SenseFunction.Voltage => "V", SenseFunction.Current => "A", _ => "ohm" }, DateTimeOffset.UtcNow, status);
        }
        finally { operations.Release(); }
    }
    public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            var state = await ReconcileAsync(token); RequireOutputForAcquisition(state);
            var prefix = $":SOUR:{SourceToken(state.Source.Function)}:READ:BACK";
            await transport.WriteAsync(prefix + " ON", token);
            if (!Boolean(await transport.QueryAsync(prefix + "?", token))) throw new IOException("2450 actual source readback could not be enabled.");
            await PrepareAcquisitionAsync(token);
            var raw = (await transport.QueryAsync(MeasureSourceSense, token)).Split(',');
            if (raw.Length != 4) throw new IOException("2450 acquisition requires one source/sense pair and both buffer status words.");
            var source = Number(raw[0]); var sense = Number(raw[1]); var status = new Keithley2450ReadingStatus(StatusWord(raw[2]), StatusWord(raw[3])); await CheckErrorsAsync(token);
            return new(state.Source.Function, state.Sense.Function,
                new(source, state.Source.Function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere),
                new(sense, state.Sense.Function switch { SenseFunction.Voltage => PhysicalUnit.Volt, SenseFunction.Current => PhysicalUnit.Ampere, _ => PhysicalUnit.Ohm }), DateTimeOffset.UtcNow, status);
        }
        finally { operations.Release(); }
    }
    private async Task PrepareAcquisitionAsync(CancellationToken token)
    {
        await transport.WriteAsync(AsciiFormat, token);
        await transport.WriteAsync(SingleCount, token);
        await CheckErrorsAsync(token);
        if (Word(await transport.QueryAsync(":FORM:DATA?", token)) is not ("ASC" or "ASCII") || Number(await transport.QueryAsync(":SENS:COUN?", token)) != 1)
            throw new IOException("2450 acquisition format/count was not confirmed.");
    }
    private static void RequireOutputForAcquisition(SourceMeterSnapshot state)
    { if (!state.OutputEnabled) throw new InvalidOperationException("Enable output under the run owner before a 2450 acquisition; SCPI measurement must not implicitly enable it."); }
    private async Task CheckErrorsAsync(CancellationToken token)
    {
        var text = await transport.QueryAsync(ErrorQuery, token); var comma = text.IndexOf(',');
        if (!int.TryParse((comma < 0 ? text : text[..comma]).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)) throw new IOException("Malformed 2450 error response.");
        if (code != 0) throw new IOException($"2450 SCPI error {code}: {text}");
    }
    private static string Word(string text) => text.Trim().Trim('"').ToUpperInvariant();
    private static bool Boolean(string text) => Word(text) switch { "0" or "OFF" => false, "1" or "ON" => true, _ => throw new IOException("Malformed SCPI boolean response.") };
    private static double Number(string text)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || Math.Abs(value) >= 9e36)
            throw new IOException("SCPI numeric reply is malformed, nonfinite or overrange.");
        return value;
    }
    private static ushort StatusWord(string text)
    {
        var value = Number(text);
        if (value < 0 || value > ushort.MaxValue || value != Math.Truncate(value)) throw new IOException("2450 buffer status must be an unsigned 16-bit integer.");
        return (ushort)value;
    }
    private static string SourceToken(SourceFunction function) => function == SourceFunction.Voltage ? "VOLT" : "CURR";
    private static string SenseToken(SenseFunction function) => function switch { SenseFunction.Voltage => "VOLT", SenseFunction.Current => "CURR", _ => "RES" };
    private const string Measure = ":MEAS? \"defbuffer1\", READ, SOURSTAT, STAT";
    private const string MeasureSourceSense = ":MEAS? \"defbuffer1\", SOUR, READ, SOURSTAT, STAT";
    private const string AsciiFormat = ":FORM:DATA ASC";
    private const string SingleCount = ":SENS:COUN 1";
}
