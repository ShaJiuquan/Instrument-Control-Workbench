# K2450 polarization pulse and current readout

`ReadPolarizationStep` owns one software-timed sequence through the existing
Application resource layer and K2450 source-meter port. Infrastructure owns SCPI;
the UI/importer never calls a driver. No new dependency or Rust modification.

## Sequence and ownership

1. Confirm source output OFF and any frozen required matrix route.
2. Configure voltage sourcing/current sensing, compliance, NPLC, sense range and
   the first polarization voltage while OFF. Then enable output. This avoids
   enabling a stale source level (Rust currently enables before its first write).
3. Hold polarization voltage for its duration; apply optional inter-pulse voltage
   only when its duration is positive; apply read voltage and wait read delay.
4. Verify route; acquire one original source-voltage/current frame through the
   existing paired acquisition API; verify source settings, time and route again.
5. Independently confirm output OFF before committing any sample/blackboard value.

Source voltage is actual readback from the same acquisition as current, rather
than a nominal setpoint copied into data. Original status/acquisition settings
are retained for both columns. The third output is a bounded integral attempt
count. Result/archive/blackboard validation requires original paired evidence and
attempt count; it never certifies a sample if final OFF confirmation fails.

Cancellation/fault/deadline uses the existing independent run cleanup token.
Failed OFF holds resources for explicit recovery. A source preparation must
explicitly authorize voltage/current operation and the same frozen sensing mode.
A pulse cannot take over an enclosing scan of the same source. Required route
preflight includes cards/points and source guards; runtime checks before enabling
and around acquisition. Matrix cleanup preserves the existing run-added policy.

## Pause behavior

Pause during any dwell cancels that dwell, confirms OFF before capturing pause
state and preserves leases/routes. Resume confirms unchanged source/sense state;
it leaves the interrupted stage OFF and restarts from the first polarization
voltage. Separate pieces of exposure are never stitched into one continuous
pulse. Attempt count increments, maximum attempts defaults to8 (native1..100),
with global execution-budget accounting as well. Exhaustion stops without a row.

Abort interrupts IO/dwell. Pause during an in-flight acquisition becomes effective
at the subsequent executor checkpoint; if frame and OFF complete successfully,
that completed point may be retained. Durations use host timers and protocol
readback latency. They specify nominal minimum holds, not hardware-triggered
precision timing or total dose. Instrument/external changes between two protocol
observations cannot be completely excluded by software readback alone.

## PPMS and native authoring

Imports `measure_polarization_readout`, `polarization_readout`, `pol_readout`,
`polread`, with source-line diagnostics. Supports the Rust voltage/duration,
inter-pulse, read-delay, compliance, sensing-mode and sense-range aliases. Requires
explicit source preparation, channel and polarization/read voltage. Defaults:
1s polarization,0V/0s interval,.1s read delay,1mA compliance,1PLC integration;
optional current range selector resolves through existing K2450 spans.
Unsupported fields, duplicate aliases, wrong modes, missing routes, source scan
inheritance, invalid durations/ranges/compliance and excessive attempts reject.

An explicit channel='none' is a C# external-wiring extension. Mapped channels
use frozen routes with OFF-before-switch. Four-wire mapped channels require named
source/sense wiring profiles; raw crosspoints alone cannot prove distinct contacts.
Native raw routes remain explicitly authored operator routes with pending physical
wiring acceptance. The native `read_polarization` codec embeds typed source/sense
configuration and all durations, output columns, attempts and route/environment.
Measurement-point/calculated rows and saved plan/archive/CSV preserve inputs.
Graph outline identifies this node; its dedicated visual form is pending.

Example `examples/plans/ppms-polarization-import.json` uses the existing
`headless-simulation.devices.json` and `headless-import-context.plan.json` with
explicit four-wire external wiring. It produces one voltage/current/attempt row.

## Verification and remaining work

Readonly Rust reference: `drivers/instruments/k2450_polarization.rs`, its tests,
`controllers/instruments/k2450_controller_polarization.rs`,
`service/experiment/measurement/polarization_readout.rs`.
The C# application uses paired readback instead of Rust scalar MEAS?, configures
first voltage before ON and requires OFF proof before publication. Source limits
are evaluated for every stage; hardware corrections are never silently accepted.

Tests cover all stages/abort, write/read/drift/stale-frame faults, failed final OFF
with retained recovery, pause/restart/attempt budget, timeout, route drift/cleanup,
source-line/alias validation, native save/fresh archive/CSV/replay/calculation,
provenance forgery and depth127/128. TCP loopback tests execute the actual adapter
with two-/four-wire fresh registers/readback/status and refused-OFF recovery.
These are loopback protocol tests, not physical instrument acceptance. No real
lab IO or native window/process action occurred. Real pulse settling, transient
compliance/overload, relay topology, waveform timing and sample response remain
unverified. Hardware-triggered pulse/buffer acquisition and dedicated form remain
future work; wider PPMS/controller/workflow/data/settings/remote goal stays active.
