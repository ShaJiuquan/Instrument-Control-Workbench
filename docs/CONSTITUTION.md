# Instrument Control Workbench Constitution

This document is the project-specific law for AI-assisted changes. It is short
on purpose: if a rule is not here, follow the closest existing clean example in
the repository.

## 0. Architecture Direction

Allowed dependency direction:

```text
App -> Application -> Core
Infrastructure -> Core
App composition root -> Infrastructure
```

The ViewModel layer must not construct concrete drivers. Infrastructure objects
belong in the application startup/composition root, then flow inward through
interfaces.

Forbidden:

- XAML code-behind calling drivers or services directly.
- ViewModels importing `InstrumentControlWorkbench.Infrastructure.*` except in
  temporary prototype code explicitly tracked as debt.
- Core referencing Avalonia, CommunityToolkit.Mvvm, Infrastructure, or tests.
- Infrastructure owning business workflow state.

## 1. Avalonia UI Shape

Views are for layout and binding. They must not encode domain workflow rules.

Rules:

- Use compiled bindings with `x:DataType`.
- Prefer small named views for major regions: shell nav, instrument list,
  selected instrument panel, run monitor, event log.
- Keep visual resources centralized once reused more than twice.
- Do not draw fake production charts with hard-coded `Canvas` polylines after a
  real plotting control is introduced.
- Buttons that start, abort, connect, or output-enable hardware must bind to
  commands with `CanExecute` state.
- Text must wrap or trim predictably at the minimum supported window size.
- No marketing page inside the app shell. The first screen is the workbench.

## 2. ViewModel Shape

ViewModels adapt application state for binding; they do not own hardware.

Rules:

- Inject application services or a screen model. Do not `new` drivers in a
  ViewModel.
- Keep demo fixture data behind a named demo data provider.
- Prefer typed UI state records over stringly state when state drives behavior.
- Long-running commands accept cancellation or call an application service that
  owns cancellation.
- Implement disposal when a ViewModel starts background listeners.
- Derived properties must notify dependents when source properties change.

## 3. Application Service Shape

Application services own use cases: connect, configure, acquire lease, run,
abort, safe stop, publish notification.

Rules:

- Use `CancellationToken` on every async operation that can wait on IO, locks,
  timers, or driver calls.
- Safe-stop cleanup must run even when the operator cancels or a driver faults.
- Exclusive leases are acquired before instrument output can be enabled.
- Service methods must publish meaningful state transitions for the UI.
- Do not swallow exceptions that indicate unsafe or unknown hardware state.

## 4. Core Domain Shape

Core contains domain records, enums, ports, validation, and state machines.

Rules:

- Core has no UI or IO framework dependencies.
- Instrument IDs, run IDs, units, ranges, and lifecycle states are explicit
  domain concepts.
- Validation belongs near the domain type it protects.
- State machines reject invalid transitions instead of silently coercing them.

## 5. Infrastructure Shape

Infrastructure adapts real or mock external systems to Core ports.

Rules:

- Mock drivers must be deterministic by default so tests and demos are stable.
- Real drivers must keep SCPI/protocol quirks inside the adapter.
- Never leak private lab addresses, credentials, or proprietary recipes.
- Hardware adapters must expose safe-stop behavior through the port.

## 6. Notifications

Notifications are facts about state changes; they are not a hidden command bus.

Rules:

- A notification includes kind, message, timestamp, and relevant ID.
- Use notifications to update UI and logs, not to perform driver operations.
- If multiple consumers are required, introduce an explicit broadcast strategy
  before relying on a single `ChannelReader`.

## 7. Units And Numeric Safety

Instrument-control code must be clear about units.

Rules:

- Public domain APIs should not accept unexplained naked doubles for voltage,
  current, resistance, time, or compliance once the concept escapes a small
  local calculation.
- Display formatting belongs in App/ViewModel, not Core.
- Parsing user-entered numeric text must use invariant culture or explicit
  culture handling.
- Clamp only UI convenience values. Domain validation should reject invalid
  requests with clear errors.

## 8. Async, Concurrency, And Cancellation

Rules:

- No fire-and-forget task without a stored lifetime, cancellation, and exception
  handling plan.
- Do not block the UI thread with `.Result`, `.Wait()`, or sync-over-async.
- Locks around async code must use `SemaphoreSlim` or another async-aware
  primitive.
- Cancellation is not failure, but cancellation must still leave outputs safe.

## 9. Errors

Rules:

- Use domain/application exceptions with actionable messages at boundaries.
- Do not display raw low-level protocol details unless useful to an operator.
- Preserve the original exception when wrapping.
- Tests should assert important failure behavior, not just exception type.

## 10. Persistence And Export

Rules:

- User data formats must be explicit and versioned once persistence is added.
- SQLite, CSV, or other storage libraries require dependency evaluation before
  implementation unless already approved.
- Export must include run metadata, instrument ID, timestamp, units, and recipe.

## 11. Tests

Required test areas:

- Instrument lifecycle valid and invalid transitions.
- K2450 limits and future instrument limit tables.
- Lease acquire, refresh, expiry, release, and conflict.
- Run success, abort, driver fault, and safe-stop cleanup.
- Notification sequence for operator-visible workflows.
- ViewModel command state and parsing for safety-critical controls.

## 12. Dependency Rule

Before adding a dependency for plotting, persistence, serial/VISA IO, DI,
logging, validation, settings, or test helpers, provide a short table:

| Option | Tradeoff |
| --- | --- |
| Hand-write | Full control, more maintenance |
| Candidate package | Less code, dependency/version risk |

Wait for explicit user choice unless the package is already present.

