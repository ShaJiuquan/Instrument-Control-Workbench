# Typed values and workflow forms

The node editor now adapts the existing PlanValue domain: Quantity, Variable,
QualifiedNumber (QuantityFromNumberValue), Boolean and Text. This stage adds UI
for existing interpreter behavior; it does not introduce string evaluation,
arithmetic expression parsing or an alternate execution path.

## Behavior and boundaries

A shared compiled-binding PlanValueFormViewModel/View is used by parameter writes,
wait-stable targets, assignments and condition right operands. Hardware numeric
constants and explicit unit qualification use the selected descriptor's actual
unit; a caller-supplied unit field cannot relabel them. Changing a device/parameter
clears numeric and variable input. Variable references retain their type/unit;
QualifiedNumber is only valid for a definitely available dimensionless quantity.
The full compiler rejects physical relabeling, missing variables, wrong type/unit
and unsafe writes; runtime parameter limits remain active on dynamic values.

Assignments support all five kinds with an explicit destination variable ID and
units for quantities. Text is literal (including Unicode/newlines/quotes), with
the existing 65,536-character domain limit. Boolean/text values are not converted
to numeric hardware settings. Numeric input is invariant and finite. Unedited
values retain their exact original PlanValue instance; this also preserves wait
target references when changing only timing/tolerance. Edited fields construct
explicit typed values and still pass the complete plan compiler.

ConditionStep, IfElseStep and WhileStep expose left variable, operator, typed
right operand and optional positive explicitly unit-tagged equality tolerance.
Branches/bodies/IDs remain unchanged. Bool/Text only support Equal/NotEqual;
numeric tolerance requires equal units and an equality operator. These rules
remain compiler-owned; the UI never claims that an unverified reference is valid.
Four additional palette kinds create typed assignment/check/branch/while steps.
Unbound placeholder conditions fail offline; branch/body sequences remain editable
through the existing graph. Execution budgets, cancellation, pause and safe stop
are inherited from the same interpreter and resource lifecycle.

Existing draft admission applies to nested value/condition fields: run/files/
imports/preparation/selection locks, form/JSON exclusivity, explicit discard,
stale form callback rejection and busy command-body checks. Readonly state is
propagated into nested controls. Obsolete parent-only scalar binding and static
wait-target caption were removed after the shared editor replaced their UI.

## Verification and limits

29 new App cases cover five assignment value roundtrips, references/qualified
writes/waits and condition/branch/while body+unit+tolerance retention, hardware
unit authority/address clearing/logical rejection, finite parsing/text limit,
full-compiler no-IO rejection of unavailable/wrong-type/physical-qualified values
and invalid comparisons/tolerances, four palette kinds and nested stale/mixed/busy/
preparation guards. A changed numeric seed plus an edited Boolean while predicate
is saved, loaded on fresh simulated devices, writes0.02Volt from a declared unit,
executes one row, turns its flag false, exits and confirms source-off/released run.
Prior target-reference tests still assert original identity; editor document
reconstruction is compared by serialized content rather than unrelated object
identity. Prior dynamic-set fallback test now proves supported reference editing.
App236 tests pass; full committed-source gate recorded in publication artifacts.

Native rendering, value/unit selectors, text editing and palette interactions are
unverified in this stage: the current callable tool inventory has no cua native
control tool. No existing user windows were manipulated or extra process launched.
Compiled bindings and VM/simulation tests are not substituted for native evidence.

Remaining: arithmetic/general expression support, richer variable discovery and
scan/analysis/subtree forms, native acceptance, data memory/indexing, real transport
and physical/full PPMS acceptance. No dependencies, Rust edits or physical lab IO.
Independent C# engine and GitHub-only publication; Gitee deferred. Goal stays active.
