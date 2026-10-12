# Live temperature-state and angle waits

The independent C# engine now executes standalone PPMS `wait_temp` and
`wait_angle` commands, including their migrated aliases and generic `wait`
with the corresponding `wait_kind`. These waits produce no measurement rows
and never program a target. Measurement nodes following them produce the
normal typed observations and evidence.

## Domain and execution boundaries

Core `WaitTemperatureStateStep` requests Reached or Stable from the existing
session temperature runtime. Reached accepts either Reached or Stable; Stable
accepts only Stable. Application polls fresh coherent temperature observations
through the claimed resource group, checks connection/fault state and reports
state changes. It reuses the controller's configured tolerance, settling
threshold and stable duration. It follows the controller's live state instead
of pinning a target from an earlier Set operation. Pause/resume resets stability
evidence; disconnected/faulted observations cannot satisfy the wait.

Core `WaitAngleStep` requests a nonnegative Degree tolerance and a positive
poll interval. An omitted target uses the fresh controller target on every
poll, matching PPMS. An explicit constant or typed variable is resolved as a
comparison threshold, never written to the instrument. Application reads a
coherent target/readback/rate snapshot through the owning AxisControllerSession,
validates connection and descriptor units/ranges, and uses inclusive absolute
error <= tolerance. This command does not require a Moving=false condition or
add stable duration: those would change PPMS angle-wait behavior.

The compiler validates controller capability/unit and target availability/type
before resource admission. Authored constants are range checked before IO;
dynamic values are range checked when resolved for execution. Axis sessions
stay under the existing atomic leases, operation gates, pause, hold and
retained-stop recovery lifetime. No UI or Infrastructure object owns workflow
state or performs a wait outside that lifetime.

## Deadlines and parsing

Infrastructure emits ordinary native wait nodes and, for positive timeouts, an
existing `TimeoutStep` around the wait. A zero timeout emits an unwrapped wait:
there is no hidden finite replacement. Native authors choose an explicit
TimeoutStep for a deadline. Finite deadlines use wall time including pause;
operator abort/caller cancellation retain priority. Noncooperative IO is
awaited to actual completion before hold or release, even after timeout.
Timeout/IO faults are never changed into ordinary workflow Failure or retried
by logical-result decorators.

Defaults match the frozen PPMS source: temperature Stable,3600 seconds;
angle tolerance0.1 Degree,30 seconds; poll500 ms. Imported poll values are
positive bounded integers and are raised to PPMS's runtime100 ms minimum.
Native intervals can be smaller if explicitly authored. `wait_temp_reached`
defaults to Reached unless `wait_for` explicitly overrides it.

Accepted temperature aliases: `wait_temp_stable`, `wait_temp_reached`,
`wait_temperature`, `wait_temp_state`; parameter `state` aliases `wait_for`.
Angle alias: `wait_angle_state`; `target`/`angle` alias `target_deg`, and
`tolerance` aliases `tolerance_deg`. Both accept `timeout` as `timeout_s`.
Multiple aliases, unknown fields/states, null/nonnumeric values, negative
tolerance/timeouts, invalid poll and out-of-profile targets are rejected with
source-line diagnostics before IO. This intentionally rejects malformed values
that Rust clamps or defaults. PPMS explicit angle targets retain its schema
-360..360 Degree bound as well as the bound controller's descriptor limits.

Native version1 gains strict `wait_temperature_state` and `wait_angle` kinds;
optional angle target absence is preserved, not serialized as a fabricated
constant. Existing pinned-target `wait_temperature` and numeric `wait_stable`
contracts remain distinct and keep their earlier behavior. Plan encoding
budgets optional angle expressions before DOM creation.

## Verification and remaining scope

68 new cases (10 Core,58 Application); the focused regression run passes
182 Application and10 Core cases. They cover alias/default/poll/zero-timeout
contracts, fresh live target changes, comparison-only targets, invalid units
and parameters, strict native formats, constant/dynamic preflight, stable vs
reached evidence, finite paused deadlines, noncooperative task drain and abort
priority, no retry/fault masking, disconnected/failed hold retention and explicit
recovery, fresh saved plans/archives/CSV and replay.

`examples/plans/ppms-controller-waits-import.json` actually runs against the
existing simulated `workbench-devices.json`: set temperature without waiting,
configure stability, wait on live temperature state, set angle, wait without a
deadline, then measure both. Unrelated SMUs and matrix remain disconnected.
The fixture is saved, archived, reopened, exported and replayed in a fresh
configured runtime.

Temperature state is the supported C# session runtime derived from fresh
observations, not a claimed vendor status byte or stale cached sensor value.
Finite C# deadlines are strict: unlike Rust's poll-first loop, a late matching
observation after expiry does not count as success. Snapshot coherence depends
on the adapter contract; tests do not prove physical sensor timing or motion.

Standalone field nonramping/numeric/duration waits are now implemented (see
field-waits.md). Vendor magnet-switch/persistent state proof, actual adapters and
lab acceptance, continuous/background scans, generic parallel/race, native wait
editor and visual acceptance remain pending. Numeric field set/scan stability
already implemented is not labeled equivalent to a vendor Holding state. The
original29 UI drafts are preserved and excluded; no new dependencies, Rust
changes or physical-device IO. Independent C# engine; GitHub only per user;
full migration goal remains active.
