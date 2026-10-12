# Variable operations and outcome callback forms

Read-only PPMS conditions.rs at35763295f63b612f70af9dc55983644f437c18ea shows
set/add/sub/mul, missing numeric variable initialized from0, and optional success/
failure mutations. A search of the experiment implementation found no generic
expression evaluator. The C# interpreter already supports these typed operations;
this stage exposes them through forms rather than adding a different evaluator.

## Behavior and boundaries

PlanMutationFormViewModel/View adapts MutateValueStep: variable ID, Set/Add/Subtract/
Multiply, finite invariant numeric input and explicit units. Switching operation
clears its numeric input. Multiply is a dimensionless factor; the bound unit label
is authoritative and caller unit metadata cannot relabel it. Other operations use
explicit units and the complete compiler protects existing variable type/units.
Missing numeric variables keep the existing zero-initialization behavior. No
Boolean/Text coercion or instrument IO is introduced.

PlanOutcomeMutationFormViewModel/View adapts OutcomeMutationStep while retaining
its body, parent ID and existing callback IDs. Success and Failure are independently
optional. A new slot has a unique generated ID and blank variable/numeric inputs;
enabling it requires explicit authoring and full compiler validation. A disabled
slot does not execute its retained draft fields. Readonly state follows parent
admission and slot inclusion. Bodies stay editable through the existing graph.
Two palette kinds create typed variable operation and outcome callback steps.

Callbacks share the existing interpreter/resource lifecycle. Ordinary StepOutcome
Failure selects the failure mutation without converting the workflow result into
success. Instrument/timeout/cancellation/arithmetic/safety exceptions are not
ordinary Failure and do not execute these callbacks. This deliberate safety
behavior remains distinct from a general error handler; no engine rule is weakened
for the UI. Dynamic overflow is still rejected by finite Quantity construction and
must propagate through the original error/finalization path.

Existing whole-plan draft/JSON/preparation/run/file/import/selection guards remain
in effect, with stale nested callbacks ignored and command bodies rechecking busy
state. Named compiled-binding views compose within the existing scroll area.
No dependency, infrastructure adapter, Core model or interpreter change is needed.

## Verification and remaining work

22 additional App cases cover all four operation roundtrips and operation-change
input clearing, finite/culture parsing, four optional callback configurations,
body/ID retention, explicit new-slot inputs, readonly/mixed/stale/busy/preparation
locks, two palette kinds and full-compiler no-IO rejection of wrong units/empty
variables/nonfinite values. A changed success increment2 saves and loads on fresh
simulated devices, controls a while loop to two rows/count4, then confirms output-off
and released run. A changed ordinary failure callback updates count5 while the
original PlanFailedException and Faulted state remain. A dynamic multiplication
overflow propagates ArgumentOutOfRangeException, leaves failure callback count0,
and still confirms source-off/released run. App258 tests pass.

Native rendering/operation/unit/callback/palette interactions remain unverified:
current callable tools still have no cua native control. No user window was
manipulated or additional app process launched. Compiled/test evidence is not a
native rendering claim; the exact source package is recorded separately.

General arithmetic expressions (including division), richer subtree/scan/analysis/
variable/callback authoring, native acceptance, data memory/indexing, real transports
and physical/full PPMS acceptance remain unfinished. Independent C# engine,
GitHub-only publication; Gitee deferred; full goal stays active. No Rust edits,
new dependencies or physical lab IO.
