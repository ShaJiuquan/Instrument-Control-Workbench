# Native temperature scan authoring

The existing plan editor now has one shared typed form for
SoftwareTemperatureScanStep and HardwareTemperatureScanStep, with separate
palette entries. This extends the established workflow editor rather than
introducing another executor or shell. The software/hardware type remains the
original type during edits; deliberate replacement is separate from changing
parameters.

Application exposes TemperatureScanDefinitions through DeviceRegistry,
MeasurementRunService and ExperimentPlanWorkspace. This immutable catalog
contains Kelvin axes whose registered sessions support independent temperature
configuration, preserving registration order. Reading the catalog makes no
connection, driver or state calls. The form consumes this metadata; full plan
compilation remains authoritative before applying or running.

Operator workflow:

1. Select a sequence node and add 软件温度扫描 or 硬件温度扫描.
2. Explicitly select a supported controller, enter the result column and choose
   temperature points. Templates do not select a physical device or retain
   placeholder points as user input.
3. Enter the rate and choose 按实际温度, 按确认设定值 (software) /
   按估算设定值 (hardware), 定时间隔 or 连续采样.
4. Expand 稳定与超时 when changing first-point stability, tolerance, consecutive
   observations, first/endpoint timeout, per-threshold timeout or poll interval.
5. Append child measurements, apply the draft and validate the complete plan.
   Save and reload use the same executable native plan.

The shared point editor supports lists and generators/segments in Kelvin.
Controller changes preserve the points and other unedited parameters, then
validate against the selected controller's physical bounds. Software validates
rounded programmed endpoints as well as raw points. Hardware rates validate
against the controller register range; software rate is a nominal setpoint
trajectory rate and does not write that register.

Software updates setpoints during foreground sampling and waits, not during a
child acquisition. Active child time is accounted for at the next update.
Paused time does not advance the setpoint trajectory or active deadlines.
Physical readback is not promised to be motionless merely by that display text.
First stability timeout and per-threshold timeout are distinct; timed/continuous
endpoint allowance starts at confirmed programmed endpoint for software and at
nominal ramp end for hardware. An outer TimeoutStep bounds child acquisitions.

The display explains actual temperature, confirmed software setpoint and
hardware estimated trajectory separately. `.setpoint`, `.index` and `.total`
refer to exported axes; total 0 means unknown for immediate sampling. Point-mode
software endpoints retain raw threshold precision, so rounding short can cause
a timeout; the form advises representable milliKelvin endpoints or a deliberate
choice of timed sampling and tolerance, without changing authored values.

Editing preserves node ID, exact child object, original unedited point values,
R-format numeric precision and original TimeSpan ticks when unchanged. Changing
sampling only changes the typed mode; hidden interval/stability settings remain.
An original unedited binding absent from the catalog is retained without claiming
capability, so a form never silently substitutes another controller. Complete
workspace validation still rejects an unavailable or incapable execution device.

The existing Apply/Discard, JSON/form conflict, dirty draft, run/document busy
lock and stale callback guards continue to apply. Editing itself sends no device
commands. The plan data view remains separate from the fixed source-meter I-V
monitor; this stage does not put temperature data into the V/mA plot.

Verification includes metadata capability/unit/order/readonly/no-IO cases,
software/hardware form codec roundtrips, invalid fields and bounds, dirty/read-
only behavior, explicit templates, scope-change rejection, real workspace save/
reload, simulated execution, fresh archives and CSV. Compiled bindings and native
package compilation are verified through the standard audit. Native visual and
minimum-window inspection remain deferred per the user's instruction, and
physical controller/calibration/thermal acceptance is not claimed.
