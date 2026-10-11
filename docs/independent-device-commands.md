# Independent source and meter configuration commands

PPMS `k2182.set_nplc`, `k2182.set_range`, `k2182.set_filter` and
`k6221.set_source_delay` now translate to ordinary native parameter steps. The
Application executor still owns admission, leases, cancellation, notifications
and cleanup. Infrastructure parses the document and supplies explicit simulators;
Core holds typed settings and parameters. No new package or physical IO is added.

## K2182 input selection and partial configuration

A selected input is an observation, not a request to copy the previous input's
registers. `IK2182MeterPort.SelectChannelAsync` changes only the active input;
Application confirms connected idle, the requested input and unchanged shared
NPLC with a fresh snapshot. The target input's actual range and filter are then
used. The simulator retains separate CH1/CH2 range/filter profiles; NPLC is shared.
A new physical adapter must implement the same observation contract. A complete
`ConfigureAsync` still intentionally applies the supplied target input profile.

The older `Keithley2182Settings.WithParameter(channel, ...)` cannot manufacture
an unobserved different input anymore. Use the owned session/registry channel
write or select through the port and observe it. Same-input writes preserve its
settings. This corrects the previous native input-switch behavior which reset
range and copied filter controls. The published two-input example now configures
each input explicitly inside its scan body.

- `set_nplc`: required numeric `nplc`, 0.01..50 PLC; shared between inputs.
- `set_range`: required `range_v` or `range`; optional integer `channel` 1/2.
  If omitted/null, the actual selected input is retained. The write-only native
  `k2182.select_range` resolves a positive expected voltage upward using that
  input's nominal spans and input headroom. Exact native `k2182.manual_range`
  remains separate. An explicit CH2 request above 12 V rejects at import; an
  omitted-input request is also validated against its actual input before IO.
- `set_filter`: optional input, required boolean `enabled`/`filter_enabled`;
  flat fields or a nested `config` object. Optional `count`/`filter_count`,
  `filter_type`/`type` (`MOV` or `REP`, case-insensitive, blank retains), and
  `window`/`filter_window` (percent). Enabled PPMS count accepts integers 2..100,
  window 0..10. Omitted/null controls retain the observed target-input registers.
  Disabled filters retain their numeric controls, matching Rust's normalization.
  Conflicting aliases, ambiguous flat/nested envelopes, fractional channels/counts
  and unknown fields reject; oversized input identifiers never wrap to a byte.

Commands require an explicit `k2182` alias declared as a standalone meter. They
never substitute an SMU. Configuration-only runs create no measurement rows.
Subsequent reads preserve their actual per-row meter settings through archive,
fresh replay and CSV. Active/paused run ownership covers both inputs of the same
physical meter, so manual selection cannot change the resume state. Refusal,
cancellation or lost idle confirmation faults/aborts without committing a reading;
failed cleanup retains the whole admitted resource group until fresh recovery.

## K6221 source delay register

`Keithley6221Settings.SourceDelay` is optional typed seconds, 0.001..999999.999.
This is the source's `SOUR:DEL` register, not scan dwell, resistance settling,
pulse width or a request to wait in software. `set_source_delay` accepts
`delay_s`, `source_delay_s` or `delay`. A register-only imported command does not
apply unused source preparations or change current/compliance/range. It requires
an explicitly bound pure K6221 source and uses the existing guarded native write.

Native `currentSourcePreparations[].sourceDelay` is optional; missing/null retains
the connected source's observed delay. Other setting writes preserve it. The
simulator leaves legacy unknown delay absent rather than inventing a default.
Reading an unobserved register reports an error. Disconnected status has no
observed registers. Run initial parameters and independent successful/failed
acquisition `sourceConfiguration.sourceDelay` retain actual observations; the
latter is checked with every other source setting across read/retry/pause/resume.
Source-delay drift rejects acquisition or prevents output restoration.

The optional quantity round-trips in native version 1 plans and archives, and in
existing independent-resistance/failure JSON CSV columns. A K2450 configuration
cannot carry K6221 delay; wrong units and out-of-range records reject. Legacy
files without the field remain readable and explicitly unobserved. Strict older
readers need updates to consume the addition. No hardware timing accuracy or
source sweep behavior is implied.

Limits and meaning are verified against the read-only Rust reference
`35763295f63b612f70af9dc55983644f437c18ea` command/driver/controller paths and the
[manufacturer reference manual](https://download.tek.com/manual/622x-901-01%20(C%20-%20Oct%202008)(Ref).pdf),
SCPI table 14-8 (`SOURce:DELay`). No manufacturer PDF is redistributed.

## Fixture and remaining work

`examples/plans/ppms-device-commands-import.json` uses the configured aliases in
`examples/devices/k6221-meter-devices.json`; pass the pure-source binding from
`k6221-current-resistance.plan.json` to `CreateImportBindings`. The fixture
configures both input profiles and the delay. Acceptance tests then execute
native meter/resistance reads, confirm each retained profile and safe cleanup.

The new cases cover partial/nested/alias controls, both input range resolution,
shared NPLC, strict rejection before connection, native persistence and fresh
archive/replay/CSV, observed delay retention, normal/invalid acquisition evidence,
manual/run ownership, cancellation, pause drift and retained cleanup failures.

Real K2182/K6221 transports, per-frame automatic span/quality, analog filter and
zero modes, delta/pulse/AC/corrected Hall, remaining PPMS controllers/BT/background,
database/environment precedence, native editors/plots and packaging remain.
No App drafts or Rust sources are modified; the whole migration goal stays active.
