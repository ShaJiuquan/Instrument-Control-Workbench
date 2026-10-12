# Routed PPMS IV and VI meter sweeps

The independent C# engine executes the real PPMS runtime shapes:

- IV: `scan_current` with a `measure_voltage` child. Programmed Ampere is
  the independent axis; the explicitly bound passive K2182 supplies observed
  Volt. Zero current is a valid point because this acquisition does not divide
  voltage by current.
- VI: `scan_voltage` with a `measure_current` child. Programmed Volt is the
  independent axis; K2450 supplies observed Ampere, buffer quality and actual
  acquisition filter settings. The axis is not claimed to be measured voltage.

The PPMS schema's `measure_iv`/`measure_vi` are authoring templates, not runtime
node kinds. Its serializer emits the scan and meter children. This stage does
not add a second template engine or silently treat a bare template name as an
executable runtime node. Existing typed scan points support lists and the
previously migrated linear/step/log/inverse/segmented contracts.

## Boundaries and ownership

Core `ReadStep` optionally carries immutable `MatrixRouteExpectation`. The
Application compiler validates its matrix, crosspoints and guarded output
sources before admission. Imported reads declare their excitation source; the
compiler requires that exact source in the matrix guard set. Both the matrix and all guards join the existing
atomic resource reservation, cancellation, pause, stop and recovery lifetime.

The interpreter confirms the accepted route before reading and again after all
measurement channels and environmental observations. It publishes measured
blackboard values and the data row only after confirmation. A changed route,
unknown/disconnected switch, acquisition exception or cancellation aborts this
read without a partial row or partial measured-value publication. Earlier
valid rows and variables remain valid. All measured-value updates follow a complete successful acquisition;
individual blackboard writes retain their existing separate revisions. No retry or logical-result decorator masks
hardware faults. A refused output stop retains ownership for explicit recovery.

Infrastructure import translates authoring route intent into ordinary native
setup/read nodes; it never performs IO:

- A meter child without an explicit channel inherits its source scan's channel.
- A named child channel must resolve to the same source, meter, matrix and
  crosspoint set. Frozen profile aliases that describe the same route are valid.
- A child cannot bypass a routed parent using `none`, or introduce a named route
  inside an externally wired scan. An energized scan is never rerouted per point.
- Independent voltage reads require explicit source-to-meter wiring identity
  and a bound current-source preparation (K6221 or current-mode K2450). The
  nearest enclosing source scan supplies excitation context; incompatible
  source/meter pairs are rejected.
- An independently routed passive voltage read outside a scan owns route setup
  and verification, but does not apply an unused excitation preparation or
  enable source output. Matrix guards still join resource admission and cleanup.
- Empty/`none` standalone channels retain the explicit externally wired or
  meter-only path. Native plans can author optional read route requirements
  directly without an importer.

Native plan version1 gains optional `read.requiredRoute`; documents without it
remain readable. Its device/closed/optional source structure is strict and has the same
4096-point domain bound. Encoding counts route points before DOM allocation.
Saved plans, archives, terminal blackboard snapshots, CSV and wide measurement
summaries use the existing validated data pipeline.

## Examples and verification

`examples/plans/ppms-vi-meter-import.json` uses the existing simulated
`workbench-devices.json`, K2450 source preparation with current sensing and
`k2450` profile wiring. `ppms-iv-meter-import.json` uses the existing simulated
`k6221-meter-matrix-devices.json` and `k6221_k2182` profile wiring with explicitly
bound source and nanovoltmeter. Both fixtures are actually imported and run.

Forty-four new cases (4 Core,40 Application) cover immutable model boundaries, real
owned localhost TCP VI acquisitions, two/four-wire status, manual source/meter
ranges and filtering, zero and negative points, inherited/equivalent channels,
wrong roles and channel bypass rejection before IO, drift before/during reads,
multiple channels, pause/abort/drain, refused stop/recovery, native malformed
routes and aggregate encoding bounds, both repository fixtures, saved-plan
replay, fresh archives/CSV and wide groups. The focused regression set passes
133 Application and4 Core cases.

## Limits

Matrix confirmation is discrete before/after read; it cannot detect a transient
change that is restored between observations or guarantee physical relay
contacts. Raw scalar rows retain meter-quality evidence and a guarded plan;
they do not gain the separate per-row wiring/electrode evidence of sample
resistance rows. VI NPLC/manual-range checks prove final register configuration;
only the buffer's wire mode and existing acquisition settings are per-reading
evidence. Simulated IV voltages are fixed fixture observations, not an
electrical model coupling the source and specimen. Coherent actual-source/sense
pairs remain available through native `ReadSourceSenseStep`.

Physical instrument adapters and lab acceptance, a native IV/VI editor and
plots, full PPMS templates and arbitrary parallel/background execution remain
pending. The29 original UI drafts are preserved and excluded from publication.
No new dependencies, Rust changes or physical device IO. GitHub only; Gitee
is deferred by the user. The full migration goal remains active.
