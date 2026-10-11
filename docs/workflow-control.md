# Workflow results, fallback and retry

Core `StepOutcome` distinguishes ordinary workflow Success/Failure from errors.
`ConditionStep` evaluates a typed variable/constant or variable/variable comparison
with matching units. `FallbackStep` tries ordered branches until one succeeds.
`RetryStep` makes a bounded number of attempts with a pause-aware delay between
failed attempts. `ResultDecoratorStep` applies ForceSuccess, ForceFailure or
Invert to the child's result. These are native plan nodes, executed by the same
Application engine and shared device resource lifecycle as scans/acquisition.

```mermaid
flowchart LR
  P[Native or imported plan] --> C[Core typed steps and outcomes]
  C --> V[Application preflight]
  V --> E[Application executor]
  E --> R[Condition / fallback / retry / decorator]
  R --> E
  E --> D[Core device ports]
  D --> I[Infrastructure adapters]
  E --> N[Typed step notifications and acquisition rows]
  E --> S[Independent group stop / release or retained recovery]
```

## Result and error semantics

Sequence, repeat, scan, iterator and selected if/else branches propagate Failure
and stop subsequent children/points. Fallback returns Success at its first
successful branch; all-fail returns Failure. Retry returns Success at its first
successful attempt; exhaustion returns Failure. Terminal Failure becomes a
`PlanFailedException` identifying the originating condition/result decorator,
a Faulted run outcome and the normal independent cleanup. Each completed node
publishes its own typed outcome in `WorkbenchNotification.Outcome`.

**Exceptions are never transformed by workflow controls.** Transport, timeout,
unsupported configuration, validation, routing, listener and safety errors remain
errors. Cancellation and operator abort propagate without another branch or
attempt. ForceSuccess cannot turn a failed stop into completion or release.
Stop failures retain the whole group until explicit successful safety recovery,
including when workflow evaluation had already succeeded.

The Rust handlers in the read-only reference at35763295 catch broad errors in
fallback/retry/decorators (with different cancellation protections). C# deliberately
makes recoverability an explicit domain decision. This stage imports PPMS control
shapes for logical results; it does not promise broad Rust exception-swallowing
parity. Existing acquisition-specific retry/invalid-point policies keep their
own fresh state/cleanup proof. Generic recovery of driver exceptions and timed-out operations,
parallel cancellation and background/subtree/script-expression behavior remains
separate unfinished work.

## Scope and provenance

Fallback and retry add dimensionless 1-based `.branch`/`.attempt` and `.total`
columns derived from their step IDs. Child measurements depend on those active
axes; rows after the control omit the optional scope columns. Failed logical
branches/attempts retain committed observations, grouped summaries and quality.
Nothing fabricates a device, rolls back physical writes or discards valid rows.

Each branch/attempt starts from the variables at control entry, plus its scope
counters. A failed branch cannot inject variables into the next alternative;
physical settings stay under the existing exclusive lease and validated ports.
Only variables available on every successful path are admitted downstream.
Decorators cannot guarantee new variables after an early child failure. If/else
merging now also removes variables absent on a path, preserving the same rule.
Optional invalid acquisitions cannot resurrect a previous observation.

Preflight visits every branch, including branches skipped at runtime, before IO.
The worst execution budget sums fallback branches and multiplies retry attempts;
existing 10,000-node/128-depth/10,000,000-execution/document limits apply. Counts,
delays, null children, enums, condition units and unknown variable dependencies
reject before admission. Pause during backoff confirms output-off/hold, reserves
the group and resumes the remaining delay with verified settings/output state.

## Formats and acceptance

Native version1 adds `condition`, `fallback`, `retry`, `result_decorator` kinds.
Retry explicitly stores `maxAttempts`, `delaySeconds`, `body`; decorators store
`effect` and `body`. Named enums and strict fields prevent unknown controls from
silently downgrading. Native saves and reopened archives retain executable steps.
CSV retains acquisition, nested branch/attempt axes, grouped evidence and plan.

PPMS `fallback`, `retry`, `force_success`, `force_failure`, `inverter` types map
to these models. Retry defaults to3 attempts and0 delay_ms; delay_ms is a bounded
nonnegative integer. Decorators/retry require exactly one child rather than
silently ignoring extra children. Fallback requires a nonempty children array.
Malformed controls preserve tree-path/source-line diagnostics. PPMS condition
expression strings are not treated as this typed native ConditionStep.

[ppms-fallback-retry-import.json](../examples/plans/ppms-fallback-retry-import.json)
runs five simulated field observations across three alternatives: one forced
failure, two bounded failed attempts, one forced-success result, then a read
outside the control. It needs no source preparation and converts to a native
plan for fresh execution.

