# Validated plan graph editing

The desktop flow view projects the actual native plan tree, with parent links,
slot/sequence positions, selection and current execution highlighting. Display
coordinates and links do not redefine execution. The initial node editor used
JSON fragments with a delay/sequence/repeat/timeout palette. Common typed forms
and instrument palette additions are described in [plan-node-forms.md](plan-node-forms.md).
Arbitrary wire editing remains pending.

Core's optional `IExperimentPlanStructureDocument` port describes structure and
accepts explicit replace/append/remove/reorder operations. The JSON Infrastructure
adapter retains the complete plan envelope, including preparations, routing,
metadata and execution budgets. It uses the native codec's depth/node/byte bounds;
value conditions are not confused with executable condition children. Group
member bodies and sample-analysis inputs retain their actual container slots.
Only direct ordinary-sequence children can be removed/reordered, and only an
ordinary sequence accepts palette additions. Required bodies and roots cannot
be deleted. Replacement retains the selected ID; full codec/compiler validation
rejects duplicate IDs, unsupported fields, invalid units, broken variable
dependencies, missing group acquisitions and exceeded budgets before draft
replacement. Editing never connects a device.

Application validates the entire candidate with the existing execution compiler.
App publishes it only after success. Unapplied node fragments disable run,
load/save, full-document edits and selection changes; apply failure preserves the
validated plan and fragment. Discard restores the original node and clears the
error. Run/document/disposal admission is checked in command bodies as well as
CanExecute; the running plan stays immutable, with inspection still available.

Native1060x720 inspection found that file controls and results left almost no
graph viewport. Both are now expandable. The actual working package verified
graph links/scrolling, add/move/remove, negative-delay rejection and discard,
legal15-second delay replacement, save/reload, pause/edit locks/resume, one real
simulation data row and reported output-off, followed by idle close/exit0.
Its archive retained the edited plan, metadata and preparations. No lab IO was
performed. Clean committed-package parity is separately recorded in the chat's
publication audit; a package build alone does not establish native acceptance.

28 new Application cases and six App cases cover structural slots/envelope,
semantic rejection, member acquisition constraints, deep native plans, command
locks, draft apply/discard and run-specific highlighting. No new dependency.

Remaining: further device-specific forms, drag/reparent UX,
viewport virtualization/collapsed subtrees, general expressions and unsupported
parallel/race/background workflows. Graph projection remains bounded by the
document limits but is not virtualized. This stage does not complete full PPMS
migration, physical-device acceptance or whole-platform native lifecycle checks.
