# Run-owned matrix routing

The C# backend implements a typed routing subset for the K3706 family. It is
available through `RouteMatrixStep`, plan documents and `DeviceRunResources`.
`IMatrixSwitchPort` is a Core port; the deterministic `MockMatrixSwitch` is the
only adapter in this stage. There is no SCPI/VISA or verified hardware support.
The published desktop does not yet register a matrix or expose routing settings.

A `MatrixSwitchDefinition` declares cards (slot, rows, columns) and every
excitation source physically associated with that matrix. A crosspoint contains
slot, row and column; protocol channel-string formatting belongs in a future
Infrastructure adapter. Card dimensions are configuration, not inferred model
limits. The demo uses one 4 x 6 card. All guarded devices must be registered and
use `OutputOff`; full-plan preflight includes them in atomic resource admission,
even when the plan contains only a routing step. A missing/invalid guard or
out-of-card point fails before connection or lease acquisition. Definitions
allow at most 64 cards/sources, and routes/snapshots at most 4096 points as software
allocation limits. Desired points must be unique; settle time is 0–1 day.

The application session captures a known initial closed set after connection,
once per owner. Reconnect never overwrites it. Unknown readbacks never expose a
cached closed set. If the initial snapshot fails, routing cannot start; cleanup
does not blindly open pre-existing connections. Transport claims perform no IO.

Routing automatically makes fresh output-off attempts for all declared excitation
sources, using the same independently timed cleanup tasks as the execution engine.
A failed/live/timed-out stop prevents relay writes. With excitation confirmed off,
the session opens current connections absent from the desired set, verifies they
are open, then closes missing desired connections and verifies the exact final
set. The settle interval runs in the interruptible plan executor. Routing leaves
excitation off; an explicit output step enables it after a fresh route readback.
This validates connection state, not electrode wiring or electrical topology.

Final cleanup starts ordinary device stops together, then permits relay cleanup
only after the declared sources have confirmed output-off. Axis holds can fail
without preventing matrix cleanup if excitation is off, but the entire group
remains reserved until every touched resource confirms its policy. Each new relay
cleanup refreshes prior successful excitation-off proof. It opens the current
closed set minus the initial closed set. It **does not reclose** a pre-existing
connection opened during execution. This preserves the Rust PPMS semantics in
`nested_experiment_service/safe_state.rs` at reference commit
`35763295f63b612f70af9dc55983644f437c18ea`, with observable failures and retained
ownership rather than best-effort error suppression. A direct public session stop
can confirm an already-clean matrix but cannot perform relay writes without the
resource scope's excitation-off guard.

Pause confirms excitation-off and retains/reads back the accepted matrix route;
it makes no relay writes. A held-route confirmation cannot release resources.
Resume verifies all held matrices, even if no source was previously enabled, and
checks again immediately before source enable. Changed/unknown routing faults
with excitation off. Aborting a paused run performs final relay cleanup. Live
uncooperative stop tasks are retained and never overlapped; recovery can retry a
completed failed task, with a fresh readback. A disconnected Hardware policy
cannot claim routing confirmation. These fault-injection tests are not physical
hardware acceptance or safety certification.

Version 1 plan documents encode `route_matrix` with `device`, `closed` objects
(`slot`, `row`, `column`) and `settleSeconds`. Saved run metadata includes cards,
guarded source IDs and initial connections; the full typed plan survives archive
reload and CSV export. `examples/plans/matrix-resistance.plan.json` is executed
from disk in tests and produces two coherent V/I/R rows on distinct verified
routes. The mock source resistance is independent of the matrix configuration;
no fabricated channel-specific sample physics is implied.

Typed sample/channel/electrode profiles and declared force/sense topology checks
now exist through ReadSampleResistanceStep (see sample-routing.md). Actual
card/protocol limits, physical wiring verification, routing UI, PPMS command
aliases/import, additional sources/meters and hardware fault acceptance remain
unfinished. The inventory marks `k3706.open_channel` and `k3706.close_channel`
partial: the typed reconcile operation supports these effects, not their existing
PPMS command encoding or complete driver profile. See `MatrixRoutingTests` and
`MatrixModelTests` for the implemented acceptance boundary.
