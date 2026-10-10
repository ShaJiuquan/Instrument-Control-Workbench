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

This repository is at the first instrument-control scaffold stage:

- Avalonia MVVM desktop shell.
- Keithley 2450-inspired source/meter ports.
- Typed source/sense parameters with K2450-style validation limits.
- Instrument lifecycle state machine.
- Exclusive instrument lease service with TTL expiry.
- Channel-based notification stream from backend services to ViewModels.
- Mock Keithley 2450 SourceMeter driver.
- Mock IV sweep flow with safe-stop cleanup.
- xUnit coverage for state transitions, leases, validators, and sweep notify.
- Four working pages: measurement, instrument connection/status, run data, events.
- Data-backed live I–V plot with automatic scaling and pointer readout.
- Validated scan configuration and state-aware start/connect/stop commands.

The workspace follows the visual direction of the companion Rust/egui PPMS
Studio: dark navy navigation, a quiet light workspace, purple acquisition
traces, separate live readouts, a large plot on the left, and compact scan
configuration on the right. Device details, current-run data, and events have
separate pages. Chinese text uses an explicit system-font fallback; no assets
from third-party instrument software are included.

Run data is currently session-only: restarting a scan replaces the previous
points. Persistent history and export remain future work. Only one simulated
Keithley 2450 is wired up; the mock does not enforce real current compliance.

## Project Layout

```text
src/
  InstrumentControlWorkbench.App/             Avalonia UI
  InstrumentControlWorkbench.Application/     Use cases, leases, notify, state orchestration
  InstrumentControlWorkbench.Core/            Domain models and use-case types
  InstrumentControlWorkbench.Infrastructure/  Mock adapters and future IO
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
  -> MockKeithley2450SourceMeter
```

The UI never calls a driver directly. All instrument access goes through the
Application layer, which owns lifecycle transitions, exclusive leases, TTL
refresh, safe stop, and notification publishing.

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
- SQLite-backed run history.
- Interactive plotting for live measurement feedback.

## License

MIT.
