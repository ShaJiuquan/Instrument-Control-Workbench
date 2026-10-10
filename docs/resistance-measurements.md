# Coherent source-meter resistance acquisition

This stage implements the K2450-style Voltage DC recipe and a typed resistance
read for either K2450 source function. It reuses `MeasurementRunService`, atomic
device ownership, pause/abort, stop confirmation, terminal listeners and archives.
It does not create another instrument workflow inside the driver or UI.

## Read contract and calculation

`ISourceMeterPort.ReadSourceSenseAsync` returns `SourceSenseReading`: actual
source readback and sense value from one acquisition, explicit source/sense
functions, finite typed quantities and the acquisition timestamp. A hardware
adapter must return those paired values from its protocol acquisition. Combining
a saved setpoint with an independently triggered meter read does not satisfy the
contract. This repository currently provides simulation and injected test ports.

`ReadResistanceStep` requires an explicit source preparation and opposite sense
function: Voltage source with Current sense, or Current source with Voltage
sense. The application checks live mode/output and validates the returned
functions before committing. It records actual V, actual I and R = V / I in one
row. Resistance declares both raw values plus active axes/elapsed time as data
dependencies. Ordinary measurement parameter addresses describe the raw voltage
and current provenance; the plan records whether each raw value came from source
readback or sense. The resistance itself is a derived column.

An absolute current at or below the configured Ampere threshold is rejected.
The standalone default matches the Rust reference `f64::EPSILON`
(2.220446049250313e-16); it is not C# `double.Epsilon`. Overflow/non-finite ratios
are rejected by `Quantity`. Failed acquisitions commit no invalid/NaN row and
fault the run; cleanup still runs and its failures are preserved with the original
error. Paired acquisition invalidates any earlier stop confirmation, including
when output was enabled externally after that stop. The group must stop again
before release. The wider PPMS recoverable quality/status-row model remains pending.

## Standalone and scan-managed use

`StartVoltageResistanceAsync` takes `VoltageResistanceRecipe`: typed voltage
within ±200 V, current compliance, two/four-wire sensing, NPLC, settling duration
and optional current threshold. Defaults are two-wire, one PLC and 0.1 s settling.
The recipe becomes preparation, output-on, interruptible settling, one read and
output-off in the same engine as IV and nested plans. Caller cancellation reports
an aborted outcome through the existing engine; a pre-cancelled request performs
no driver IO. Actual read/cleanup faults remain exceptions and faulted outcomes.

For nested scans put `ReadResistanceStep` in the scan body, with a preceding
`DelayStep` when settling is needed. The read never changes source level and does
not stop output at every point. Source levels are owned by the scan and final
cleanup confirms output off. Pause stops outputs under the existing group lease;
resume restores their previous enable state. Abort during settling retains the
empty or partial finalized data set.

The runnable [field/resistance plan](../examples/plans/field-resistance.plan.json)
loads from disk and produces three field targets × three nonzero voltage biases,
with stable-field checks and nine V/I/R rows on fresh simulation sessions. Field
axis values are commanded targets, not a separate field readback column. Voltage
axis is the programmed bias; the voltage measurement column is actual paired
readback. An injected test port returns 0.099 V and 10 µA for a 0.1 V setpoint,
and the result is 9900 Ω rather than 10000 Ω.

## Validation and scope

Source validation now checks the coupled K2450-style envelope: above 21 V,
current compliance is at most 105 mA; above 105 mA, voltage compliance is at most
21 V. This application admission policy follows the output ranges and 105%
overrange stated in the [manufacturer operating specifications](https://www.tek.com/en/datasheet/smu-2400-graphical-sourcemeter/model-2450-touchscreen-source-measure-unit-smu-instrument).
Individual descriptor ranges still apply. Preflight validates fixed
counterparts for constant/scanned writes. Plans changing both level and compliance
are checked against the actual current configuration before each live write;
this avoids using stale preparation values to reject a valid sequence.

Two/four-wire mode selects source-meter sensing configuration. It does not prove
that physical matrix/electrode routing is correct. Neither the simulator nor these
constants certify hardware safety. The source mock still does not enforce actual
current compliance.

`measure_r` is **partial** in the migration inventory. Still required: PPMS JSON
aliases/parameter import, standalone channel binding, explicit source/meter ranges,
K6221/K2182 current DC/delta/pulse modes, SR830 AC modes, sample Hallbar/Van der
Pauw derived results/geometry, electrode checks, quality rows and real
transport/device evidence. Typed sample/electrode routing and raw Hall/vdP
assignments now exist through ReadSampleResistanceStep; see sample-routing.md.
Desktop selection/editor/plot acceptance remains pending with the other UI work.

`ResistanceExecutionTests` cover paired actual values, both sensing modes,
current-source interpretation, nested schema dependencies, saved plan/archived
result reload and CSV, zero current/functions/read faults, retained ownership on
cleanup failure, preflight rejection, dynamic combination checks, planned
compliance changes, pause/abort and precancelled no-IO behavior. Core tests cover
units, threshold, duplicate columns and coupled limits. No new package was added.
