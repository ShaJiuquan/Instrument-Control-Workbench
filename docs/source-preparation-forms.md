# Source preparation forms

The independent C# execution engine remains the runtime authority. The desktop
plan editor now exposes K2450 source/sense preparation and K6221 DC current-source
preparation using the execution registry's device identities. Editing is offline;
no ViewModel constructs a driver or starts a connection.

## Boundaries and behavior

`ExperimentPlanWorkspace` upserts either preparation family or removes one device's
preparation. It previews the original plan and compiles the complete candidate
before returning a replacement document. The root, metadata, frozen routing,
other preparation family/order and execution budget survive. Wrong device family,
unknown IDs, coupled source/compliance/range violations and removing a preparation
required by paired resistance reads fail without changing the original plan.

`SourcePreparationFormViewModel` preserves all currently supported settings:

- K2450 source function, typed level/compliance, optional nominal source span;
  sense function, wiring mode, NPLC, automatic/fixed sense range and optional
  Auto Zero, Autorange Rebound and Repeat/Moving averaging/count.
- K6221 current/compliance, optional nominal current span and optional device
  Source Delay. Delta, pulse and waveform configuration are not added here.

New preparations have blank physical level/compliance. Changing source function
clears both numeric inputs and source range, so a number is never silently carried
from volts to amperes. Changing sense function clears its selected range. Numeric
input is invariant and finite; manual spans must be from the declared nominal
profile and the complete compiler still checks combinations against the plan.
Unspecified acquisition or K6221 Source Delay preserves existing device registers;
it does not mean disabled/zero or a software wait. A legacy fixed sense request
without a span also remains unspecified until the operator explicitly requests one.

The host tracks preparation drafts separately from node drafts. Either locks run,
file operations, validation, imports and graph edits. Dirty device selection and
whole-document replacement cannot silently discard preparation edits. Applying
requires an editable, validated base and no node draft; a mixed programmatic draft
can be explicitly discarded. Busy/disposed admission is checked inside command
bodies as well as bindings. Stale form callbacks are ignored. A late pending import
cannot replace a newly introduced preparation draft.

Named preparation/source/sense views use compiled bindings and a scrollable body
with pinned apply/discard/remove controls. The 1060 x 720 native inspection exposed
a redundant header that left too little editing space; it was removed. Unit labels
are beside the field labels so they remain visible when the body scrolls.

## Validation and limits

26 additional cases: 11 Application, 13 new App form cases, and two additions to
configured import tests. Actual save/fresh load/execute tests cover both source
families, one joint row, exact optional settings and output-off. The repository's
routed sample fixture retains its catalog and nested scan, produces three rows,
then confirms source-off and matrix cleanup. Offline rejection cases prove both
simulators remain disconnected. App cases exercise dirty/mixed/stale drafts,
finite parsing, nominal ranges, register-preserving nulls, mode changes and busy
command guards; configured pending-import cases independently check late results.

Native working-package evidence: four explicitly simulated startup devices;
minimum-size preparation tab; NaN locks run and validation, apply reports rejection,
discard restores the original value and run admission. The first layout-only
package closed normally with exit 0. The compact working package was left open
when the user selected the instrument page; no successful native valid apply/run,
K6221 dropdown, or final clean committed-package parity is claimed. The later unit
label and fixed-span-checkbox refinements have compiled/test evidence only.

Physical IO, real K6221 transport, remaining device-specific forms and the full
PPMS migration remain unfinished. GitHub-only publication; Gitee is deferred.
