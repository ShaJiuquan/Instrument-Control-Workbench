# Caller-managed temperature control

PPMS `set_temperature` / `set_temp` now supports `enable_control=false` in
software, direct and hardware strategies. False means preserve the freshly
observed control flag: enabled stays enabled and disabled stays disabled. It does
not issue a control-off command. True retains explicit control-start behavior.

| Strategy | Ramp mode | Rate register | Control flag |
| --- | --- | --- | --- |
| Software, positive rate | Off; lazy integer-millikelvin targets on 1 s tick | Preserved | True starts; false preserves |
| Direct, absent positive rate | Off; unrounded final target | Preserved | True starts; false preserves |
| Hardware, positive rate | On | Requested rate | True starts; false preserves |

All paths use the existing exclusive run resources, controller operation lock,
cancellation, pause/resume and bounded independent cleanup. The application
validates a fresh connected temperature/readback/rate before any preparation.
It parks the target at live readback before changing ramp/control mode, then
confirms the parked target, actual flags and actual rate before writing a final
setpoint. Hardware mode no longer enables at a stale stored target through a
standalone rate write. Software ticks carry their captured control/rate evidence;
flag or rate changes fail rather than substituting control-on defaults.

The preserved-control path requires the actual existing optional
`ITemperatureConfigurationPort` capability, checked at whole-plan preflight.
Enabled legacy paths still work with the original `ITemperatureControlPort`.
Adapters own protocol details; the application owns the parked preparation and
confirmation sequence. No new interface, package dependency or physical adapter
is introduced. The simulator verifies that disabled control can change the target
register without moving readback or enabling heating.

Native version-1 `software_temperature_ramp` adds optional `enableControl`,
defaulting to true when omitted for compatibility. New
`hardware_temperature_setpoint` steps contain device, target, required positive
rate, and the same optional flag. Boolean values are strict; named modes/rates
remain typed and validated. Plans and both step types save/reopen through the
existing codec, archives and CSV export. Initial run metadata remains an initial
snapshot, not every target written during a run.

Further tracing corrected a previous migration mismatch: reference
`set_temperature` invokes stability configuration and settle only when waiting
for stable/reached. For `wait_mode=none`, C# now skips both effects. Provided
optional fields still pass import validation. For a waited set, ordering is target
programming, threshold update, settle, then fresh stability evidence. Temperature
step scans apply explicit thresholds after each ramp, and settle before polling.
Their `enable_control=false` option remains rejected: the reference step-scan
factory does not propagate that option; C# does not silently reinterpret it as
control-off. Background ramping, automatic cooling, PID/heater controls,
continuous stability cache/persistence, native controls and hardware acceptance
remain pending. This is a tested simulation/API slice, not full PPMS parity.

The fixture `examples/plans/ppms-preserved-temperature-setpoints-import.json`
explicitly turns control off, programs a direct software target and a hardware
ramp target without starting control, then reads two unchanged temperatures.
Tests save/load its native conversion, archive/reload, replay in a fresh session
and export CSV. Additional cases cover original enabled/disabled flags, hardware
parking, refusal/drift, cancellation, pause restoration, full-group retained failed
hold/recovery, actual optional capability preflight and old native defaults.
