# Polarization voltage point iteration

The Rust registry maps `measure_polarization`, `pol_voltage` and
`scan_pol_voltage` to an injection-only iterator. `scan_voltage` with
voltage_mode polarization/polarization_voltage/pol_voltage/scan_pol_voltage uses
that same iterator. It generates points, injects pol_voltage_v into direct
measurement children and overrides their channel when the parent names one.
It performs no source write itself. C# preserves this boundary using ordinary
`ForEachStep` plus `ReadPolarizationStep` children, not a hardware ScanStep.

## Point and child semantics

- Bounded Volt points support arrays, explicit lists, linear/log/step/inverse
  generators and segments through the existing point engine. Segment behavior
  preserves the existing PPMS drop-first rule. Empty/malformed/unbounded points
  and mixed flat/nested controls reject.
- Actual Rust aliases above are recognized; inventory names polarization_sweep
  and measure_polarization_sweep are additional C# compatibility aliases.
- Parent channel overrides a direct measurement child's explicit channel. If no
  parent channel exists, the child's channel is required. Current point overrides
  the child's stored pol_voltage_v; a child may omit that field in this scope.
- Injection applies only to direct measurement children, including their control
  decorators. Sequence descendants keep their own fixed pulse voltage, matching
  Rust scan_children. Other direct measurements keep their own acquisition logic.
  Rust parallel unwrapping is not migrated; parallel nodes still reject.
- pol_voltage is a Volt condition/callback alias; direct child iterator condition
  context uses the existing one-based native index/total. Iteration timing is
  immediate; pulse children own all dwell/output/cleanup/routing.
- Parent source/range/compliance controls are not silently applied. Such fields
  reject; the child explicitly owns its source configuration and source lease.
  Empty measurement children produce no instrument IO and no sample rows.

## Dynamic targets and data

`ReadPolarizationStep` accepts an optional numeric PolarizationTarget with Volt
inference, using the existing typed value/blackboard evaluator. Existing fixed
constructor/API and old native documents remain valid. Dynamic targets require
an explicit requested-polarization column, separate from actual readback voltage,
current and attempts. The compatibility fixed-value getter refuses a dynamic
value instead of returning the configuration seed as a fake pulse voltage.

Compilation checks every direct variable target against its active ForEach
literal points (including voltage/compliance/sense-range coupling) before any
connection. General numeric expressions also require Volt and are validated
at runtime before per-pulse IO. Runtime resolves/freezes the target once per
point, including all paused-attempt restarts. Native configuration retains a
valid static seed; the optional polarizationTarget expression is authoritative
for dynamic execution. Encoders never label that seed as a measured/requested
pulse value. Old fixed encoding is unchanged; optional requestedPolarizationColumn
and polarizationTarget fields extend the current format.

Each row retains the iterator value/index/total, requested polarization voltage,
actual paired readout voltage/current, attempt count and original quality.
Archive validation checks requested voltage units/limits and agreement with a
constant or target whose variables are present in the same row. Global mutable
variables outside row scope cannot be reconstructed from the archive's final
blackboard; their requested value remains recorded, without claiming waveform
or historical variable verification. Missing/conflicting requested values reject.
Calculated/measurement-point input declarations and footer units include this
optional output. The existing pulse OFF-before-publication rule is unchanged.

## Pause, abort and compatibility

Paused pulses remain OFF and resume from the same frozen point. They do not
repeat already completed points or advance the iterator before a successful
sample. Attempt count remains bounded. Abort/fault preserves completed points
and stops later acquisition. Native plan save/fresh archive/CSV/replay preserve
requested points and paired evidence.

Literal iterator validation now lives in a small compiler helper outside the
large recursive visitor, with scoped point tables restored in finally. Deep
native iterator/codec checks preserve the existing128-depth limit. All existing
static pulse/native/TCP/maximum-depth cases remain verified. An actual previous
published single-pulse archive is loaded with current assemblies to verify old
format compatibility; no archive contents are rewritten.

Example: `examples/plans/ppms-polarization-sweep-import.json`, existing
headless-simulation.devices.json and headless-import-context.plan.json. Three
points5/0/-5V produce three OFF-confirmed samples with requested values and
readback1V. The iterator is reusable beneath temperature/field/time loops; it
never asserts that the axis is a continuously applied source voltage.

Readonly Rust references: scan_axis/registry.rs, scan_axis/scan_iter.rs,
nested_experiment_service/node_execution/handlers/scan_children.rs and its
polarization injection test. Actual TCP adapter loopback exercises three dynamic
points; this is protocol verification, not physical pulse acceptance. Host timing,
hardware-triggered sweeps/buffers, physical waveform/relay/sample response, native
pulse/sweep forms and wider PPMS/controller/workflow/data/settings/remote remain
incomplete. No dependency, Rust, lab IO or native window action changed.
