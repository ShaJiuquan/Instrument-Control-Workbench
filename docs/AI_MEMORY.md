# AI Memory

This file stores durable project decisions so future AI sessions do not
re-litigate them or accidentally undo them.

## Product Direction

- Native desktop application, not a web app.
- Portfolio-friendly public demo: no private lab protocols, addresses,
  credentials, or proprietary recipes.
- The app should feel like a scientific/industrial operator workbench: dense,
  calm, inspectable, and safe.

## Architecture Decisions

- Core owns domain types, validation, ports, and state machines.
- Application owns workflows, leases, safe-stop, cancellation, and
  notifications.
- Infrastructure owns driver adapters and protocol details.
- App owns Avalonia UI and composition.
- UI should observe and command application services; it should not drive
  hardware directly.

## Current Known Debt

- NotificationHub is a single-reader channel, not a broadcast hub.
- Run data currently holds only the last run in memory. It is explicitly marked
  as session-only and is replaced on the next run; there is no disk history yet.
- The mock driver does not model current compliance enforcement. Its readings
  must not be used as evidence of real hardware protection.

## Workbench UI (2026-10-10)

- Four functional pages: measurement, instruments, current run data, events.
- MainWindow composes named views; shared visual values live in Workbench.axaml.
- Visual direction follows the companion Rust/egui PPMS Studio: navy navigation,
  light workspace, purple accent, separate readout cards, left plot/right setup.
- Readout values and units are separate typography; Chinese glyphs use an explicit
  platform font fallback. No new fonts or plotting dependencies were introduced.
- Configuration can scroll at the minimum 1060 x 720 size while start/stop stay
  pinned; the plot and optional point table share the remaining vertical space.
- App startup constructs the driver/services and injects them into the ViewModel.
- MeasurementPlot draws received MeasurementPoint data with Avalonia's existing
  drawing API. It is a small I–V renderer, not a general analysis plotting library.
- Start validates text and explicit units; configuration locks during runs.
- Stop means request cancellation and wait for output-off notifications. There
  is no software button claiming to be a physical emergency stop.
- Closing the window awaits running commands and stops the notification listener.

These are tracked as design debt, not as permission to copy the pattern.

## Approved Dependencies

- Avalonia 11.x
- CommunityToolkit.Mvvm
- xUnit

Dependencies not listed here require a short evaluation and user choice before
implementation.
