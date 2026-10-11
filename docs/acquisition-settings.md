# K2450 acquisition profiles

Core owns the immutable optional `Keithley2450AcquisitionSettings` profile:
automatic zero/reference updates, autorange rebound, instrument averaging enable,
count 1..100 and Repeat/Moving mode. `SenseConfiguration.Acquisition == null`
means retain the target function's observed profile. It does not assume defaults.
Simulation starts with explicitly simulated per-function profiles; disconnected
hardware still has no observed parameter evidence.

The implementation follows independently authored native SCPI handling based on
the [official Model 2450 reference manual](https://www.tek.com/en/keithley-source-measure-units/keithley-smu-2400-series-sourcemeter-manual/model-2450-interactive-sou),
Rev. E sections 12-44 through 12-61. No manual is redistributed.

Infrastructure reads the actual active function's AZER, AVER state/count/type and,
for voltage/current only, range-rebound registers. Boolean replies, enum words
and integral counts are validated; malformed settings cannot become cached
defaults. Resistance does not have a rebound command; enabled rebound with an
explicit resistance preparation is rejected, and its rebound parameter is
unavailable. A disabled rebound field in a resistance profile denotes
inapplicability, not a successful hardware query.

Configuration applies the requested profile while preserving actual source,
output and terminals, then confirms the complete sense profile. Changed filter
configuration is disabled while count/type are set and restored to its requested
state. Equal sense configuration performs no writes. Repeated unchanged single
reads do not reselect the function or rewrite NPLC/filter settings. Thus software
does not reset Moving history between those reads; actual firmware behavior still
requires hardware acceptance. A function switch retains that target function's
profile rather than carrying another function's profile over.

Application exposes typed numeric parameters for the active sense function,
zero updates, rebound and averaging. Function/mode codes and Boolean controls
must be integral; counts must be integral and bounded. Native constants and scan
tables are checked before connection; dynamic values are checked before setting
IO. Writes use existing ownership/leases, preserve source state and use ordinary
fault cleanup/recovery. Fresh sense-profile comparison detects drift during
single/paired reads and prevents output restoration after paused-profile drift.

`refresh_zero` is a separate write-only action with value 1, not a persistent
third autozero mode. Output must already be verified off. Native SCPI sends
`:SENS:AZER:ONCE`, awaits `*OPC?`, checks errors and confirms source/sense/terminal
state unchanged. Cancellation flows through all waits and transport calls.
Failure is not reported as successful calibration. Simulation provides an
explicit simulated action, not evidence of physical calibration accuracy.

Initial observed profiles are captured as instrument parameters. Each electrical
row also retains its five-field `acquisitionConfiguration` with the original
column evidence. Application confirms this profile in configuration snapshots
before and after the read. It is separate from the two status words returned
inside the instrument buffer, and is not proof against an external change that
occurs and reverses between snapshot queries. It does not include a used autorange
span, device timestamp, NPLC or individual constituent readings. Dynamic count
changes can therefore be distinguished from the run's initial profile. Analysis
and wide points preserve the same input profiles; archive/CSV retain them.

A filtered result is one returned buffer reading, not Count independent exported
rows. Reported quality flags describe that returned reading; this implementation
does not recover every internal constituent status or prove alignment of source
readback with a complete averaging window. Moving history may span earlier scan
points. Operators must choose Repeat/Moving according to their measurement and
use hardware-appropriate settle and query timeouts. Physical timing, settling,
filter accuracy and effective limit recovery are not established by loopback tests.

Native plan v1 adds optional `sense.acquisition` with all five fields; partial,
invalid or unknown fields are rejected. Legacy documents remain unspecified.
Archive v1 adds optional column-evidence `acquisitionConfiguration`; old records
remain readable. Existing strict consumers may require updates.

PPMS current/voltage measurement import now selects the active function first,
then applies the runtime's Repeat filter semantics. Disabled filtering disables
the target function's filter while preserving stored count/type. PPMS counts use
its 2..100 bound and default 10; enabled NPLC x count is limited to its recorded
200 PLC voltage / 500 PLC current budget. The native API retains the full 1..100
instrument count range. PPMS voltage-filter window percentage is validated and
retained in `k2450_unused_filter_window_percent` metadata: the Rust K2450 role
also does not apply the K2182-style window. Current measurement has no such
window option. Moving profiles remain available through native configuration.

This stage introduces no dependency, touches no Rust source and performs no lab
IO. Native UI acceptance remains pending. Source-readback setting controls,
other meter families, full per-reading sense/range provenance and real firmware
acceptance remain separate migration work; paired acquisition still enforces
measured-source readback using same-buffer source status.
