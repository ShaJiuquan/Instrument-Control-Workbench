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
   UI remain pending. See matrix-routing.md. The sample follow-up freezes
   channel/wiring/sample catalogs, resolves and validates electrode topology,
   executes TwoWire/FourWire and raw Hall/vdP assignments through the same engine,
   preserves nested scan setpoints and supports verified external route management.
   Rows retain sample/selection/electrode identity in archives and streamed CSV;
   supplied context is checked against frozen plan configuration. The analyzed
   follow-up adds complete Hall/vdP measurement groups, typed geometry, sheet
   resistance/resistivity, bounded row-reference validation and archived/CSV
   summaries. Corrected Hall/offset/quality analysis, separate source/meter
   profiles, settings/UI and physical wiring/hardware acceptance remain pending.
   See sample-routing.md and sample-analysis.md.
   The source/sense follow-up exposes coherent actual V/I without requiring
   resistance calculation in both source functions. IV rows/plots use actual
   readbacks while preserving the requested axis. Full source configuration is
   checked after acquisition and before pause restoration. A nine-row nested
   field/current file, archive/CSV and fault/pause tests cover the simulated API.
   See source-sense-acquisition.md; standalone meters, range/quality profiles,
   PPMS decoding and hardware remain pending.
   The import follow-up adds a separate bounded PPMS runtime-tree codec and
   Application import/preflight APIs. Explicit source/controller/channel bindings,
   source-line metadata, repeat/delay, environment settings and step field/angle/
   source scans translate into native plans. The real-schema six-row matrix-routed
   field/voltage fixture executes, saves/reloads, archives and exports. Software
   temperature ramps/scans are added in the follow-up: an explicit temperature
   port confirms control mode, the executor migrates the current Rust one-second
   millikelvin stepping, and pause/abort/fault cleanup retains ownership until
   hold confirmation. The 12-row temperature/field/routed-voltage runtime file
   saves, archives, reopens, re-executes and exports. Firmware status, remaining
   BT/controller/range/measurement semantics and hardware remain required.
   The resistance/sample follow-up translates current registry/schema aliases,
   volt_dc parameters and frozen sample-mode wiring into ordinary owned or
   scan-managed V/I/R and full Hall/vdP groups. Post-enable sampling waits and
   explicit route verification are separate from relay settling. The field/sample
   file now retains 26 original records plus two scan points,
   28 rows in total, and saves, archives, reopens, re-executes and exports. Other
   current/AC acquisition roles, device/settings UI and physical hardware remain required. See
   ppms-resistance-import.md, ppms-plan-import.md and temperature-control.md.
   The profile-file follow-up parses all three routing schemas, preserves authored
   metadata and unbound physical families in a frozen catalog, and binds explicit
   supported devices. Channel/sample aliases resolve without duplicate route maps.
   The field/sample file now executes 28 rows from imported profiles and
   round-trips metadata in
   native plans/archives. Actual bundled files were parsed and converted locally
   without hardware. Native offline catalog editing and atomic file persistence
   now retain all layouts independently of device binding. Paused-run edits leave
   the captured plan unchanged; a fresh document binds/imports the next run.
   Device/settings UI, PPMS write-back/database migration, remaining runtime
   flags/families and hardware remain required. See ppms-profile-import.md and
   routing-profile-workspace.md.
   The measurement-group follow-up collects consecutive scan measurements and
   scan-local parallel wrappers into complete wide points with bounded input
   identity. Raw/analysis rows, partial failures and resource recovery are retained;
   live append, archive writers/readers and streamed CSV validate point evidence.
   General parallel IO and visual filtering remain
   pending. See measurement-groups.md.
   The environment follow-up captures all three fresh environment roles and
   explicit errors/unbound sources, preserving each observation time and raw
   sample/member identity through archive/CSV. Atomic resource admission includes
   bound controllers; read errors do not bypass independent hold confirmation.
   See environment-acquisition.md and ppms-environment-point-import.json.
   The device-settings follow-up retains native instance/alias/primary/enable and
   typed simulation/TCP/VISA/serial configurations, imports all eight PPMS
   connection keys and stores explicit matrix cards/guards. Offline typed edits
   do not mutate live state. Startup validates the whole enabled set before port
   construction and builds existing sessions/shared resources without IO;
   supported simulations execute the 28-row profile experiment with multiple SMUs.
   Unsupported hardware rejects before construction, never falls back to a mock.
   Published desktop composition, physical adapters, DB/env precedence and
   operator restart/device-specific settings remain pending. See
   device-configuration.md.
   Generic execution now accepts DeviceRegistry directly with no selected SMU.
   Three-controller native and imported PPMS scan fixtures execute, archive,
   export and replay with no placeholder source. Resource-free control plans and
   explicit SMU bindings share the same lifecycle. Pause/abort, independent stop
   failures, retained ownership/recovery and listener finalization are verified;
   legacy IV/resistance shortcuts still require explicit primary selection.
   See source-independent-execution.md.
   Startup now resolves saved settings or an explicit missing-file initial profile,
   returns the matching runtime/executor with origin, and never falls back on
   invalid existing settings. Display names are frozen in source/axis/matrix
   snapshots and fresh archives. Local desktop startup/device/settings draft is
   wired and ViewModel-tested but still awaits native visual acceptance. See
   configured-workbench-startup.md.
   The first physical-transport adapter now registers native-SCPI K2450 raw TCP
   behind the same Core port and Application lifecycle. Owned loopback acceptance
   covers fresh register reconciliation, actual paired source/sense data,
   valid coupled configuration order, output/readback guards, cancellation
   invalidation, retained unsafe ownership and explicit reconnect recovery.
   Saved config/plan execution and archive reload work with or without selected
   primary. No hardware was contacted; real acceptance, other physical adapters,
   range/filter/quality features and native operator acceptance remain pending.
   See k2450-tcp.md.
   Source ranges now use typed nominal choices through preparations, parameter
   writes, PPMS import, compiler, mock and actual TCP reconciliation. Fixed-range
   level/compliance bounds, live-change exclusion, acquisition/pause drift and
   initial snapshot/archive evidence are verified. A native three-row fixture
   executes against configured simulation. Sense ranges, per-frame quality,
   automatic selected full scale and hardware acceptance remain pending. See
   source-ranges.md.
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

