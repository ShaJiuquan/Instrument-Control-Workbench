# PPMS import from the command line

The CLI `import` command delegates to the existing Application workspace and PPMS
format adapter. It adds no executor or JSON-to-driver path.

```sh
dotnet run --project src/InstrumentControlWorkbench.Cli -- import \
  --devices examples/devices/headless-routing.devices.json \
  --plan examples/plans/headless-import-context.plan.json \
  --ppms examples/plans/ppms-field-voltage-import.json \
  --routing examples/profiles/headless-import-routing.json \
  --output /absolute/path/imported.plan.json

dotnet run --project src/InstrumentControlWorkbench.Cli -- run \
  --devices examples/devices/headless-routing.devices.json \
  --plan /absolute/path/imported.plan.json \
  --archive /absolute/path/runs --csv /absolute/path/export.csv
```

`--plan` is an explicit validated native context, not the tree to execute during
import. Its source preparations/current-source preparations and execution budget
are used. Device aliases, controller definitions, current-source and standalone
meter roles come from the same configured runtime used for full preflight.
Its root is validated but is not appended to the imported tree. Native routing
inside that context is not silently converted into external import bindings.

`--ppms` names a raw runtime-tree file. `--output` receives the fully validated
native plan via the existing atomic save API. Import does not connect devices,
acquire run leases, enable outputs or create archives. An unsupported node,
invalid binding/profile/budget or cancelled import leaves an existing output
unchanged. CLI argument validation prevents output from replacing other named
inputs; the routing loader also protects its three referenced profile files.

Without channel/sample routing, omit `--routing`. For example:

```sh
dotnet run --project src/InstrumentControlWorkbench.Cli -- import \
  --devices examples/devices/k6221-meter-devices.json \
  --plan examples/plans/headless-current-import-context.plan.json \
  --ppms examples/plans/ppms-current-resistance-import.json \
  --output /absolute/path/current.plan.json
```

The explicit K6221 current preparation and aliased K2182 standalone meter retain
their different roles. No combined device or channel wiring is invented.

## Routing selection document

The optional routing document describes import selections, not physical driver
commands. It is bounded to 64 KiB, JSON depth 16 and 1–64 wiring bindings. Unknown
and duplicate fields, unsupported versions and malformed values reject.

```json
{
  "format": "instrument-workbench-import-routing",
  "version": 1,
  "channels": "channel_profiles.json",
  "samples": "samples.json",
  "ports": "instrument_ports.json",
  "matrix": "matrix",
  "channel_wiring": "k2450",
  "route_settle_s": 0,
  "wirings": [{"id": "k2450", "source": "k2450"}]
}
```

Profile paths resolve relative to this document's directory; absolute paths are
also accepted. `matrix`, `source` and optional `voltage_meter` are actual enabled
configured device IDs. Wiring IDs refer to the loaded physical profile catalog.
The Application workspace verifies matrix/source/meter categories and constructs
one frozen channel/sample routing authority, then full plan compilation checks
actual card bounds, guards, sample topology, units, ranges and dependencies.
Unbound profile layouts/metadata remain preserved according to the existing
profile import model; they are not made into fictitious devices.

All four profile/example files in `examples/profiles` are simulation fixtures;
the accompanying device document declares a K3706 simulator with its actual
8×12 card and the K2450 guard. The source context declares FourWire preparation
explicitly. Update these authored documents to match a different supported setup.

## Reports and acceptance

On success stdout emits one version-1 JSON `imported` fact: native plan name,
input/output paths, routing presence, preserved execution budget and required
registered IDs. It does not claim an experiment ran. Input/import failures use the
CLI's existing nonzero host error path. Run/archive/CSV remain separate commands.

Tests cover routed field/voltage import -> six rows, and field/four-wire/Hall/vdP
sample import -> 28 rows in the current collection model (raw observations,
analyses and joint-point summaries). Saved plans run in fresh runtimes, reopen
complete archives and export CSV. Pure K6221/K2182 import runs two rows without
routing. Tests also cover wrong roles, malformed/duplicate/version/oversized
selection, missing inputs, unsupported nodes, cancellation, budget rejection,
input protection and argument slots. No device connects during import.

Actual CLI subprocess acceptance imports/runs both routed fixtures and checks
six/28 rows and finalized archives. Only deterministic simulation is exercised.
Unsupported PPMS measurements/controller modes/protocols are still unsupported;
this command does not broaden the underlying importer or certify real hardware.
No dependency, Core plan/archive format, desktop window or Rust changes are added.
