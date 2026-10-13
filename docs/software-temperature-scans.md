# Software continuous temperature scans

SoftwareTemperatureScanStep migrates the software branch of Rust scan_temp_ramp
through the existing independently implemented C# executor and owned controller
resources. HardwareTemperatureScanStep and this step share validated Kelvin
points, sampling settings and child traversal through TemperatureScanStep.
No new dependency, background task, physical controller adapter or laboratory
operation is introduced.

Preparation parks a fresh live origin, confirms hardware ramp OFF/control ON,
programs the first point and optionally waits for consecutive strict-tolerance
observations. Software rate is the trajectory speed, independent of the
controller's hardware-rate register: that register is preserved and verified.
The foreground polling loop programs first +/- rate/60 * active elapsed time,
clamps at the endpoint and rounds to milliKelvin. Decimal midpoint rounding is
AwayFromZero; this intentionally avoids Rust f64's binary half-point artifact
(e.g. 4.0005 K rounding below the mathematical decimal midpoint).

Each write first confirms the previous target, hardware rate and both control
flags. Its subsequent coherent snapshot must confirm the new target and flags
before the child can receive the observation. External changes fault instead
of being overwritten. Runtime software goal/rate is published with that final
snapshot, cleared at the rounded programmed endpoint and reset at stop/fault.

Sampling modes:

- Threshold: actual readback crosses each declared raw point in order.
- EstimatedSetpoint: confirmed rounded programmed target crosses each raw
  point; actual temperature remains separate. The existing enum name is kept
  for native compatibility, but software mode uses observed target evidence.
- Timed: minimum wall-time interval between sample starts, including child and
  pause time; setpoints still advance during the polling ticks of the wait.
- Immediate: back-to-back child executions with unknown total 0.

Axes include actual Kelvin, confirmed `.setpoint` Kelvin, zero-based `.index`
and `.total` (point count, ceil(nominal duration/interval)+1 timed estimate, or
0). Timed estimates may be exceeded while settling. As in Rust, point modes
may repeat the final point's index until the programmed path finishes; this is
an explicitly dynamic loop bounded by the global execution budget.

Pause holds the controller under retained leases and restores the confirmed
prior setpoint/configuration on resume. All held time, including preparation
and restoration IO, is excluded from the trajectory and active deadlines.
No setpoint writes run in the background during a child acquisition: the next
foreground observation accounts for active child time, matching the inspected
Rust algorithm. An outer TimeoutStep is required to bound child acquisition.

First stability and per-point waits have explicit deadlines. Timed/immediate
endpoint guard starts when the rounded endpoint is first programmed and
requires actual arrival within tolerance. It also runs during long interval
waits. Timely arrival observed during an interval wait satisfies the guard
without shortening the requested sample interval; fresh observations must keep
confirming it. Missing arrival faults rather than Rust warning/success. Raw thresholds
are never silently broadened: an endpoint rounded short of a raw threshold can
time out; authors should use representable milliKelvin endpoints or the timed
modes with an appropriate physical tolerance. Exact rounded endpoint completion
is required instead of Rust's <=1 milliKelvin finish epsilon.

Failure, cancellation and logical failure keep a hardware-ramp-OFF obligation
through root cleanup/recovery. A paused position hold cannot release this
obligation. Refused OFF retains the whole lease group, even after an electrical
source has independently turned OFF; explicit recovery confirms the controller.
Normal scope clears software runtime; whole-run terminal policy still parks at
fresh live temperature rather than leaving unowned thermal motion.

Native v1 kind is software_temperature_scan; hardware v1 JSON fields remain
compatible. PPMS ramp_mode=software and absent ramp_mode select software;
explicit hardware still selects hardware, unknown modes reject. The workbench
outline traverses the real child, with JSON authoring until a dedicated native
form is added. Native/PPMS, graph, blackboard, fresh archive, CSV and 128-depth
checks are included. Native visual and physical thermal acceptance remain
pending per the user's instruction to continue code checks.

Headless example: use headless-cooling.devices.json, the existing
headless-hardware-ramp-context.plan.json binding context, and
ppms-software-temperature-ramp-import.json (deliberately no ramp_mode, testing
the PPMS default). The packaged CLI imports, validates and executes this
three-point 4 to 4.02 K simulation without a desktop launch.
