# Hall-bar and van der Pauw measured-input analysis

`SampleResistanceAnalysisPlan.Create` and
`MeasurementRunService.StartAnalyzedSampleResistanceAsync` add derived analysis
to the existing sample-resistance workflow. The original raw-only recipe API
remains available for TwoWire, FourWire, HallBar and VanDerPauw. The analyzed API
supports HallBar and VanDerPauw; two/four-terminal geometry formulas are not
silently substituted for those topologies.

`SampleAnalysisStep` owns a complete ordered measurement group: two Hall readings
or eight vdP permutations. Each child uses the existing source-off, guarded matrix
reconciliation, settle, source-enable, coherent acquisition and output-disable
path. The group requires internal route management; externally managed individual
reads remain a separate operation. Compilation checks every child, including
children in unselected branches, and leases all dependencies atomically before IO.
Pause/resume continues the current group; cancellation/fault retains acquired raw
rows and never averages a partial group. Final cleanup uses the existing run
lifecycle. Analysis does not perform driver calls or add a second execution engine.

Each execution publishes all raw rows, followed by one summary row. The summary
includes the active scan coordinates, elapsed time and available derived columns.
Its `SampleAnalysisSummary` references the group step, the one-based first input
row and input count, issue flags and analysis algorithm version 1. The plan's
frozen routing configuration supplies sample/wiring identity and geometry. Every
input retains its exact electrode/channel/crosspoint provenance. Nested and
repeated groups cannot reuse a previous group's readings or pool different scan
points. Raw-only plans and rows retain their prior serialization and CSV layout.

`SampleGeometry` accepts optional positive `Quantity` dimensions in meters:
width, length and thickness. The version 1 plan codec stores the optional geometry
object inside each sample profile; absent geometry stays absent in legacy files.
New units are appended to the unit enum: Meter, OhmPerSquare and OhmMeter.

| Topology | Always retained | Geometry-dependent result |
| --- | --- | --- |
| HallBar | Signed longitudinal `r_xx` and raw transverse `r_xy`, in Ohm | `r_sheet = r_xx * width / length`; `rho_xx = r_sheet * thickness` |
| VanDerPauw | Mean absolute resistance of the four horizontal and four vertical permutations, in Ohm | `r_sheet` solves `exp(-pi*Rh/Rs) + exp(-pi*Rv/Rs) = 1`; `rho_xx = r_sheet * thickness` |

The vdP equation was checked against the [NIST measurement algorithm](https://www.nist.gov/pml/nanoscale-device-characterization-division/popular-links/hall-effect/hall-effect-measurements-2).
The grouping/absolute-mean and Hall-bar geometry rules follow the captured Rust
PPMS `measurement/sample_r/vdp.rs`, `hallbar.rs` and `atoms/compute.rs` at
`35763295f63b612f70af9dc55983644f437c18ea`. Signed Hall inputs remain signed.
The vdP solver bisects a normalized logarithmic variable rather than multiplying
a potentially huge resistance bracket. Small exponential complements use a series
to avoid loss of precision. Tests include the isotropic analytic solution,
anisotropic equation residuals, symmetry, tiny/large scales, extreme input ratios
and representability failures. These are numeric algorithm checks, not measurement
uncertainty or instrument accuracy certification.

Missing dimensions produce `MissingGeometry`, not an invented zero result. A
nonpositive vdP group mean produces `NonPositiveVanDerPauwMean`; raw means remain,
but sheet resistance is absent. Overflow/underflow of a nonzero derived result
produces `UnrepresentableResult`. These are analysis availability flags, not
instrument compliance/overrange quality. Optional sheet/resistivity columns are
not guaranteed plan variables; compilation rejects downstream unconditional use
of those variables. The raw means or longitudinal/transverse values can be used
as typed variables after the group. Field reversal, Hall coefficient, carrier
density/mobility, thermal-offset correction, reciprocity/linearity criteria and
instrument-quality propagation remain pending. A raw transverse resistance is
not reported as a corrected Hall coefficient.

`SampleAnalysisRowValidator` is the shared domain boundary for live append,
archive writing and streaming archive reading. It indexes declared groups once
and retains only eight previous rows. It verifies consecutive input references,
ordered selections, configured sample/wiring/context, coherent V/I/R, shared scan
coordinates, complete result keys/units, issue flags and recomputed values. Derived
values allow relative differences up to 1e-12 for platform Math.Log/Exp variation;
recorded finalized bytes still have the existing SHA-256 integrity checks.
Unknown algorithm versions and missing summary provenance for declared derived
values are rejected. Ordinary raw rows still have the previously documented
optional-context limitation in unfinished archives.

Archives preserve geometry, full groups and summary references. Cancellation or
listener failure after local summary commit is reconciled by finalization using
the same validator. Analysis plans add a quoted `analysis_json` CSV column;
raw routing plans retain their existing `routing_json` layout. Export streams rows
through validation and preserves an existing destination on failure. No new
package, storage dependency or unbounded analysis history was introduced.

`examples/plans/sample-hall-analysis.plan.json` is loaded from disk in acceptance
and executes three bias points, each with two raw rows and one independent
summary. Tests also exercise voltage/current sourcing, analyzed vdP, nested scans,
pause/resume/abort, listener faults, fresh archive reload/export, invalid
geometry/groups and corruption of input pairs, values, references, flags,
algorithm version and missing context. All devices are simulated. Native
profile/analysis controls, physical wiring validation and hardware acceptance
remain unfinished.
