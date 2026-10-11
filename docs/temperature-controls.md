# Independent temperature controls

Native version-1 `temperature_control` steps and PPMS `temp_ramp_on`,
`temp_ramp_off`, `temp_control_on`, `temp_control_off` use the existing executor
and exclusive resource lifecycle. They emit no synthetic measurement rows.

| Action | Mutation | Preserved |
| --- | --- | --- |
| RampOn | Hardware ramp enabled and supplied rate | Target and control flag |
| RampOff | Hardware ramp disabled | Target, rate and control flag |
| ControlOn | Temperature control enabled | Target, rate and ramp flag |
| ControlOff | Temperature control disabled | Target, rate and ramp flag |

RampOn requires a finite 0.01..20 KelvinPerMinute rate validated against the bound
controller profile. Other actions must omit rate. PPMS accepts `rate_k_per_min`
or `rate`, rejecting simultaneous aliases, unknown fields and invalid values
with source-line diagnostics. Native actions are strict named enums. Import
requires the explicitly bound temperature controller; no source preparation or
instrument is manufactured from the command name.

Core's optional `ITemperatureConfigurationPort` is separate from the existing
software-ramp capability. Plan preflight checks the session's actual optional
capability before any IO. The application observes current target/rate/flags,
derives a configuration changing only the requested flag and confirms the target,
rate and flags through fresh readback. Refusal, drift, IO errors and cancellation
remain errors; result decorators cannot conceal them.

ControlOn intentionally enables control at the stored target, matching an
explicit operator command. Software-ramp initialization continues to park at the
live temperature before enabling control. These two operations have different
semantics. In simulation, disabled control preserves the target register while
readback remains stationary; enabling a ramp alone cannot enable control.

An unreadable control flag cannot suppress the emergency hold, but it prevents
safety confirmation; observation and hold errors are preserved together.
Stop parks the target at live readback and must preserve the observed control
flags. It must never enable disabled control. Pause captures flags and rate;
resume refuses changed rate or flags before restoring any source output. Target
restoration reconfirms the rate and flags. Independent bounded cleanup and
retained stop tasks/group claims remain the shared lifecycle policy. An
unconfirmed or control-enabling hold retains ownership until explicit recovery.

The plan, initial observed configuration, raw temperature rows and source lines
round-trip through native save/load, a fresh run archive, CSV and re-execution.
Instrument metadata is the initial run snapshot, not a history of every command
readback. See `examples/plans/ppms-temperature-controls-import.json` for a
two-measurement example using all four commands.

50 new cases (8 Core, 42 Application) cover independent flags and target/rate
preservation, deterministic disabled-control behavior, refused/drifting writes,
cancellation, pause/resume/abort, retained failed hold, strict native/PPMS import,
published fixture, fresh archive/CSV/replay. Existing software-ramp tests remain
applicable. No dependency or laboratory IO is introduced. Temperature hardware
adapters, `temp_config`/`temp_set_config` stability state, PID/heater/cool-down,
disabled-control set_temperature/scans, background runs and native operator UI
acceptance remain unfinished. The full PPMS migration remains active.
