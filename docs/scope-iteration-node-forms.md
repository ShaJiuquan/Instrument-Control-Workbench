# Scoped subtree and numeric iteration forms

## Behavior and boundaries

Named compiled-binding forms expose existing SubtreeStep and ForEachStep models.
The scoped subtree editor preserves its ID and children while allowing explicit
parent-to-local inputs and local-to-parent exports. Mapping rows use mandatory
source/destination IDs, a readonly observable collection and guarded add/remove
commands. Empty maps are valid; duplicate sources/destinations, missing producers
and incompatible types remain subject to Core and whole-plan compiler validation.

The existing interpreter snapshots parent values for simultaneous input binding,
restores parent state on scope exit, and publishes selected exports only after
successful execution. Neither failed nor cancelled execution publishes exports.
Local edits do not leak automatically; this editor adds no execution shortcut.

The document structure adapter now permits append/remove/reorder of direct
children in ordinary sequences and scoped subtrees. Required body/branch/root
slots stay protected. Every structural edit is reparsed and compiled as a complete
candidate; removing a required variable producer fails without replacing the plan.
The graph palette can create both scoped subtrees and numeric iterations.

Numeric iteration exposes variable ID, finite invariant comma-separated values,
explicit units, Immediate versus DelayBeforePoint timing, and zero/one-based
indices. Existing body and ID are retained. Timing/unit changes clear numeric
input to prevent silently relabelling values. DelayBeforePoint always binds seconds
and waits before each body execution. Core timer bounds, collection limits and
compiler execution budget remain authoritative. The existing .index/.total columns
are preserved. New iteration palette entries require explicit variable and values.

App -> Application -> Core boundaries, whole-plan admission, mixed form/JSON,
stale nested forms, preparation and busy-state guards remain in effect. No new
dependencies, Core model/interpreter changes, physical IO or Rust edits.

## Evidence and remaining work

24 new App cases cover all timing/index combinations, retained children/units,
invalid/nonfinite/negative/oversized delays, map retention and readonly commands,
invalid mapping candidates with no source connection, palette construction and
stale/mixed/busy/preparation guards. Structural append/reorder/remove preserves
mapping metadata; deleting a required export producer retains the previous plan.

An edited subtree input saves and reloads on fresh simulated devices, produces one
0.02 Volt row, exports only the selected result, preserves the parent's 0.01 Volt
value, then confirms output-off and released run. Edited immediate/timed iterators
save and reload on fresh simulated devices, produce three rows with correct
.index/.total and units, and confirm output-off/released run.

Native rendering and interactive acceptance remain pending because the current
callable tool inventory has no native cua control. No user window is manipulated
or new app process launched; compiled bindings and simulation tests are not native
rendering proof. Clean committed-source packaging is recorded separately.

Scan generators, richer variable discovery/analysis/other node forms, general
arithmetic expressions, bounded data storage/indexing, real transport and physical
PPMS acceptance remain unfinished. Independent C# engine, GitHub publication only,
Gitee deferred per user; the overall migration goal remains active.
