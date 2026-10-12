# Acquisition node forms

The existing execution/compiler/ownership boundaries remain unchanged. The native
node form now adapts `ReadSourceSenseStep`, `ReadResistanceStep`,
`ReadCurrentResistanceStep`, and either acquisition inside `ReadSampleResistanceStep`.
Three direct-acquisition kinds are also available as explicit palette templates.

## Operator behavior and boundaries

`PlanAcquisitionFormViewModel` is composed by the existing `PlanStepFormViewModel`.
It resolves selected source/meter IDs from frozen choices built from the same
execution registry as the compiler. Integrated reads require the K2450 output-off
profile; independent reads can select K2450 or K6221 and the passive K2182 profile.
Caller-supplied selection metadata cannot change actual profile eligibility.
Templates have no selected source/meter/reference and do not enter the plan until
the full candidate validates. No connection/driver/output operation occurs in a form.

Editable fields include source function, V/I/R result IDs with explicit units,
minimum absolute current in Ampere, independent current-reference selection,
optional constant expected programmed current, and optional terminal acquisition
failure policy (output ownership, settle and invalid-point continuation).
Sample wrappers also expose matrix settle, acquisition settle and external routing.
Independent samples cannot choose external routing or turn off retry output
ownership; domain validation independently protects programmatic changes.

K6221 offers only confirmed programmed setpoint as the current reference. Switching
independent sources clears reference selection and constant expected current, so
the operator explicitly chooses again. Original variable/qualified expected-current
expressions are retained; they remain editable through advanced JSON. Existing
sample/wiring/selection bindings, exact required matrix guards and environment
bindings also survive unchanged and are summarized in the form. Rebinding these
contexts remains later UI work; the existing complete native JSON/import paths
continue to support them.

All numeric text is finite and invariant; thresholds are nonnegative Ampere and
retry/sample durations remain in the timer budget. Duplicate result IDs,
environment collisions, source/preparation mode mismatches, incompatible retries,
required routes and complete resource/variable/budget constraints still fail at
the domain/full compiler boundary. No rule is weakened to accept UI input.

The existing node draft workflow supplies form/JSON exclusivity, preparation/run/
file/import locks, busy command-body checks, explicit discard and stale callback
rejection. Readonly state propagates into nested acquisition controls. The named
acquisition and independent views use compiled bindings inside the existing
scroll area, keeping apply/discard controls fixed at the minimum window size.

## Verification and remaining work

29 additional App cases exercise nine complete roundtrips, source-role/reference
resolution including forged selection metadata, finite/negative thresholds,
result/environment collisions, original variable/qualified expressions, source
mode conflicts, retry/sample ownership, mixed/busy/stale drafts and all three
palette kinds. Each palette case saves and reloads on fresh simulated devices,
executes one row with real units and verifies output-off. Sample cases edit the
repository's frozen four-wire catalog and both integrated/independent acquisitions,
reload the native document, execute three/one rows and confirm route provenance,
source-off and matrix cleanup. Independent sample fixtures supply the full exact
required electrode route; frozen catalog preservation is compared by document
content rather than object identity after codec reconstruction.

Native working-package1060x720 inspection selected an actual read_resistance leaf,
verified units/scrolling, negative threshold rejection/discard/locks, valid1e-12
apply, nine nested field/voltage rows with Completed/output-off, save/reload and
idle close exit0. Archive rows/checksum/terminal state/edited plan threshold also
match. Independent/source/meter/reference dropdown, sample/route/retry controls,
palette actions and clean committed-package parity are not claimed by that run.

Remaining: richer sample/wiring/environment/analysis UI, general expressions,
other PPMS functionality, real transports and physical acceptance. No dependency,
Rust source or physical lab IO change. Independent C# engine, GitHub-only
publication; Gitee remains deferred and the full migration goal stays active.