Focused acceptance:9 Core `ControlStepTests`,53 Application `ControlFlowTests*`.
Tests include both result directions, all parent propagation, variable isolation
and definite availability, bounded attempts, local raw-TCP acquisition rejection
through all five controls, cancellation, pause/backoff, committed listener fault,
failed hold/root-cause preservation/recovery, hidden invalid branch and execution
budgets, strict native/import codecs, actual PPMS fixture and native grouped
quality save/reopen/archive/CSV/re-execution. No new dependency, Rust write,
physical laboratory IO or App draft change. This is an active migration slice;
full PPMS, native editor/operator acceptance and hardware remain incomplete.


## Wall-clock timeout scopes

`TimeoutStep` owns one executable child and a typed TimeSpan deadline. Native v1
stores `kind: timeout`, `timeoutSeconds` and `body`. PPMS `timeout` and schema alias
`with_timeout` import `timeout_s` (default 60 s) and require exactly one child.
Nonfinite, negative, unknown, null and wrong-type fields reject before IO. Zero
means immediate expiration before child execution, not an unlimited wait. The
upper bound is the existing .NET timer range (4,294,967.294 seconds).

The reference Rust handler uses `tokio::time::timeout`; its deadline includes
operator pause. C# likewise counts wall-clock time, including child dwell and
paused waits. Scope entry creates a deadline linked to the parent token, and all
existing child locks, port calls, waits and ordinary listeners receive that token.
Nested scopes preserve their parent cancellation. Completion restores the parent
token and disposes the timer, so later steps cannot inherit an expired deadline.

Expiry is `PlanStepTimeoutException` with the timeout step ID, deadline and original
cancellation. It faults the run and prevents fallback/retry/forced-result controls
from hiding it. Explicit operator abort remains Aborted; genuine driver/listener
errors remain their original errors. A child's ordinary Failure still propagates
with its original workflow step identity. Timed-out nodes do not publish a success
completion notification. Acquisition rows already committed remain partial data.

The engine awaits the actual child until it settles; it never abandons a task or
releases claims while an instrument call remains live. Deadline expiry initiates
cancellation; prompt stop therefore requires the adapter to honor cancellation.
An adapter that ignores it retains the active run and its whole resource group
until the call returns. This is not a hard real-time deadline or forceful hardware
interrupt. After drain, the normal independent bounded stop runs; failed or live
stops retain the group for explicit recovery. Pause expiry cannot restore output.

Compilation and all data/document validators descend into timeout children. A
short or zero deadline cannot hide unsupported devices, invalid variables, depth,
node/value limits or the worst-case execution budget. Variables produced by a
completed scope can flow downstream; a forced-success wrapper around an early
logical failure cannot guarantee variables defined after that failure.

The executable [PPMS timeout fixture](../examples/plans/ppms-timeout-import.json)
collects three simulated field observations through nested deadlines. Acceptance
covers native save/load, fresh archive/CSV/re-execution, partial faulted archives,
vdP raw/derived sample evidence under a deadline, nested cancellation, pause,
operator abort, refused cleanup and noncooperative live-call retention. No new
package, physical IO, Rust edit or original UI draft change. Parallel/subtree/
background/script-expression controls, native flow editor and hardware acceptance
remain pending; full PPMS migration remains active.


## Subtree variable scopes

`SubtreeStep` contains authored children and explicit typed `VariableBinding`
inputs/outputs. A child inherits the current parent quantities. Inputs map parent
source IDs to child destination IDs, reading one complete parent snapshot; local
input aliases can shadow inherited units within the child. Child assignments and
acquisitions update local variables. Only declared outputs are copied back, using
one completed child snapshot. Output destinations must retain existing parent
units, or introduce a new typed parent variable.

A successful empty scope may export inherited values. A logical Failure, exception
or cancellation restores the parent variables and exports nothing. An outer
ForceSuccess may convert ordinary Failure, but cannot leak failed-scope writes or
make newly defined outputs guaranteed. Nested scopes restore the nearest parent.
All children, input dependencies, output availability/units and execution budgets
are validated before admission, including branches skipped at runtime. Missing
inputs/outputs and duplicate/ambiguous mappings reject instead of falling back to
stale values or mapping order. Both mappings freeze their caller-owned lists.

Variable isolation leaves acquisition rows, quality, analysis summaries and actual
instrument writes under the same run/resource group. It does not roll back device
state. Actual scan coordinates remain in row axes; an input alias does not relabel
those observations. Pause resumes the same local scope. Timeout/abort and driver
faults unwind scopes before the existing independent stop, retained claims and
explicit recovery. Local variable cleanup cannot authorize release after bad hold.

