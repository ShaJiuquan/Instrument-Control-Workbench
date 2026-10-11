# Independent current DC sample resistance and analysis

Two-wire, four-wire, Hall-bar and van der Pauw samples now support an explicitly
bound current source and separate K2182 voltage meter. K6221 simulation and K2450
current excitation share the native C# executor, ownership and cleanup lifecycle.
No new dependencies, Rust changes or physical lab IO are included.

## Domain and boundaries

`ResistanceAcquisitionStep` is the metadata contract consumed by sample geometry,
analysis, schema compilation and persistence. `ReadResistanceStep` retains
integrated source/sense acquisition. `ReadCurrentResistanceStep` retains distinct
source and meter roles, programmed-versus-measured current policy, separate
capture times, actual model/configuration and exact required matrix route. A
current excitation function does not imply that K6221 has SMU sensing.

`InstrumentWiringProfile.VoltageMeter` and `ResolvedSampleRoute.VoltageMeter` are
optional explicit identities. Integrated wiring keeps them absent. Independent
wiring requires four distinct physical instrument ports, including two-terminal
samples whose same-polarity source/sense columns coincide. Frozen sample electrode
assignments determine every route; source, meter and matrix remain distinct.

`RoutingProfileCatalog.Bind` accepts optional `wiringVoltageMeters` alongside
existing `channelVoltageMeter`. Conflicting declarations or meter-only mappings
without a bound wiring source reject. Import bindings validate explicit current
preparations and standalone meter declarations for each independent sample wire.

## Execution and evidence

Independent samples own source cleanup. Each selection routes with excitation
off, settles, enables output, waits the acquisition dwell, confirms the route,
acquires and disables output. Both source and route are freshly confirmed around
the meter read. Sample wiring targets K2182 input 1; its actual input is checked
inside the session operation lock before triggering acquisition. Generic meter
reads retain their existing interface method and per-input configuration evidence.

Hall acquires longitudinal and raw transverse selections. Van der Pauw acquires
eight ordered permutations. Existing Core math computes signed Hall quantities,
mean-absolute vdP resistances, sheet resistance and optional resistivity. Field
reversal, Hall coefficients and carrier-density inference are not implemented.

Analysis requires complete immediately preceding inputs at one scan point and
retains every independent acquisition record and quality observation. It rejects
source or meter configuration changes between inputs, mixed acquisition families,
changed current-reference/expected-current policies, wrong routes and missing or
reordered evidence. Programmed current remains unobserved actual current. No
simultaneous sampling, actual autorange span or confirmed compliance is invented.

Pause/resume validates held state. Abort or listener failure preserves committed
raw rows and omits an incomplete summary. Failed stops retain the entire resource
group until fresh explicit recovery. Existing independently bounded cleanup and
live-stop retention semantics remain in force.

## Import, documents and reproducible fixture

PPMS curr_dc imports sample aliases and all four sample primitives, numeric-string
current/range/NPLC values and existing source/meter role aliases. Current-source
scans cannot make sample routing externally managed. A channel override, non-input1
sample meter or incompatible primitive/source/meter/mode fails before connection.
Independent sample geometry does not configure the excitation SMU's sensing mode.

Native version-1 documents add kind `read_independent_sample_resistance`, containing
an ordinary `read_current_resistance` acquisition with the same step ID and a
sample binding. Legacy integrated sample encoding stays unchanged. Optional meter
identity survives frozen native wiring and per-row archive/CSV routing provenance.
Older strict consumers must upgrade to understand the new kind/optional fields.

The four-device configuration `examples/devices/k6221-sample-devices.json`, existing
three files under `examples/profiles`, and
`examples/plans/ppms-current-samples-import.json` run four sample topologies at two
field points. The resulting 30 rows include raw selections, complete analyses and
wide point summaries. The test saves/reloads native plans, reloads a fresh archive,
re-executes it and exports CSV, preserving source/meter/route/current-model identity,
all per-input quality and fresh environment observations.

48 new cases cover models and frozen identity, all topologies, both source families
and current policies, signed Hall/vdP math, role/ownership/input rejection,
preflight, partial abort/listener fault, pause/drift, retained failed-stop ownership,
configuration drift between inputs, authored fixtures and damaged archive/CSV
rejection. Existing integrated and standalone meter regressions remain required.

## Remaining scope

Real K6221/K2182 transports and physical switching/timing acceptance, PPMS retry/
recover-to-invalid-point behavior, delta/pulse/AC, corrected Hall acquisition,
advanced settings, native operator/editor/history/plot acceptance and remaining
PPMS families are unfinished. This is a verified current-DC backend subset;
the full migration goal remains active.

The read-only Rust reference remains commit
`35763295f63b612f70af9dc55983644f437c18ea`: sample_r context/four_wire/hallbar/vdp,
channel_resistance/curr_dc and channel/sample routing resolvers. The current Rust
sample context requires cleanup ownership and K2182 input 1; Hall/vdP handlers
perform two/eight routed acquisitions and call shared Core-style math.

Optional terminal K2182 acquisition single retry is now implemented with fresh
owned/managed cleanup and retained attempt evidence. See
[bounded current resistance retry](current-resistance-retry.md). Invalid-point recovery,
physical acceptance and full UI/PPMS parity remain pending.
