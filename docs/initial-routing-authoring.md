# Initial routing catalog authoring

A plan without a routing catalog can now create its first matrix/channel/wiring/
sample directory in the Samples editor. This removes the requirement to import
profiles before authoring routed resistance nodes.

## Operator flow

1. Register matrix and supported source devices in the device configuration.
2. Select **配置初始路由** in a validated plan with no catalog.
3. Select the actual matrix, source, and optional independent K2182 meter.
   K6221 needs the independent meter. All selections start empty.
4. Enter wiring ID, instrument port card/row addresses, channel ID/name,
   sample ID/name/topology, and connection IDs with card/column addresses.
   Geometry and other sample metadata use the existing details form.
5. Apply the catalog. The application checks every direction for the topology
   against registered matrix specifications, excitation guards, and device roles.
6. Select **样品电阻** or **独立源样品电阻** in the node palette, explicitly select
   sample/wiring/direction and minimum current; independent reads also require
   the current reference. Configure source preparations separately, then save/run.

Device or topology changes clear affected physical addresses. Busy/unapplied
sample drafts block run/save/import/node/preparation edits; discard restores the
original validated plan. Initialization cannot replace an existing catalog.
The palette refreshes after successful sample catalog edits so it resolves the
new snapshot rather than retaining stale sample choices.

## Boundaries and evidence

Registry exposes immutable actual matrix definitions through the existing
Application service/workspace. The form resolves choices by registered identity;
caller-provided matrix specifications and device metadata cannot alter admission.
The Application initialization use case validates dormant catalog routes without
connecting instruments, then compiles/serializes the whole candidate. Existing
root steps, preparations, metadata and budget remain unchanged.

31 new App and 10 new Application cases cover missing fields, duplicate IDs and
terminals, integer parsing, actual card bounds (including inactive Hall direction),
unknown devices, guard/meter roles, stale callbacks, busy state, physical address
clearing, forged choice metadata and overwrite rejection. Eight new-catalog
save/reload/fresh-simulation runs cover integrated two/four-wire/Hall/vdP and
independent four-wire/Hall/vdP with K6221+K2182, plus K2450-current+K2182. They
verify actual route provenance, exact independent guards, resistance rows and
source-off/matrix-open/released-run cleanup.

Native interaction and minimum-window visual acceptance are deferred at the
user's request to continue code checks. No real instrument IO or new dependency
was introduced. General channel/wiring editing and multi-profile creation remain
future work; this editor initializes one channel, one wiring, and one sample.
