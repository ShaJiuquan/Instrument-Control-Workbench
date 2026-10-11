# Typed threshold switch

`ThresholdSwitchStep` is a Core composite over a numeric blackboard variable,
an ordered child list and a prefix of physical `Quantity` lower bounds.
Application selects one child and reuses the normal run/resource/lifetime
execution path. Infrastructure handles native documents and PPMS import.

## Selection

For value `x`, the last declared threshold satisfying `x >= threshold` wins.
Thresholds are not sorted; duplicate thresholds retain their order. No epsilon
is applied. If none matches, or no thresholds are configured, the last child
is the default. That last child can also have its own threshold. Zero children
are an explicit no-op and require no selector read.

For thresholds `[10, 0, 5]` and four children, `-1` chooses child4, `0` chooses
child2, and both `5` and `10` choose child3 (one-based labels here). Numeric
selector and thresholds must have identical physical units. Boolean/text and
unavailable selectors reject before resource admission.

Compiler validates every child, intersects definitely available variable types
incrementally and budgets the longest branch rather than adding mutually
exclusive branches. All compiled resource requirements still participate in
normal atomic reservation/preparation; only the chosen child executes.
Expected child failure retains its original identity and can drive an outcome
branch. Driver faults, cancellation and deadlines retain the existing cleanup
and quarantine semantics; the switch does not catch or retry them.

## Documents and PPMS

Native version1 uses `kind: threshold_switch`, a variable ID, ordered `children`
and `thresholds` quantity objects. Node/depth/value/UTF-8 bounds include all
children and thresholds before encoding. Core row/sample/blackboard validators
walk every child, so scientific evidence and final variables survive fresh
archive reload/CSV/re-execution.

PPMS `switch` and schema alias `select_case` import numeric `case_1`, `case_2`,
etc. Only the contiguous prefix up to the child count affects selection; valid
gapped/excess fields are ignored as in the inspected Rust handler. Empty
switches normalize to an empty native sequence. Known physical axis and migrated
numeric callback units are explicit. Closest enclosing iterator child context
also resolves value/iterator_value/index/total through intermediate containers.

The importer deliberately rejects malformed/nonfinite thresholds, unknown
fields/noncanonical case names and unknown unit contracts, even when an unused
legacy field would have been ignored. Case names are bounded to1..10000. It
does not infer units or treat missing variables as zero. General script/dynamic
axis contracts, native visual branch editor and physical acceptance remain
pending. The frozen source inspected is PPMS Rust35763295f63b,
`node_execution/handlers/switch.rs` and `schema/commands.json`. Rust is unchanged.

The repository fixture `examples/plans/ppms-threshold-switch-import.json` runs
a simulated three-point field scan, taking default/first/second children in
that order. Tests include boundary/unsorted/duplicate/default cases, type/unit
and definite availability, exact branch execution budget, expected failure,
real local TCP read fault, abort/cleanup, nested iterator import, strict codec,
save/restart/re-execute/CSV and switched Hall scientific/terminal provenance.
No UI draft, dependency or physical instrument change is included.
