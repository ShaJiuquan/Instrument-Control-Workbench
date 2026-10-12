# Shared scan value generators

## Behavior and boundaries

PlanScanValuesFormViewModel and its named compiled-binding range/collection views
adapt the existing Core ScanValues rules for both parameter scans and ForEach
iterations. Explicit lists retain duplicate points; linear point counts include
both endpoints; fixed positive step sizes derive ascending/descending direction
from endpoints and include the final endpoint; logarithmic endpoints must be
positive. Segments concatenate in list order and collapse adjacent duplicates per
the existing Core rule. Empty segments and invalid definitions are rejected.

All generator inputs use finite invariant parsing and typed units. The actual
registered writable parameter supplies a hardware scan's unit and range limits.
Iteration units are explicit, with DelayBeforePoint always bound to Second and
validated using the existing timed iteration constructor. Switching hardware
address, iteration timing or iteration unit clears all numeric fields in active
and inactive range/segment drafts and the preview. There is no physical relabel.

The preview command explicitly computes point count and the first twelve values,
validates parameter range or iteration timing, and sends no device command. It is
not a replacement for full plan compilation: variable availability, resource
ownership, coupled settings and execution budgets remain checked on Apply/Start.
Preview text is invalidated by edits and does not itself make a plan dirty.
Readonly map collections and guarded commands respect run/preparation admission;
stale nested drafts and mixed JSON/form edits keep the existing conflict checks.

A shared form replaces the two duplicated list parsers. Core/domain/interpreter
algorithms and version-1 document schema are unchanged. Saved generated values
are explicit lists, not unevaluated formulas; a fresh form loads as List and
reproduces exactly those values. Generator formula provenance is not persisted.
Original body/ID/column/dwell/timing/index metadata is retained. No dependency,
Rust or physical transport change is introduced.

## Verification and remaining work

New cases cover four modes, descending endpoint inclusion, twelve invalid
numeric/count/step/log/overflow definitions, segment order/duplicate handling and
readonly membership commands, clearing all hidden/visible physical inputs, range/
budget/empty-segment full candidate rejection with no device connection, preview
and stale/mixed/busy/preparation locks. Actual hardware parameter rebinding clears
all generated drafts; both palettes require explicit authoring.

Four generated source scans save/reload on fresh simulated devices, reproduce
expanded values and body/dwell metadata, execute actual readbacks for every point,
and confirm output-off and released run. Immediate/timed generated iterations
save/reload and execute three rows with correct units/index/total and cleanup.
Existing scan and scope/iteration cases are updated to the shared editor contract.

Native inventory was checked through cua_repl; it reports the Mac locked and
unable to unlock automatically. An asynchronous request asks the user to unlock
when convenient. No native window or new app process was manipulated; compiled
bindings and simulated tests are not native visual/interaction acceptance. Native
minimum-window checks and committed-package parity remain pending.

Richer analysis/variable/catalog authoring, general arithmetic expressions,
bounded data storage/indexing, real instrument transports and physical/full PPMS
acceptance remain unfinished. Independent C# engine, GitHub only per user; Gitee
deferred and overall goal active.