26. Typed K2450 fixed sense ranges: complementary manual current/voltage spans,
    explicit autorange action and actual nominal readback through existing ports,
    compiler/resource/manager limits, live owned changes without changing source
    settings, initial parameter/archive/CSV evidence and combined native fixture.
    PPMS standalone meter and owned/scan-managed resistance/sample selectors now
    accept finite numbers/strings and aliases. Single/paired/pause drift and
    range-fault/failed-stop recovery share the existing lifecycle. 62 new tests;
    no lab IO or new dependencies. Read sense-ranges.md. Ohmmeter selection,
    per-row autorange span/quality, other families, native UI and real hardware
    acceptance remain required. Whole migration goal remains active.

## Acquisition quality stage (2026-10-11)

Native K2450 single and paired reads now preserve same-buffer source/measurement
status. Capture mismatch, protection and overtemperature fault before row commit;
finite limit/questionable/unknown-bit warnings persist through resistance, sample
analysis, wide points, archive and CSV. Missing status is explicitly unobserved;
legacy rows remain readable. Initial settings and later queried autorange spans
are not per-reading range evidence. See [reading-quality.md](reading-quality.md)
for policy, compatibility and remaining hardware/UI limitations.

## Acquisition profile stage (2026-10-11)

Optional typed autozero/rebound/Repeat-Moving profiles now retain actual target
function settings when unspecified. Active-function parameters, guarded owned
writes and off-only completed zero refresh use existing lifecycle boundaries.
Single/paired reads preserve confirmed per-row profiles separately from buffer
status; analysis/archive/CSV inherit them. PPMS filtering imports runtime Repeat
semantics and retains unused voltage window metadata. No dependency, Rust edits
or lab IO; native UI and physical acceptance remain pending. See
[acquisition-settings.md](acquisition-settings.md).

## Standalone voltage meter stage (2026-10-11)

A model-specific passive K2182 port/session and explicit simulation now share
registry ownership, abort-acquisition cleanup, pause confirmation and archive
boundaries with the source executor. Optional per-row meter settings never
claim SMU buffer quality. Native two-input plans and direct PPMS input-1 voltage
filtering are supported; physical transports, curr_dc resistance, routed meter
reads and native UI acceptance remain pending. See [standalone-meter.md](standalone-meter.md).

