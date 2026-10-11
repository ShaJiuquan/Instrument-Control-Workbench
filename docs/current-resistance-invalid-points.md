# Current DC invalid points and available-input sample analysis

The independent C# executor can continue after explicitly typed terminal K2182
voltage acquisition failures when safe cleanup and unchanged configuration are
freshly confirmed. PPMS curr_dc channel/sample import enables this policy. Native
plans opt in; the default still faults on acquisition errors.

## Execution and errors

`CurrentResistanceRetry` adds optional `ContinueOnAcquisitionFailure`, false by
default. Existing native documents and single-retry behavior remain supported.
The JSON member is omitted when false:

```json
"retry": {
  "ownsSourceOutput": true,
  "settleSeconds": 0.1,
  "continueOnAcquisitionFailure": true
}
```

The example `examples/plans/current-resistance-invalid-points.plan.json` runs with
`examples/devices/k6221-meter-devices.json`. Its deterministic normal simulation
records healthy measurements; it does not inject failures to demonstrate recovery.
Acceptance tests explicitly inject terminal read failures at the port boundary.

Read/buffer timeouts and command -221 are retried once after the existing fresh
bounded cleanup. Eligible final or non-retried failures are overrange, read/buffer
timeout, invalid response, VISA read and command -221/-241/419/301. Actual
`ReadAsync` alone enters the typed meter acquisition boundary. Wrong-device or
untyped errors, parameters, snapshot/configuration operations, source readback,
source/meter/route drift, cancellation and cleanup failure are not invalid points.

Owned excitation confirms output-off plus meter idle after the failed attempt;
managed current scans confirm meter idle while preserving/confirming parent
output and programmed excitation. Stop tasks remain attached until terminal.
A failed/live timed-out stop retains the group and cannot overlap recovery or a
later read. Fresh observation consumes completed stop proof, so a newly observed
unknown state cannot silently reuse an earlier successful confirmation. Source
configuration, exact held route and observed meter settings/input are checked
again before an invalid row is committed. Original first/final acquisition and
cleanup exceptions are retained when cleanup prevents recovery.

Cancellation racing a typed terminal read stays cancellation and retains that
error as the cause. Independent bounded cleanup still runs. A canceled unfinished
point has no row; a listener failure/cancellation after a committed invalid row
retains that row and never manufactures a complete sample/wide summary.

## Data boundary

`IndependentResistanceFailure` is separate from successful electrical evidence.
It retains step/source/meter/current-reference identity, columns and threshold,
required matrix route, observed source configuration and meter settings,
kind/code/message/status, attempt/failure and fresh source/idle/off confirmation
times, plus optional first retry proof. Source configuration is programmed
excitation metadata; it is not a successful current measurement.

The failed row contains elapsed time, applicable scan axes and actual environment
observations. Its voltage/current/resistance columns are absent, its successful
quality and independent electrical evidence are empty, and its failure collection
is populated. There is no zero/NaN substitute. Every affected runtime variable is
removed, and preflight refuses downstream use of a value not guaranteed on every
path, including a stale earlier assignment under the same ID.

`MeasurementDataRow.AcquisitionFailures` freezes bounded distinct input identities.
Quality state `ContainsFailedAcquisitions` makes failures visible even when a
summary also has successful quality from other inputs. The live/archive/CSV
validator checks policy, expected constant/scan excitation, family/identity,
route/input1, chronology, complete environment and absence of invented electrical
values or successful quality for failed inputs. No proof-free blank electrical
row is accepted. These checks establish software-record consistency, not physical
clock authenticity or cryptographic hardware certification.

## Partial sample math

A complete group still records every planned selection, including invalid inputs.
`SampleResistanceAnalysis.CalculateAvailable` accepts the complete ordered set of
nullable Ohm inputs and marks `FailedAcquisition` when any is unavailable.

| Input availability | Retained derived results |
| --- | --- |
| Hall longitudinal valid, transverse invalid | Longitudinal resistance and eligible longitudinal sheet/resistivity |
| Hall longitudinal invalid, transverse valid | Raw transverse resistance |
| Hall both invalid | No derived electrical value |
| vdP one complete four-permutation subgroup | That group's mean absolute resistance |
| vdP both complete subgroups | Existing sheet/resistivity calculation |
| vdP a subgroup contains an unavailable input | That mean is absent; sheet/resistivity need both means |

Measured zero and missing input remain different. Existing numeric/geometry issue
flags and full-input math remain unchanged; the existing algorithm version1 gains
an explicit unavailable-input flag. No field-reversal or corrected Hall result is
implied. Analysis derives from each committed raw row, never a reused variable.
Summaries retain all successful and failed constituent evidence in order and
require common observed source/meter settings. Wide points preserve member
results and failure proof even when some members have no electrical values.

## Persistence and acceptance

Version1 native policies and optional `acquisitionFailures` records round-trip
through saved plans and fresh run archives. CSV adds `acquisition_failures_json`
for plans that support recovery; absent electrical cells stay empty. Failure
status must match its kind/code. Structural damage to policy, identity, status,
proof/time, excitation, input, values or derived summary is rejected, and failed
export preserves the prior accepted CSV. Older strict readers may reject new
populated fields or issue flags; absent legacy fields remain supported.

68 new tests cover18 Core and50 Application cases: eligible failures and retry
counts/status, owned and two-point parent-managed scans, both source references
on K2450 loopback, all sample topologies/partial or wholly unavailable groups,
mathematical subgroup/zero behavior, stale variable preflight and failed-middle
iteration, drift/ineligible errors, first/final cleanup failure, non-cooperative
stop without overlapping recovery, cancellation races and listener finalization,
healthy native fixture, raw/analysis/wide native/fresh archive replay and CSV,
and forged failure/summary rejection without replacing accepted exports.

Physical K2182/K6221 adapters, real timing/range/quality and lab acceptance remain
unfinished. Source-side acquisition error families and other resistance modes,
advanced settings/delta/pulse/AC/corrected Hall, remaining controllers/BT/config
and native UI/packaging remain in the full migration scope. This stage adds no
library, Rust edit or laboratory IO; all App/App.Tests drafts remain excluded.
