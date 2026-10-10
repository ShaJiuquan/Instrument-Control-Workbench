# Typed experiment execution

`MeasurementRunService.StartPlanAsync` runs an immutable `ExperimentPlan`.
`StartIvSweepAsync` converts the desktop IV recipe to the same plan model; it no
longer owns a separate hardware lifecycle. The current desktop still presents
IV controls. This API is the execution boundary for a future flow editor.

## Ownership and validation

The compiler uses registered capability metadata to check the whole plan before
admission, including branches that may not execute. It rejects unknown devices,
unsupported node types, duplicate step IDs, inconsistent result columns, wrong
units, read-only writes, unbound variables, excessive nesting/node count and an
estimated execution cost over 10,000,000 steps. Literal scan/write values are
range-checked up front. Dynamic variable values are range-checked before each
write; a failure finalizes partial results and stops all touched resources.

Admission claims one device group atomically. Manual and legacy IV operations
use the same session ownership checks. A failed admission does not touch or stop
another owner's devices. Claimed resources that have never received an IO
attempt can be released without driver cleanup; all touched resources require
successful stop confirmation. Output-off is the source policy; hold at current
readback is the axis policy. Magnet zero is not an automatic cleanup action.

A failed/timed-out stop keeps every group lease and the original pending task.
`RecoverSafetyAsync(token)` retries completed failures, never a live stop.
`RecoverSafetyAsync(token, reconnect: true)` explicitly reconnects under the
same owner first when disconnected hardware needs transport recovery. Neither
API resumes an interrupted experiment. Simulation descriptors may confirm their
offline model's stop state; disconnected Hardware descriptors cannot.

## Steps

| Model | Behavior |
| --- | --- |
| VerifyMatrixRouteStep | Confirm the exact accepted route without relay writes or output changes |
| SoftwareTemperatureRampStep | Confirmed temperature control, live origin, optional integer-millikelvin ramp; separate stability wait ([contract](temperature-control.md)) |
| SequenceStep | Run children in order; empty sequence is allowed |
| RepeatStep | Execute body for a positive bounded count |
| ScanStep | Write each typed axis value, run body, then dwell; nesting creates outer/inner axes |
| ForEachStep | Bind value plus `<variable>.index` (1-based) and `.total`, then run body |
| SetParameterStep | Write a literal or already-defined variable through the owned registry |
| AssignValueStep | Assign a literal or variable with stable units |
| SourceOutputStep | Enable/disable an implemented source-meter profile under ownership |
| ReadStep | Read named channels sequentially into one committed row |
| ReadSourceSenseStep | Read actual V/I from one integrated source-meter frame with its timestamp |
| DelayStep | Wait cooperatively; pause preserves remaining duration |
| IfElseStep | Compare matching numeric units and execute the selected branch |
| WaitStableStep | Require readback within tolerance continuously for a duration, with polling and timeout |

`ScanValues` builds linear, positive-step ascending/descending, positive-endpoint
logarithmic, or segmented values. Endpoints are retained exactly; consecutive
shared segment boundaries are merged. A scan does not itself assert that an axis
has reached its target. Put `WaitStableStep` before measurement when required.
Joint reads share a row but do not claim simultaneous hardware sampling.

Variables are run-local. Assignment/read values and the final iterator value
remain available after a loop; active row axes restore to the enclosing scope.
Variables created only on one condition branch cannot be used afterward.
Nested loop bodies need distinct step IDs; iterations reuse those IDs and can be
highlighted by the editor using `StepStarted`/`StepCompleted` facts.

## Pause and results

Pause is cooperative at step/scan checkpoints and during waits. It snapshots
axis targets and enabled outputs, confirms all stop policies, and retains run
identity/leases. Resume restores axis targets before enabling only previously
enabled outputs. Abort during pause finalizes partial data without restoring
outputs. A stability timeout excludes paused time, and its continuous stable
window restarts after resume.

Source configurations are captured before pause and verified before restoration;
changing mode, level, compliance, sense settings or terminals rejects resume.
See [source-sense-acquisition.md](source-sense-acquisition.md) for these checks and
the actual-value IV data/plot contract.

Each read commits the active axes, elapsed wall time and its measurement columns
before notifying listeners. Local history receives committed rows first.
`DataRowReceived` carries the immutable row and step/run IDs; the IV adapter also
publishes the existing plot points. The run remains active through safe stop and
all result finalizers. Different measurement branches have optional schema
columns; present columns still require their declared axes/time. No placeholder
NaNs stand in for missing measurements.

The native format now has a separate bounded PPMS runtime-tree importer; see
[ppms-plan-import.md](ppms-plan-import.md) for explicit bindings and unsupported
semantics. Durable run archives are documented in run-archives.md. There is no
parallel/race/retry
BT execution or real hardware transport. The inventory marks relevant families
partial. Tests in `ExperimentExecutionTests` demonstrate a 3 x 3 field/voltage
scan, conditional/iterator rows, pause restoration, abort, stability timeout,
preflight failures, runtime limits and explicit reconnect recovery. These are
simulation and policy tests, not physical hardware acceptance.