Native version1 stores `kind: subtree`, `children`, `inputs` and `outputs` (objects
mapping source ID to destination ID). PPMS `subtree`, `scope`, `isolated` use
optional `params.inputs`/`params.outputs` objects and a required children array.
Map values must be valid string IDs; duplicate destinations are invalid. Zero to
10,000 children and up to10,000 bindings per map are bounded by the existing
whole-document/plan budgets. The Rust reference copies inputs parent→child and
outputs child→parent through an inherited blackboard. C# implements that numeric
quantity scope; Rust Bool/Text blackboard values and expression controls
remain further migration work.

The [nested PPMS subtree fixture](../examples/plans/ppms-subtree-import.json) runs
four field observations inside two number-scan iterations. Acceptance adds native
checks consuming the exported final number/field and a fifth observation outside
the scan, then saves, reopens, exports CSV and re-executes on fresh sessions.
Additional tests preserve vdP raw/derived evidence and grouped-quality archives
under a subtree. Mapping swaps, inheritance, local unit shadow, output unit
collision, early failure through ForceSuccess, cancellation, timeout, pause,
actual device state and retained failed hold are exercised. No new package,
physical IO, Rust edits or original29 UI draft changes. Full PPMS migration,
non-numeric blackboard/expressions, parallel execution and native editor/hardware
acceptance remain incomplete.

## Branching on a child outcome

`BranchOnOutcomeStep` executes Condition once on the same run context, then
executes Then for Success or optional Else for ordinary Failure. Missing Else
returns Success after a false condition. The selected child's outcome becomes the
branch outcome; a selected Failure keeps its original step identity. Condition
observations and variable/device mutations remain; there is no entry snapshot or
physical rollback. Instrument IO, listener faults, timeout and cancellation
propagate through the branch without choosing Else.

Preflight visits all children before device admission and budgets the condition
plus the largest branch. Then can use values guaranteed after condition success.
Else conservatively retains entry variables for a condition which can fail early;
a completed IO-only child wrapped in ForceFailure/Invert retains its observed
values on the failure path. Values after the branch must have the same unit on
both paths. No speculative failed-path variable is treated as an observation.

Native v1 uses `branch_outcome` with required `condition`/`then` and optional
`else`; explicit null children and extra fields reject. PPMS `if_then_else`,
`conditional`, `branch` node mode takes exactly two or three children; `then` and
`else` are sequence wrappers. Source locations survive conversion. PPMS numeric mode (`variable`, `op`, `value`) is described below. Existing
typed `IfElseStep` remains the numeric API; this node-result slice does not
claim while or arbitrary expression parity.

Tests cover exact branch selection/outcomes, condition state, guaranteed-variable
preflight, hidden invalid branches and costs, IO/timeout/listener error
propagation, abort in condition and selected branches, pause, retained ownership
on failed stop and recovery. `ppms-outcome-branch-import.json` runs five rows,
saves/reloads native plans and archives, exports CSV and executes in a fresh
service; grouped quality and vdP raw/derived evidence also round-trip under the
branch. These are simulation and owned loopback protocol tests, not lab or UI
acceptance.


## PPMS numeric conditions and pure iterators

Native ValueCondition keeps exact equality by default. Optional typed
EqualityTolerance enables strict abs(lhs-rhs) < tolerance for Equal and its
complement for NotEqual. Preflight rejects zero/negative tolerance, mismatched
units or use with an ordered comparison; IO and cancellation stay faults/abort.
Native v1 preserves optional equalityTolerance in both condition and if_else,
rejects explicit null, and reads older documents with exact comparison.

PPMS if_then_else/conditional/branch numeric mode lowers into a typed input
assignment and IfElseStep. It accepts gt/lt/gte/lte/eq/neq and their symbolic
forms; omitted op is gt. Equality uses the PPMS strict 1e-9 absolute tolerance
in the operand's physical unit. Then is required, Else optional. Both branches
preflight before resource admission. Missing/unavailable variables, malformed
injections, unknown fields/operators and unsupported variable contracts reject.

Supported snapshot axis names are field (T), temperature (K), angle (degrees),
current (A), voltage/bias (V), scan_time (s) and scan_number (dimensionless).
Migrated scans explicitly assign these names to ppms.axis.* variables each point;
voltage publishes both voltage and bias. Assignments do not create data rows.
Last values remain after the scan, including a nested scan's most recent value;
the next outer point publishes its new value. Subtree isolation still applies.
Preflight requires an actual definition on every path; names never invent a
readback, hardware connection or fallback zero.

PPMS for_each/iterate generates dimensionless points with the shared bounded
point parser and executes ordinary authored children. Native ForEachStep has a
validated IndexBase; existing API/docs default OneBased, this PPMS iterator uses
ZeroBased. Row columns, labels and native indexBase preserve that choice.
Both nested points and flat controls are supported; points_mode from the PPMS
schema is normalized to mode and conflicting dual modes reject.

