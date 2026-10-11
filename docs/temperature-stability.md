# Temperature stability settings and waits

The independent C# executor now imports PPMS `temp_set_config`. The schema's
`temp_config` is a command group, not a separate executable command. Configuration
belongs to the Application device session, rather than the hardware adapter.

| Setting | Unit | Session default |
| --- | --- | --- |
| tolerance / tolerance_k | Kelvin | 0.1 |
| settling_threshold / settling_threshold_k | Kelvin | 2 |
| stable_duration / stable_duration_s | Seconds | 5 |

Each field is optional; omitted fields retain their current session value and an
empty patch does nothing. Aliases cannot occur together. Values must be finite
and non-negative; durations must fit the existing timer limit. Unknown fields,
null, numeric strings and malformed values fail with PPMS source-line context.
Zero tolerance and zero duration are valid. As in the reference, classification
checks settling threshold before tolerance, even if a caller sets threshold
below tolerance. Invalid values fail explicitly rather than being silently ignored.

`set_temperature` and step `scan_temperature` inherit the current session settings.
Explicit tolerance/duration overrides update the session after programming the
ramp when waiting for stable/reached; later steps inherit those updates. A set
with `wait_mode=none` skips threshold updates and settle. Temperature step scans
apply overrides after each ramp and settle before their wait. This corrects the
initial migration's treatment of the no-wait branch.
Fields unrelated to stability retain their previous behavior. Field/angle waits
and existing native `wait_stable` steps retain their explicitly declared numeric
settings. No changes to ramp enable, control enable, target or rate result from a
stability patch. No new package dependency or physical adapter is introduced.

Core's typed observation classifies disconnected as Fault, disabled temperature
control as Cooling, error beyond settling threshold as Ramping, error beyond
tolerance as Settling, otherwise Reached until the declared duration has elapsed,
then Stable. With zero duration it immediately becomes Stable. Reached waits
accept Reached or Stable; stable waits accept only Stable. A disabled controller
cannot satisfy either wait merely by matching the temperature numerically.

Application reads current control flags on both sides of a fresh controller
snapshot under the device operation gate. Inconsistent flags, IO failure, target
drift or disconnect stay fatal. Target comparison allows only the existing
software ramp's half-millikelvin rounding error. State transitions and effective
settings are published in run/step log notifications. Cancellation reaches locks,
IO and the existing pause-aware dwell implementation. Output and hold cleanup
continue through the existing separately bounded resource lifecycle; failed hold
retains ownership until explicit safety recovery. Manual patches/observations
are unavailable while a run owns the device.

The device session now shares stability evidence across status, notifications and
successive waits. Changed configuration, faults/disconnect, explicit hold, pause
and resume reset that evidence; unchanged configuration retains it. Software
ramping uses the final requested target. See
[temperature-runtime.md](temperature-runtime.md) for typed state, time and sampling
boundaries. Settings are not persisted across app restart; autonomous background
operations, PID/heater/cooling, caller-managed scans, full UI and physical
acceptance remain pending. Caller-managed setpoints are covered in
[temperature-setpoints.md](temperature-setpoints.md).

Version-1 native plans add `temperature_stability` (device and optional typed
thresholds/duration) and `wait_temperature` (device, target, named mode, polling
interval, timeout). They use the existing plan compiler, whole-plan preflight,
resource claims, bounded document codec, archive and CSV paths. Initial session
thresholds/duration are recorded in the run's instrument configuration snapshot.
Explicit plan patches remain in the archived plan. Archive reload does not apply
initial instrument snapshots to a fresh device session automatically; specify
initial settings in the plan for reproducible replay.

`examples/plans/ppms-temperature-stability-import.json` sets all three settings
explicitly, then runs two temperature points including a quantized endpoint.
Tests save/load its native conversion, execute, reload through a fresh archive,
re-execute on a fresh session, and export the two rows to CSV.
