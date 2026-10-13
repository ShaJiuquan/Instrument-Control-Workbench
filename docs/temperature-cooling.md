# Passive temperature cooling scans

TemperatureCoolingStep observes natural cooling with hardware ramp and heater
control both disabled. It supports threshold, timed and immediate sampling
through the existing C# executor and its atomic device ownership. Ports must
provide independent temperature configuration; a generic axis or ramp-only
port cannot claim this capability. Natural cooling is observed, never assumed
from target programming. A stationary controller times out rather than
fabricating a crossed threshold.

The implementation follows Rust scan_temp_cooling.rs at the recorded PPMS
commit. Descending point/list/generator/segmented thresholds are supported. A
lower end temperature is appended once. The low setpoint must not exceed the
end temperature, which must not exceed the final threshold. Sampling records
actual live Kelvin, zero-based sample index and threshold count. Early arrival
at end + tolerance emits that observation and skips later thresholds. Timed
and immediate sampling publish total 0 and continue until the observed end;
the global execution budget still bounds their work. Timed samples use a
minimum wall-clock interval between starts, including child acquisition and
operator pause; the next sample can proceed immediately after a long pause.
Threshold timeout is per wait and includes paused/read time.

Before programming a low target, the session disables and confirms ramp and
control. Every sampling observation confirms connected state, fixed target and
rate, both flags OFF and valid readback. The compiler rejects children that
reconfigure that controller or take over an enclosing controller scan. Native
steps retain physical limits, request settings and their executable child tree.
Child reads/branches/calculations continue through the ordinary engine.

Normal cooling scope completion verifies the original low target/rate and
restores control ON with ramp OFF. Error, cancellation or logical failure keeps
an OFF obligation attached to the owned resource group. Root cleanup and
explicit recovery must confirm both flags OFF even when an ordinary current-
position hold could succeed. Refusal retains all claims, including an electrical
source that has independently stopped. ForceSuccess cannot erase this safety
obligation. Pauses retain claims, allow natural temperature drift and require
unchanged configuration before restoring the original low target.

With temperature control OFF, passive readback drift is compatible with stop
confirmation: a physical temperature cannot be frozen by setting a target.
The session confirms connected/no active movement and unchanged disabled
control rather than requiring target == every later readback. Control-ON
stops retain the existing strict current-position hold requirement. The whole
workbench run still parks the controller at live temperature after successful
scope completion; this is its existing terminal policy and differs from Rust's
standalone cooling cleanup, which leaves the low target. Continued automatic
refrigeration/thermal handoff and physical controller policy remain unverified.

Native version 1 adds temperature_cooling. PPMS scan_temp_cooling,
temperature_cooling, temp_cooling and scan_temperature/scan_temp with cooling
scan_mode import the same step. Sample mode aliases map to threshold, timed
or immediate behavior; unknown fields/modes, malformed values, ascending
paths and conflicting timeout aliases reject. Out-of-range values reject
instead of silently clamping. Existing 1..310 K controller bounds remain in
force. Native save, graph traversal, terminal blackboard, fresh archive and
CSV retain the executable scan and actual sample axes. Dedicated graphical
forms remain pending; the existing native JSON editor can edit the steps.

SIM-CRYOSTAT is explicitly a deterministic simulation: initial 4 K, natural
120 K/min descent while control is OFF, bounded at 1.7 K. This refrigerator
model is independent of its nominal hardware ramp register and is not a real
controller specification. The ordinary SIM-TEMPERATURE behavior is unchanged.
The CLI examples use headless-cooling.devices.json,
headless-cooling-context.plan.json and ppms-cooling-import.json.

Acceptance includes 11 Core and 40 Application cases plus temperature
regressions: three sampling modes, early end, global/threshold/parent deadlines,
pause/cadence/drift, source and controller claim retention/recovery, result
listener faults, failed initial OFF/normal restore, child mutation rejection,
strict codecs/imports, 128-level traversal and fresh archive/CSV. Native
visual and physical cryostat checks are deferred/unperformed. No dependencies
or Rust modifications; broader PPMS migration remains active.
