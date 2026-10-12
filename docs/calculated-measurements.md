# Per-point calculated measurement rows

`CalculatedMeasurementStep` wraps one bounded logical measurement (including its
ordinary setup sequence), a sample analysis, or a joint measurement group. Raw
rows are delivered first. One additional calculated row copies the final input
row's actual quantities, timestamp, environment, route/analysis/group references
and acquisition evidence, then adds ordered numeric expression results. Its
`CalculationSummary` identifies the wrapper, immediately preceding one-based
input row, algorithm version and outcome for every result. This is a calculated
record with the input's acquisition time, not another hardware observation.

Expressions use only that input row, its persisted axes/elapsed and earlier
calculated columns in the same row. The compiler checks eligible inputs, ordered
references, inferred units and globally distinct result IDs; schema columns are
optional, have no device source, and preserve dependencies. Ordinary blackboard
variables cannot supply stale inputs. Results cannot replace acquisition columns.

## Failures and lifecycle

The default stops on unavailable results after preserving raw data. Explicit
`ContinueOnUnavailable` records missing input/division-by-zero/unrepresentable
outcomes with absent result values and can continue scanning. It never fabricates
zeros. All result blackboard entries clear before the next acquisition, then only
successful columns are assigned. Existing pause/abort/output-off/resource ownership
and listener finalization remain authoritative. Abort or failed raw delivery does
not emit a calculated row; failed calculated delivery retains the already appended
row and faults the run.

## Evidence, persistence and editor

The live/writer/streamed-reader row validator retains just one previous input for
calculation checks. It reevaluates the entire result from the frozen plan/input,
compares values/outcomes/timestamp/all copied provenance, rejects stale indices or
missing markers, and verifies derived schema units/source/optionality/dependencies.
Existing analysis/group validators still track their bounded input windows. Native
v1 plans add a strict `calculated_measurement` body/columns/policy kind; archive v2
rows add optional calculation evidence. CSV includes actual derived columns and
`calculation_json`. Old files keep their meaning; unsupported older readers reject
the new fields/kind.

The **读取后计算** palette starts with a coherent source/sense acquisition and an
explicitly blank result ID. The named form edits its supported measurement body,
ordered result columns and recursive typed expressions, with an explicit continuation
checkbox. Complex child nodes remain separately editable in the graph. Original
unedited bodies are retained; readonly/stale/command/full-plan admission is shared.
Lazy typed templates avoid null-control recursion.

## Verification and remaining scope

9 Core +25 Application +14 App new cases. Three-point scans produce six raw/result
rows, save/reload, fresh archive replay and CSV. Seven integrated/independent sample
cases preserve actual route provenance; four Hall/vdP analysis cases preserve original
ranges/derived units on fresh archive. Joint-group results also replay their original
wide point. Wrong values/indices/outcomes/times/quality/missing markers and spoofed
schema reject. Zero/missing environment inputs, next-point clearing, abort and raw/
calculated listener failures preserve expected data and stop outputs/releases.
Forms author/save/reload/run actual values, propagate readonly, retire callbacks and
instantiate typed lazy templates. `examples/plans/calculated-source-scan.plan.json`
is a real six-row example with per-point Ohm/Watt results.

Native rendering/minimum-window acceptance remains deferred per user. No real
instrument IO or dependency was introduced. Calculations do not nest or appear
inside existing joint-group members; the whole group can be wrapped. Arbitrary
multi-row window/statistical functions, broader workflow/PPMS script/controller/
physical parity and bounded whole-run storage/indexing remain goal work.
