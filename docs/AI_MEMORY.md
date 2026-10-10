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

- NotificationHub broadcasts to independent subscriptions. Queues remain
  unbounded; consumers must manage subscription lifetimes.
- LatestDataSet is the latest run; InMemoryRunHistory keeps terminal snapshots
  for this process. Neither is disk history or restart recovery.
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

## C# execution migration (2026-10-10)

- The user chose an independent C# execution engine inspired by OpenTAP,
  not an OpenTAP package integration. Publish stage commits to GitHub only;
  Gitee synchronization is deferred. See ppms-migration.md and its inventory.
- IV runs own leases until output is verified off and result listeners finish.
  Cleanup failure/timeout retains ownership and requires RecoverSafetyAsync.
  Do not expire these leases on heartbeat TTL or report cleanup success merely
  because a stop command returned.
- Pause cooperatively stops output at a checkpoint and retains run identity and
  resources. Dwell timers react to pause. Pause/recovery UI is still migration work.
- A finalizer failure faults RunOutcome while retaining the acquired data set.
- Desktop manual configuration/start commands also check backend ownership.

## Device platform foundation (2026-10-11)

- DeviceRegistry uses IDeviceSession application contracts. InstrumentManager
  and AxisControllerSession share the same ownership gate; the legacy IV path
  and new multi-device path cannot bypass each other's active leases.
- Group lease acquisition is atomic. Claims do not touch hardware, so an
  admission failure rolls back its own claims and never stops a foreign owner.
- DeviceRunResources retains each pending stop task and requires fresh successful
  stop confirmation for all devices before release. Explicit recovery retries
  completed failures; output/parameter mutations invalidate previous success.
- Source stop means confirmed output-off. Axis stop means hold current position,
  not magnet zero or temperature shutdown. No hardware safety certification is
  implied. These simulation profiles are explicitly DriverKind.Simulation.
- Temperature rate is Kelvin/minute, field rate Tesla/second, rotation rate
  Degree/second. TimeProvider-backed simulators have no detached background task.
- Source-mode-dependent parameters require explicit mode/compliance selection.
- Actual transport adapters, remaining PPMS instruments, generic plan execution,
  persistent settings and device panels remain migration work.

## Source preparation safety (2026-10-11)

- InstrumentManager.EnterRunAsync requires a claimed run owner before output
  enable. Manual source output must also use an exclusive resource scope.
- ConfigureAsync confirms output is off before changing source mode/configuration.
  A connected instrument with output already on is rejected until safe stop;
  this rejection preserves its configuration and is not a driver-fault event.
- The follow-up regression gate passes 77 tests. Actual startup recovery UX and
  hardware transport acceptance remain pending.

## Group reconnect recovery (2026-10-11)

- A completed failed stop may require reconnecting its device before another
  confirmation can succeed. ConnectAllAsync allows that reconnect under the
  same resource owner; it does not permit output/parameter changes while an
  unresolved stop remains, and cannot overlap a live pending stop.
- Reconnection invalidates prior confirmation. StopAllAsync must confirm every
  device again before ReleaseAsync. A failed initial connect no longer leaves
  the owned group without a recovery path.