## Independent current resistance stage (2026-10-11)

The independent C# executor now supports source-plus-K2182 `curr_dc` resistance,
with explicit programmed-current versus measured-source-readback references,
separate acquisition evidence, guarded matrix verification, managed scan-current
binding, wide-point aggregation and validated archive/CSV. Real K2182 and K6221
drivers, automatic independent sample wiring/analysis, retry-to-invalid-point
semantics, native UI and lab acceptance remain pending. See
[current-resistance.md](current-resistance.md); full goal remains active.

## Pure K6221 DC current-source stage (2026-10-11)

Dedicated settings/snapshot/Core port, Application session and explicit simulator
now support K6221 DC output without SMU sensing. Native preparations, current
parameters/scans, owned/managed PPMS curr_dc and basic concrete K6221 commands
share lifecycle/leases/pause/output-off/recovery. Model-tagged independent
resistance evidence uses the actual family's constraints and validates through
wide/archive/CSV, retaining legacy K2450 archives. Physical transports, advanced
modes, independent sample routing/analysis, retry-to-invalid-point semantics and
UI/lab acceptance remain pending. See [k6221-current-source.md](k6221-current-source.md).
All App/App.Tests drafts remain excluded; the full goal stays active.

## Independent current channel catalog stage (2026-10-11)

Catalog binding preserves K6221 preparations and standalone voltage meters.
Optional channelVoltageMeter explicitly binds the selected source/meter/matrix;
curr_dc aliases and mode maps resolve without SMU sensing assumptions. Owned
channels and managed current scans use native steps and existing safety/ownership.
32 new cases cover both source families, authored three-file profiles, role/port/
mode errors, drift, retained failed-stop claims and native/wide/archive/CSV.
Independent sample/Hall/vdP, real drivers, retry semantics and UI/lab acceptance
remain pending. App/App.Tests drafts remain excluded; the full goal stays active.
See [independent-channel-routing.md](independent-channel-routing.md).

## Independent current DC sample stage (2026-10-11)

Two-/four-wire/Hall/eight-permutation vdP acquisition shares the existing sample
executor and pure math through a metadata-only ResistanceAcquisitionStep boundary.
Frozen wiring/context retains explicit independent meter identity; samples own
cleanup and target observed input1. Native kind read_independent_sample_resistance
wraps the ordinary current acquisition, with unchanged integrated encoding.
Complete analysis preserves every independent record/quality, common observed
source/meter configuration and environment; native/wide/archive/CSV validators
reject mismatched/missing/reordered evidence. Owned PPMS sample aliases and four
source/sample/meter/route fixtures run30 rows overtwofieldpoints, reload/re-execute/
export from fresh archives.48 new cases cover11 Core+37 Application. The standalone
meter's three-argument observation interface must stay implemented: extra sample
input enforcement is a separate four-argument overload, avoiding loss of meter
configuration evidence through the default interface implementation. Real ports,
retry/invalid points, advanced modes and remaining UI/lab/PPMS remain unfinished.
All App/App.Tests drafts remain excluded. See independent-sample-resistance.md.

## Independent current acquisition retry stage (2026-10-11)

Explicit native CurrentResistanceRetry policy and PPMS curr_dc import now allow
one retry of terminal K2182 voltage ReadTimeout/BufferTimeout/-221 errors only.
The actual port-read boundary excludes snapshot/configuration/route/source errors.
Owned source-off plus meter-abort or managed meter-only cleanup uses attached
bounded stop tasks; live/faulted cleanup cannot overlap retries or release claims.
Fresh source/meter/route equality precedes restore/read. Retry dwell must use the
ordinary pause checkpoint outside the resource gate to avoid deadlock; leases
stay held and fresh full configuration checks follow. Successful attempts retain
first failure, observed meter settings and cleanup times through raw/sample/wide/
archive/CSV; exhausted retry preserves both errors and faults, with no invalid
or fabricated zero row.54 new tests (10 Core44 Application). Physical ports,
recover-invalid-point semantics and full UI/PPMS remain unfinished. App drafts
remain excluded. See current-resistance-retry.md; full goal remains active.

## Invalid current DC points and available-input analysis (2026-10-11)

