# Headless execution and control

The standalone .NET 9 console project has no Avalonia dependency. It composes
existing device configuration, registry, compiler, run ownership and file archive
services. It is a second host for the same execution engine, not a new interpreter.
No dependency package is added.

## Validate and run

From the repository root:

```sh
dotnet run --project src/InstrumentControlWorkbench.Cli -- validate \
  --devices examples/devices/headless-simulation.devices.json \
  --plan examples/plans/calculated-source-scan.plan.json

dotnet run --project src/InstrumentControlWorkbench.Cli -- run \
  --devices examples/devices/headless-simulation.devices.json \
  --plan examples/plans/calculated-source-scan.plan.json \
  --archive /absolute/path/runs --csv /absolute/path/export.csv --sample "Sample A"
```

Device and native plan documents are mandatory. Missing/invalid documents do not
fall back to demo hardware or simulation. The supplied example explicitly declares
simulation devices with IDs matching the plan. Validation loads/compiles metadata
without connecting, acquiring leases or creating run records. Execution uses the
existing registered adapters; this stage's acceptance uses simulation only.
The run/validate commands consume native versioned plans. Raw PPMS trees can be
converted with the CLI import command and explicit context/routing selections;
see headless-ppms-import.md.

Stdout is newline-delimited JSON with `version: 1`. Facts include `validated`
(name, required device model/driver kind and execution budget), `started`
(actual `run-...` ID after archive metadata exists), `safety_recovery_required`
and `result` (state, total acquired rows, retained ownership, paths and errors).
These are host lifecycle reports; no unbounded stream of measurement telemetry is
queued to the console. They do not claim every diagnostic log or device heartbeat.

Exit codes: 0 completed/validated; 1 execution, cleanup or reporting failure;
130 aborted; 2 invalid input/startup/host/export error. Run results and partial
archives survive abort/fault. CSV uses the existing archive exporter. The engine
retains complete rows in the archive and bounded live memory. Used devices are
disconnected after confirmed cleanup. Exports may replace a user-specified existing
CSV under the exporter's established atomic replacement rules.

Ctrl+C and POSIX SIGTERM request cooperative cancellation. The process awaits
source-off/hold/matrix cleanup and archive finalization before returning. A
non-cooperative or failed stop leaves ownership retained: the process remains
alive and does not dispose its archive or pretend cleanup succeeded. OS force
termination and power loss cannot provide that guarantee; persisted archives
remain evidence, not hardware state.

## Local control file

Pass `--control-file /absolute/path/control.json`. Write a complete UTF-8 JSON
object (prefer atomic replacement) using the exact run ID reported by this host:

```json
{"version":1,"request_id":"cfb6dc8a-6867-42df-b20f-5293d7ed874a","run_id":"run-ACTUAL_ID","command":"pause"}
```

Commands are `status`, `pause`, `resume`, `abort`, `recover`. Recovery optionally
accepts `"reconnect": true`; this is an explicit fresh reconnect/stop through the
existing retained run and recovery gate. A failed recovery reports rejection and
keeps the process/ownership alive. Without a control file, stop failures require
external intervention; the process deliberately waits instead of exiting unsafe.

Pause reports request acceptance; use status until phase is `Paused` before
requesting resume. Status reports engine phase/rows/ownership, not a cached
assertion about live hardware outputs. Replies are `control_applied` or
`control_rejected`. Unknown/duplicate JSON fields, bad version/types, unknown
commands, stale run IDs and reused actuation request IDs reject. A new request ID
is required once a syntactically valid actuation request is processed, including
rejected run targets or operation attempts.

Polling runs every 100 ms with an 8192-byte input bound. Identical payloads are
not re-applied. Up to 4096 actuation IDs are retained for this one run; status does
not consume that budget. At capacity, new energizing controls are disabled while
status/abort/recovery remain available. New safety requests beyond that budget
are not added to the full ID cache, although identical file payloads and cached
old request IDs still reject/refrain from reapplying. Malformed/read errors do not
change owned hardware state. The poll task has a stored lifetime and is cancelled
and awaited at shutdown.

This is a local operator interface, not an authenticated remote server. Use a
control file writable only by the intended operator. Leases are process-local;
separate GUI/CLI instances do not coordinate physical device ownership. Operate
a physical setup from one host process. Do not treat this as cross-process leasing,
startup hardware reconciliation, or certified field-controller support.

## Packaging and verification

```sh
bash scripts/package-cli.sh --runtime osx-arm64 --output /absolute/new/cli-directory
/absolute/new/cli-directory/InstrumentControlWorkbench.Cli --help
```

The script publishes a self-contained executable, refuses existing/dangling output
paths and checks the executable/runtime manifests. Its runtime choices are osx
arm64/x64, Linux arm64/x64 and Windows x64; only osx-arm64 is executed in this
stage. Source archives without Git report unknown source revision until the stage
publication audit appends the exact archived source commit.

Tests cover metadata-only validation, actual simulated run/archive/CSV, partial
cancellation, scoped file commands, malformed/stale controls, failed-stop hold and
explicit recovery, reporting failure and option/path errors. Separate actual CLI
process acceptance validates, executes a six-row calculated scan, and responds
to SIGTERM with an Aborted finalized archive and exit 130. No desktop window is
opened; native rendered UI acceptance remains deferred per the user.

## Field-rate migration finding

The recorded Rust schema's `field_set_ramp_rate` takes `rate_a_per_s` and updates
two magnet-current zones. `command/field.rs` supports an optional zone index;
`drivers/autocube/field_writes.rs` supplies ranges 45.275/100 and caps
0.0379/0.0189 A/s. Its `range_t` naming conflicts with the driver's current-zone
comments/raw constants. These are source observations at the inventory commit,
not verified hardware specifications. The existing C# axis rate is Tesla/second.
No implicit A/s -> T/s mapping was introduced. Dedicated zone/configuration ports,
actual readback and model-specific conversion evidence remain required before
claiming this command migrated.
