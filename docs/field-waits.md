# PPMS nonramping field waits

The frozen Rust `WaitFieldStateController` completes when
`!is_field_ramping(field_state) && abs(field_t-setpoint_t) <= tolerance_t`,
optionally continuously observed for duration_s. Its `wait_for` string is
reported in progress but is not used as an exact-state completion comparison.
`is_field_ramping` recognizes ramping/ramp/running. This is different from
certifying a vendor magnet-switch or persistent-field Holding state.

Core `WaitFieldStep` models the actual comparison: explicit nonnegative Tesla
tolerance, nonnegative stable duration, positive bounded poll interval and a
live target. It has no target setter, fabricated status string, measurement
row or hidden timeout. Application requires a Tesla axis before admission,
then reads coherent validated target/readback/rate/Moving snapshots through
the existing owned AxisControllerSession resource path. `Moving` is the adapter's
ramping fact; numeric proximity cannot conceal an ongoing ramp. A disconnected
or invalid observation faults instead of satisfying the wait.

The live target is reread on every poll. Stable evidence begins only when the
field is within inclusive tolerance and nonramping; either condition failing
resets the timer. Like other C# waits, pause/resume also resets evidence and
paused/unobserved time does not certify stability. The Rust implementation does
not reset its timer on pause, so this is an intentional stronger observation
contract. Discrete polls cannot prove continuous physical field behavior.

## Plans, deadlines and import

Native version1 gains strict `wait_field` with device/tolerance/stableSeconds/
pollSeconds. A native TimeoutStep supplies a deadline when requested. PPMS
wait_field,wait_field_holding,wait_field_stable and generic wait_kind=wait_field
lower to that node, wrapped in the existing TimeoutStep for positive timeouts.
Zero timeout means genuinely indefinite but cancelable waiting. Defaults are
3600s timeout,0.0005 T tolerance,0s duration and500ms polling; imported positive
integer polls are raised to PPMS's100ms runtime floor.

Parameter state aliases wait_for; stable_duration_s aliases duration_s; timeout
aliases timeout_s. Only the supported palette label holding is accepted for
wait_for. This label is not treated as a vendor byte. Unknown state labels,
multiple aliases, malformed/null/nonnumeric values, negative duration/tolerance/
timeout, invalid polls, target overrides and unknown fields reject with source
line diagnostics before IO. Rust accepts or clamps some of these; strict C#
import does not silently normalize malformed physical intent.

Finite deadlines include pause and strictly reject late satisfaction. Root
abort retains priority. Actual noncooperative IO must drain before independent
hold/lease release. Refused holds retain resource ownership for explicit
recovery. Workflow failure decorators/retry do not mask timeout or IO exceptions.
Existing moving targets are observed at admission, not parked early to manufacture
success. Ending or aborting a run still executes its documented hold policy.

## Verification and limits

49 new cases (7 Core,42 Application); the focused regression set passes
84 Application and7 Core cases. They cover aliases/defaults/zero/poll, exact
inclusive tolerance, matching readback while still moving, fresh live targets,
stability reset from moving/out-of-tolerance/pause, finite paused deadline,
noncooperative drain and root abort priority, no fault masking/retry, refused
hold retention/recovery, strict native/import formats and wrong controller role.

The actual repository fixture `examples/plans/ppms-field-wait-import.json`
starts a simulated0.01 T move without waiting, executes wait_field_holding with
20ms duration, then records measured field. It imports with the existing device
configuration, saves/loads native plan, executes, reopens archive and complete
terminal blackboard, exports CSV and replays in a fresh configured runtime.
Unrelated SMUs and matrix remain disconnected.

The simulator provides trustworthy Moving from its explicit numeric slew model;
it is labeled Simulation. Physical adapters must map real ramp status correctly
and fault if it cannot be observed. No real field adapter, magnet-switch state,
persistent mode, quench recovery or lab verification is claimed. This stage
implements the actual PPMS nonramping/numeric wait, not full magnetic safety.
Native wait editor, background/continuous scans, physical controllers and full
migration acceptance remain pending. Original29 UI drafts are unchanged and
excluded. No dependencies, Rust writes or physical device IO. Independent C#
engine; GitHub only per user; full goal active.