Native policy adds explicit ContinueOnAcquisitionFailure=false by default;
PPMS curr_dc enables it. Only terminal typed K2182 voltage acquisitions recover
(overrange/read-buffer-timeout/invalid-response/VISA-read/-221/-241/419/301)
after fresh attached bounded cleanup and exact source/meter/route/input checks.
Source-side failures, drift, parameters, snapshots and cleanup stay fatal;
cancellation terminates without an unfinished row. First and final read errors remain with cleanup errors; a typed read racing
cancellation becomes OperationCanceledException retaining the original cause.
Completed stop proof is consumed before new observation, so unknown state cannot
reuse it. Invalid rows retain axes/elapsed/environment and separate immutable
IndependentResistanceFailure with source configuration, settings, error/status,
chronology and optional retry; V/I/R and successful quality are absent. Remove
runtime variables and reject optional/stale preflight dependencies. Complete
ordered sample groups use each committed row, nullable Ohm input math and explicit
FailedAcquisition; Hall preserves valid longitudinal geometry independently of
transverse, vdP needs all four inputs for each mean and both for sheet. Raw/sample/
wide failure collections and kind-derived status round-trip native/fresh archive/
CSV; tamper rejection preserves previous exports.68 new cases (18 Core50 App).
Physical ports, source-side/other modes and full UI/PPMS/packaging/lab remain
unfinished;29 UI drafts excluded. See current-resistance-invalid-points.md.

## Independent device commands and per-input settings (2026-10-11)

PPMS K2182 NPLC/range/filter and K6221 source-delay commands now use existing
native parameter execution. Input selection is an explicit Core port operation:
observe target range/filter instead of copying/resetting prior input, shared
NPLC retained; simulation keeps both input profiles. Optional filter controls and
flat/nested aliases match Rust; unknown/ambiguous/fractional/overflow inputs reject.
Actual-input expected-voltage selector is distinct from exact native manual span.
Configuration-only runs emit no rows. Source delay is an optional second register
with native/archive successful/failed current-excitation evidence, not software
settling; missing preparation retains observed state and legacy unknown remains
absent. Register-only import never applies unused current/compliance preparation.
Read/pause drift rejects, manual selection/write is denied during run/pause, and
cancel/refusal/failed cleanup preserves the existing whole-group safety lifecycle.
Published command and corrected per-input scan examples verify later raw evidence.
No dependencies, Rust edits, lab IO or App drafts changed. Physical transports,
advanced modes, full controllers/BT/background/settings UI/plots/packaging remain;
whole goal active. See independent-device-commands.md.

This stage adds45 cases (9 Core36 Application); full working1377 passed.
Mandatory commit and exact archive audits are required for every publication.


## Number/time iteration scans (2026-10-11)

PPMS `scan_number`, `scan_time` and `time_s` now use native iterators with
explicit delay-before-point timing, bounded values, nested axes/index/total,
pause/cancellation, independent stop and retained failed cleanup, native/archive/
CSV re-execution. Existing iterators default to immediate execution. See
[iteration-scans.md](iteration-scans.md) and the five-row repository example.
K6221 raw TCP/mode safety is still under investigation; no hardware adapter or
UI changes are included. The full migration goal remains active.


## Workflow result controls (2026-10-11)

Native typed condition/fallback/retry/result decorators and PPMS control-node
import now share ordinary executor/resources. Logical failure propagates through
parents; errors/cancellation/safety are not swallowed. Branch/attempt axes, strict
preflight/dataflow/budgets, grouped quality/native save/fresh archive/CSV and
re-execution verified. See [workflow-control.md](workflow-control.md) and the
five-row `ppms-fallback-retry-import.json` example. Full exception recovery,
script conditions, parallel/timeout/background/subtree/native UI/hardware remain
incomplete. The full migration goal remains active.


## Independent temperature controls (2026-10-11)

Native temperature_control and PPMS temp_ramp_on/off, temp_control_on/off now
use an explicit optional Core configuration port and existing executor/resources.
Fresh target/rate/flag confirmation preserves the other flag; RampOn cannot
implicitly enable disabled control. Mock disabled control retains its target but
never fabricates movement. Hold preserves flags and parks at live readback; pause
restoration rejects flag/rate drift before source output restoration. Refusal,
cancellation, failed/control-enabling hold and retained whole-group recovery,
strict aliases/source lines, native save/fresh archive/CSV/re-execution verified.
50 new cases (8 Core42 Application). See [temperature-controls.md](temperature-controls.md)
and the two-row ppms-temperature-controls-import.json fixture. No dependencies,
Rust writes, physical IO or29 UI drafts changed. Stability configuration, PID/
heater/cooling/background/physical controller/native UI remain pending; full
PPMS migration remains active. GitHub only; independent C# executor.


