# K6485 passive current acquisition

This stage adds a C# passive picoammeter profile, session and deterministic
simulation. It uses the existing run engine, exclusive leases, pause/resume,
stop/recovery and ordinary parameter/read steps. The UI and importer never call
an adapter directly. No dependency or Rust code changes.

## Settings and acquisition

- Eight exact manual current spans: 2 nA, 20 nA, 200 nA, 2 µA, 20 µA, 200 µA,
  2 mA, 20 mA. Autorange has no claimed manual/actual conversion range.
- NPLC 0.01..60, zero check and zero correct enable registers, averaging enabled,
  count 1..100 and Repeat/Moving. Integer/boolean controls reject fractions.
- The simulation starts with zero check enabled. An explicit authored write must
  disable it before sample acquisition; the session and mock both refuse to
  return sample current while zero check is enabled.
- Reads observe settings before/after acquisition. Disconnect, active acquisition,
  configuration drift, wrong unit, stale/future capture and current outside
  ±21 mA or 105% of an observed manual span discard the reading.
- A reading invalidates any earlier acquisition-idle proof. Stop uses the
  independent cleanup token; failed confirmation retains ownership until explicit
  recovery. Pause holds ownership and confirms unchanged settings before resume.

`ParameterObservation` and `AcquisitionQuality` carry optional
`PicoammeterConfiguration`. Live/archive validation requires it for K6485 current
rows; it cannot contain zero check ON, K2450 status/settings or K2182 settings.
NDJSON/CSV and measurement points retain each original acquisition's settings.
Old archives remain readable because the added field is optional in the format.
Observed configuration does not prove overload flags, effective autorange span,
zero-correction acquisition, calibration, noise or timing. Quality is Unobserved.

## PPMS import and examples

The configured `Meter/K6485` profile explicitly supplies standalone current-meter
bindings. Supported commands: `k6485.set_nplc`, `set_range` (null/omitted range
enables auto), `set_auto_range` (enabled/auto_range alias, manual range_a required
when disabled), `set_zero_check`, `set_zero_correct`, `set_averaging` (optional
enabled/count/MOV-or-REP mode, at least one actual change). Unknown fields,
conflicting aliases and nonnominal ranges fail with source-line context.

`measure_current` with an explicitly bound K6485 is a C# direct-acquisition
extension: Rust experimental channel_current restricts itself to K2450, whereas
Rust manual_current includes direct K6485. This extension supports exact
meter_range_a, NPLC and optional Repeat filter, count 2..100 with enabled
NPLC×count ≤500 PLC. It never disables zero check implicitly. It rejects matrix
channels, enclosing source scans, remote sense, another input, voltage ranges
and K2182 filter windows. It has no source preparation or excitation control.

Examples: `examples/devices/k6485-devices.json` and
`examples/plans/ppms-k6485-import.json` produce two fixed 1 nA rows with different
configuration evidence. Native generic parameter/write/read authoring uses the
registered descriptors; no dedicated picoammeter form is added in this stage.

## Protocol and remaining work

Readonly reference: Rust `drivers/instruments/k6485_{validate,reads,writes}.rs`,
`service/experiment/command/k6485.rs` and `manual_current.rs`.
Manufacturer reference:
[Model 6485 Instruction Manual](https://download.tek.com/manual/6485-901-01(A-Nov2001)(Instruction).pdf),
measurement overview and zero check, range/rate/filter chapters, SCPI tables.
The manual confirms ±21 mA and 105% range headroom. It also limits integration to
50 PLC under 50 Hz operation. The simulation retains the Rust general 60 PLC
limit; a future physical adapter must observe line frequency before accepting
that setting. Count 1 follows Rust validation; manual filter tables differ on
minimum count and require physical/firmware verification.

Only simulation is registered. Enabled serial/VISA/TCP K6485 configurations fail
before any runtime construction; no mock substitution. Real serial/GPIB/bridge
communication, fresh SCPI readback, ASCII status/overload parsing, trigger abort
confirmation, line frequency, median/advanced filter effects and zero-correction
acquisition require separate protocol and physical acceptance. No real IO or
native window inspection occurred; visual acceptance remains deferred per user.
