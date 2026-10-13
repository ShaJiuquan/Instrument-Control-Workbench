# SR830 active AC acquisition with registered isolation

This stage adds an atomic `ReadSr830AcFrameStep` to the existing independent C#
executor. It records raw X/Y/R/theta, input settings and actual reference
readback. The frame helper is metadata, not a child step. Existing passive
SR830 recipes and mocks keep their previous capability.

```mermaid
flowchart LR
  JSON[Native plan and device configuration] --> Compiler[Application preflight]
  Compiler --> Lease[Atomic group ownership]
  Lease --> Open[Registered paths open and readback]
  Open --> Config[Configure and confirm generator]
  Config --> Close[Connect and confirm declared paths]
  Close --> Settle[Interruptible settle]
  Settle --> Read[Frame with before and after checks]
  Read --> Isolate[Fresh open and readback]
  Isolate --> Row[Commit values and isolation evidence]
  Row --> Final[Fresh terminal isolation and group stop]
  Final --> Release[Release only after confirmed cleanup]
```

## Configuration and operator flow

Use `examples/devices/headless-sr830-ac.devices.json` with
`examples/plans/headless-sr830-ac.plan.json` for three simulated frames.
`SR830-AC-SIM` explicitly exposes the excitation port. The ordinary `SR830`
profile remains passive. Device configuration registers a separate generator,
complete disconnect set, card bounds, and relay amplitude/frequency envelope.
A recipe must match that registered generator and entire set. This stage permits
only one isolation matrix per generator; multiple independently controlled
sample paths require a later coordinated isolation design. Recipe flags
cannot grant isolation capability; generic matrix routing, required-route reads and ordinary DC output-enable
operations cannot bypass it.

The qualification envelope covers the complete declared SR830 sine output range
(5 V and 102000 Hz), so emergency opening is not conditional on the last
requested amplitude. This is a configuration declaration, not hardware
qualification. Existing DC guards must also confirm OFF before normal routing.
A matrix with only the dedicated AC duty can have no DC guards.

Active acquisition is authored through native JSON in this stage. The outline
identifies the atomic operation. A dedicated active form, automatic sensitivity,
first-point discard and sample resistance calculation remain later work.

## Evidence and cleanup

A successful row carries four ordered route observations: before configuration
open, before frame closed, after frame closed, after acquisition open. Reference,
IDs, complete connection set, frame time and quality times must agree with the
frozen step. Unrelated routes must remain identical. Archive reload validates
this evidence; CSV includes `sr830_ac_json`. Wide measurement groups and
calculated measurements retain their raw input evidence.

Pause opens the declared sample paths and restarts the whole attempt after
resume. It does not restore a previously closed AC route. Cancellation, drift,
frame failure or failed final-open discard that attempt's row. The terminal
cleanup obtains its own fresh proof even after a successful sample open.
Failed or still-running isolation retains all group claims. Explicit recovery
observes the stored task and may retry completed failures; it cannot overlap a
still-running relay operation. Passive acquisition abort alone does not prove
that the sine generator is electrically disconnected.

## Scope and acceptance

Only deterministic simulation and fault-injection adapters are exercised here.
No VISA/TCP SR830 or relay qualification is introduced, no physical experiment
is run, and native visual acceptance remains deferred at the user's request.
The evidence describes controller readback, not measured electrical continuity
or certified protection. Physical wiring and relay ratings require independent
verification before a real active adapter is enabled.

This is the active raw-frame foundation. Rust `curr_ac` current inference,
series-resistor calibration, auto gain, channel routing and `dI/dV` demodulation
are not claimed as migrated. Full PPMS parity remains an active objective.