## macOS application packaging and native probe (2026-10-11)

A Release self-contained .app packaging script now publishes the existing SDK/
Avalonia runtime, validates native manifest/runtime/executable and refuses existing
output including dangling links; exported source without Git metadata remains
buildable with explicit unknown provenance. App name matches bundle/native menu.
Actual local draft UI opened: four simulators, rotation connect/readback, device
document validation, two-device15-row plan pause/owned source-off/manual disable/
resume/finish, persisted history and UI CSV checked. A fresh self-contained Release
process reopened15-row history. Closing during another active simulated run
terminated the original process and finalized10-row Aborted archive; subsequent
CUA AX observation automatically opened a new process, not continued old run.
Native coordinate resize rejects noWindowsAvailable while AX/process live;
1060x720 and failed-safety recovery/close acceptance still pending.29 original UI
drafts unchanged/excluded; package probe includes them and is distinct from clean
committed-source package. No Rust edits, physical IO or new package references.
Script guards, mandatory hook, exact clean audit/package and GitHub publication
are required. See [macos-packaging.md](macos-packaging.md). Full goal active;
remaining controller/stability/BT/advanced measurement/DB/flow/UI/packaging/hardware
work is not complete. GitHub only; independent C# executor inspired by OpenTAP.


## Temperature stability settings and inherited waits (2026-10-11)

Independent C# session configuration now accepts PPMS temp_set_config strict
optional threshold/duration aliases; empty patches preserve fields, invalid input
fails with source lines. Native temperature_stability/wait_temperature steps are
compiled through full resource/preflight/lifecycle, serialized and archived.
Temperature set/step scans inherit current settings; explicit overrides persist
before ramp including wait_mode=none. Initial instrument metadata includes the
session settings. Fresh flag/snapshot/flag observation and wait-local Core states
prevent disabled control from satisfying waits. Pause resets active-time evidence;
cancellation/IO/flag mismatch/target drift stay errors, failed hold retains owner
until recovery. Quantized endpoints allow only half-millikelvin target rounding.
See [temperature-stability.md](temperature-stability.md) for the explicit difference
from Rust's continuous observer/cache and cross-wait timer reuse. Background,
PID/heater/cooling, disabled-control sets/scans, persistence, UI and physical
acceptance remain pending. No Rust edits, new packages or original29 UI edits.
Targeted tests, mandatory precommit and exact clean committed-source audit precede
GitHub publication. Full migration remains active; GitHub only, independent engine.


## Caller-managed temperature setpoints and wait ordering (2026-10-11)

PPMS set_temperature enable_control=false now preserves the actual enabled or
disabled control flag through software/direct/hardware target changes. It does
not mean control-off. Default enabled software paths keep the original port;
preserved paths preflight the actual optional configuration port. Hardware sets
use a typed atomic parked preparation instead of rate-enable at a stale target.
Software ticks carry captured control/rate evidence, with drift/refusal/fault/cancel
and pause guards through existing owned resources. Native optional enableControl
is backward compatible; new hardware_temperature_setpoint saves/archives/replays.
Further reference tracing corrects the prior stability-stage statement: no-wait
set skips threshold configuration and settle, waited set and step scans update
thresholds after ramp and settle before waiting. Prior before-ramp/no-wait override
behavior was a migration mismatch, not reference semantics. Scan false-control
option remains explicitly unsupported because the Rust factory does not carry it.
See [temperature-setpoints.md](temperature-setpoints.md). No new packages, physical
IO, Rust edits or original29 UI edits. Mandatory hook and exact committed-source
audit precede GitHub stage publication. Full migration remains active; continuous
runtime/persistence, PID/heater/cooling/background, advanced instruments/BT/data/
graphical flow/native UI and hardware acceptance remain pending. GitHub only.


## Shared temperature runtime and software intent (2026-10-11)

