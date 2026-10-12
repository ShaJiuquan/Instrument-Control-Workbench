# Current-row calculation inputs in the editor

Calculated measurement expressions have a different input boundary from ordinary
workflow variables. They read the just-completed acquisition row, active persisted
coordinates, elapsed time, and earlier calculation results in order. Previous
blackboard readings or unrelated workflow assignments must not become candidates.

Application `DescribeCalculationDraft` supplies metadata for a replacement
calculation or a palette append to a sequence/subtree. It first fully compiles the
unchanged host plan. It then visits the proposed acquisition at the selected
position with the same registered capabilities, preparations, routing, variable
and axis context. A private, request-specific inspection exit returns only draft
metadata; it never returns a partly compiled runnable experiment. Inspection does
not acquire leases, publish facts, connect, or issue driver commands.

This is an authoring preview, not admission: later host nodes with references to
changed results, global collisions declared after this position, complete step
budgets and post-visit coupled write checks still require the normal full
EditStep/PreviewPlan/StartPlan validation. No inspector path is used by execution.

## Ordered inputs and units

The preview obtains logical acquisition outputs from the same Core measurement
member/group definitions used by calculations. It includes declared environment
columns and active coordinates plus elapsed time; it does not include every
schema column or every known blackboard value. Joint points include all members.

For each result it freezes a sorted candidate list before checking the expression.
Unit inference uses Core numeric/unit rules in Application. Only a valid, unique
preceding result joins later candidates. Blank/incomplete results, self/forward
references, unrelated variables, duplicate/reserved/existing columns, and unit
errors produce a per-column message and no usable result binding. Later columns
can still be authored with independent inputs while earlier fields are incomplete.

Raw input absence flags use definite availability from the compiler and explicit
environment observation semantics, rather than the schema's optional flag for
all acquisition columns. Normal source/read pairs are present; environment values
and allowed failed/derived observations can be absent. Prior result choices are
conservatively marked potentially unavailable because arithmetic can fail at
runtime. Existing stop-on-unavailable/explicit-continue policies retain ownership
of that behavior. No missing numeric value is replaced with zero.

## Desktop changes

Calculation value forms and every nested arithmetic operand receive these
current-row candidates. The panel reports input IDs/units (first sixteen plus
count), optional inputs, inferred result unit or an actionable draft error.

Acquisition edits, result ID/expression edits, addition/removal of columns and
palette position changes refresh the metadata. Renaming/removing a source keeps
an already authored reference as text, clears its unavailable selection, and
reports the dependency error. The operator explicitly chooses the new name.
Invalid acquisition fields clear candidate lists until repaired. Refresh alone
does not mark the node dirty or discard a formula. Read-only/retired callbacks and
existing busy/JSON/document guards remain enforced. Manual IDs remain available
and must pass complete plan validation on apply.

## Evidence and limits

Tests cover current-row/axis/elapsed boundaries, exclusion of stale workflow values,
ordered units, self/forward/duplicate/incomplete references, renamed acquisitions,
optional environment data, joint points, append/replacement identity, subtree local
setup, invalid host/body and immutable metadata. Managed form tests cover stale
references, body repair, prior-result rename/removal, read-only/clean loading and
palette context restoration.

A picker-authored three-column power -> recovered voltage -> resistance chain
saves, reloads in a fresh runtime, executes simulated acquisition, preserves input
row 2 with units Watt/Volt/Ohm, safely switches source output off, reopens the full
archive and exports units and calculation evidence to CSV.

Preview refresh is synchronous and recomputes compiler metadata; there is no
incremental analysis cache yet. Specialized mutation/scope mapping ID fields and
wider workflow/device/controller parity remain separate work. No scripts, new
dependency, Core data/plan format, physical IO, or Rust changes are introduced.
Native rendered layout is not proven by managed construction/build; native
acceptance stays deferred per the user's request to continue code checks first.
