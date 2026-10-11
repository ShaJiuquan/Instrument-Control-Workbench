# Number and time iteration scans

The C# importer supports PPMS `scan_number`, `scan_time` and the registered
`time_s` alias without constructing or claiming an artificial device. Imported
plans use the existing Core `ForEachStep`, Application executor and whole-group
resource lifecycle. IO stays behind the existing device ports.

## Meaning and validation

`scan_number` accepts nonnegative integer points, including zero, descending
order and repeated values. As in Rust `scan_iter.rs`, a generated value within
1e-9 of its nearest integer is rounded; negative values are rejected before
rounding. A flat start/end/step specification or count, an explicit list, nested
points, and segments use the same bounded generator as other imported scans.
The first value of each segment after the first is dropped, following existing
PPMS behavior. Segment objects may explicitly declare `mode: segments`.
Unknown or conflicting controls reject instead of silently selecting a mode.

`scan_time` waits **each point's requested seconds before its children**. For
`[0, 1, 2]`, the waits are zero, one and two seconds; these are not deadlines at
elapsed seconds zero, one and two. This matches the current Rust implementation
at `scan_axis/scan_iter.rs` (`IterKind::Time` and `wait_for_duration`), although
its header and schema help text can suggest an elapsed-time schedule. Hardware
latency and child execution add to wall time. This is a desktop timer, without
real-time timing guarantees.

The explicit Core `IterationTiming.DelayBeforePoint` requires Second values in
0..4294967.294, the existing supported timer range. Other units, negative/huge
values and unknown enum values reject before execution. Existing generic
iterators retain `Immediate` timing and their existing arbitrary-unit behavior.

An optional import `axis` must agree with the scan name (or registered alias).
Nested points cannot be mixed with flat point controls. Unknown fields and
malformed/empty point lists retain path/source-line diagnostics. Existing node,
depth, aggregate value, document-byte and preflight execution budgets still apply.
Import/preflight/save validation does not connect devices.

## Execution and data

Each iteration publishes its value and 1-based index/total only after the wait
and a cancellation/pause checkpoint. Native columns use deterministic paths,
for example `ppms.root.0.axis`, `.axis.index`, `.axis.total`; nested loops have
separate columns. Electrical/environment measurements retain their normal
elapsed-second evidence and dependencies, so requested delay and actual elapsed
time remain distinct. Outer axes survive inner loops; rows after a loop omit
its optional axes. Empty bodies produce no synthetic measurement rows.

Waiting reuses `DwellAsync`: pause cancels the timer, confirms output-off/axis
hold and keeps the entire group reserved. The remaining delay resumes after
verified restoration; paused wall time does not consume that delay. Abort or
external cancellation stops waiting before a row can be acquired. Listener
faults stop later points and retain already committed data. Failed hold/stop
keeps all group claims until explicit successful safety recovery.

## Persistence and acceptance

Native version1 `for_each` accepts optional string `timing: DelayBeforePoint`.
Missing timing remains `Immediate`; the writer omits that default. Invalid,
numeric or unknown timing cannot silently downgrade to immediate execution.
A fresh native save/load and archive reopen preserve timing and permit execution
on a new registry. CSV exports retain normal axis/unit/elapsed/plan evidence.
Older applications which reject unknown fields will reject a timed plan rather
than execute it without waiting.

[ppms-number-time-import.json](../examples/plans/ppms-number-time-import.json)
is a five-row example: two number points, two time delays per number, then one
field observation outside both loops. It uses the simulated field binding and
requires no source preparation.

Focused acceptance: `TimedIterationTests` and `IterationScanTests*` (35 cases:
7 Core and28 Application). Includes real waits, pause/resume with reserved source
and controller, cancellation/abort, listener fault, failed hold/recovery, optional
nested axes, native save/reopen, fresh archive/CSV and re-execution. No new
packages, physical instrument calls, UI acceptance, database migration, background
scans or full behavior-tree semantics are added by this slice.
