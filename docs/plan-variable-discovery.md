# Variables at an editor position

The editor offers variable IDs from the same compiler data-flow state used for
preflight. It does not scan all schema columns and claim they are available.
Application `DescribeVariables(plan, step, position)` validates the complete plan
using registered metadata, without leases, connection, or driver commands.

`BeforeStep` reports variables definitely defined before the step's first static
entry. `AppendChild` reports the state after existing children of a sequence or
scoped subtree, before the subtree copies its outputs to the parent. Unsupported
positions and missing step IDs reject. Inspection retains only the requested
position's snapshot, rather than a variables-by-every-node matrix. Ordinary
execution/preview compilation does not retain inspection snapshots or origins.

Each entry carries a typed ID, blackboard kind/unit, whether its ID is in the
active persisted coordinate list, and possible last defining step IDs for that
static entry. Assignment, reads, mutations, scan/iterator binding, subtree input
and output copies retain their defining steps. Branch joins union definitions
only for variables defined with the same type on every path. Synthetic joins do
not become fake writers. Input copies point to the subtree; output copies point
to the subtree, not a leaked private writer. Snapshots/lists returned by the
compiler are immutable and sorted by ordinal ID.

These are static authoring facts, not runtime values, timestamps, a history of
all writes, or exhaustive loop-back provenance. A coordinate flag describes an
active coordinate ID; a subtree can map a local value under an inherited ID.
Runtime entry provenance remains in the existing blackboard/result model.

## Desktop behavior

The shared picker shows ID, kind/unit and up to eight definition labels. It is
used by value forms, condition left and right operands, and recursive arithmetic
operands. Hardware-bound direct variable choices require the parameter's unit;
QualifiedNumber accepts only dimensionless numeric inputs; arithmetic operands
accept quantities, while logical-capable forms also offer Boolean/Text.

Selecting a choice writes the explicit variable ID. Manual IDs remain editable
and must pass full compiler validation on apply. No value is evaluated in the UI,
no implicit physical conversion occurs, and unknown IDs are not invented.

Loading or refreshing choices does not dirty or rewrite a draft. Unit/address
changes retain existing rules that clear references; the choices then refresh.
New operands inherit the position's immutable context. Read-only and foreign
choice changes do not write IDs. Retired form callbacks cannot dirty the active
node. Existing guards still block stale/busy/editor/JSON conflicts from applying.

Node replacement uses `BeforeStep`. Palette append uses the selected container's
`AppendChild` state. Moving selection refreshes palette choices without discarding
its typed draft. A draft reference that becomes unavailable keeps its ID, loses
its selected choice, and is rejected on append before connecting; it is never
silently substituted with a different variable.

## Verification and remaining work

Tests verify future/read ordering, branch-specific exclusion and merged writers,
subtree local/output boundaries, iterator coordinates, immutable results,
decorator writer preservation, invalid positions and later invalid plan nodes.
Managed UI tests cover filtering, context refresh, read-only/stale choices,
recursive operands and palette rejection. A picker-authored arithmetic expression
saves, reloads in a fresh runtime, acquires the expected 0.04 V, closes source
output and reopens its archived row with the same value.

Per-row calculated expression columns use their own current-row context, documented
in calculated-row-variable-picker.md. Mutation/scope mapping and other specialized
form ID fields still accept manual IDs. Their existing
full preflight remains authoritative. No language scripts, new dependencies or
physical instrument transport are added by this stage.

Compiled XAML and managed control construction do not prove rendered native
layout; native acceptance remains deferred per the user's code-first request.
