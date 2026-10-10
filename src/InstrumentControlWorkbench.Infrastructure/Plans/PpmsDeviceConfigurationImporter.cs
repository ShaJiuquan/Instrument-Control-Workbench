using System.Text.Json;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using static InstrumentControlWorkbench.Infrastructure.Plans.JsonDeviceConfigurationDocument;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

// Explicit file snapshot only; no env, DB, builtin defaults or transport fallback.
public sealed class PpmsDeviceConfigurationImporter : IDeviceConfigurationImporter
{
    public DeviceConfigurationCatalog Parse(string text, InstrumentId? primarySource)
    {
        try
        {
            var root = Object(ParseObject(text), "devices"); var entries = Object(root["devices"]);
            if (entries.Count is < 1 or > 8) throw new FormatException("PPMS connection files require one to eight known devices.");
            var devices = entries.Select(p =>
            {
                var (model, category) = p.Key switch
                {
                    "k2450" => ("K2450", ConfiguredDeviceCategory.SourceMeter),
                    "k3706" => ("K3706", ConfiguredDeviceCategory.Matrix),
                    "k6221" => ("K6221", ConfiguredDeviceCategory.Source),
                    "k2182" => ("K2182", ConfiguredDeviceCategory.Meter),
                    "k6485" => ("K6485", ConfiguredDeviceCategory.Meter),
                    "k6514" => ("K6514", ConfiguredDeviceCategory.Meter),
                    "sr830" => ("SR830", ConfiguredDeviceCategory.LockIn),
                    "rotator" => ("ROTATOR", ConfiguredDeviceCategory.Rotation),
                    _ => throw new FormatException("Unknown PPMS device key " + p.Key),
                };
                var c = Object(p.Value); DeviceConnection connection;
                switch (Text(c, "transport"))
                {
                    case "tcp" when category != ConfiguredDeviceCategory.Rotation:
                        Object(c, "transport", "host", "port", "terminator", "timeout_s");
                        connection = new TcpDeviceConnection(Text(c, "host"), Integer(c, "port"), Text(c, "terminator"), Timeout(c, "timeout_s")); break;
                    case "visa" when category != ConfiguredDeviceCategory.Rotation:
                        Object(c, "transport", "resource_name"); connection = new VisaDeviceConnection(Text(c, "resource_name")); break;
                    case "serial" when category == ConfiguredDeviceCategory.Rotation:
                        Object(c, "transport", "port", "timeout_s"); connection = new SerialDeviceConnection(Text(c, "port"), Timeout(c, "timeout_s")); break;
                    default: throw new FormatException("Unsupported transport for PPMS device " + p.Key);
                }
                return new DeviceConfiguration(new(p.Key), model, model, category, connection);
            }).ToArray();
            var aliases = devices.Select(d => KeyValuePair.Create(d.Id.Value, d.Id)).ToList();
            if (devices.Any(d => d.Category == ConfiguredDeviceCategory.Rotation)) aliases.Add(KeyValuePair.Create("angle", new InstrumentId("rotator")));
            return new(primarySource, devices, aliases);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw new FormatException("Invalid PPMS connection document: " + e.Message, e); }
    }
    public async Task<DeviceConfigurationCatalog> LoadAsync(string path, InstrumentId? primarySource, CancellationToken token)
        => Parse(await ReadTextAsync(path, token), primarySource);
}
