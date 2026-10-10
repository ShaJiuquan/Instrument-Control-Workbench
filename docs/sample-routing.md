# Sample and electrode routing

`RoutingConfiguration` is an immutable catalog frozen into an `ExperimentPlan`.
It declares one matrix instance, channel profiles, source-meter wiring profiles
and sample profiles. Core resolves the catalog without IO; Application preflights
and executes it; Infrastructure serializes it. No driver owns sample workflow and
no global active-profile file is consulted halfway through a run.

A channel connection names a matrix column and an optional card slot. Instrument
wiring names the source meter and the slot/row of SourcePlus, SourceMinus,
SensePlus and SenseMinus. Sample electrodes link to channel/connection IDs and
can cross channel profiles. The resolver combines each role's instrument port
with its linked sample column. A declared connection slot must match the port's
slot. Missing/disabled electrodes, missing/disabled connections, missing columns
and unresolved IDs are explicit errors. IDs and electrode lookup use ordinal
case-insensitive matching, with canonical configured IDs retained in results;
full PPMS aliases and active-profile selection are not implemented by this codec.

Required electrode labels and raw assignments follow the captured Rust PPMS
`sample/mod.rs`, `sample/assignments.rs` and `sample/route_resolver.rs` at commit
`35763295f63b612f70af9dc55983644f437c18ea`:

| Sample topology | Labels | Raw recipe reads |
| --- | --- | --- |
| TwoWire | A, B | A/B sourcing and A/B sensing |
| FourWire | I+, I-, V+, V- | One four-terminal resistance |
| HallBar | I+, I-, Vxx+, Vxx-, Vxy+, Vxy- | Longitudinal and transverse V/I/R |
| VanDerPauw | A, B, C, D | AB/CD, BA/CD, AB/DC, BA/DC, BC/DA, CB/DA, BC/AD, CB/AD |

Two-wire routing shares each polarity's electrode and deduplicates identical
crosspoints. Opposite polarities must use different sample terminals and cannot
share any instrument matrix port. Four-wire, Hall and vdP reads require four
independent instrument ports and four distinct sample terminals. The port rule
matches the Rust channel switch topology check; distinct physical sample terminals
are an additional conservative configuration check. This validates declared
configuration, not physical continuity, connector wiring or isolation. Profiles
support at most 64 channels/wirings/samples and 64 connections/electrodes per
profile as software limits; the configured card dimensions still govern points.
Unused incomplete connections may be saved, but every referenced step, including
an unselected branch, must resolve before device admission.

`ReadSampleResistanceStep` wraps the existing paired `ReadResistanceStep` contract
with a sample, wiring and route-selection binding. Compilation resolves and freezes
its route once, checks the source preparation/function/sense mode, configured card
bounds and matrix excitation guards, and includes all required devices in atomic
admission. The present execution profile is the integrated K2450-style source meter,
with either source function and opposite sensing; separate K6221/K2182 or SR830
wiring/acquisition profiles remain unfinished. Live source mode and output are
checked by the shared acquisition boundary. The required two-/four-wire sensing
mode is also checked before internal output enable and again before acquisition.

Internal management performs fresh source-off confirmation, guarded break-before-
make routing/readback, interruptible settle, verified source enable, coherent
V/I/R acquisition and source disable. It preserves the source level established by
an enclosing scan. With `routingManagedExternally: true`, it verifies the accepted
matrix route equals the binding's exact connection set, then reads without relay
writes, extra route settle or changing the caller's output state. The caller must
already have enabled source output. Both paths use the same pause, abort, retained
cleanup tasks, resource leases, result listeners and recovery as other plans.
Route inputs to the resource scope are copied before waiting so a caller cannot
change the requested route while excitation-off is pending.

`SampleResistanceRecipe` / `StartSampleResistanceAsync` produce the full raw
selection sequence for the chosen topology. `SampleResistanceAnalysisPlan` /
`StartAnalyzedSampleResistanceAsync` additionally publish grouped Hall/vdP sheet
resistance and geometry-dependent resistivity; see [sample-analysis.md](sample-analysis.md).
Hall coefficients, field reversal, offset correction and instrument quality are
still pending. The mock source does not simulate sample-specific or
electrode-polarity-dependent physics. Actual V/I/R and route identity remain
available independently of derived analysis.

Each sample row carries `ResolvedSampleRoute`: canonical sample ID/name/topology,
wiring and selection, source/matrix IDs, and the four electrode/channel/connection/
crosspoint mappings. The numeric schema still defines V, I, R, elapsed and active
scan dependencies. A mixed-sample run therefore keeps per-row sample identity,
independently of optional run-level sample metadata. Data-set append and archive
write/read validate any supplied routing context against the frozen catalog.
Finalization hashes include routing context; changing delivered context cannot
finalize an archive. Unfinalized row contexts that differ from the catalog are
rejected. As with legacy unfinished archives, absence of optional context alone
is not proof of which acquisition produced a row; no restart execution or physical
safety is inferred from data. Older rows without the optional routing field retain
their previous encoding and load behavior.

Version 1 plan documents add an optional `routing` catalog and the
`read_sample_resistance` kind. Older plans stay unchanged. The catalog and bindings
survive save/load and full-plan archive persistence. Run rows add an optional
`routing` object; CSVs whose plan has a catalog append a quoted `routing_json`
column. Legacy CSV column layout stays unchanged. Export validates streamed
contexts and preserves an existing destination if validation fails. Parsing must
respect quoted CSV records, including newlines and escaped quotes.

`examples/plans/sample-four-wire.plan.json` is loaded from disk in acceptance
and acquires three bias points. Tests additionally run raw Hall/vdP recipes, both
source functions, mixed samples, nested scans, external route verification,
front-panel sensing-mode faults, pause/abort during settle, cancellation before
archive delivery, context tampering, prefix changes and whole-tree preflight.
The published desktop still lacks matrix/profile registration and settings UI;
operator/device/hardware acceptance, PPMS file import and the remaining corrected
measurements remain separate required work. No new dependency was added.
