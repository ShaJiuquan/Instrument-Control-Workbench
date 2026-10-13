# Dedicated rotational homing

The read-only Rust `command/rotator.rs` dispatches zero_angle/rotator.zero to
`zero_home`; `drivers/instruments/rotator.rs` emits the dedicated `[z,1,1501]`
command, and the controller polls angle with a 30s deadline, strict 0.5 degree
threshold and 500ms polling. It is mechanical homing, not a coordinate offset
reset or ordinary rotate_to(0). Evidence was read at Rust commit 3576329.

The C# port now has an explicit optional IRotationHomingPort capability.
AxisControllerDefinition advertises it only for a bounded degree axis containing
zero. Session construction rejects an advertised profile without that dedicated
port; compiler preflight rejects unsupported axes before any connection. Existing
axis constructors remain compatible through the optional false default.

HomeRotationStep calls the owned dedicated operation and then polls fresh axis
snapshots. Its completion additionally requires the target to remain zero and
motion to be idle, as well as abs(position) strictly less than the authored
positive degree tolerance. Invalid units/polling intervals reject. The step itself
is explicit about having no intrinsic deadline; a native TimeoutStep supplies one.
PPMS zero_angle/angle_zero/rotator.zero imports use the captured 30s/0.5degree/500ms
defaults and reject unexpected params. Original source-line metadata and the native
home operation persist through codec/archive/CSV.

## Lifecycle

The Application session and shared run resources own admission, serialization,
start confirmation, cancellation and cleanup. The source parameter writer is not
used as a homing substitute. A run tracks an active homing operation: pause holds
motion, and resume reissues the dedicated home command rather than restoring a
zero target through SetTarget. The marker clears only after confirmed home
completion. Ordinary axis pause/resume behavior remains unchanged.

Cancellation, timeout, target drift and disconnected snapshots prevent the next
measurement. Terminal cleanup still calls the existing hold policy. Unconfirmed
hold retains the leased group until explicit safety recovery; simulation cannot
provide a hardware stop certification. A canceled start is still a touched owned
operation and must pass hold confirmation before release.

The configured ROTATOR simulation explicitly advertises and implements the
dedicated operation; the generic Angle catalog keeps its legacy false default
unless homing is explicitly requested, so delegating ports cannot inherit a
capability they do not implement. Like the
recorded Rust mock, it snaps to zero immediately. It does not model mechanical
travel, limit switches, encoder index, home direction or calibrated repeatability.
No hardware adapter or private controller address is added.

## Reproducible example

```sh
dotnet run --project src/InstrumentControlWorkbench.Cli -- import \
  --devices examples/devices/headless-routing.devices.json \
  --plan examples/plans/headless-import-context.plan.json \
  --ppms examples/plans/ppms-rotation-homing-import.json \
  --output /absolute/path/home.plan.json
```

The fixture starts an ordinary angle motion, then performs dedicated home, then
reads the completed angle. Run the saved native plan with the same simulation
manifest and archive/CSV destinations; it produces one zero-degree acquisition.
Tests distinguish home calls from normal target writes, capability/port mismatches,
strict boundary and still-moving rejection, target drift, canceled starts,
disconnection, abort, dedicated pause restart, failed hold/recovery and native
save/fresh archive/CSV. The graph outline names the operation; a dedicated homing
form and native visual/physical acceptance remain pending. No new dependency,
Rust write, real motion or desktop window action occurs. Full PPMS parity remains
an active goal.
