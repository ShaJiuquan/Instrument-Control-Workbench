# Versioned plan documents

`JsonExperimentPlanDocument` implements the Core `IExperimentPlanDocument`
boundary using .NET's existing System.Text.Json and asynchronous file APIs.
There is no new storage package or database. `ExperimentPlanWorkspace` parses,
preflights and saves plans; a preview never acquires leases or connects drivers.
File IO and JSON representation stay in Infrastructure. The domain remains
independent of serialization attributes and UI frameworks.

## Format

The envelope is `format: "instrument-workbench-plan"`, `version: 1`, `name`,
`preparations`, `metadata` and `root`. These are required. An optional `routing`
catalog freezes sample/channel/wiring configuration into the plan. Unknown fields, duplicate
fields, unsupported versions and unknown step kinds are rejected. This format
is intentionally identified separately from PPMS JSON. A separate
[PPMS runtime-tree import API](ppms-plan-import.md) converts a verified subset
to this native format; full PPMS aliases/schema semantics remain migration work. A file must be UTF-8, with an optional BOM on load.

See the runnable [field/voltage example](../examples/plans/field-voltage.plan.json).
It needs registered instances `k2450` and `field`, matching the simulation
profiles. A 3-point field scan contains a stability wait followed by a 5-point
voltage scan and current/field joint reads. It produces 15 rows. Tests load this
actual repository file and execute it on fresh sessions; it is not just a schema
example. Physical hardware support is not implied. The [resistance example](../examples/plans/field-resistance.plan.json)
uses the same device instances and produces nine coherent V/I/resistance rows.

Parameters are addressed by `{ "device": "field", "parameter": "field.target" }`.
Quantities use `{ "value": 0.02, "unit": "Tesla" }`. Unit names are exact domain
enum names, not numeric enum values. A value expression is exactly one of
`{ "constant": { "value": 0.02, "unit": "Tesla" } }` or
`{ "variable": "field_axis" }`.

Every step has `id` and `kind`. Supported kinds are `sequence`, `repeat`, `scan`,
`for_each`, `set`, `assign`, `output`, `delay`, `read`, `read_resistance`,
`wait_stable`, `if_else`, `route_matrix`, `read_sample_resistance`,
`read_source_sense`, `sample_analysis`.
Their fields correspond to the typed models in `experiment-plans.md`; elapsed
settings are explicitly named `seconds`, `dwellSeconds`, `stableSeconds`,
`pollSeconds` and `timeoutSeconds`. Scan values are materialized as one unit and
a numeric array, preserving descending, logarithmic and segmented values exactly.
Source preparations include mode, level, compliance and sense configuration.
`read_source_sense` specifies `device`, `function`, `voltageColumn` and
`currentColumn`; see [source-sense-acquisition.md](source-sense-acquisition.md)
and the nine-row field-source-sense.plan.json current-source example.
Plan metadata carries numeric quantities; sample/provenance metadata is supplied
separately to the run and persisted in its archive. `read_resistance` specifies
`device`, `function`, `voltageColumn`, `currentColumn`, `resistanceColumn` and a
`minimumCurrent` Ampere quantity. See resistance-measurements.md. The sample
kind adds `binding`, `settleSeconds` and `routingManagedExternally`; see
sample-routing.md and the runnable sample-four-wire.plan.json example.

## Limits and failure behavior

Preparations optionally include a typed `manualSourceRange` quantity. Absence/null
means source autorange; exact nominal choice/unit and level/compliance envelope
validate before execution. Existing auto documents omit the field. See
[source-ranges.md](source-ranges.md) and its runnable native example. Older strict
version-1 readers may reject the new optional field.

- Documents are bounded at 4 MiB, including bounded reads if a file grows during
  load. Encoding also bounds aggregate scan-value allocation before building a
  JSON object tree. Depth is at most 128 and the tree has at most 10,000 nodes.
- Workspace preflight checks registered devices, capabilities, units, variable
  paths, result dependencies and the execution budget before saving or running.
  Dynamic values are still range-checked at the actual write.
- Saves write and flush an exclusively opened temporary file in the destination
  directory, then rename it over the destination. Invalid input and cancellation
  before rename preserve the previous file. Own temporary files are cleaned up.
  This is an atomic file replacement policy, not a power-loss durability promise.
- Load does not rewrite files. Unsupported versions are never silently upgraded.
  The caller receives an exception and can preserve its current draft.
- Plan storage is not persistent result history, run journaling or restart recovery.
  Result archives/export are a separate API; durable settings remain pending.

## Recovery and shutdown boundary

Source transport can reconnect from Faulted under its current resource owner.
A reconnect after a failed stop leaves a fresh-confirmation requirement: parameter
writes, observation through the resource scope and output enable remain rejected
until a subsequent successful stop. A reconnect cannot overlap a live pending
stop. Merely restoring transport never releases the group lease.

`MeasurementRunService.WaitForFinalizationAsync` observes the current run's
finalization lifetime, including result listeners, and accepts cancellation.
It does not request abort, confirm hardware safety, or release an unsafe group.
A caller must request abort separately and, after finalization, inspect whether
safety recovery remains required. Tests prove archive completion precedes the
finalization waiter completing.

A desktop document workspace with pause/recovery controls is being implemented.
Visual acceptance is pending; this published stage provides the backend document
and execution APIs, not a completed graphical flow editor.

Sample profiles may include positive meter-valued geometry. `sample_analysis`
groups two Hall or eight vdP `read_sample_resistance` children and four typed
derived column declarations; child nodes count toward the existing budgets.
See [sample-analysis.md](sample-analysis.md) for analyzed execution and row evidence.

The `software_temperature_ramp` step stores `device`, a typed `target` expression
and an optional `rate` quantity in KelvinPerMinute. Omitting rate explicitly
selects a direct, unrounded set with hardware ramp off. This policy survives
native documents and full-plan archives. Temperature mode/control flags in run
instrument metadata describe the initial snapshot; subsequent changes are
specified by the archived plan. See [temperature control](temperature-control.md).

`verify_matrix_route` stores `device` and a bounded `closed` crosspoint list and
performs observation only. Sample readings may optionally store
`acquisitionSettleSeconds`, which waits after source enable. Older sample steps
omit the key and retain a zero acquisition wait. Both additions survive full
plan archives. Read [PPMS resistance import](ppms-resistance-import.md) for
channel/sample source ownership and snapshot limitations.

Routing profiles also preserve optional authored IDs/aliases, connection
index/label/displayRow/raw/notes, sample material/substrate/notes, electrode
notes, wiring labels and meter spacing. Empty metadata omits, retaining earlier
encodings. New fields require a reader that understands these version-1
extensions. See ppms-profile-import.md.

`preparations[].sense.manualRange` is an optional typed nominal measurement span.
It requires `autoRange=false`; absent values preserve old automatic or observed
retain-mode semantics. Exact units/choices and complementary source constraints
validate before execution. Old strict readers can reject the populated optional
version-1 field. See [sense-ranges.md](sense-ranges.md).


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