Device session now owns one Core observer shared by manual status, acquisition,
waits and typed DeviceStatus notifications. Monotonic TimeProvider and fresh
flag/snapshot/flag evidence replace separate wait-local timers; second waits can
reuse stable evidence. Changed settings, failed/disconnected observations, hold,
pause and resumed targets reset evidence; identical patches retain it. Software
ramp status uses final requested target and separate software rate, preserving
actual hardware quantities; direct/final/aborted ramps clear intent. Pause captures
and restores goal/rate only after existing control/rate/target guards. Core rejects
contradictory status facts; no autonomous sampler/worker or continuously sampled
interval is claimed. See [temperature-runtime.md](temperature-runtime.md). No
physical IO, new dependencies, Rust or original29 UI edits. Persistence, firmware
states, background/PID/heater/cooling, caller-managed scans, full native/graphical
UI and other PPMS/hardware scope remain pending. Mandatory hook and exact committed
source audit precede GitHub publication. Full goal active; GitHub only.

### Confirmed disconnect and temperature evidence

Disconnect success requires fresh connection readback. Adapter exceptions,
cancellation and ignored writes fault the axis session and reset temperature
evidence; a moving precondition rejection preserves state. PID/heater method
names were traced in the Rust sample namespace, but their units, bounds and
control-mode/stop contract remain unverified. These writes remain pending;
existing mock values do not prove physical behavior. Independent C# execution
and GitHub stage publication remain the accepted decisions.

### Wall-clock timeout workflow

Native TimeoutStep and PPMS timeout/with_timeout wrap one validated child.
Expiry propagates cancellation through the child and faults with typed step/deadline
identity; operator abort stays Aborted. Pause counts toward the deadline. Live child
calls are drained before cleanup/release, and failed cleanup retains the group.
Strict native/import codecs, all resource/data/document visitors, executable nested
field fixture, partial archives/CSV and vdP summary replay are covered. No hard
real-time or noncooperative force-interrupt claim; adapters must honor cancellation
for prompt stop. See workflow-control.md. Remaining parallel/subtree/script/
background/editor/physical scope is unchanged. No new package or Rust/UI edits.

### Subtree quantity scopes

Typed inherited variables, input aliases and explicit successful outputs now
execute in the owned C# run. Nested scopes, strict preflight and all data/document
visitors preserve units and provenance. Failures/cancellation restore parents;
actual writes and committed acquisition remain under the same resource group.
Native and PPMS subtree/scope/isolated formats, executable scan/field fixture,
vdP/grouped archives and CSV/fresh replay cover the slice. Rust Bool/Text
blackboard and expression controls remain pending alongside parallel/editor/
physical scope. See workflow-control.md. No new package, Rust or UI-draft edits.

### If/Then/Else node outcomes

Independent C# BranchOnOutcomeStep now executes a condition child once, selects
Then/optional Else from its explicit workflow result and preserves observations
and state. Selected failure propagates its origin; IO/listener/deadline/abort
errors never select another branch. All preflight/data/document visitors include
every child, with conservative variable availability and unit validation before
admission. Native v1 and PPMS node aliases/Then/Else wrappers, source locations,
five-row fixture/archive/CSV/fresh replay, grouped quality and vdP evidence are
covered. Numeric PPMS mode, while, expressions, parallel controls and full UI/lab
acceptance remain pending. See workflow-control.md. No new package or Rust/UI
changes are part of this slice.

### Numeric comparison and pure for_each import

PPMS numeric if_then_else now imports supported physical/number/time axes and
injected iterator value/index/total into typed IfElseStep. Scan aliases are
explicit assignments, retain most recent values after nested scans and preserve
units. Strict 1e-9 equality is opt-in on ValueCondition so existing native exact
comparison remains unchanged. Pure for_each/iterate uses bounded dimensionless
points, direct-child injection with explicit overrides and zero-based indices;
native iterators still default one-based and preserve the authored base. Native
codec, strict import/preflight, fixture/native save/archive/CSV/fresh replay,
compatibility, operator/unit/availability cases and pause/abort/TCP/timeout faults
are tested. Custom nonnumeric/other axes, dynamic commands, advanced point
modifiers, while/expressions/parallel and UI/lab parity remain pending. See
workflow-control.md. No new dependency or Rust/protected UI edit.

### While, numeric mutation and node pre/post conditions

