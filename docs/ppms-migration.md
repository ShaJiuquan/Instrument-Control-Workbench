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
   abort, long-dwell pause and archive ordering/failure. This stage passed 56 tests
   across Core, Application and App with warnings-as-errors. These are backend APIs;
   pause/recovery controls and generic PPMS plan execution remain pending.
4. **Device/platform boundary — partially implemented.** Immutable capability
   descriptors and parameter/status validation; a registry of independent device
   instances; shared manual/run authorization; atomic group leases and rollback;
   run resource groups with retained cleanup tasks, per-device errors, recovery
   and verified release. K2450 current/voltage/readback and disconnect paths share
   the same ownership gate as existing IV execution. Deterministic temperature,
   field and rotation simulators implement target, rate, evolving readback and
   hold, using explicit Kelvin/minute, Tesla/second and Degree/second units. No
   automatic field return to zero is implemented. Device metadata explicitly says
   Simulation. The standard audit passes 75 tests (23 Core, 40 Application,
   12 App), with a warnings-as-errors build. Changed C# files pass formatting
   verification. Hardware transports, other instrument families, saved settings,
   heartbeat automation and device UI integration remain pending. The desktop
   still presents its existing K2450 workflow; the new registry is exercised
   through its application API and acceptance tests, not a new device screen.
5. **Generic plan execution — partially implemented.** Immutable typed plans,
   preflight capability/unit/access/branch-variable/dependency validation, scan
   value strategies (linear, stepped, logarithmic and segmented), nested scans,
   sequence/repeat/for-each/assignment/if-else, source output, joint reads, delay
   and readback stability windows. IV is now an adapter to this same executor.
   Optional result columns support different measurement branches without NaN
   fillers; every present measurement still requires its declared axes/time.
   Multi-device pause captures targets and enabled outputs, confirms group stop,
   and restores original targets and only previously enabled outputs. Stability
   time excludes pause and requires a fresh stable window after resume. Plan
   facts carry step IDs and committed rows. Tests cover a 3 x 3 field/voltage
   scan, conditional rows, typed variables, preflight rejection, partial abort,
   stability timeout, hardware-policy reconnect recovery, and IV regressions.
   PPMS JSON/alias decoding, BT failure/retry/parallel/subtree semantics, remaining
   measurements and real drivers are still pending; this is not full BT parity.
   The standard audit passes 102 tests (27 Core, 63 Application, 12 App),
   with zero build warnings/errors and changed-file formatting verification.
   See experiment-plans.md for supported semantics and recovery boundaries.
6. **Plans/data/history — partially implemented.** Versioned UTF-8 JSON plans
   support every typed step, source preparation, scan values and numeric metadata.
   The application workspace validates loaded/saved documents against registered
   capabilities without connecting hardware. Bounded parsing/encoding and atomic
   replacement preserve previous files on validation failure/cancellation. The
   repository's field/voltage example is loaded from disk and produces 15 rows
   on fresh simulation sessions. The published stage passes 118 tests (28 Core,
   78 Application, 12 App). See plan-documents.md. The following result archive
   stage passes 135 tests (29 Core, 94 Application, 12 App): immutable sample ID,
   name, notes and attributes; full plan/schema/device snapshots; versioned file
   archives, metadata catalog, bounded reload and streamed CSV. Tests reopen fresh
   adapter instances, recover complete rows from unfinished records, reject corrupt
   records, reconcile committed rows missed by a cancelled listener and preserve
   previous exports on failure. See run-archives.md. This is an application API;
   the published desktop does not yet inject the archive. UI integration, settings,
   querying/filtering, paged plots and physical startup recovery remain pending.
   The measurement follow-up adds the K2450 voltage-resistance recipe and paired
   source/sense resistance reads for Voltage/Current source modes. It uses the
   same resources and finalization, supports nested scan-managed reads and adds
   a runnable nine-row field/resistance file. Source value/compliance combinations
   are checked together, including before each live parameter write. The published
   backend stage passes 159 tests (35 Core, 112 Application, 12 App). Only the
   tested `measure_r` subset changes to partial; channel/matrix routing, explicit
   ranges, other resistance modes, PPMS import and hardware remain pending.
   See resistance-measurements.md. The matrix follow-up adds typed card/crosspoint
   routing and a two-channel resistance file, source-off-before-relay ordering,
   break-before-make readback, baseline-preserving final cleanup, held-route pause
   and verified resume, quarantined failures/timeouts and explicit recovery. Plans
   and archives retain routing/configuration. This is still a simulation/API subset:
   sample/electrode topology, PPMS command import, actual card protocols and routing
   UI remain pending. See matrix-routing.md.
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
`InMemoryRunHistory`. Injecting `IRunArchive` adds durable result history; the
file adapter reopens complete data prefixes but never resumes physical execution.
The live run and in-memory history still retain all rows in RAM. Data-set
terminal state describes acquisition/cleanup; `RunOutcome` also reports result
listener failures. A successfully acquired data set can therefore be completed
while its overall outcome is faulted by an archive error. Finalization currently
waits for every started listener; no archive timeout or persistent retry queue is
claimed. Drivers and row listeners must honor cancellation during acquisition;
the bounded quarantine path covers safety cleanup, not arbitrary driver reads.
Notifications use unbounded subscriber queues; retention/backpressure and the
remaining source-meter-specific configuration APIs still require platform work.

Device groups deliberately retain all their resources if any stop confirmation
fails. `DeviceRunResources.StopAllAsync(retryFailed: true)` retries only completed
failed attempts; live timed-out attempts stay attached. `ReleaseAsync` requires
fresh successful confirmation for every touched resource. Parameter writes or source
output enable invalidate prior confirmation. Stop policy means output-off for a
source and hold-current-position for an axis; it is not a claim that a magnet has
been de-energized or a cryostat is physically safe. Axis limits and mock field slew
are simulation settings, not hardware certification. Conditional source parameters
require explicit source-mode/compliance configuration before writing.

The captured PPMS `safe_state.rs` deliberately retains magnetic field; its
`environment_hold_commands.rs` freezes field/temperature/angle at their current
readbacks. These are the semantic references for this stage. Generic pause target restoration and numeric readback stability now exist;
PPMS controller-specific stability/status rules, full sample/channel routing and
BT command and alias decoding remain separate requirements.

The optional global formatting check still reports pre-existing formatting in
`ViewLocator.cs`, `ViewModelBase.cs`, `Program.cs` and `ParameterDataSetTests.cs`.
Stage 3 changed C# files were formatted separately without rewriting unrelated
files. The standard warnings-as-errors build/test audit remains the commit gate.

Full-goal completion requires evidence against every inventory entry and the
additional families above. A green small test suite, registered names, mock UI,
or documentation does not prove parity or physical safety.