Iterator injection applies only to direct children, matching the PPMS handler.
Numeric conditions resolve value/iterator_value, index and total from the current
iterator, unless their corresponding explicit iterator_value/index/total field
wins. Omitted comparison value uses the injected current point. Nested iterators
have distinct row axes/counters; this is serial owned execution, not a generated
per-point DOM or parallel runner. An injected RHS point becomes a QuantityFromNumberValue with the axis's explicit
unit. Compiler and executor require the source variable to be dimensionless; an
already physical variable cannot be relabelled to another unit. Native v1
quantityFromNumber preserves this contract with strict variable/unit fields.

Tests cover operator symbols/defaults and epsilon boundary, old exact/one-based
compatibility, tolerance/unit/availability validation, nesting/overrides/direct
injection, scan aliases including voltage/bias, strict native index fields,
no Else, pause/abort and owned TCP/timeout faults. The five-row
ppms-numeric-conditions-import.json saves/loads native plans, reopens archives,
exports CSV and reexecutes in a fresh service. Arbitrary/custom Bool/Text
blackboard variables, pol_voltage/remaining unmigrated axes, dynamic command
injection, advanced point modifiers, while, expressions/parallel, full UI and
physical acceptance remain pending.


## While, variable mutations and node guards

WhileStep reevaluates its typed condition at each iteration after the pause/abort
checkpoint. False finishes successfully, body Failure stops with its original
failure step, and exceptions/cancellation propagate. Its body shares variables,
resources and observations. Rows carry a one-based optional iteration column;
outer axes are restored when it exits. Zero iterations cannot make body-only
variables definite. Every body/condition/resource is preflighted, including
unused paths and units.

MutateValueStep supports Set/Add/Subtract in the variable's unit and Multiply by
a dimensionless factor. A missing input starts at typed zero, matching numeric
PPMS mutations; known units cannot change. Finite Quantity construction rejects
arithmetic overflow. These are run-context values, not device writes or invented
readbacks; data rows remain immutable observations.

OutcomeMutationStep runs only the success or ordinary-failure callback after its
body completes. It preserves that outcome and original logical failure identity.
IO/listener/timeout/budget/abort faults do not run callbacks. This deliberately
keeps the C# engine's fault/result distinction rather than applying Rust's broad
post-error callback. Preflight validates both hooks and conservatively excludes
new failure-path variables from success-only availability.

PPMS while uses variable/op/value, lt default, the same strict 1e-9 equality and
operator symbols as numeric branches. Custom dimensionless variables must be
declared by _onSuccess/_onFailure mutations; physical aliases retain their units.
Callbacks accept variable/action/value (default action set; set/add/sub/mul).
_skipIf/_failureIf accept variable/op/value (eq default). Failure has priority;
early skip/failure runs neither body nor post-callback. Strict malformed/missing
variable/operator contracts fail before device admission, instead of Rust's
silent no-op/false fallback. Parallel nodes requiring generic execution are still
pending and do not gain inferred concurrency from these wrappers.

The existing ten-million-step plan budget is now enforced during execution,
including software-temperature target ticks. Native plans may author a lower
maximumStepExecutions (1..10,000,000); older documents retain the same limit.
Budget exhaustion throws PlanExecutionBudgetException before the next step/write,
uses independent safety cleanup and cannot be decorated into Success. It is not
normal while termination or a wall-clock/driver-transaction deadline. Started IO
is still awaited before stop/release. Dynamic previews expose HasDynamicLoops and
report the enforceable whole-plan cap, not a fabricated exact iteration count.
Static plans retain their calculated upper bound and reject work exceeding the
authored cap at preflight. Empty-body loops still execute/yield/count their body nodes.

Native v1 while/mutate_value/outcome_mutation preserve conditions, units and hooks;
null/wrong-kind hooks and malformed budgets reject. The four-row
ppms-while-callbacks-import.json seeds a counter, acquires three iterations and
checks its final value. Native save/load, archive/CSV/fresh execution, grouped
quality and vdP raw/derived evidence are covered, alongside zero-body variables,
arithmetic/unit guards, budget fault/overflow, logical failure, precondition
priority, pause/abort, owned TCP/timeout faults, retained failed stops/recovery.
Bool/Text/custom physical metadata, arbitrary expressions, parallel/background
controls, physical devices and full graphical/native UI acceptance remain pending.

The PPMS importer checks raw and known control-wrapper depth with an iterative traversal before recursive decoding. Duplicate JSON keys and excessive plain or guarded nesting produce format errors before device IO; the deepest supported plain tree remains importable.