Typed WhileStep now reevaluates variables on the same owned serial run, records
iteration axes and keeps logical Failure distinct from exceptions/abort. Numeric
Set/Add/Subtract and scalar Multiply, outcome-specific callbacks and priority
failure/skip guards import PPMS custom counters and supported physical aliases.
Both callback/guard/body paths preflight; callbacks never run for transport,
timeout/budget/abort faults. Malformed/missing contracts reject rather than use
Rust's silent fallback. Native plan budgets now enforce the existing10m limit
at step/temperature-tick execution; lower authored limits persist. Dynamic
previews expose a cap, not a guessed iteration count. Fixture/native/archive/CSV/
fresh execution, grouped/vdP evidence and unhappy safety cases cover the slice.
Nonnumeric/unknown physical metadata, generic parallel/background, expressions,
remaining source/controller/UI/lab parity remain pending. See workflow-control.md.
No new dependency, Rust or protected UI edit; full migration remains active.


### Typed blackboard workflow stage

C# workflow state now holds immutable quantities, booleans and text with explicit
types and per-write step/time provenance. Native assignments, conditions,
Boolean/text while, branch joins and subtree mappings are integrated with the
existing run resource and safety lifecycle. Numeric instrument parameters never
coerce nonnumeric values. Text is ordinal and bounded; malformed typed JSON is
reported at the format boundary. Native typed plans archive/export/replay with
actual numeric observations. A portable no-device fixture is included.

Rust Bool/Text storage APIs are present but current axis and callback producers
remain numeric. Their undocumented import syntax is not guessed. The completed
run API exposes immutable blackboard snapshots; runtime entry historical archive
and live diagnostics still need integration, alongside expressions, concurrent
execution, remaining instruments/controllers, editor/device UI and lab acceptance.


### Terminal variable history

The run-owned typed blackboard now persists its final root scope through Core
data snapshots, in-memory history and file archive version 2. New terminal
records explicitly distinguish captured-empty from unknown and retain all
quantity units, Boolean/Text values, source steps and time stamps. Shared
workflow provenance checks and payload/terminal checksums reject malformed,
foreign or changed snapshots; fresh metadata/load/catalog and CSV expose them.
Version 1 remains readable with unknown variable history. Record-size failures
report delivery errors after safe-stop and keep committed rows unfinalized,
without silent truncation. Complete variable change history/live diagnostics,
paged/lazy history, expression/concurrent execution, the editor/device UI and
remaining physical PPMS feature acceptance are still unfinished.


### Live blackboard observations

Application execution now exposes real run-owned variable observations and
coalesced async streams. Reads are frozen and revision-aware, preserve typed
values/units/source/UTC/elapsed and identify nested subtree local scopes.
Concurrent observers independently cancel; stream generations survive later
run replacement. All existing executor writes, failed-value removal and
subtree/retry/fallback restores use the synchronized producer boundary.
Starting/finalizer/recovery phases and terminal outcomes remain distinct from
hardware safety/resource release; terminal archive snapshot is shared.
Reservation failures close known-run waiters without invented acquisition data.
This is a tested Application use case; native variable panels, paged history,
per-write journaling and full parallel/background execution remain unfinished.
See workflow-control.md and ControlFlowTests.BlackboardLive/BlackboardAdmission
and Keithley6221ExecutionTests.BlackboardLive. No UI/Rust/dependency changes;
full migration remains active, GitHub only.


### Bounded archive catalog and row pages

Core typed filter/keyset/row-byte/cross-page version contracts and Application
RunHistoryService use cases now support paged persistent history. Directory
results are lightweight summaries with bounded prefix/text/issue retention;
selecting a run restores full metadata/variables. Data pages retain requested
rows but validate all source/evidence/terminal bytes, so summary-only pages
cannot discard raw provenance or overlook later corruption. Version stamps
reject changed header/final/data, even equal-size edits or unfinalized growth.
Real restarted100005-row synthetic storage, wide UTF8 byte limits, filters,
legacy/v2 variables and cross-page group checks are covered. Full integrity/
metadata scans remain; indexing, live dataset memory bounds, lazy plot/native
history UI and physical acceptance remain unfinished. See history-pages.md.
Original29 UI drafts/Rust/dependencies untouched; full migration active;
GitHub only.
