# Native SR830 active raw-frame authoring

The plan editor now supports `ReadSr830AcFrameStep` with a native parameter form
and an explicit “SR830 主动交流原始帧” palette entry. The existing passive frame
and nominal input conversion remain separate operations.

## Authoring flow

1. Add an active raw-frame node and explicitly select the generator and isolation
   matrix. No device, input mode or result prefix is automatically selected.
2. Inspect the complete registered disconnect set. It is read-only; recipe edits
   cannot grant an isolation capability or rewrite its wiring.
3. Enter frequency in Hz, generator sine amplitude in Vrms, detection harmonic,
   expected input mode, result prefix and fixed settling time in seconds.
4. Apply the draft and validate the complete plan, then save and run.

`Registry -> MeasurementRunService -> ExperimentPlanWorkspace` provides an
immutable authoring directory filtered by the actual excitation/reference port
capability and registered generator/matrix mapping. Reading it performs no IO.
The runtime compiler remains authoritative. A missing profile or mismatched
saved path retains its original IDs and connections and blocks apply rather
than silently rebinding. Explicitly choosing another registered mapping uses
its complete connection set. Calculated-measurement body forms receive the same
capability directory and editing locks; passive nominal-conversion generation
is not offered for an active raw-frame body.

Loaded IDs, connection order, round-trip numeric values and TimeSpan ticks are
preserved. Settling input uses decimal seconds, without exponent/grouping
notation or precision below one runtime tick. Draft/apply/discard, JSON
conflicts, stale callbacks and busy/run locks use the existing editor transaction.
Authoring and validation do not connect or command equipment.

The input mode is an expectation checked against the device, not an input
configuration command. Generator amplitude is not measured sample voltage;
fixed settling does not prove signal stability. X/Y/R are raw voltage-domain
observations and theta is phase. This node does not produce calibrated sample
current or differential resistance. Template numeric values are explicitly
labelled examples, not measurement recommendations.

## Recent isolation readback

The plan page has a separate, collapsible “交流通路最近回读” panel. Matrix sessions
attach their existing immutable snapshot to `DeviceStatus.Matrix`, with matching
profile, connection state and timestamp. No extra device read is introduced.
Only typed controller facts determine declared-path open, closed, mixed,
unknown or disconnected state. `SafetyConfirmed`, a saved data row or a run
completion event never substitutes for routing observation.

Run-start/finalization fences invalidate prior observations; failed/aborted runs
and notification observation loss clear the display conservatively. Older or
duplicate facts cannot replace newer observations, and missing/foreign/future
observations cannot claim open. Readback updates do not dirty the plan or change
its editing locks. The panel shows the observation timestamp and explicitly
states that controller readback is neither electrical continuity verification
nor proof that run claims have been released. It describes a recent observation,
not a continuously measured contact state.

## Verification boundary

Basic and independent integration tests cover identity/precision, explicit
selection, metadata mismatch, malformed settings, full-plan column conflicts,
no authoring IO, locks/stale edits, saved three-frame simulation, fresh archive
and CSV. Typed status tests cover malformed facts, all route states and epoch
invalidation. Publication also runs the exact packaged CLI three-frame fixture.

The native XAML compiles, but desktop launch and visual/minimum-window acceptance
remain deferred at the user's request. Real adapters, relay/wiring qualification,
`curr_ac` inference/calibration/auto gain, differential measurement and broader
PPMS migration remain pending. See sr830-active-isolation.md for runtime safety.
