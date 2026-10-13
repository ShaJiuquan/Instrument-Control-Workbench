# K6514 function-aware acquisition

This stage adds a four-function electrometer port, validated settings/session,
configured simulation and ordinary parameter/read authoring. Application owns
connection, writes, leases, acquisition, pause/resume and cleanup. No dependency,
UI layout, physical communication or Rust changes.

## Function and settings boundaries

The model retains four independent selected nominal range registers and each
register's autorange flag. Autorange toggles retain the selected register;
neither that register nor this evidence claims the actual autoranged conversion
span. Native manual writes require exact nominal spans. Import range selectors
resolve to the next nominal span with the documented maximum headroom.

Current, voltage, resistance and charge parameters have fixed Ampere, Volt, Ohm
and Coulomb units. Reads require the matching active function and explicit zero
check OFF. A single read step cannot request multiple functions of the same
instrument; compilation rejects it before connection. Function changes require
an explicit zero-check ON write first. Enabling zero check discharges accumulated
charge in the real instrument; authoring never inserts that action implicitly.
Function selection confirms all unrequested observed registers unchanged.

Integration is shared, 0.01..10 PLC. Averaging enable/count (1..100)/Repeat-or-Moving
and zero-check/zero-correct enable are global modelled registers. These settings
survive function selection; inactive function range writes do not select it.
Fractions, invalid enums, wrong units, nonnominal native spans and oversized
selectors reject. Reads discard wrong returned function, configuration drift,
disconnection, active acquisition, stale/future timestamp, negative ohms and
values beyond the function or observed manual span.

A read invalidates earlier acquisition-idle evidence. Independent cleanup token,
failed-stop retained ownership and recovery, paused settings confirmation and
manual access exclusion use the existing resource engine. The simulation returns
fixed per-function 1 pA, 1 mV, 1 kΩ and 1 nC values. It does not model charge
integration/discharge dynamics, filter timing, excitation or calibration.

## Import and data

`Meter/K6514` supplies explicit electrometer bindings. Seven device commands:
`set_function`, `set_nplc`, `set_range`, `set_auto_range`, `set_zero_check`,
`set_zero_correct`, `set_averaging`. Function names accept CURR/CURRENT,
VOLT/VOLTAGE, RES/RESISTANCE and CHAR/CHARGE. Range/NPLC/autorange commands validate
the function but do not select it. Range null enables autorange; disabling auto
retains the observed selected span. Missing controls, unknown fields, duplicate
aliases, invalid ranges and empty averaging changes fail with source-line context.

Manufacturer documents shared NPLC including charge. C# applies that setting;
Rust currently returns early for charge. C# rejects averaging count outside bounds
instead of Rust's silent clamp. Resistance/charge ranges are bounded instead of
Rust's finite-only TODO. These are deliberate corrections, not literal emulation.

Direct acquisition extensions: explicit K6514 `measure_current`, `measure_voltage`,
`measure_charge`, `measure_electrometer_resistance` use matching `meter_range_a`,
`meter_range_v`, `meter_range_c`, `meter_range_ohm`; NPLC and optional Repeat filter
(count2..100, enabled integration×count ≤500 PLC). Mode selection still requires
zero check ON; sample read requires authored OFF. Matrix channels, enclosing
source scans, foreign unit ranges, remote sense, additional input and filter
windows reject. No source preparation, source output or resistance sample routing
is inferred. `measure_resistance` retains the existing PPMS source/sample meaning.

Each acquired column carries optional `ElectrometerConfiguration` in original
`AcquisitionQuality`. Live/archive validation requires it for K6514 readings,
checks function/unit/range and rejects other instrument evidence. NDJSON, fresh
archive reader, CSV and wide points retain it. Old documents keep their format
because the added optional field is absent for other devices. Zero-correct enable
is not acquired correction/calibration proof; quality stays Unobserved.

Examples: `examples/devices/k6514-devices.json`,
`examples/plans/k6514-import-context.plan.json`,
`examples/plans/ppms-k6514-import.json`. Four explicit zero-check/function/read
sequences exercise all units and preserve earlier modes' spans. Generic native
parameter forms work from descriptors; dedicated electrometer form is pending.

## Verified reference and remaining physical work

Readonly Rust `drivers/instruments/k6514_{validate,enums,reads,writes}.rs`,
`service/experiment/command/k6514.rs`, `k6514_meter_role_impls.rs`.
Manufacturer:
[6514 instruction manual](https://download.tek.com/manual/6514-901-01(D-May2003)(Instruction).pdf),
sections2,3–6 and17: function spans, zero check, shared rate/filter and SCPI tables.
Ohms measurements source an internal test current. Thus acquisition-idle alone
cannot certify all hardware excitation/guard outputs safe. This release registers
only simulation, and the session rejects hardware profiles until stop evidence
covers those effects. Serial/VISA/TCP enabled configurations fail before runtime
construction; they never become mocks.

Real adapter work must observe intrinsic excitation/guard/external feedback,
charge auto-discharge and group settings, median filter, math/relative modes,
overload/status and effective span; verify line rate, fresh protocol readback and
physical stop. No real output, calibration or continuous charge integration is
claimed. Native visual acceptance remains deferred per user; broader goal active.
