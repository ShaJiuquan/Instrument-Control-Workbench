# Environment and sample routing forms

Environment reads and sample routing can now be edited in the native node form.
The complete compiler remains the authority; editing is metadata-only and never
connects a device or changes relays/output.

## Boundaries

DeviceRegistry freezes AxisDefinitions from its actual AxisControllerSession
instances. MeasurementRunService and ExperimentPlanWorkspace expose that same
read-only catalog. App does not infer an axis from a model name or any readable
parameter: choices use the registered definition's actual readback, filtered by
Kelvin/Tesla/Degree for Temperature/Field/Angle. Targets are not offered.

The shared environment form adapts ReadStep and all supported source/sense,
resistance, independent and sample acquisitions. Each role has inclusion, result
column and explicit bound/unbound controls. Unbound requests record a missing
observation; they do not fall back to a target or fabricate a value. Existing
order and bindings survive unedited roundtrip; after changes, surviving roles
retain authored order and new roles append. A directly constructed form without
an axis catalog can preserve an original address, but an explicit binding change
requires a catalog choice. The full runtime compiler still validates every address.
Caller-supplied choice metadata is resolved against the form's frozen catalog.

Sample forms receive the plan's frozen RoutingConfiguration. Selectors expose its
samples and architecture-compatible wirings; topology limits the measurement
selection (two/four wire, Hall longitudinal/transverse, eight vdP arrangements).
Changing the sample requires a new explicit measurement direction. Resolution
uses catalog identities, not caller-provided labels/topology/ports, and previews
source/meter/matrix and all four electrode/crosspoint assignments.

For sample acquisitions, wiring determines source and independent voltage meter;
direct source/meter controls are hidden. Rebinding an independent sample replaces
its exact required route with the newly resolved electrode route. Changing the
wiring's source clears reference and constant expected-current input for explicit
reselection. Unchanged sample bindings, guards, expressions, retry, timings,
result columns, preparation envelope and metadata/budget remain intact. Domain
and full compiler reject incomplete choices, disabled/missing electrodes, wrong
architecture, polarity/slot/card/source/preparation and derived-analysis conflicts.
A form without a routing catalog preserves the original binding and cannot
silently accept a requested rebind.

Nested readonly/draft changes use existing form/JSON exclusivity, stale callback
checks, preparation/run/file/import locks and busy command-body admission. Named
compiled-binding views are expandable within the existing scroll area; no new
UI/workflow dependency or device adapter was introduced.

## Verification and remaining work

22 new App cases cover catalog identity/read-only/no-IO, environment preservation,
three read families saved/reloaded on fresh simulated runtimes with actual Kelvin
readback and explicit missing-field observation, invalid columns/roles/unselected
controllers, stale/mixed/busy/preparation locks, all topology choice sets, forged
sample metadata, and integrated/independent sample/wiring rebinding. The latter
execute a new sample on columns5..8 and, independently, source current2/meter2
with the new complete guard, then verify route provenance, source-off/matrix
cleanup and that unused source sessions remain disconnected. Invalid topology,
unknown sample/wiring, wrong architecture and disabled-electrode candidates keep
the original plan and leave every device disconnected. Prior acquisition/step-form
roundtrip suite also passes. These tests do not certify hardware protection.

Native rendering/action acceptance is pending. Prior same-bundle AX window
attribution was unresolved, so this stage does not manipulate or close the user's
existing windows or claim native interaction from compiled/test evidence. The
clean release package is recorded separately in publication artifacts.

Remaining: richer catalog creation/import UX, analysis and general expression
forms, other PPMS features, data-memory/indexing work, real transport and physical
acceptance. Independent C# engine; GitHub-only publication; Gitee deferred. Full
migration goal remains active. No Rust edit or physical lab IO.
