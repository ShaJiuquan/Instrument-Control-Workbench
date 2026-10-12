# Typed arithmetic expressions

Native plan values now support nested Add/Subtract/Multiply/Divide expressions in
assignment, device writes, condition operands, controller targets and expected
independent-source excitation. The shared value form exposes authored operator
and two recursive operands. Existing unedited trees retain their exact original
objects; new operands/operator start blank. Hardware address changes clear the
expression; operand units are explicit and never relabeled by a target parameter.

## Domain and execution

`ArithmeticValue` is an immutable AST with at most 16 levels and 255 logical nodes,
including shared subexpression occurrences. `NumericPlanValue` owns pure inference,
evaluation, static folding and variable discovery. `QuantityArithmetic` owns
finite operations and declared product/quotient relations. No script execution or
external math dependency is introduced. Logical values cannot become operands.

Addition/subtraction requires identical units. Dimensionless scaling and equal-unit
ratios work for every declared unit. The table includes A×Ohm=V, V×A=Watt,
A×Second=Coulomb, Ohm×Meter=OhmMeter, field/angle rates times seconds and
Hz×Second=Dimensionless, plus corresponding quotients. Kelvin/minute uses an
explicit 1/60 factor when multiplied by seconds. Unsupported dimensions reject;
this is not a generic SI algebra or implicit unit conversion library. Watt is an
appended unit; existing unit values and files retain their meaning.

The compiler infers quantities on every workflow path, rejects unknown/logical/
mismatched variables and uses folded literals for device limits, coupled source
checks and controller target ranges. Runtime dynamic division by zero, overflow
and nonzero multiply/divide underflow produce faults under the existing resource
lifecycle; workflow success/fallback/retry decorators cannot swallow these errors.

## Documents and history

Version-1 values have a strict optional `arithmetic` object with operator/left/right.
Old files retain their encoding; older readers reject unknown variants. Encoding
charges recursive nodes against the existing pre-DOM value budget and checks the
exact final 4 MiB UTF-8 size. Decoding enforces bounded numeric ASTs and fields.

Expected programmed-current expressions require all variable inputs to be active
persisted axes. Successful and failed acquisition evidence reevaluates the same
expression from each row's axes and compares actual excitation. Blackboard
arithmetic writes validate output type/units; literal-only results validate exact
values. Dynamic terminal blackboards do not claim to reconstruct historical input
values after later writes or scope changes.

The example `examples/plans/arithmetic-source-scan.plan.json` runs three voltage
points with an offset, coherent V/I rows and terminal R=V/I, P=V×I quantities.
Calculated assignment values are terminal blackboard entries, not new per-row
measurement columns. Generic per-row calculation recording remains future work.

## Verification and boundaries

32 new Core, 35 Application and 12 App cases. They cover declared units/minute
scales, finite arithmetic and structural budgets; static preflight/no IO; 12
workflow wrappers around dynamic division failure and two dynamic overflow cases;
saved/fresh three-point scans, coherent actual R/P, archive reload/export, literal
snapshot forgery, K6221 success/failure persisted-axis excitation and missing-axis
rejection; malformed codec/aggregate budget, recursive form preservation/read-only/
stale-child/depth/address behavior and three saved/fresh actual source form edits.
Managed control/template creation checks that null operands do not recursively
instantiate views. Native rendering/minimum-window acceptance remains deferred per
user; no physical instrument IO or new dependencies. PPMS script-expression import
and further workflow/data/controller/hardware parity remain incomplete.
