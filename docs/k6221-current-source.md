# Pure K6221 DC current source

K6221 DC output now has its own typed Core settings/snapshot/port, Application
session and explicit Infrastructure simulator. It shares the independent C#
executor's registry, admission, non-expiring claims, stop tasks, pause/resume and
archive paths. It has no SMU sense configuration, voltage/current measurement or
fabricated K2450 snapshot. Real K6221 transports remain unsupported.

## Model and boundary

`Keithley6221Settings` stores programmed Ampere current, Volt compliance and an
optional fixed current span. The nominal spans are 2/20/200 nA, 2/20/200 µA,
2/20 mA and 100 mA. Current is bounded by ±105 mA and by 105% of any fixed span;
compliance is 0.1..105 V. An exact nominal span is required for native writes;
PPMS positive selectors resolve upward, with the top span's 5% headroom.
Concrete `k6221.set_range` aliases accept sign-insensitive selectors, matching
the Rust command validator. No value is silently clamped.

These limits were checked against the manufacturer
[Model 6220/6221 Rev. C reference manual](https://download.tek.com/manual/622x-901-01%20%28C%20-%20Oct%202008%29%28Ref%29.pdf),
section 3, table 3-1 and the compliance discussion. The real instrument's 10 mV
compliance programming resolution is not a simulator claim: a future adapter
must reconcile the actual setting and report quantization/refusal rather than
inventing confirmation. No manufacturer PDF is redistributed.

`IK6221CurrentSourcePort` owns connect, snapshot, configure, output, safe-stop and
disconnect. `Keithley6221CurrentSourceSession` serializes manual and owned IO,
confirms settings/output after writes and denies enabling a faulted session.
Preparation requires connected output-off; disconnect requires fresh off proof.
Native active current/range/compliance writes preserve the observed output state.
A real adapter must reject unsupported active modes and cancel autonomous output
actions before returning a stop confirmation. No sweep/delta/waveform engine is
represented by the DC simulator.

## Plans, configuration and imports

- Device configuration uses model `K6221`, category `Source` and explicit
  `simulation`. A primary SMU is optional; pure sources are registry sessions,
  not entries in `ConfiguredDeviceRuntime.Sources` (the existing SMU map).
- Native plans add optional `currentSourcePreparations`, each with a device,
  typed current/compliance and nullable `manualRange`. Pure and SMU preparations
  must have distinct device IDs. Existing plans without the field remain valid.
- `SourceOutputStep`, native current parameter scans, cancellation, cleanup,
  guarded matrix routes and run snapshots use the shared executor. Preflight
  validates model, parameters, literals and fixed prepared spans; dynamic values
  are checked against actual settings at each write.
- PPMS bindings explicitly supply `CurrentSourcePreparation`. Supported imports
  include generic `set_current`, step `scan_current`, owned/managed `curr_dc`
  resistance, `k6221.set_current`, `.set_compliance`, `.set_output`, `.set_range` /
  `.set_source_range`, and explicit enable of `.set_auto_range` /
  `.set_source_auto_range`. Disabling autorange requires an explicit manual span;
  no unobserved selected full-scale is guessed. Off-only commands do not apply an
  otherwise unused preparation. Other K6221 commands reject instead of becoming
  silent no-ops.

The common current import binding chooses the actual prepared model. K2450
current excitation remains supported with its own range and sense rules; it is
not automatically substituted for K6221. Current scans override child current
with their persisted axis, as the Rust runtime does. Source settings and settling
in a managed child are unused metadata; parent source ownership is preserved.
Explicit routed resistance bindings still identify both source and voltage meter.

## Resistance and stop evidence

Independent resistance supports `ProgrammedSetpoint` on K6221. Its freshly
confirmed setting is **not measured current**; `MeasuredSourceReadback` rejects
before connection. External voltage retains its own meter settings and time.
Source setting/output drift across the read discards resistance. Matrix routes
are confirmed before/after acquisition. Neither simulator models electrical
coupling, compliance-limited actual current, noise or laboratory timing.

`CurrentExcitationConfiguration` records the actual source model and applies
that model's constraints. It replaces the K2450-only validation of independent
resistance metadata. Archive readers match this model to the frozen native
preparation, validate V/I and retain separate input quality/times. Missing model
tags from earlier K2450 archives default to K2450; a K6221 archive without its
model tag rejects. Wide points preserve each member's evidence, and CSV exports
include the model in `independent_resistance_json`. Older strict consumers need
an update for the new optional fields.

Pause confirms source-off/passive-idle while retaining claims. Resume verifies
every actual source/meter setting and route before restoring output, then checks
source settings again at enable. Failed/disconnected/timed-out stops retain the
entire group; cleanup uses its own bounded token. A still-running stop cannot be
replaced by recovery. Its eventual failure must become terminal before retrying
fresh off confirmation. Later writes invalidate earlier successful off proof.

## Fixtures, evidence and remaining work

`examples/devices/k6221-meter-devices.json` and
`examples/plans/k6221-current-resistance.plan.json` run the two explicit simulators.
The existing `ppms-current-resistance-import.json` signed scan also runs with
these devices when the native plan's `CurrentSourcePreparations` are passed to
`ConfiguredDeviceRuntime.CreateImportBindings`.

Read-only Rust reference commit `35763295f63b612f70af9dc55983644f437c18ea`:
`k6221_validate.rs`, `service/instruments/role/current_source.rs`,
`service/experiment/command/k6221.rs`, `channel_resistance/curr_dc.rs`, and
`nested_experiment_service/node_execution/handlers/scan_children.rs`.

Tests cover nominal/model limits, explicit bindings, configuration/plan fixtures,
native/imported signed scans, manual confirmation/refusal, drift, paused abort,
blocked acquisition cancellation, retained failed/disconnected/non-cooperative
stops, fresh stop-proof invalidation, wide/archive/CSV provenance, damaged
unfinished archives and preservation of existing exports. All enabled physical
TCP/VISA/serial K6221 configurations reject before constructing any catalog port.
No dependency, Rust edits, lab IO or native UI acceptance is included.

Real K6221/K2182 drivers, observed automatic full-scale/current quality, source
delay/output filtering/response and advanced modes, independent automatic sample
wiring/Hall/van der Pauw analysis, retry-to-invalid-point semantics, delta/pulse/AC,
native operator/editor/history/plot acceptance and packaging remain required.
This stage is a tested DC subset; the full PPMS migration goal remains active.
