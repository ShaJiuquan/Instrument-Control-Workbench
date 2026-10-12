# Plan sample metadata and geometry editor

## Behavior and boundaries

A named Samples tab exposes existing plan sample names, original identifiers,
material, substrate, notes and four optional geometry dimensions. Geometry is
explicit Meter data; selected dimensions require finite positive invariant values.
Unselected dimensions remain null, not zero. Sample ID, topology and electrode
links remain authoritative and are not editable through this details form.

Application ExperimentPlanWorkspace.UpdateSampleDetails resolves a case-insensitive
ID against the existing frozen routing catalog. It validates the original plan,
constructs the replacement using original canonical ID/topology/electrodes, retains
channels/wiring/matrix, root, preparations, metadata and execution budget, and
compiles/serializes the complete candidate. Supplied profile metadata cannot replace
routing authority. Editing sends no device commands; changes belong to the plan.
No Core/domain/schema/driver change or new dependency is required.

Sample edits join the existing unapplied draft admission. A dirty sample blocks
run, validate, load/save, PPMS/profile import, sample/node/preparation selection and
node/preparation mutation. JSON changes are rejected while sample/preparation edits
are pending. Current sample fields remain editable until Apply/Discard, while busy,
document operations and other drafts make them readonly. Stale form callbacks are
ignored by current-instance identity checks. Selection rejects forged choices.

File load/save now also checks text/path and unapplied drafts after awaiting IO.
A late load result is not applied over a newer draft. Save still writes its original
snapshot but reports that newer edits remain locally, instead of silently clearing
them. Cancellation is checked again before applying a result. Existing import gates
already depend on the shared unapplied flag and late-result guard.

## Evidence and remaining work

Eight Application cases verify canonical sample details and preserved envelope/
route authority, frozen original plan, codec roundtrip, unknown/no-catalog/blank/
large/null details and whole-compiler rejection of invalid original plans without
source/matrix connection.

Nineteen App cases verify optional/readonly Meter dimensions, nine invalid geometry
values, three invalid metadata cases, file/node/preparation/JSON/selection admission,
stale/busy/forged selection and controlled delayed load/save. An edited Hall sample
saves and loads on fresh simulated devices, runs the existing three-bias plan for
nine rows, preserves route identity/electrodes while updating sample name/metadata,
and derives each separate summary from actual raw readings and new geometry.

A separate real file archive test runs old/new/missing-thickness plans. New geometry
changes sheet resistance and resistivity by the actual width/length/thickness ratios;
old live data and a fresh reopened archive retain old geometry and results. Removing
thickness keeps raw readings and sheet resistance, sets MissingGeometry and omits
unavailable resistivity. All simulated runs confirm output-off, matrix empty and
released run resources. The test uses fixture geometry as authoritative rather than
assuming a nominal initial thickness.

Native cua inventory was rechecked: Mac locked and automatic unlock unsuccessful.
The earlier optional asynchronous unlock request remains pending. No user window or
additional app process was manipulated; native minimum-window rendering/interactions
and committed-package parity remain unverified. Compiled bindings/test evidence is
not a native rendering claim.

New sample/channel/wiring/topology/electrode creation, remaining workflow/variable
forms, arithmetic expressions, bounded data storage/indexing, real transport and
physical/full PPMS acceptance remain unfinished. Independent C# engine, GitHub only
per user, Gitee deferred; overall migration goal active. No Rust edits or physical IO.
