# Shared temperature session state

The Application temperature session now owns one Core stability observer. Manual
status, readback acquisition, run waits and device notifications use that same
state rather than creating a separate timer for each wait. The observer is pure
computation; sessions obtain hardware evidence under their operation gate.

A connected observation reads control flags, a fresh controller snapshot, then
flags again, rejecting inconsistent flags and invalid quantities. Operations that
already confirm flags on both sides of their snapshot publish that confirmed
observation directly; publishing does not duplicate IO. Disconnected status reports
Fault with unknown control/error/duration rather than fabricated off defaults.
Failed observations invalidate evidence and put the session into Fault until an
explicit successful connect/hold recovery. Exclusive ownership and the existing
bounded cleanup/quarantine rules remain in effect. Read-only sampling cannot
release a run's resources or weaken its hold/output-off proof.

`DeviceStatus.Temperature` carries a typed immutable runtime status: state,
signed temperature error, actual control flags, stability settings, in-tolerance
duration, active software final target and requested software rate. It is absent
for other controller profiles. Core rejects contradictory control/state/error/
duration evidence and incomplete software target/rate pairs. Snapshot values
continue to report the actual hardware target, readback and hardware rate; they
are not replaced by software intent. `ReadTemperatureStabilityAsync` returns the
same runtime status with its fresh raw observation. DeviceStateChanged notifications
carry that status for observers. Existing native UI layouts are not changed.

The session uses injected `TimeProvider` monotonic elapsed time. In-tolerance
evidence can persist between successive status reads, run steps and completed
runs while the target/configuration remain unchanged and the controller stays
connected. A second stable wait may complete immediately when the shared state
is already Stable. Empty or identical settings patches retain the timer; changed
thresholds/duration reset it. This replaces the prior wait-local-only behavior.

Fault/disconnect, explicit hold/stop, pause and resumed target restoration reset
stability evidence. A resumed wait resets it again at the pause-version boundary,
so unobserved operator pause is not credited. Configuration settings remain
session-owned. The device state is updated on actual observations and confirmed
operations; there is no detached sampling worker, autonomous background ramp or
claim that the entire interval was continuously sampled. Unobserved excursions
cannot be reconstructed from polling snapshots.

During a software ramp, the observer compares readback with the final requested
target, not an intermediate programmed setpoint. The runtime reports requested
software rate separately from the preserved hardware rate. Integer-millikelvin
stepping and pause-aware one-second ticks retain the previous semantics. Direct
sets never claim an active software ramp; the final confirmed tick clears intent.
Pause captures final target and software rate before holding, reports the actual
held state, then restores both only after existing target/rate/control resume
checks. Abort/fault/recovery clear intent rather than leaving phantom activity.

Tests verify deterministic shared time and typed notifications, unchanged versus
changed configuration, disconnect and failed-read invalidation, second-wait reuse,
final-target classification, hardware/software rate distinction, pause restoration
and intent clearance. Existing native plan/archive/CSV fixtures execute against
this shared state. Settings/runtime persistence, firmware status mapping,
background/PID/heater/cooling, physical adapters, visual flow and native UI
acceptance remain pending. Full PPMS migration remains active.

Disconnect completion is a fresh observed fact: after the adapter returns, the
session reads its connection state and rejects a still-connected result. Driver
failure, cancellation or refused disconnection invalidates shared stability and
software intent and marks the session faulted. No success notification is emitted.
A subsequent connected status reports Fault until explicit recovery. A moving-axis
precondition rejection occurs before disconnect IO and preserves known state.
Five regression cases cover these boundaries and successful reconnect evidence.
