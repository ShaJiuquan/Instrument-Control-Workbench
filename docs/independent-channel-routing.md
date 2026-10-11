# Independent current channel routing

Frozen routing catalogs now explicitly bind a current source, standalone voltage
meter and matrix for channel acquisition. Import creates ordinary native steps
and uses the existing independent C# executor and resource lifecycle.

## Binding

`RoutingProfileCatalog.Bind` appends optional `channelVoltageMeter` after
`routeSettle`. It preserves SMU and pure K6221 preparations, standalone meter
bindings and controller definitions. Existing calls without a meter remain
supported. Independent channels require a current-function K2450 or pure K6221
preparation and an explicitly declared standalone meter. Source, meter and matrix
must be distinct. The four instrument ports must also be distinct, even when
same-polarity source/sense columns coincide for a two-terminal sample.

`ImportChannelRouting.VoltageMeter` is import configuration. Native plans retain
both actual device IDs and exact required routes in `ReadCurrentResistanceStep`.
The frozen catalog preserves channel IDs, aliases, layouts and notes. Its legacy
`SourceMeter` property identifies the excitation instrument; it does not invent
SMU sensing for K6221. No native document/archive format change is required.

PPMS `curr_dc` resolves IDs, names, numeric shorthand, original IDs, aliases and
`__active__`, validating the mode-to-wiring map and requested source/meter pair.
Other measurement families cannot silently use that independent binding.

## Execution and data

Owned acquisition switches output off before routing, configures source/meter,
enables output, settles, acquires and switches output off. Current scans route
once while excitation is off. Managed children inherit the signed scan axis and
verify the exact held route; requesting another channel fails without switching
relays under excitation. External meter geometry is independent of the source
SMU's own sensing mode.

Compiler admission includes source, meter, matrix and declared excitation guards.
Acquisition brackets the meter read with fresh source/route confirmations. Drift
rejects the row. Failed cleanup retains the group until fresh explicit recovery.
Native documents, wide points, fresh archive reload/re-execution and streamed CSV
retain per-acquisition source/meter/route evidence.

## Reproducible fixtures

- `examples/devices/k6221-meter-matrix-devices.json`: simulated K6221, K2182 and
  an 8-row / 10-column matrix guarded by the current source.
- Existing `examples/profiles/channel_profiles.json`, `samples.json` and
  `instrument_ports.json`: authored source rows 5/6 and meter rows 7/8.
- `examples/plans/ppms-current-channel-import.json`: one owned channel read,
  then a signed two-point current scan on another channel.

Supply a K6221 preparation to `ConfiguredDeviceRuntime.CreateImportBindings`.
Bind the loaded profile catalog with matrix `matrix`, wiring `k6221_k2182`
mapped to source `current-source`, and
`channelVoltageMeter: new InstrumentId("nanovoltmeter")`. Import the PPMS fixture
and execute through `MeasurementRunService`.

32 new cases cover both source families, the authored three-file profiles,
command/scanning routes, role/mode/port rejection before connection, route drift,
failed-stop ownership, native save/reload, wide/archive/re-execution/CSV.
Programmed current remains unobserved actual current. The simulator does not
model physical coupling or certify real relay switching.

## Remaining work

Independent current-DC sample acquisition and analysis are now implemented; see
[independent-sample-resistance.md](independent-sample-resistance.md). Physical
adapters, advanced modes, retry/recover semantics and UI/lab acceptance remain
required. The full migration stays active.

The read-only Rust reference remains commit
`35763295f63b612f70af9dc55983644f437c18ea`, principally
`service/experiment/channel/route_resolver.rs`,
`measurement/channel_resistance/curr_dc.rs` and `measurement/sample_r/context.rs`.
No dependencies, Rust edits, physical IO or UI drafts are included.

Independent current-DC sample acquisition and Hall/van der Pauw analysis now share
the existing sample pipeline, preserve both source/meter roles and retain every
input record through wide points, native documents, archives and CSV. See
[independent-sample-resistance.md](independent-sample-resistance.md) for verified scope and remaining work.

Optional terminal K2182 acquisition single retry is now implemented with fresh
owned/managed cleanup and retained attempt evidence. See
[bounded current resistance retry](current-resistance-retry.md). Invalid-point recovery,
physical acceptance and full UI/PPMS parity remain pending.
