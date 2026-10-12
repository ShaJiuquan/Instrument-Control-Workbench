# Sample creation, electrode mapping and removal

## Behavior and boundaries

Existing channel/wiring catalogs now support new sample drafts with mandatory ID,
name, topology and real electrode choices. Metadata and optional Meter geometry
reuse the details editor. Two-wire/four-wire/Hall/van der Pauw topology determines
required labels; switching topology clears all electrode and verification-wiring
choices. New samples never inherit a hidden physical mapping. Old topology form
callbacks are ignored after replacement.

Each electrode selects a channel and one of that actual channel's connections.
Changing channel clears the point. Choice labels expose slot/column and disabled
state. Build resolves canonical IDs against the frozen catalog, ignoring caller-
supplied profile/connection metadata. Existing unedited electrodes retain original
IDs/enabled/notes, including extra electrodes; edited rows retain their fixed labels.
Existing sample IDs/topologies remain fixed in this editor.

Application AddSample/UpdateSampleDefinition/RemoveSample preserve root,
preparations, metadata, routing matrix/channels/wirings and execution budget, then
compile/serialize the complete candidate. Duplicate IDs are case-insensitive;
catalog limits remain 1–64. Mapping updates retain current sample details and
identity. New/changed mappings explicitly select a verification wiring.

MeasurementRunService.ValidateSampleRoutes performs pure admission against its
actual registered matrix cards, source guards and optional meter role. It resolves
all required directions, checks enabled links and polarity/terminal constraints,
and validates physical crosspoint bounds. It does not configure, connect or switch
hardware. Validation wiring does not automatically rewrite any measurement step.
A fixed independent acquisition route must still match the catalog, so an
incoherent active independent mapping change is rejected rather than weakening
its guard. Coordinated guard/definition changes can use the full plan JSON path;
a dedicated atomic UI workflow remains pending.

New samples reuse SampleDirty admission for run/file/import/node/preparation/JSON
and selection locks. Existing hidden forms become readonly during creation; stale
callbacks, busy state and other drafts are guarded. Discard restores the current
validated catalog. Removal succeeds only when the remaining catalog and every
plan reference still compile; used/last/unknown samples cannot disappear silently.
No user file is overwritten until the existing Save command is invoked.

Named compiled-binding creation/electrode views compose inside one Samples scroll
owner. No Core model/schema/interpreter/driver changes or dependencies are added.

## Evidence and remaining work

28 new App cases and ten new Application cases cover explicit authoring, all four
topologies, three independent source/meter variants, invalid IDs/names/topology/
point/wiring/disabled/duplicate-terminal/bounds, topology clearing, frozen choice
metadata, stale/busy/other editor admission, existing mapping edits and removal.
The existing 19 App and eight Application details cases also pass in focused runs.
Additional API tests check an out-of-card Hall transverse terminal even when the
current plan only references its original sample, wrong source guard/meter role,
unknown wiring, catalog count limits and referenced/last/missing removals.

Seven new samples save and reload on fresh simulated devices, rebind an actual
measurement to new sample terminals in columns 7–12 and produce a real resistance
row with exact route provenance. Independent required guards derive from the new
selected binding. Four existing integrated mapping edits likewise save/reload and
run new crosspoints while preserving old immutable sample snapshots. Output-off,
empty matrix and released run are verified. Invalid changes send no source/matrix
connection commands and retain the previous plan.

Native cua inventory again reports the Mac locked and unable to unlock automatically.
The earlier optional unlock request remains pending; no user window/new app process
was manipulated. Native minimum-window rendering/interactions/committed-package
parity remain unverified. Build and tests are not native visual acceptance.

Starting a brand-new routing catalog, channel/wiring creation/editing, atomic UI
coordination of active independent guards, remaining workflow/variable forms,
arithmetic expressions, bounded data memory/indexing, real transport and physical/
full PPMS acceptance remain unfinished. Independent C# engine/GitHub-only publication
per user; Gitee deferred; whole migration goal active. No Rust edits or physical IO.
