# Standalone voltage meter slice

The backend now runs an explicitly simulated K2182 independently, or reserves it
atomically alongside a K2450 excitation source. The meter has no source setpoint,
output-enable operation, or source-meter snapshot. Independent `curr_dc`
resistance now uses this session in the separate [current-resistance.md](current-resistance.md)
slice. Neither acquisition claims synchronized source/current/voltage frames. Primitive native steps can enable a source, wait, and acquire voltage;
the caller authors that order and settling duration.

## Boundaries and stop policy

`IK2182MeterPort` is a model-specific passive voltage capability in Core.
`Keithley2182MeterSession` adapts it to the existing Application `IDeviceSession`
boundary; the executor, registry, admission, leases, notifications and archive
remain shared. Infrastructure currently supplies `MockKeithley2182Meter` only.
The model-specific port avoids forcing other meters into K2182 or K2450 limits.

The passive stop policy is `AbortAcquisition`: the adapter aborts its acquisition
and returns a fresh idle snapshot. Physical instruments additionally require a
connected observation. Successful source-off cannot release the group while
the meter stop is failed or unknown. Cleanup uses an independent timeout token;
explicit recovery can reconnect and retry. A new acquisition invalidates any
earlier idle proof. Manual calls remain denied during run and pause ownership.
Pause captures meter settings; resume verifies connected idle and exact settings
before restoring source output. Drift refuses resume and invokes fresh cleanup.

## Configuration and evidence

`Keithley2182Settings` is immutable: input 1/2, optional manual voltage span,
NPLC, digital filter enable/count/Repeat-Moving/window. Native manual spans are
exact nominal values: CH1 0.01/0.1/1/10/100 V, CH2 0.1/1/10 V. An absent span
means autorange. Integration accepts 0.01..50 PLC (the common 50/60 Hz bound),
native filter count 1..100, window 0..10 percent. The named parameter catalog
shares these limits with preflight/session validation. Discrete values must be
integral. Dynamic values receive the same validation before adapter mutation.
Input selection now observes the target input's own range/filter profile through
`SelectChannelAsync`; it preserves shared NPLC and does not copy or reset the
other input. The simulator retains both input profiles. PPMS direct NPLC/range/
filter commands and partial controls are supported; see
[independent-device-commands.md](independent-device-commands.md).

Disconnected status has no settings placeholders. Reads bracket acquisition with
fresh configuration snapshots and reject drift, stale/future frame timestamps,
incorrect units and voltages beyond CH1 ±120 / CH2 ±12 V. Confirmed settings are
attached as optional `quality[].meterConfiguration`, preserved through native
save/reopen, run archive and `quality_json` CSV. Initial run parameters remain
separate from settings changed later by the plan. The complete typed object is
validated on archive read/export; unknown, incomplete and mixed SMU evidence
reject. Older archives without this optional field remain readable; strict old
readers may need updating. Existing analysis/wide rows retain immutable input
evidence, rather than manufacturing new device status.

Configuration confirmation is not measurement-quality confirmation. No K2450
buffer words are attached to a passive meter. Rows without device quality words
remain `Unobserved`; an autorange setting is not the actual span of that reading.
Autozero, analog filter, relative/ratio/temperature functions, buffer trigger
status, device timestamps and constituent digital filter samples are not in this
slice. Hardware adapters must reconcile relevant measurement modes before
producing a plain DC voltage reading; they cannot trust default cached settings.

## Native and PPMS use

Runnable, transport-free fixtures:

- `examples/devices/standalone-meter-devices.json` selects a meter without a primary source.
- `examples/plans/standalone-meter.plan.json` scans its two inputs and records voltage.

Native plans use ordinary `set`, `scan` and `read` steps with `k2182.*` parameter
IDs. The deterministic mock returns CH1 0.001 V / CH2 0.002 V; settings are state
simulation, not electrical coupling, noise reduction or timing emulation.

PPMS import requires an explicit `StandaloneVoltageMeters` binding. Runtime
bindings are produced only from enabled, aliased K2182 meter configurations.
The PPMS `channel_voltage` reference selects input 1, Repeat filtering, count
2..100/default 10 and window default 0.1 percent. Import preserves those rules,
including the enabled-filter NPLC × count ≤800 PLC budget and leaving stored
count/mode/window unchanged when disabling the filter. Unlike K2450 voltage
filtering, the K2182 window is an applied setting. Positive PPMS expected-reading
range selectors resolve upward to a nominal span, including maximum headroom.
Only direct, externally wired `channel='none'` or empty channel is supported.
Secondary inputs are native-plan only; routed acquisitions, four-wire SMU sense
requests and current reads through K2182 reject. Existing K2450 binding behavior
is retained.

## Evidence and remaining work

Read-only PPMS reference at `35763295f63b612f70af9dc55983644f437c18ea`:
`k2182a.rs`, `k2182_validate.rs`, `k2182_writes.rs`, `k2182_service.rs`,
`channel_voltage.rs`, and `channel_resistance/curr_dc.rs`. Manufacturer limits
were checked against the [Model 2182A Rev. C user manual](https://download.tek.com/manual/2182A-900-01C_July_2022_User.pdf)
(range sections 4 and 13, NPLC and digital filter sections). No vendor manual,
private addresses, Rust changes or new dependencies are committed.

Tests cover isolated/multiple resources, native/configuration round trips,
PPMS filtering, archive/export, invalid controls, drift, cancellation, pause,
failed/disconnected stop and explicit group recovery. Protocol and laboratory
acceptance are absent: enabled VISA, serial or TCP K2182 configurations reject
the complete catalog before constructing any port. GPIB/RS-232 and K6221
passthrough need a separate transport/dependency decision and driver acceptance.
Independent current/DC resistance and explicitly bound matrix routes now have
a tested subset; automatic sample routing, delta/pulse/AC and K6221 execution
remain pending, as do native settings/editor/history/plot acceptance and packaging.
All pending App/App.Tests changes remain outside this backend stage.

## Pure K6221 DC current-source stage (2026-10-11)

Dedicated settings/snapshot/Core port, Application session and explicit simulator
now support K6221 DC output without SMU sensing. Native preparations, current
parameters/scans, owned/managed PPMS curr_dc and basic concrete K6221 commands
share lifecycle/leases/pause/output-off/recovery. Model-tagged independent
resistance evidence uses the actual family's constraints and validates through
wide/archive/CSV, retaining legacy K2450 archives. Physical transports, advanced
modes, independent sample routing/analysis, retry-to-invalid-point semantics and
UI/lab acceptance remain pending. See [k6221-current-source.md](k6221-current-source.md).
All App/App.Tests drafts remain excluded; the full goal stays active.
