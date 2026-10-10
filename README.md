# Instrument Control Workbench

Instrument Control Workbench is a cross-platform desktop application prototype
for laboratory instrument control, measurement automation, and safe run
monitoring. It is built with Avalonia and .NET.

The project is intentionally public and portfolio-friendly: it demonstrates
instrument orchestration patterns without depending on private laboratory
hardware, credentials, or proprietary experiment definitions.

## Goals

- Connect and monitor mock laboratory instruments.
- Configure and run measurement workflows such as IV sweeps and timed reads.
- Show live readings, run status, logs, and safety state in a native UI.
- Keep measurement execution in the backend layer; the UI observes and commands.
- Grow toward real serial/VISA/WebSocket instrument backends behind testable
  interfaces.

## Current Status

The desktop prototype and execution foundation currently provide:

- Avalonia MVVM desktop shell.
- Keithley 2450-inspired source/meter ports.
- Typed source/sense parameters with K2450-style validation limits.
- Instrument lifecycle state machine.
- Manual leases with TTL expiry; non-expiring run ownership through safety confirmation and result finalization.
- Broadcast notification subscriptions with typed device/step/row facts.
- Mock Keithley 2450 SourceMeter driver.
- Native-SCPI K2450 raw TCP adapter behind the same execution/resource ports, with [protocol acceptance and hardware limits](docs/k2450-tcp.md).
- Typed manual source ranges with actual readback, PPMS import, scan bounds and pause drift guards; [contract and runnable example](docs/source-ranges.md).
- IV recipes and typed multi-device plans share an executor with pause, abort, verified cleanup and retained unsafe ownership.
- Simulated temperature, magnetic field and rotation controllers with explicit target/rate/readback/hold policies.
- Nested scans, joint reads, iteration/conditions, delay and stability waits through the application API.
- Versioned plan files with preflight validation, bounded JSON parsing and atomic replacement.
- Runnable [field/voltage](examples/plans/field-voltage.plan.json) and [field/resistance](examples/plans/field-resistance.plan.json) plans, with a [document format](docs/plan-documents.md).
- Coherent source/sense resistance reads and a typed Voltage DC recipe, sharing the same executor and cleanup.
- Versioned run archives with sample metadata, plan/configuration snapshots, typed rows, terminal markers and streamed CSV export through the application API.
- xUnit coverage for state transitions, leases, validators, acquisition and archive integrity.
- Four working pages: measurement, instrument connection/status, run data, events.
- Data-backed live I–V plot with automatic scaling and pointer readout.
- Validated scan configuration and state-aware start/connect/stop commands.

The workspace follows the visual direction of the companion Rust/egui PPMS
Studio: dark navy navigation, a quiet light workspace, purple acquisition
traces, separate live readouts, a large plot on the left, and compact scan
configuration on the right. Device details, current-run data, and events have
separate pages. Chinese text uses an explicit system-font fallback; no assets
from third-party instrument software are included.

The published desktop retains its IV workflow and session-only plot. Multi-device
plans and durable run history/export are tested through the application API; their
desktop integration is pending native acceptance. See [run archive format and
recovery limits](docs/run-archives.md). Plan/pause/recovery/history UI changes are
under implementation.
The published desktop defaults to simulation; the source mock does not enforce
real current compliance. The configured backend additionally supports K2450 TCP
with local protocol tests; actual hardware acceptance remains pending.

## PPMS implementation program

The C# PPMS implementation is progressing on `codex/ppms-csharp-platform`.
See [migration scope and stage evidence](docs/ppms-migration.md) and the
[PPMS feature inventory](docs/ppms-feature-inventory.json). The execution engine
will be implemented in C#, borrowing OpenTAP lifecycle concepts without depending
on OpenTAP itself. Parameters and run data now have typed units, validated
dependencies and immutable recipe/instrument snapshots. They are populated by
the working IV and generic plan flows. Versioned plans can be saved and loaded;
the result archive API persists run data and streams CSV exports. PPMS JSON
import, desktop acceptance and full command parity remain pending.

## Project Layout

```text
src/
  InstrumentControlWorkbench.App/             Avalonia UI
  InstrumentControlWorkbench.Application/     Use cases, leases, notify, state orchestration
  InstrumentControlWorkbench.Core/            Domain models and use-case types
  InstrumentControlWorkbench.Infrastructure/  Mock/TCP adapters, documents and archives
tests/
  InstrumentControlWorkbench.Core.Tests/      Unit tests
  InstrumentControlWorkbench.Application.Tests/
  InstrumentControlWorkbench.App.Tests/       Input and command-state tests
docs/
  roadmap.md                                  Implementation plan
```

## Architecture

```text
XAML View
  -> ViewModel
  -> MeasurementRunService
  -> InstrumentManager + InstrumentLeaseService + NotificationHub
  -> ISourceMeterPort
  -> MockKeithley2450SourceMeter / TcpKeithley2450SourceMeter
```

The UI never calls a driver directly. All instrument access goes through the
Application layer, which owns lifecycle transitions, exclusive group leases,
run finalization, safe-stop recovery and notification publishing.

## Run

```bash
dotnet restore
dotnet run --project src/InstrumentControlWorkbench.App
```

## Build And Test

```bash
dotnet build InstrumentControlWorkbench.sln
dotnet test InstrumentControlWorkbench.sln
```

## Portfolio Direction

The project is aimed at desktop engineering and scientific/industrial control
roles. The planned feature set focuses on:

- MVVM desktop application architecture.
- Safe instrument control with abort and output-off semantics.
- Realtime measurement status.
- Mock-first drivers for reproducible demos.
- Persistent run history (current file archive adapter; database choice remains open).
- Interactive plotting for live measurement feedback.

## License

MIT.
