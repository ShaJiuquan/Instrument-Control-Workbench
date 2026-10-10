# PPMS C# implementation program

## Objective and decisions

Implement as much of the existing Rust/Tauri PPMS functionality as possible in
the C# Instrument Control Workbench, preserving explicit architectural boundaries.
The user selected an independently implemented C# execution engine inspired by
OpenTAP; do not add OpenTAP as the engine dependency. Borrow QCoDeS parameter and
data-set concepts, Home Assistant device lifecycle concepts, and Node-RED flow
editing concepts. Existing Avalonia desktop direction remains in effect.

The user requested stage commits and publication. For now, publish to GitHub
only, on `codex/ppms-csharp-platform`; Gitee was deferred by the user. Do not
silently merge to main or publish credentials, lab endpoints, or private recipes.

## Authoritative scope

`ppms-feature-inventory.json` captures the current PPMS command schema and runtime
node registry at the recorded commit: 101 schema entries and 21 runtime node
kinds. Aliases describe compatibility requirements, not additional implemented
features. The schema, runtime implementation, instrument interfaces and UI must
be checked together; historical Python specifications are not sufficient proof
of current Rust behavior.

Additional functional families to carry forward:

- Device configuration, connection, disconnection, heartbeat, faults, capabilities
  and manual operation: K2450, K6221, K2182A, K3706, K6485, K6514, SR830, rotator,
  temperature, magnetic field, pressure and environment monitoring.
- Current/voltage sourcing, channel routing, sample/electrode configuration,
  resistance, two-/four-wire resistance, Hallbar, Van der Pauw, electrode tests,
  dV/dI, dI/dV and polarization acquisition.
- Nested scans, ramp/wait/stability behavior, behavior-tree flow controls,
  pause/resume/abort, progress, condition evaluation and reusable subflows.
- Versioned plans/settings, data-save sessions, experiment metadata, persistent
  history, export, real-time/history plots and operator-visible events.
- Headless execution/control boundary, transport status, startup recovery,
  shutdown safety, user/access-management behavior where PPMS implements it.

No simulated behavior counts as verified hardware support. Unsupported commands
must produce explicit errors; never register no-op implementations to inflate
parity. A feature needs implementation and relevant acceptance evidence before
its status changes to implemented.

## Boundaries

```text
Avalonia views -> ViewModels -> Application use cases -> Core contracts
                                                  <- Infrastructure adapters
```

- Core: typed IDs/units, parameter descriptions, capabilities, plan/data contracts,
  validation and local state machines; no UI, database, transport or scheduling IO.
- Application: device and run ownership, registry dispatch, pause/cancellation,
  resource leases, result delivery and finalization ordering.
- Infrastructure: deterministic mock and actual protocol drivers, persistence,
  transport and export adapters; no experiment workflow ownership.
- App: editing, configuration, monitoring and presentation; no driver access.
- Run-local context owns axes, variables, results and recipe snapshots. A run
  stays active through safe-stop, data finalization and archive/export completion.
- Notifications report facts. They do not call hardware or mutate workflow state.

## Stages and current evidence

1. **Desktop baseline — committed.** Preserve the working four-page Avalonia UI,
   configuration validation, cancellation and output-off reporting. Baseline
   warnings-as-errors build and 23 tests passed. Commit `5921ba3` is on GitHub.
2. **Parameter/data foundation — implemented and tested.** Typed
   parameter IDs/units/ranges/access; immutable configuration snapshots; checked
   column dependencies; run-local data with terminal write protection. Existing
   IV acquisition writes this model while retaining current chart notifications.
   Warnings-as-errors build and 35 tests passed, including unit/dependency
   rejection, snapshot isolation, consecutive runs and aborted partial data.
   No persistence or general experiment engine is claimed at this stage.
3. **Execution/resource foundation — implemented for the IV use case.** Unique
   run contexts, serialized admission, run-owned non-expiring leases, device
   ownership checks, cooperative pause/resume with output-off confirmation,
   interruptible dwell, bounded safe-stop and explicit safety recovery. A timed-out
   cleanup task remains attached to its run; recovery cannot overlap it. Execution
   and cleanup failures are preserved together. Result listeners receive snapshots;
   the run stays active through finalization. Independent notification subscribers
   receive ordered facts with a single publication timestamp. Tests cover conflict,
   retained unsafe ownership, non-cooperative stop, original/cleanup errors, paused
   abort, long-dwell pause and archive ordering/failure. These are backend APIs;
   The stage passes 56 tests across Core, Application and App with warnings-as-errors.
   pause/recovery controls and generic PPMS plan execution remain pending.
4. **Device/platform boundary — pending.** Capability registry and routing,
   multiple instruments, settings and actual transport adapters. Mock and real
   device paths remain visibly distinct.
5. **PPMS execution parity — pending.** Implement the inventory's command,
   measurement, scan and control-flow families with typed validation and tests.
6. **Data/history — pending.** Approved persistence/export dependencies, versioned
   storage and plans, metadata/provenance, history queries and interrupted-run
   recovery. Real data must survive application restart.
7. **Operator workflows — pending.** Flow editor, reusable steps, device panels,
   routing/sample settings and live/history views connected to the backend.
8. **Acceptance — pending.** End-to-end representative PPMS experiments,
   simulated transport faults, cancellation/cleanup ordering, UI interactions,
   portability/package checks and separately recorded hardware evidence.

## Verification and continuation

Run focused tests while implementing and `bash scripts/audit.sh` before commits.
The existing architecture scanner currently reports one warning for Infrastructure
imports in `App.axaml.cs`, the explicitly allowed composition root. This is not
a ViewModel-to-driver dependency. A later gate improvement should distinguish
those cases without removing actual boundary checks.

The service retains finalized snapshots for the current process in
`InMemoryRunHistory`. It has no disk persistence or restart recovery. Data-set
terminal state describes acquisition/cleanup; `RunOutcome` also reports result
listener failures. A successfully acquired data set can therefore be completed
while its overall outcome is faulted by an archive error. Finalization currently
waits for every started listener; no archive timeout or persistent retry queue is
claimed. Drivers and row listeners must honor cancellation during acquisition;
the bounded quarantine path covers safety cleanup, not arbitrary driver reads.
Notifications use unbounded subscriber queues; retention/backpressure and the
remaining source-meter-only ports still require platform work.

The optional global formatting check still reports pre-existing formatting in
`ViewLocator.cs`, `ViewModelBase.cs`, `Program.cs` and `ParameterDataSetTests.cs`.
Stage 3 changed C# files were formatted separately without rewriting unrelated
files. The standard warnings-as-errors build/test audit remains the commit gate.

Full-goal completion requires evidence against every inventory entry and the
additional families above. A green small test suite, registered names, mock UI,
or documentation does not prove parity or physical safety.
