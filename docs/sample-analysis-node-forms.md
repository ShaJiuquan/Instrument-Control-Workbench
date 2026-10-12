# Sample analysis group authoring

## Behavior and boundaries

Named compiled-binding PlanSampleAnalysisFormView and typed models expose the
existing SampleAnalysisStep. Four derived columns keep their fixed quantity kinds
and physical units while column IDs are editable. Required ordered inputs remain
a fixed readonly list of two Hall or eight van der Pauw directions; inputs cannot
be removed or reordered through these controls.

Each direction composes its own existing acquisition, sample-routing and environment
forms in an expander. All fields are edited as one parent draft and compiled once
on Apply. No direction is copied over another: per-input minimum current, route/
acquisition dwell, independent expected-current expression, reference, retry,
environment and original IDs are preserved. Shared raw columns, source/sample/
wiring/function, environment policy, ordered directions, exact independent routes
and derived/raw column separation remain enforced by Core.ValidateRouting and the
whole-plan compiler. A partial coordinated edit can remain a draft, but cannot
replace the validated plan or start a run.

The geometry display resolves the first draft binding against the actual immutable
plan sample catalog; caller-supplied choice metadata cannot provide substitute
geometry. Width/length/thickness display explicitly in meters or as missing.
Geometry/catalog creation and editing remain a separate unfinished workflow.

Four palette kinds create Hall/van der Pauw groups for integrated K2450 or independent
K2450/K6221 plus K2182 acquisition. New derived IDs and minimum-current inputs are
blank and require authoring; sample/wiring/directions and independent reference
must be explicitly selected. New raw column defaults are scoped to the unique
analysis step ID, so an unrelated existing environment policy is not accidentally
shared through generic column names. Full compiler validation stays authoritative.

App -> Application -> Core boundaries, stale nested draft identity, readonly,
preparation/busy admission and mixed form/JSON conflict guards remain. No driver
calls, dependency, Core/interpreter/schema or Rust changes are introduced. Existing
raw input rows plus separate bounded analysis summary evidence remain the result
contract; no invented Hall coefficient/carrier estimate or UI analysis algorithm.

## Verification and remaining work

24 App cases cover four acquisition/topology roundtrips with distinct per-reading
settings, ten invalid whole-group candidates (raw/derived collisions, missing IDs,
single raw/environment/sample/wiring edits, wrong direction, external route and
negative threshold), four coordinated sample/wiring/raw/derived-column rebinds,
four explicitly authored palettes, missing geometry and draft/admission guards.
Invalid candidates retain the old plan and leave simulated source/matrix disconnected.

Edited integrated/independent Hall and van der Pauw groups save and reload on fresh
simulated devices. Their ordered raw routes and readings produce three/nine rows,
including a separate summary that agrees with the Core analysis of those actual
raw values. Independent routes are rebuilt from the selected sample catalog.
New palette groups likewise save/reload and execute on fresh simulated devices.
Source/current outputs are off, matrix connections empty and run ownership released.
Missing geometry keeps both raw Hall rows and measured longitudinal/transverse
results, marks MissingGeometry and omits unavailable sheet resistance/resistivity.

Native cua inventory was rechecked and reports the Mac locked with automatic
unlock unsuccessful. The prior asynchronous unlock request remains pending; no
user window, additional app process or physical instrument was manipulated.
Native minimum-window rendering, interactions and committed-package parity remain
unverified; compiled/test evidence is not a native visual acceptance claim.

Catalog/geometry creation, variable discovery/other workflow forms, arithmetic
expressions, bounded data storage/indexing, real transports and physical/full PPMS
acceptance remain unfinished. Independent C# engine/GitHub-only publication per
user; Gitee deferred; full migration goal active.
