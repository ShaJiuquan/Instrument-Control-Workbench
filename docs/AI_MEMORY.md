# AI Memory

This file stores durable project decisions so future AI sessions do not
re-litigate them or accidentally undo them.

## Product Direction

- Native desktop application, not a web app.
- Portfolio-friendly public demo: no private lab protocols, addresses,
  credentials, or proprietary recipes.
- The app should feel like a scientific/industrial operator workbench: dense,
  calm, inspectable, and safe.

## Architecture Decisions

- Core owns domain types, validation, ports, and state machines.
- Application owns workflows, leases, safe-stop, cancellation, and
  notifications.
- Infrastructure owns driver adapters and protocol details.
- App owns Avalonia UI and composition.
- UI should observe and command application services; it should not drive
  hardware directly.

## Current Known Debt

- NotificationHub broadcasts to independent subscriptions. Queues remain
  unbounded; consumers must manage subscription lifetimes.
- LatestDataSet is the latest run; InMemoryRunHistory keeps terminal snapshots
  for this process. The injected FileRunArchive separately persists results.
  Live data and in-memory history still retain all rows; durable storage alone
  does not bound acquisition memory or resume hardware after restart.
- The mock driver does not model current compliance enforcement. Its readings
  must not be used as evidence of real hardware protection.

## Workbench UI (2026-10-10)

- Four functional pages: measurement, instruments, current run data, events.
- MainWindow composes named views; shared visual values live in Workbench.axaml.
- Visual direction follows the companion Rust/egui PPMS Studio: navy navigation,
  light workspace, purple accent, separate readout cards, left plot/right setup.
- Readout values and units are separate typography; Chinese glyphs use an explicit
  platform font fallback. No new fonts or plotting dependencies were introduced.
- Configuration can scroll at the minimum 1060 x 720 size while start/stop stay
  pinned; the plot and optional point table share the remaining vertical space.
- App startup constructs the driver/services and injects them into the ViewModel.
- MeasurementPlot draws received MeasurementPoint data with Avalonia's existing
  drawing API. It is a small I–V renderer, not a general analysis plotting library.
- Start validates text and explicit units; configuration locks during runs.
- Stop means request cancellation and wait for output-off notifications. There
  is no software button claiming to be a physical emergency stop.
- Closing the window awaits running commands and stops the notification listener.

These are tracked as design debt, not as permission to copy the pattern.

## Approved Dependencies

- Avalonia 11.x
- CommunityToolkit.Mvvm
- xUnit

Dependencies not listed here require a short evaluation and user choice before
implementation.

## C# execution migration (2026-10-10)

- The user chose an independent C# execution engine inspired by OpenTAP,
  not an OpenTAP package integration. Publish stage commits to GitHub only;
  Gitee synchronization is deferred. See ppms-migration.md and its inventory.
- IV runs own leases until output is verified off and result listeners finish.
  Cleanup failure/timeout retains ownership and requires RecoverSafetyAsync.
  Do not expire these leases on heartbeat TTL or report cleanup success merely
  because a stop command returned.
- Pause cooperatively stops output at a checkpoint and retains run identity and
  resources. Dwell timers react to pause. Pause/recovery UI is still migration work.
- A finalizer failure faults RunOutcome while retaining the acquired data set.
- Desktop manual configuration/start commands also check backend ownership.

## Device platform foundation (2026-10-11)

- DeviceRegistry uses IDeviceSession application contracts. InstrumentManager
  and AxisControllerSession share the same ownership gate; the legacy IV path
  and new multi-device path cannot bypass each other's active leases.
- Group lease acquisition is atomic. Claims do not touch hardware, so an
  admission failure rolls back its own claims and never stops a foreign owner.
- DeviceRunResources retains each pending stop task and requires fresh successful
  stop confirmation for all devices before release. Explicit recovery retries
  completed failures; output/parameter mutations invalidate previous success.
- Source stop means confirmed output-off. Axis stop means hold current position,
  not magnet zero or temperature shutdown. No hardware safety certification is
  implied. These simulation profiles are explicitly DriverKind.Simulation.
- Temperature rate is Kelvin/minute, field rate Tesla/second, rotation rate
  Degree/second. TimeProvider-backed simulators have no detached background task.
- Source-mode-dependent parameters require explicit mode/compliance selection.
- Actual transport adapters, remaining PPMS instruments, generic plan execution,
  persistent settings and device panels remain migration work.

## Source preparation safety (2026-10-11)

- InstrumentManager.EnterRunAsync requires a claimed run owner before output
  enable. Manual source output must also use an exclusive resource scope.
- ConfigureAsync confirms output is off before changing source mode/configuration.
  A connected instrument with output already on is rejected until safe stop;
  this rejection preserves its configuration and is not a driver-fault event.
- The follow-up regression gate passes 77 tests. Actual startup recovery UX and
  hardware transport acceptance remain pending.

## Group reconnect recovery (2026-10-11)

- A completed failed stop may require reconnecting its device before another
  confirmation can succeed. ConnectAllAsync allows that reconnect under the
  same resource owner; it does not permit output/parameter changes while an
  unresolved stop remains, and cannot overlap a live pending stop.
- Reconnection invalidates prior confirmation. StopAllAsync must confirm every
  device again before ReleaseAsync. A failed initial connect no longer leaves
  the owned group without a recovery path.

## Generic typed plans (2026-10-11)

- StartIvSweepAsync translates its recipe into an ExperimentPlan and uses the
  same interpreter, DeviceRegistry ownership and result lifecycle as StartPlanAsync.
- Plan validation checks every branch and scan value before admission. Variable
  values are range-checked at each write; an unbound variable is rejected before
  hardware. Step IDs and result column sources/dependencies must be consistent.
- Scan dwell is after the body. For-each index is 1-based and total is explicit.
  Sequence/repeat/scan/iterator state is run-local; pause retains the same run ID.
- Pause saves axis targets and enabled outputs, stops the group, then restores
  axes before re-enabling only outputs that were enabled. Stability time excludes
  pauses and its continuous stability window restarts after resume.
- Untouched claimed devices need no driver cleanup; once an IO attempt begins,
  fresh stop confirmation is required. A Simulation descriptor permits its
  offline model to confirm output-off/hold; disconnected Hardware cannot claim
  confirmation. Hardware-policy tests use a labeled fault-injection adapter,
  not real hardware evidence. RecoverSafetyAsync(reconnect: true) explicitly
  reconnects under the same owner before retrying completed failed stops.
- Generic rows use optional columns for different measurement branches, while
  present columns require all declared dependencies. No placeholder NaNs are used.
- The UI still offers the IV adapter. Generic plan authoring, PPMS file import,
  durable plans/results and device/pause/recovery screens remain future stages.

## Plan documents and source reconnect (2026-10-11)

- Version 1 Instrument Workbench JSON plans are distinct from PPMS's schema;
  do not claim PPMS file import/alias parity. System.Text.Json and async file IO
  use the existing SDK; no new persistence dependency was selected or added.
- Workspace preview/save validates the whole plan without leases or driver IO.
  File size, tree depth/node count and aggregate encoding allocation are bounded.
  Atomic temporary-file replacement preserves the old file on validation failure
  or cancellation before rename. No power-loss durability guarantee is claimed.
- The field-voltage repository example is loaded from disk in acceptance tests
  and produces 15 joint rows. Numeric metadata is supported; richer metadata,
  results/settings/history/export and restart recovery remain unfinished.
- Source Faulted can reconnect transport. A resource reconnect after failed stop
  retains a separate fresh-stop requirement; it cannot enable output or write
  parameters before successful stop confirmation. Pending live stops never overlap.
- WaitForFinalizationAsync waits through result listeners but does not abort or
  certify safety. A close workflow must separately abort and recover unsafe state.
- Desktop plan/pause/recovery changes remain under implementation until native
  layout and interaction inspection is completed. Do not infer visual quality
  from a passing build or ViewModel tests.

## Run archive API (2026-10-11)

- IRunArchive extends the existing result-listener boundary. FileRunArchive uses
  SDK System.Text.Json/FileStream/SHA256 only; no new dependency was added.
  The published desktop composition does not yet inject it; native plan/history
  integration is pending inspection while the Mac is locked.
- Each run has immutable versioned metadata, flushed NDJSON rows and a separate
  terminal marker. Plan, schema/units/dependencies, sample metadata, recipe and
  device configuration are captured. Row finalization reconciles committed
  application rows missed by cancellation and rejects changed committed prefixes.
- Catalog enumeration validates metadata, not the entire row stream. Load and
  export validate row sequence/schema/units and finalized length/count/SHA256.
  Reload defaults to at most 100,000 rows; CSV export streams larger archives.
- No terminal marker means unfinalized. Only an unterminated last row may be
  excluded. It never implies the devices are stopped, active or safe, and cannot
  automatically resume an experiment. Flush/atomic rename do not guarantee
  power-loss durability. See run-archives.md for precise limits.
- Export includes version, IDs, timestamps, units, sample metadata, full plan and
  instrument snapshots. CSV fields are quoted and can contain embedded newlines;
  readers must parse CSV records rather than skip fixed physical line counts.
- Acquisition/cleanup state in the data set is distinct from RunOutcome, which
  also reports listener failures. There is no persistence retry queue or deadline
  for finalizers. Settings, database queries and paged plots remain separate work.

## Resistance acquisition (2026-10-11)

- ISourceMeterPort exposes ReadSourceSenseAsync: actual source readback and sense
  value from the same acquisition, with explicit functions, typed units and time.
  Never calculate resistance using a configured source level as the numerator.
- ReadResistanceStep supports K2450 source Voltage/Current with opposite sensing
  function, explicit preparation, distinct V/I/R columns and nonnegative current
  threshold. It commits one schema-validated row, with axes/time and raw V/I as
  dependencies of Ohm resistance. Zero/near-zero current or non-finite ratio is
  fatal with safe-stop; no NaN/invalid placeholder is substituted.
- VoltageResistanceRecipe translates standalone voltage mode into the existing
  engine; nested plans read without reconfiguring source level or disabling output
  at each point. Source/sense config and user-selected two/four-wire mode are
  captured. Mock four-wire settings do not validate actual electrode wiring.
- Source level and compliance are checked together in Core and before live
  parameter IO. Static preflight checks fixed counterparts; when both change,
  actual configuration is validated at every write. Do not reject valid plans
  using stale initial compliance after a planned compliance change.
- The explicit Rust zero-current reference is f64::EPSILON =
  2.220446049250313e-16; C# double.Epsilon is a different value.
- PPMS matrix/channel routing, explicit source/meter ranges, K6221/K2182/SR830
  modes, sample topology, recoverable quality rows, aliases and physical device
  evidence remain outstanding. See resistance-measurements.md.

## Matrix routing (2026-10-11)

- Typed matrix definitions name configured cards and all guarded excitation sources.
  Preflight adds guards to atomic admission. Infrastructure owns the port/mock;
  Application owns baseline capture, source-off ordering and route reconciliation.
- Route operations park sources with fresh confirmation, open before closing and
  verify both stages. Explicit output enable verifies routing again. Final cleanup
  waits for excitation-off and opens current-minus-initial connections, never
  automatically reclosing old routes. Failed initial snapshots cannot authorize
  routing or blind open-all fallback. Reconnect preserves the original baseline.
- Pause keeps and verifies relays while excitation is off; held proof cannot release
  a group. Resume verifies all matrices, including when excitation was already off.
  A direct session stop cannot bypass guarded relay cleanup. Stop failures/timeouts
  retain all leases; a live stop task cannot overlap another operation.
- Documents/archives support route_matrix and matrix configuration snapshots.
  This remains a typed simulation subset; PPMS aliases, sample/channel topology,
  card protocols, routing settings/UI and hardware acceptance are pending.
  See matrix-routing.md for limits and reference semantics.

## Frozen sample routing and row provenance (2026-10-11)

- ExperimentPlan may freeze a RoutingConfiguration of channel connections,
  integrated source-meter ports and sample electrode links. Core resolution is
  pure and bounded; Application preflight resolves every referenced branch,
  validates cards/mode/source guards, then stores immutable per-step routes.
- ReadSampleResistanceStep reuses coherent V/I/R acquisition. Internal management
  parks sources, routes, settles, enables, reads and disables without resetting a
  scan's source level. External management verifies the exact accepted route and
  does not change relays/output. Actual sensing mode is checked before internal
  enable and again before acquisition. Resource routing copies mutable caller
  input before waiting for source-off.
- Two-wire sharing/dedup, four independent force/sense ports and sample terminals,
  Hall longitudinal/transverse and eight vdP raw assignments are tested. No derived
  Hall/vdP/geometry/offset result or sample-dependent mock physics is claimed.
- Sample rows retain typed immutable sample/wiring/selection/electrode provenance;
  append/archive read/write validate supplied contexts against the frozen catalog.
  Archives include context in prefix checks; catalog CSVs append routing_json.
  Legacy rows/plans/CSV layout stay unchanged. Optional context absence in an
  unfinished row alone cannot prove acquisition identity.
- Source/meter combinations beyond K2450, PPMS import/active profiles, durable
  settings/profile UI, derived quantities/quality and hardware acceptance remain
  required. See sample-routing.md for the exact boundary.

## Grouped sample analysis

- The raw sample API remains; analyzed Hall/vdP plans add one summary after every
  complete ordered group, through the existing engine/lease/cleanup path.
- Geometry is optional positive meters. Signed Hall geometry and mean-absolute vdP
  sheet/resistivity analysis follow captured PPMS behavior; field reversal,
  corrected Hall coefficients, offsets and instrument-quality propagation remain
  pending. Availability issues explicitly omit unavailable quantities.
- Summary provenance includes group step, consecutive input range, issues and
  algorithm version 1. Live append/archive write/read share a bounded eight-row
  validator; new CSVs retain those references. Derived recomputation uses relative
  tolerance 1e-12 for platform math; finalized bytes retain SHA-256 checks.
- All evidence is simulated/numeric. See sample-analysis.md and the Hall file
  acceptance. Full migration and native/hardware acceptance remain incomplete.

## Coherent general/IV source-sense acquisition

- ReadSourceSenseStep uses a single typed port frame in both source functions;
  explicit preparation/sensing mode is required. Zero current is valid without
  resistance division. IV preserves its requested voltage axis and emits plot
  points from actual V/I and the frame timestamp.
- Acquisition compares before/after source, sense and terminal snapshots before
  committing the row. Pause captures full source settings; resume checks all
  sources before axis restoration and checks each again before enabling output.
  Configuration drift rejects restoration and enters normal cleanup.
- Documents/archives/CSV retain the new kind and measured/requested columns;
  legacy ReadStep remains sequential. The field/current disk example verifies
  nine nested rows. Port coherence and snapshot checks are software contracts,
  not proof of physical atomicity, compliance limiting or an interlock.
- See source-sense-acquisition.md. Standalone meters, range/quality profiles,
  PPMS command import, durable configuration, UI and hardware remain pending.

## PPMS runtime-tree import

- IExperimentPlanImporter is a real Core format boundary used by the Application
  workspace for import/preflight; Infrastructure translates PPMS runtime JSON
  into existing plans. No direct importer-to-driver path or second interpreter.
- Bindings freeze explicit instrument instances, axis definitions, source
  preparations and channel crosspoints. Unknown/default active routes and
  unmapped devices are not guessed. An off-only command does not apply an unused
  preparation. Source configuration parks zero before compliance changes and
  source scans configure their first level before output enable.
- Source-line metadata persists through native plans/archive/CSV. Repeat's
  default three cycles, pre-child delays, PPMS spacing/single-point and segment
  first-point removal are retained. Generated wrappers and aggregate points have
  budgets as well as the raw tree. Invalid fields/functions/ranges/routes fail
  before IO; workspace capability preflight is still required after parsing.
- Controller-status waits, persistent mode,
  remaining BT semantics, remaining resistance modes/persistent profile editing, ranges/filtering,
  separate meter families, text DSL/database export and hardware remain required.
  See ppms-plan-import.md and the six-row field/voltage import acceptance.

## Software temperature control follow-up

- The independent engine now executes SoftwareTemperatureRampStep. A real
  ITemperatureControlPort capability is required; generic axis targets are not
  treated as sufficient proof of temperature control. Hardware rate writes use
  its explicit mode-setting/confirmation path.
- Live readback is validated before control mutation. Preparing software mode
  parks at that origin before enabling control. Read failure has no cached/target
  fallback. Each target write confirms hardware-ramp-off/control-on and target
  readback. Positive rates use the current Rust one-second integer-millikelvin
  algorithm; direct sets remain unrounded. Minimum rate quantization is explicit.
- Pause holds current temperature and retains the group. Config drift rejects
  restoration before source enable and invalidates prior hold proof for fresh
  cleanup. Failed holds quarantine resources until explicit recovery. Cleanup
  holds temperature, never implies heater-off, zero kelvin or automatic cooldown.
- PPMS sets support software/hardware/auto and direct-no-rate semantics. Step
  temperature scans translate to iterator + ramp + numeric stability + children.
  The 12-row temperature/field/routed-voltage file saves, archives, reopens,
  re-executes and exports. See temperature-control.md. Actual firmware states,
  continuous/cooling/background modes, disabled control and hardware remain
  incomplete; the full migration goal stays active.

## PPMS resistance and sample import follow-up

- Runtime resistance aliases translate into native coherent V/I/R. The verified
  `volt_dc` source/meter family is K2450; curr_dc/delta/pulse_delta/AC still require
  their own device roles. Mode defaults remain curr_dc, which rejects instead of
  silently converting to volt_dc. Finite numeric strings and declared parameter
  aliases are accepted; conflicting aliases and invalid requests reject before IO.
- ImportSampleRouting freezes the catalog, mode-wiring map, aliases and route
  settle. IDs/numeric slots/unique names resolve deterministically. No active
  profile files are guessed. Topology and preparation sensing mode must match.
  Supplied 4-terminal profiles are simulation evidence, not proof of the bundled
  PPMS two-terminal K2450 hardware wiring.
- Owned channel/source settings park zero before compliance; sample group routing
  and source cleanup stay in the engine. A separate post-enable AcquisitionSettle
  now uses pause/cancellation-aware dwell and re-verifies routing before sampling.
  VerifyMatrixRouteStep offers the same explicit read-only route condition in
  native plans/documents.
- Imported channel reads beneath the same source scan inherit its channel and
  source ownership, verify route and retain current level/compliance/output.
  Sample measurements reject enclosing scans of that same source, consistent
  with their required cleanup ownership. Explicit managed reads need source
  enable earlier in the same admitted plan; they do not adopt a manual output.
- The two-field/three-sample file produces 26 acquisition/analysis rows plus two
  scan-point summaries (28 total), including full Hall/vdP
  summaries and raw route identity; disk plans, fresh archives, re-execution and
  CSV pass. Partial/faulted groups retain raw rows without fabricated summaries;
  original and cleanup failures keep resources for recovery. See
  ppms-resistance-import.md. Persistent profile editing, remaining acquisition modes,
  remaining environment controller semantics, settings/UI and hardware remain required.

## PPMS routing configuration import follow-up

- IRoutingProfileImporter is the format/file boundary. Workspace loads three
  current PPMS routing files into RoutingProfileCatalog; parsing/binding has no
  driver IO. All physical wiring layouts and mode maps are retained, including
  unimplemented independent source/meter families. Only explicit known device
  bindings become executable native wiring; they do not certify those families.
- Catalog lookup accepts authored keys/IDs/names/aliases, numeric/channel forms
  and channel __active__. Ambiguous candidates reject, including sample IDs that
  conflict with a numeric slot. No expanded alias dictionary consumes the 64
  channel budget. Applying profiles rejects competing routing authorities.
- Native channel/sample/electrode/wiring models and version-1 documents preserve
  authored descriptive fields, original IDs, labels, connection display row/raw,
  notes and spacing. Execution uses wiring rows plus enabled connection columns;
  display addresses are never a missing-column fallback. Hall/vdP algorithms do
  not reinterpret spacing. Empty new fields omit, preserving old encodings.
- Format budgets: each file 4 MiB/depth32/10000 values; catalogs/maps/lists64;
  text8192 with existing native name/ID bounds. Unknown/duplicate schema data and
  invalid/missing links reject. Async reads honor cancellation, BOM/strict UTF-8
  and growth bounds. Files are read sequentially, not atomically across writers.
- All four channel roles, polarity separation and four-wire independence are
  enforced. Selected volt_dc channel wiring must match the authored mode map;
  whole-plan card/device/sense/resource preflight still precedes connection.
- The profile fixtures are newly authored simulation data. The field/sample tree
  now retains 26 original records plus two scan points (28 total), imports with
  these files and saves/reopens/re-executes
  native plans and archives with metadata. Local read-only actual-file acceptance
  parsed six channels/samples/wirings and eight modes and round-tripped the native
  plan without hardware. Bundled K2450 shared force/sense ports are preserved.
  Settings/profile editing, remaining modes/runtime flags, UI/physical transport
  and hardware still require work. See ppms-profile-import.md.

## Scan-point measurement aggregation follow-up

- MeasurementGroupStep executes labeled members through the existing interpreter.
  Scan import collects consecutive measurements and unwraps scan-local parallel
  wrappers in authored order. General parallel IO remains unsupported. Single
  measurements keep their existing row layout; non-measurements flush collection.
- Raw rows and existing Hall/vdP analysis remain independent evidence. Complete
  wide points copy member final results and first-input timestamp/elapsed, with
  one-based contiguous input ranges and collision-safe repeated display labels.
  Distinct result columns, 2..64 members and <=1024 inputs are enforced.
- RunDataRowValidator validates live append, archive writing and streaming reads;
  it retains at most 1024 recent inputs. Forged values/time/labels/ranges and mixed
  provenance reject, including unfinished archives without terminal checksums.
  CSV includes optional measurement_group_json and preserves old exports on error.
- Pause/resume preserves collector state. Abort/fault retains committed raw rows
  and earlier points; read-plus-stop faults retain both errors and recovery
  ownership. This is simulated acceptance, not physical acquisition simultaneity.
- The field/sample fixture now yields 28 rows: 26 original records plus two
  complete scan points. General BT parallelism,
  persistent editing/settings, visual acceptance and hardware remain required.
  See measurement-groups.md; the full migration goal stays active.

## Fresh environment observation follow-up

- Current Rust automatically reads temperature/field/angle with each measurement;
  record_* flags are legacy auxiliary-node authoring conveniences. PPMS imports
  now attach all three roles, with explicit unavailable observations for unbound
  controllers. Context policy persists in native reads and reserves bound axes.
- Native primary reads, paired V/I and resistance accept typed optional context.
  Preflight checks actual controller readbacks/units and consistent column policy;
  target substitution rejects. C# resource IO is serialized and context follows
  electrical acquisition, unlike Rust concurrent futures. No physical atomic
  sampling is claimed. Each observation retains actual value or bounded error,
  source and software UTC completion time. Optional errors never become NaNs,
  zero, cache values or targets; successful electrical rows remain usable.
- Cancellation aborts an unfinished capture. Cleanup is independent: controller
  hold failures still quarantine resources until explicit recovery. Environment
  error-as-data does not convert stability/interlock failures into success.
- Every Hall/vdP electrode row has its own observations; derived summaries retain
  the first input context. Wide points preserve final member context and retain
  original raw/analysis input ranges. Shared row validation checks policy, source,
  value/error and exact summary observations/time in live/archive/streamed CSV.
- Optional new environment fields omit for old native plans/rows; CSV conditionally
  adds environment_json. Old readers can reject extensions. Six-row, twelve-row,
  mixed-sample/profile and new six-row field-point fixtures save/archive/re-execute;
  hardware/quality/remaining modes, durable settings and native UI remain pending.
  See environment-acquisition.md. The full migration goal remains active.

## Native routing document and offline edits

- Core IRoutingProfileDocument separates authored physical catalogs from bound
  run configurations. Application RoutingProfileWorkspace owns offline use cases;
  Infrastructure SDK JSON/file IO implements version 1 bounded persistence.
  Shared channel/sample native codecs preserve existing plan compatibility.
- Typed upsert/delete/active/mode operations rebuild the whole immutable catalog;
  active keys and electrode/mode references validate before save. No silent delete
  cascade, driver operation, fabricated combined source/meter or device ID.
- 12 MiB/depth 32/100000-value budgets, strict UTF-8/BOM and duplicate/unknown
  fields; temporary sibling save/flush/rename preserves previous data on failure
  and aggregates cleanup errors. Last writer wins; no CAS or power-loss promise.
- Native profile fixture round-trips all PPMS fixture metadata/unbound layouts.
  Saved shared ports still reject four-wire preflight before connection. A run
  paused after acquisition can coexist with offline save; its 28 archived rows
  keep old names, fresh profile load sees the edit, fresh archived plan executes.
- Device settings, PPMS write-back/database, additional runtime flags/families,
  native profile UI and hardware acceptance remain required. No UI files included
  in this backend stage; see routing-profile-workspace.md. Goal remains active.

## Native device settings and startup runtime

- Core immutable DeviceConfigurationCatalog owns explicit identity/category/name,
  optional selected enabled source meter, enable states, typed simulation/TCP/VISA/serial
  connections, aliases and optional matrix cards/guards. Whole-catalog references
  validate; enabled duplicate physical endpoint declarations reject. No DNS or
  cross-transport/cross-process equivalence guarantee.
- DeviceConfigurationWorkspace offline CRUD/primary/alias and native file port
  never call drivers. SDK JSON v1 uses 1 MiB/depth 16/50000-value budgets, strict
  fields/UTF-8/BOM, positive timer-range tick-exact timeouts and atomic sibling
  replacement/cleanup-error aggregation. Last writer wins; no CAS/power-loss promise.
- PPMS explicit-file importer retains eight current connection keys/fields and
  all hardware kinds. No DB/env/default lookup, silent invalid-entry fallback or
  auto-mock fallback. Matrix cards/guards and environment controller definitions
  are supplied explicitly after import. DB/env precedence remains migration work.
- ConfiguredDeviceRuntime validates every enabled factory profile before any
  construction, checks identities/kinds/axis units/matrix declarations and wraps
  typed Core ports in existing Application sessions/registry. No constructor IO or
  lease acquisition; existing live runtime remains immutable during offline save.
- Default factory supports simulated K2450/K3706/three axes, multiple distinct
  identities/names and disabled hardware templates. Unsupported physical drivers
  reject; native K2450 TCP support is recorded below, while VISA/serial and other
  physical models remain unsupported. Published desktop
  composition/operator controls still require integration and native acceptance.
- Device tests execute/import/archive the 28-row field/sample fixture from a fresh
  native config, edit/save while paused, preserve old source identity and apply
  secondary source at fresh startup. Existing output/route cleanup and lease
  lifecycle remain in use. See device-configuration.md; whole goal remains active.
- Local read-only acceptance at unchanged Rust 35763295f63b612f70af9dc55983644f437c18ea
  parsed the actual bundled data/instrument_connections.json: eight devices,
  three TCP, four VISA and one serial. Native round-trip matched; no construction
  or hardware IO and no original source/config changes. No lab addresses copied
  into the independently authored fixtures or published documentation.
- Null primary supports axes-only configuration/startup/manual/resource cleanup
  without constructing an unused SMU. MeasurementRunService now accepts
  the registry directly for generic execution. Native null primary and partial
  rotator-only import are verified; legacy IV/resistance shortcuts still need an
  explicit primary selection.

## Source-independent execution

- Generic MeasurementRunService construction accepts DeviceRegistry/NotificationHub
  and listeners, with no placeholder InstrumentManager. The same compiler,
  admission, interpreter, pause/resume, stop/quarantine/recovery and finalization
  path executes axis-only, explicitly bound SMU and resource-free control plans.
- Legacy IV/resistance convenience recipes retain selected-source behavior and
  reject missing selection before IO. Run lifecycle notifications use only a
  participating legacy primary or the sole actual resource; multi-resource generic
  and resource-free runs have null InstrumentId and the actual RunId. Per-device
  notifications/snapshots retain their own IDs. An unrelated primary is untouched.
- Independently authored axes-only config/native scan and PPMS import fixtures
  execute 6 joint rows and 24 rows (six groups of three raw plus one wide row),
  persist/reopen/export and replay on fresh runtimes with no SMU. Twelve acceptance
  cases cover pause/resume/abort, independent read/hold faults, retained group
  claims/recovery, listener finalization/error, pre-cancel/preflight and foreign
  live-source exclusion. See source-independent-execution.md.
- This is simulated/backend acceptance. Native composition/editor acceptance,
  hardware transports, DB/env precedence and other migration inventory work remain;
  the whole goal remains active.

## Configuration-driven workbench startup

- DeviceConfigurationStartup resolves native saved settings and returns their
  immutable validated document/origin, runtime and matching existing executor.
  Explicit initial configuration applies only to FileNotFound/DirectoryNotFound
  and is never auto-saved. Existing malformed/access/unsupported hardware errors
  reject with no simulation fallback. Whole-set factory prevalidation and null
  primary semantics remain in use; no constructor driver IO or leases.
- Listener input materializes once. Saved primary controls legacy shortcuts,
  while null primary uses the generic engine. Fresh startup sees saved edits and
  existing runtime stays unchanged. Fourteen acceptance cases cover these paths,
  pre/post-load cancellation, native six-row axes plan/archive, finalization errors
  and archived source/axis/matrix display names after offline edits.
- All three resource families now store authored display_name in their immutable
  instrument snapshots, preserving hardware/model identity and archive reload.
- Local App draft integrates async startup, real registry device list/status,
  null-primary plan UI, offline settings with save/restart/export distinction,
  close drain and manual disconnect. Eight new ViewModel tests cover resources,
  failed-close retry, selected-instance facts and paused settings isolation.
  Native 1060x720 acceptance remains blocked by freshly observed locked Mac;
  all App/App.Tests changes are excluded from this backend stage. See
  configured-workbench-startup.md. Whole migration goal remains active.

## Native K2450 TCP protocol stage

- Infrastructure TcpScpiTransport uses SDK sockets only: no constructor IO,
  explicit open, serialized bounded ASCII LF/CRLF exchanges, per-exchange timeout,
  rejection of surplus/unrequested bytes and close on dispatched failure/cancel.
  Queued cancellation preserves active owner; async disposal drains active IO.
- Native-SCPI-only TcpKeithley2450SourceMeter checks full manufacturer/model
  identity and language without reset/mode change, reconciles actual registers,
  validates Core coupled limits before writes, chooses valid intermediate write
  order and requires exact setting readback. Output-on function change rejects.
  Both source functions enable their own actual readback for one source/sense pair;
  format/count and device errors confirm before rows. NaN/overflow/malformed data
  never become fabricated observations. Disconnected hardware status values are
  absent rather than presenting initial/last-known parameters as live facts.
  Hardware parameter capture also rejects while disconnected, both before initial
  connect and after interrupted IO; cached values cannot enter a new snapshot.
- The configured factory registers only K2450 SourceMeter TCP with LF/CRLF.
  Other enabled unsupported hardware still rejects the full set before creation.
  Independently authored loopback template is disabled/null-primary; no lab IO.
- Forty-four focused cases cover transport edge/fault paths, identity/mode rejection,
  both functions, coupled writes, errors/readback refusal, live mode/disconnect
  guards, pause/resume/abort/archive, group stop refusal and reconnect recovery.
  Saved native config/plan startup executes via selected-primary and generic
  engines and reopens actual hardware identity/source data. Protocol tests are
  not hardware certification; hardwareVerified remains false.
- SCPI behavior checked against official 2450 reference manual E, while current
  Rust k2450_reads/writes and TCP runtime were inspected read-only at unchanged
  35763295f63b612f70af9dc55983644f437c18ea. No Rust edits, private lab addresses or
  vendor manual redistributed. Exact write equality rejects physical rounding;
  manual ranges, autozero/filter/quality flags, other physical families and native
  visual acceptance remain work. See k2450-tcp.md. UI changes are excluded from
  this backend stage and the whole migration goal remains active.

## Typed K2450 source ranges

- SourceSetpoint nullable ManualRange and SourcePreparation optional
  ManualSourceRange use typed canonical nominal quantities. Core exposes immutable
  choices, unit/full-scale/headroom and range-limited compliance validation.
  Source range mode is distinct from level/compliance and sense autorange.
- Existing source descriptors/sessions provide manual V/A range read/write,
  source autorange mode read and explicit enable-auto write action. No new port,
  dependency or executor. Range changes require observed output off; ordinary
  level/compliance updates preserve actual fixed range. Disconnected hardware
  observations remain absent. Mock and TCP apply the same declared limits.
- Compiler checks declared range support, literal choices, fixed-range points and
  range-limited compliance independently of counterpart changes; range-changing
  plans/dynamic values retain actual per-write checks. Native v1 optional field is
  omitted for auto; strict older readers may reject manual-source additions.
- PPMS set/scan voltage/current and source-owned volt_dc accept auto or positive
  finite number/string selectors, canonicalizing upward to nominal full scale.
  Manual scan points/power settings preflight before IO; invalid selectors retain
  path/line errors. Managed resistance inherits enclosing source settings.
  Source-range aliases work for routed sample acquisition. Sense ranges stay pending.
- TCP freshly reconciles mode/manual range and rejects requested/actual mismatch.
  Changed ranges cannot be applied live; unchanged manual ranges survive scans.
  Source equality now includes range, so acquisition drift discards the frame and
  pause drift prevents output restoration. Existing cleanup/ownership is unchanged.
- Forty-five new cases (22 Core, 23 Application) cover all nominal boundaries,
  power/units, native fixture/old auto format, both TCP functions, save/archive/CSV,
  actual/manual parameter actions, no-IO preflight, dynamic fault cleanup,
  setting refusal/live change, pause/acquisition drift and PPMS imports. No lab IO.
  Run snapshots capture initial after-preparation settings; later planned writes
  are not per-row actual range evidence. Automatic selected full-scale, sense
  range/quality, native editors and hardware certification remain pending. See
  source-ranges.md; all App/App.Tests stay excluded; full goal remains active.

## Typed K2450 sense ranges

- SenseConfiguration optional typed ManualRange is distinct from source range;
  true AutoRange plus a manual value rejects. Immutable nominal voltage/current
  spans and observed resistance spans use active-function units. Complementary
  manual V/A selection supported; resistance span is read-only until ohmmeter
  source strategy exists. Legacy false/null retains an observed matching register.
- Core ValidateConfiguration checks source/compliance, >0.1%..105% fixed-sense
  bounds and source/sense nominal power combinations. Compiler validates literal
  choices/capability/static fixed-span changes; actual per-write checks remain for
  dynamic/range-changing plans. Native optional sense.manualRange omits when absent.
- Existing sessions/ports/descriptors provide active manual sense V/A read/write,
  observed R span read, sense auto mode read and explicit enable action. Registry
  ownership denies manual writes during run/pause. Live owned span changes are
  supported and require unchanged actual source settings/output/terminals.
- TCP freshly reconciles span, confirms writes and rejects implicit compliance
  adjustment. Initial parameters capture actual mode/span; single/paired reads
  discard configuration drift and pause drift denies output restoration.
  Preparation clears old fixed sensing only while off before new source settings.
- PPMS standalone meter and volt_dc channel/sample/managed import accept finite
  numeric/string selectors and aliases. Owned source clears old fixed sensing,
  managed acquisition preserves source state, defaults explicitly enable auto.
- 62 new cases (27 Core/35 Application) verify tables/limits, both source modes,
  save/archive/CSV, legacy actual retained settings, literal/dynamic guards, live
  writes/refusal/implicit source changes and retained failed-stop recovery, drift,
  consecutive runs, imported range aliases and diagnostics, overflow and native
  combined-range fixture. Existing sample range test verifies both fixed ranges.
  No lab IO. Full per-frame ranges/status/quality and automatic selected full
  scale, ohmmeter selection, other families/UI/hardware remain required.
  All App/App.Tests stay excluded; whole goal remains active. See sense-ranges.md.

## Acquisition quality stage (2026-10-11)

Native K2450 single and paired reads now preserve same-buffer source/measurement
status. Capture mismatch, protection and overtemperature fault before row commit;
finite limit/questionable/unknown-bit warnings persist through resistance, sample
analysis, wide points, archive and CSV. Missing status is explicitly unobserved;
legacy rows remain readable. Initial settings and later queried autorange spans
are not per-reading range evidence. See [reading-quality.md](reading-quality.md)
for policy, compatibility and remaining hardware/UI limitations.

## Acquisition profile stage (2026-10-11)

Optional typed autozero/rebound/Repeat-Moving profiles now retain actual target
function settings when unspecified. Active-function parameters, guarded owned
writes and off-only completed zero refresh use existing lifecycle boundaries.
Single/paired reads preserve confirmed per-row profiles separately from buffer
status; analysis/archive/CSV inherit them. PPMS filtering imports runtime Repeat
semantics and retains unused voltage window metadata. No dependency, Rust edits
or lab IO; native UI and physical acceptance remain pending. See
[acquisition-settings.md](acquisition-settings.md).

## Standalone voltage meter stage (2026-10-11)

A model-specific passive K2182 port/session and explicit simulation now share
registry ownership, abort-acquisition cleanup, pause confirmation and archive
boundaries with the source executor. Optional per-row meter settings never
claim SMU buffer quality. Native two-input plans and direct PPMS input-1 voltage
filtering are supported; physical transports, curr_dc resistance, routed meter
reads and native UI acceptance remain pending. See [standalone-meter.md](standalone-meter.md).

## Independent current resistance stage (2026-10-11)

The independent C# executor now supports source-plus-K2182 `curr_dc` resistance,
with explicit programmed-current versus measured-source-readback references,
separate acquisition evidence, guarded matrix verification, managed scan-current
binding, wide-point aggregation and validated archive/CSV. Real K2182 and K6221
drivers, automatic independent sample wiring/analysis, retry-to-invalid-point
semantics, native UI and lab acceptance remain pending. See
[current-resistance.md](current-resistance.md); full goal remains active.

## Pure K6221 DC current-source stage (2026-10-11)

Dedicated settings/snapshot/Core port, Application session and explicit simulator
now support K6221 DC output without SMU sensing. Native preparations, current
parameters/scans, owned/managed PPMS curr_dc and basic concrete K6221 commands
share lifecycle/leases/pause/output-off/recovery. Model-tagged independent
resistance evidence uses the actual family's constraints and validates through
wide/archive/CSV, retaining legacy K2450 archives. Physical transports, advanced
modes, independent sample routing/analysis, retry-to-invalid-point semantics and
UI/lab acceptance remain pending. See [k6221-current-source.md](k6221-current-source.md).
All App/App.Tests drafts remain excluded; the full goal stays active.

## Independent current channel catalog stage (2026-10-11)

Catalog binding preserves K6221 preparations and standalone voltage meters.
Optional channelVoltageMeter explicitly binds the selected source/meter/matrix;
curr_dc aliases and mode maps resolve without SMU sensing assumptions. Owned
channels and managed current scans use native steps and existing safety/ownership.
32 new cases cover both source families, authored three-file profiles, role/port/
mode errors, drift, retained failed-stop claims and native/wide/archive/CSV.
Independent sample/Hall/vdP, real drivers, retry semantics and UI/lab acceptance
remain pending. App/App.Tests drafts remain excluded; the full goal stays active.
See [independent-channel-routing.md](independent-channel-routing.md).

## Independent current DC sample stage (2026-10-11)

Two-/four-wire/Hall/eight-permutation vdP acquisition shares the existing sample
executor and pure math through a metadata-only ResistanceAcquisitionStep boundary.
Frozen wiring/context retains explicit independent meter identity; samples own
cleanup and target observed input1. Native kind read_independent_sample_resistance
wraps the ordinary current acquisition, with unchanged integrated encoding.
Complete analysis preserves every independent record/quality, common observed
source/meter configuration and environment; native/wide/archive/CSV validators
reject mismatched/missing/reordered evidence. Owned PPMS sample aliases and four
source/sample/meter/route fixtures run30 rows overtwofieldpoints, reload/re-execute/
export from fresh archives.48 new cases cover11 Core+37 Application. The standalone
meter's three-argument observation interface must stay implemented: extra sample
input enforcement is a separate four-argument overload, avoiding loss of meter
configuration evidence through the default interface implementation. Real ports,
retry/invalid points, advanced modes and remaining UI/lab/PPMS remain unfinished.
All App/App.Tests drafts remain excluded. See independent-sample-resistance.md.

## Independent current acquisition retry stage (2026-10-11)

Explicit native CurrentResistanceRetry policy and PPMS curr_dc import now allow
one retry of terminal K2182 voltage ReadTimeout/BufferTimeout/-221 errors only.
The actual port-read boundary excludes snapshot/configuration/route/source errors.
Owned source-off plus meter-abort or managed meter-only cleanup uses attached
bounded stop tasks; live/faulted cleanup cannot overlap retries or release claims.
Fresh source/meter/route equality precedes restore/read. Retry dwell must use the
ordinary pause checkpoint outside the resource gate to avoid deadlock; leases
stay held and fresh full configuration checks follow. Successful attempts retain
first failure, observed meter settings and cleanup times through raw/sample/wide/
archive/CSV; exhausted retry preserves both errors and faults, with no invalid
or fabricated zero row.54 new tests (10 Core44 Application). Physical ports,
recover-invalid-point semantics and full UI/PPMS remain unfinished. App drafts
remain excluded. See current-resistance-retry.md; full goal remains active.

## Invalid current DC points and available-input analysis (2026-10-11)

Native policy adds explicit ContinueOnAcquisitionFailure=false by default;
PPMS curr_dc enables it. Only terminal typed K2182 voltage acquisitions recover
(overrange/read-buffer-timeout/invalid-response/VISA-read/-221/-241/419/301)
after fresh attached bounded cleanup and exact source/meter/route/input checks.
Source-side failures, drift, parameters, snapshots and cleanup stay fatal;
cancellation terminates without an unfinished row. First and final read errors remain with cleanup errors; a typed read racing
cancellation becomes OperationCanceledException retaining the original cause.
Completed stop proof is consumed before new observation, so unknown state cannot
reuse it. Invalid rows retain axes/elapsed/environment and separate immutable
IndependentResistanceFailure with source configuration, settings, error/status,
chronology and optional retry; V/I/R and successful quality are absent. Remove
runtime variables and reject optional/stale preflight dependencies. Complete
ordered sample groups use each committed row, nullable Ohm input math and explicit
FailedAcquisition; Hall preserves valid longitudinal geometry independently of
transverse, vdP needs all four inputs for each mean and both for sheet. Raw/sample/
wide failure collections and kind-derived status round-trip native/fresh archive/
CSV; tamper rejection preserves previous exports.68 new cases (18 Core50 App).
Physical ports, source-side/other modes and full UI/PPMS/packaging/lab remain
unfinished;29 UI drafts excluded. See current-resistance-invalid-points.md.

## Independent device commands and per-input settings (2026-10-11)

PPMS K2182 NPLC/range/filter and K6221 source-delay commands now use existing
native parameter execution. Input selection is an explicit Core port operation:
observe target range/filter instead of copying/resetting prior input, shared
NPLC retained; simulation keeps both input profiles. Optional filter controls and
flat/nested aliases match Rust; unknown/ambiguous/fractional/overflow inputs reject.
Actual-input expected-voltage selector is distinct from exact native manual span.
Configuration-only runs emit no rows. Source delay is an optional second register
with native/archive successful/failed current-excitation evidence, not software
settling; missing preparation retains observed state and legacy unknown remains
absent. Register-only import never applies unused current/compliance preparation.
Read/pause drift rejects, manual selection/write is denied during run/pause, and
cancel/refusal/failed cleanup preserves the existing whole-group safety lifecycle.
Published command and corrected per-input scan examples verify later raw evidence.
No dependencies, Rust edits, lab IO or App drafts changed. Physical transports,
advanced modes, full controllers/BT/background/settings UI/plots/packaging remain;
whole goal active. See independent-device-commands.md.

This stage adds45 cases (9 Core36 Application); full working1377 passed.
Mandatory commit and exact archive audits are required for every publication.


## Number/time iteration scans (2026-10-11)

PPMS scan_number and scan_time/time_s now reuse native ForEachStep, with explicit
Core IterationTiming (default Immediate, optional DelayBeforePoint requiring
bounded seconds). Time values are per-point waits before children, as current
Rust scan_iter.rs code actually executes, not absolute elapsed deadlines. Number
values nonnegative/near-integer rounded; flat/nested list/step/count/segments,
strict controls and source-line diagnostics. Index/total and nested optional axes
persist; existing elapsed column remains actual wall time. Existing Dwell pause
budget, output-off/hold/group claims, cancellation, listener fault and retained
failed hold/recovery verified. Native version1 optional timing defaults/unknown
rejection, fresh save/archive/CSV/re-execution and five-row fixture.35 new cases
(7 Core28 Application). No dependencies, Rust edits, lab IO or UI draft changes.
K6221 raw socket1394 confirmed by manufacturer; fresh pure-DC/armed evidence and
zeroing abort semantics still unresolved, so do not register a guessed hardware
adapter. See iteration-scans.md and k6221-tcp-investigation.md. Whole goal active;
full controllers/BT/background/DB/native UI/packaging/lab remain unfinished.


## Workflow controls and typed results (2026-10-11)

Core StepOutcome Success/Failure distinct from exceptions; native typed condition,
fallback, bounded retry/backoff and ForceSuccess/ForceFailure/Invert decorator.
Existing sequence/repeat/scans/iterators/if-else propagate status and halt children;
terminal logical failure preserves originating step via PlanFailedException and
normal stop/retained recovery. Typed StepCompleted outcomes; branch/attempt/total
axes, retained valid failed-branch rows and grouped quality; variable entry-scope
isolation and conservative definite availability (including removed if-else vars).
All branches and worst retry cost preflight before IO; strict native version1/
PPMS wrappers with source lines and child/count/delay checks. Rust broad catches
are not copied: driver/timeout/routing/listener/cancellation/safety exceptions
never become success or implicit retry. Existing acquisition-specific policies
stay explicit.62 new cases (9 Core53 Application), including local TCP negative
read, pause/backoff, cancellation, listener fault, retained failed hold and both
logical/safety causes, native grouped archive/CSV/re-execution and PPMS fixture.
No dependencies, Rust writes, lab IO or29 App drafts changed. Native Mac inspection
still locked this stage; no new UI acceptance/liveness. Full PPMS/controllers/BT
exception recovery/timeout/parallel/background/DB/UI/packaging/hardware remains.
See workflow-control.md; whole goal active.


## Independent temperature controls (2026-10-11)

Native temperature_control and PPMS temp_ramp_on/off, temp_control_on/off now
use an explicit optional Core configuration port and existing executor/resources.
Fresh target/rate/flag confirmation preserves the other flag; RampOn cannot
implicitly enable disabled control. Mock disabled control retains its target but
never fabricates movement. Hold preserves flags and parks at live readback; pause
restoration rejects flag/rate drift before source output restoration. Refusal,
cancellation, failed/control-enabling hold and retained whole-group recovery,
strict aliases/source lines, native save/fresh archive/CSV/re-execution verified.
50 new cases (8 Core42 Application). See [temperature-controls.md](temperature-controls.md)
and the two-row ppms-temperature-controls-import.json fixture. No dependencies,
Rust writes, physical IO or29 UI drafts changed. Stability configuration, PID/
heater/cooling/background/physical controller/native UI remain pending; full
PPMS migration remains active. GitHub only; independent C# executor.


## macOS application packaging and native probe (2026-10-11)

A Release self-contained .app packaging script now publishes the existing SDK/
Avalonia runtime, validates native manifest/runtime/executable and refuses existing
output including dangling links; exported source without Git metadata remains
buildable with explicit unknown provenance. App name matches bundle/native menu.
Actual local draft UI opened: four simulators, rotation connect/readback, device
document validation, two-device15-row plan pause/owned source-off/manual disable/
resume/finish, persisted history and UI CSV checked. A fresh self-contained Release
process reopened15-row history. Closing during another active simulated run
terminated the original process and finalized10-row Aborted archive; subsequent
CUA AX observation automatically opened a new process, not continued old run.
Native coordinate resize rejects noWindowsAvailable while AX/process live;
1060x720 and failed-safety recovery/close acceptance still pending.29 original UI
drafts unchanged/excluded; package probe includes them and is distinct from clean
committed-source package. No Rust edits, physical IO or new package references.
Script guards, mandatory hook, exact clean audit/package and GitHub publication
are required. See [macos-packaging.md](macos-packaging.md). Full goal active;
remaining controller/stability/BT/advanced measurement/DB/flow/UI/packaging/hardware
work is not complete. GitHub only; independent C# executor inspired by OpenTAP.


## Temperature stability settings and inherited waits (2026-10-11)

Independent C# session configuration now accepts PPMS temp_set_config strict
optional threshold/duration aliases; empty patches preserve fields, invalid input
fails with source lines. Native temperature_stability/wait_temperature steps are
compiled through full resource/preflight/lifecycle, serialized and archived.
Temperature set/step scans inherit current settings; explicit overrides persist
before ramp including wait_mode=none. Initial instrument metadata includes the
session settings. Fresh flag/snapshot/flag observation and wait-local Core states
prevent disabled control from satisfying waits. Pause resets active-time evidence;
cancellation/IO/flag mismatch/target drift stay errors, failed hold retains owner
until recovery. Quantized endpoints allow only half-millikelvin target rounding.
See [temperature-stability.md](temperature-stability.md) for the explicit difference
from Rust's continuous observer/cache and cross-wait timer reuse. Background,
PID/heater/cooling, disabled-control sets/scans, persistence, UI and physical
acceptance remain pending. No Rust edits, new packages or original29 UI edits.
Targeted tests, mandatory precommit and exact clean committed-source audit precede
GitHub publication. Full migration remains active; GitHub only, independent engine.


## Caller-managed temperature setpoints and wait ordering (2026-10-11)

PPMS set_temperature enable_control=false now preserves the actual enabled or
disabled control flag through software/direct/hardware target changes. It does
not mean control-off. Default enabled software paths keep the original port;
preserved paths preflight the actual optional configuration port. Hardware sets
use a typed atomic parked preparation instead of rate-enable at a stale target.
Software ticks carry captured control/rate evidence, with drift/refusal/fault/cancel
and pause guards through existing owned resources. Native optional enableControl
is backward compatible; new hardware_temperature_setpoint saves/archives/replays.
Further reference tracing corrects the prior stability-stage statement: no-wait
set skips threshold configuration and settle, waited set and step scans update
thresholds after ramp and settle before waiting. Prior before-ramp/no-wait override
behavior was a migration mismatch, not reference semantics. Scan false-control
option remains explicitly unsupported because the Rust factory does not carry it.
See [temperature-setpoints.md](temperature-setpoints.md). No new packages, physical
IO, Rust edits or original29 UI edits. Mandatory hook and exact committed-source
audit precede GitHub stage publication. Full migration remains active; continuous
runtime/persistence, PID/heater/cooling/background, advanced instruments/BT/data/
graphical flow/native UI and hardware acceptance remain pending. GitHub only.


## Shared temperature runtime and software intent (2026-10-11)

Device session now owns one Core observer shared by manual status, acquisition,
waits and typed DeviceStatus notifications. Monotonic TimeProvider and fresh
flag/snapshot/flag evidence replace separate wait-local timers; second waits can
reuse stable evidence. Changed settings, failed/disconnected observations, hold,
pause and resumed targets reset evidence; identical patches retain it. Software
ramp status uses final requested target and separate software rate, preserving
actual hardware quantities; direct/final/aborted ramps clear intent. Pause captures
and restores goal/rate only after existing control/rate/target guards. Core rejects
contradictory status facts; no autonomous sampler/worker or continuously sampled
interval is claimed. See [temperature-runtime.md](temperature-runtime.md). No
physical IO, new dependencies, Rust or original29 UI edits. Persistence, firmware
states, background/PID/heater/cooling, caller-managed scans, full native/graphical
UI and other PPMS/hardware scope remain pending. Mandatory hook and exact committed
source audit precede GitHub publication. Full goal active; GitHub only.


## Confirmed axis disconnect and temperature evidence (2026-10-11)

Axis disconnect now requires a fresh disconnected snapshot. An adapter error,
cancellation or ignored disconnect cannot preserve old Stable evidence or emit
a success status. Moving-axis rejection remains before mutation and does not
mark the existing known state faulty. Five focused cases cover failures, fresh
confirmation, notifications, recovery and reconnect evidence.
Read-only PPMS tracing found sample getPID/setPID and getHeaterPower/setHeaterPower
RPC calls in device_sample.rs, but no vendor units, bounds or manual/automatic
mode side-effect contract. sanitize_f64 maps nonfinite values to zero; this is
not a valid C# safety contract. PID/heater writes remain pending model/firmware
evidence; no 0..100 percent assumption or simulated physical parity. No new
packages, Rust edits or original29 UI edits. Mandatory hook plus exact committed
source audit precede GitHub publication. Full goal active; independent C# engine,
GitHub only.


## Wall-clock workflow timeout (2026-10-11)

Independent C# engine adds typed TimeoutStep with nested scoped cancellation,
wall-clock expiry (including pause), typed fault identity and root abort distinction.
Child is awaited to settlement before final cleanup: no detached live IO, overlapping
stop or early release. Noncooperative ports keep the run/group until drain; expiry
initiates cancellation and is not a forceful hardware interrupt. Failed final stop
retains the group for explicit recovery. Existing logical controls cannot swallow
errors, and all preflight/document/raw/derived-data visitors include timeout bodies.
Native v1 timeoutSeconds/body and PPMS timeout/with_timeout timeout_s default60s
require one child, strict finite bounded duration, zero immediate expiry. Native,
import, fixture/archive/CSV/fresh execution and vdP evidence tests cover the slice.
See workflow-control.md. No new packages, Rust or original29 UI edits. Mandatory
hook and exact committed-source audit precede GitHub publication. Parallel/subtree/
script/background/full UI and hardware remain pending. Full goal active; GitHub
only, own engine inspired by OpenTAP.


## Numeric subtree scope and explicit variable mapping (2026-10-11)

Core SubtreeStep/VariableBinding and Application preflight/executor implement
inherited numeric quantity scopes, simultaneous parent→child input aliases,
local unit shadow and selected child→parent outputs after success only. Parent
variables restore on failure/error/abort; even outer ForceSuccess cannot leak
failed local writes or guarantee late outputs. Actual instrument writes and
committed rows remain run-owned. All raw/derived/document visitors descend into
scopes; native v1 and PPMS subtree/scope/isolated preserve mappings and source
lines. Missing/ambiguous/invalid mappings and output unit changes reject before
IO. Fixture scan+field exports, archive/CSV/fresh replay, vdP and wide-group
provenance, pause/cancel/timeout/driver faults/retained cleanup are verified.
Rust blackboard also has Bool/Text; these are not implemented by the
quantity-only C# scope. Full non-numeric/expression/parallel/UI/hardware and other
PPMS work remains active. See workflow-control.md. No new dependencies, Rust or
original29 UI edits. Mandatory hook and exact committed-source audit precede
GitHub publication; independent C# engine and GitHub-only decision unchanged.

## Node outcome branching (2026-10-11)

BranchOnOutcomeStep executes condition once on the existing owned serial run,
then selected Then/optional Else. Missing Else succeeds; selected Failure keeps
origin. Condition rows and mutations remain. IO/listener/deadline/abort propagate
as errors, never logical False. Compiler validates all children, condition+max
branch cost, and same-unit variables across paths. IO-only decorators retain
completed observations on forced failure/inversion, while early-failing controls
cannot guarantee new variables on Else. All resource/data/document visitors,
native branch_outcome codec, PPMS if_then_else/conditional/branch node mode and
then/else wrappers are integrated. Strict malformed/numeric-mode rejection,
fixture/native save/archive/CSV/fresh replay, grouped quality/vdP, pause/abort,
failed-stop claims/recovery and owned TCP fault tests cover the change. PPMS
numeric mode/while, Bool/Text/expressions, parallel, native UI and lab parity
remain pending. Independent C# engine, GitHub only; no new dependency, Rust edit
or protected UI-draft inclusion.

Full audit also exposed an owned LoopbackScpiServer teardown race: Stop could
precede the accept loop's next iteration. Token-cancelled accept now drains
before listener Stop; a 32-session regression checks handler/socket-read drain.
This changes only the local test peer, not physical transport behavior.

## Numeric condition and pure iterator contracts (2026-10-11)

ValueCondition optional typed EqualityTolerance uses strict absolute tolerance
only for Equal/NotEqual; native old exact behavior remains. Full compiler checks
positive/unit/operator compatibility; codecs preserve optional fields and reject
explicit null. ForEachStep IndexBase defaults OneBased; PPMS for_each/iterate
uses ZeroBased, preserves row counters/labels/native documents. PPMS numeric
if_then_else aliases lower to typed assignment+IfElseStep with 1e-9 equality,
gt default and strict operator/child/field validation. Migrated scans publish
ppms.axis.* quantity aliases each point, with voltage/bias dual names and latest
nested/post-scan value retained; no fake observation/fallback zero. Numeric pure
iterator direct children resolve value/iterator_value/index/total; explicit
injection overrides dynamic values and missing RHS uses the point. Nested
counter/row axes are distinct. Schema points_mode normalizes to mode with
conflicts rejected. Fixture/archive/CSV/fresh execution, availability/units,
compatibility, epsilon boundaries, pause/abort and owned TCP/timeout error tests
cover the slice. Unknown/custom/nonnumeric/pol_voltage variables, dynamic command injection, advanced
point modifiers, while/expressions/parallel/native UI/lab remain pending.
Independent C# engine and GitHub only; 29 UI drafts excluded; no new dependency
or Rust edit.


QuantityFromNumberValue explicitly qualifies an untyped dimensionless variable
in an authored PhysicalUnit. Compiler/Resolve reject missing or already physical
inputs; native quantityFromNumber codecs preserve and strictly validate it.
This makes an injected point a correctly unit-tagged threshold for a physical
axis without permitting arbitrary physical unit conversion. Numeric fixture
archive/CSV/fresh replay includes this expression, plus a field/iterator test
selects opposite branches for negative/positive dynamic Tesla thresholds.

## While and node guard/callback execution (2026-10-11)

WhileStep condition reevaluates after pause/abort checks; body Failure preserves
origin, false succeeds, errors escape. Same context, one-based iteration row axis,
outer axis restore and zero-iteration variable preflight. MutateValueStep Set/Add/
Subtract preserve unit, scalar Multiply preserves input unit; absent variables
start zero and nonfinite arithmetic faults. OutcomeMutationStep executes only the
matching ordinary outcome hook and does not transform body result. No callbacks
for IO/listener/deadline/budget/abort faults (intentional safety difference from
Rust broad error post-callback). PPMS while defaultlt, strict equality/symbols;
custom callback-declared dimensionless variables, fixed physical aliases; top
_onSuccess/_onFailure defaultset, _failureIf precedes _skipIf and early guards
bypass callbacks. Strict syntax/availability/units before resource admission.
Native while/mutate_value/outcome_mutation codecs and all data/content/resource
visitors updated. Existing10m plan execution budget now enforced at step start
and software-temp target ticks; native optional lower maximumStepExecutions,
typed PlanExecutionBudgetException, dynamic preview HasDynamicLoops plus actual
cap. No forceful IO cancellation or silent loop truncation. Four-row fixture,
archive/CSV/fresh replay, grouped/vdP, compatibility/malformed/units/overflow,
logical failure, pause/abort, TCP/timeout/no-error-callback and retained-stop
recovery tests. Bool/Text, custom physical metadata, expressions, generic
parallel/background, native UI/lab and full migration remain pending. GitHub
only; independent C# engine;29 UI drafts preserved/excluded;no new dependency or
Rust edit.


## Typed blackboard values and completed provenance (2026-10-11)

Core immutable quantity/Boolean/Text values and bounded text literals; Application
preflight tracks type+unit across definite paths, typed equality/inequality,
assignment/while/if/subtree and no numeric coercion. Existing numeric IO resolves
only quantities. Local input mapping may shadow type; selected outputs cannot
change an existing parent type. Immutable entries stamp source StepId/UTC write
and monotonic elapsed, with input/output transfer kind. All existing numeric
writes migrated; snapshot restore retains stamps. LatestBlackboard defensive
completed-run snapshot includes faults/abort; previous snapshots remain frozen.
Native version1 additive boolean/text literals are strict and bounded; text
allocation preflight and final byte limits; archive/CSV preserve plan and fresh
replay. Portable no-IO fixture, ordinal Unicode/empty labels, typed branch joins,
physical write/multiply/qualification/tolerance rejection, retry/fallback,
subtree fault/timeout/abort restore, acquisition/listener provenance covered.
Rust read-only: blackboard.rs Bool/Text APIs exist, actual axis/callback writes
remain numeric. No invented imported Bool/Text dialect. Runtime entry archive
persistence/live feed, expressions/parallel/background and native UI/lab/full
migration remain pending. Independent engine;GitHub only;29 UI drafts untouched;
no dependencies or Rust writes.

Numeric outcome-hook admission now checks possible body bindings, not just definitely available variables after branch joins. Scoped private shadows are excluded; exported mappings count. Prefix checks are conservative across retry/fallback attempts; exact symbolic outcome-path analysis remains pending.


## Terminal blackboard archive (2026-10-11)

Core RunBlackboardSnapshot owner/UTC/elapsed/frozen bounded entries; source/key/
type/unit/literal/count/scope-transfer checks through shared row provenance
boundary. RunDataSet terminal snapshots include it; Application captures after
safe-stop, errors fault/finalize without fabricated variables and existing
release/recovery stays active. LatestBlackboardSnapshot carries RunId; entries
convenience API preserved. In-memory history/final listeners share snapshot.
Archive v2 required Captured/NotCaptured status, payload+SHA pair, matched
header/final versions; reader accepts legacy v1 as unknown. Observed-empty is
explicit. Typed canonical payload checksum and final raw-record checksum during
streaming; fresh metadata/catalog/load/CSV preserve full final values/stamps.
No per-write event log, replay proof, signature or live inspection UI claimed.
8 MiB terminal payload failures retain rows/unfinalized archive and report
post-safe-stop delivery fault rather than truncation. Existing corrupt-row test
fixtures now explicitly clear terminal Blackboard when reconstructing acquiring
headers. Paged/lazy catalog, live diagnostics, expressions/concurrency and other
PPMS/native UI/lab scope remain pending. GitHub only; independent C# engine;
original29 UI drafts untouched; no dependency/Rust/lab changes.


## Live run blackboard observations (2026-10-11)

Application-owned RunBlackboardState is the actual mutable producer boundary;
all writes/removals/scope restores and diagnostic copies synchronize. Core
RunBlackboardSnapshot reused as frozen live/terminal data. Public immediate
revision reads and pinned async streams include owner, phase, subtree lexical
stack and optional producer outcome. Signals coalesce with no event queue or
background task; multiple readers independently cancel. Slow stream remains
on its original run across replacement, while new immediate reads only resolve
active/latest run. No added global per-write notifications. Phase/revision
changes on recovery retain the same terminal snapshot; producer done is not
resource/safety proof. Admission failure closes waiters with unknown snapshot;
empty normal runs remain observed-empty. Finalizer failure can differ from
acquired dataset state. Targeted tests include true concurrent readers, 1000
writes, paused/aborted/nested/timeout controls, stream cancellation/replacement,
failed reservation/no hardware IO, final delivery errors and invalid-point
stale removal. Original29 UI drafts preserved/excluded, Rust read-only, no new
dependencies. Paged/lazy history and diagnostics, per-write journal, native UI
integration, expressions/parallel/background and full PPMS/lab remain pending.
Independent C# engine; GitHub only; full goal active.


## Bounded paged archive history (2026-10-11)

Core query/cursor/version/summary/page boundaries, Application RunHistoryService
browse/metadata/row APIs, SDK FileRunArchive implementation. Lightweight catalog
projection discards full schema/plan/notes/attributes/variables; timestamp+Guid
keyset with filters, bounded selected prefix/text and issue details plus total
omission counts. Text-budget cutoff preserves contiguous sort prefix across
arbitrary directory order. Row pages retain only selected records and raw
UTF8 byte budget, but stream/validate the entire captured file with existing
scientific evidence/terminal integrity. Cross-page run/header/data/final hash
stamp rejects equal-size rewrites, appends and terminal promotion; header
rechecked during reads. Unknown vs observed-empty variable history maintained
in legacy/v2/selected metadata. Real100005-row restarted synthetic storage
passes paging beyond default full-load limit; wide UTF8 and rehashed cross-page
group mismatches verified. Full scans required per query/page; no index/seek
latency claim, live dataset memory unchanged, native history/plot integration
and visual acceptance remain pending. Original29 UI drafts preserved/excluded,
Rust read-only, no new dependencies; GitHub only; full goal active.


## Paged history plot projection (2026-10-11)

Application RunHistoryService.ReadPlotPageAsync projects verified bounded row
pages into ordered typed XY samples with original one-based indices/timestamps,
schema axis descriptors, nullable missing coordinates, row provenance kind and
whole-row quality state/issues/observed count. One sample per source record,
including gaps; row-byte budget/version/incomplete-tail/next offset preserved.
Unknown axes rejected; no sorting/interpolation/quality filtering/downsampling.
No source rows/unselected values retained in result. Missing measurements,
unobserved status and failed acquisitions remain distinct. Seven new cases plus
two existing TCP sample-analysis/group quality cases verify sparse restart,
byte budgets, version replacement, corruption outside page, empty/unfinalized,
cancellation and recovered failed acquisition. Every query still whole-scans;
no index/cache/full-run decimation or native chart integration claimed. Mac
currently locked prevents native desktop inspection;29 original UI drafts
untouched/excluded. No new dependencies, Rust changes or lab IO. Independent
C# engine; GitHub only; full goal remains active.


## Paged desktop history state (2026-10-11)

New App PagedRunHistoryViewModel consumes real Application history browse/
metadata/XY APIs, retaining one50-summary catalog and one500-record plot page.
Schema-driven default/selected axes; original units/indices/gaps and whole-row
quality exposed. Keyset replacement/filter reset, invariant1-based jump, pinned
next/jump, explicit fresh reload; corruption retains trusted data and disables
continuation. Single tracked cancellable read; selection/axes/search/jump
changes supersede revision; late metadata/pages discarded and busy/drain kept
through noncooperative IO. Concurrent disposals await same task and release
pages without disposing shared history.21 new App cases use fresh real file
archives,505/51-record boundaries, adverse controlled read handles, archive
replacement/scientific corruption and unfinalized tails. Native wiring/chart
rendering/series partitioning and visual acceptance remain pending because
current CUA verified locked Mac; async unlock request pending. Existing29 UI
drafts untouched/excluded. No dependency/Rust/lab changes; full goal active,
independent C# engine, GitHub only. See history-desktop.md.


## Typed PPMS threshold switch (2026-10-11)

Core ThresholdSwitchStep: ordered numeric physical lower bounds, last >= match
wins, last child also default, prefix bounds, duplicates/unsorted kept, empty
no-op. Application compiler quantity/type/unit/definite-availability validation,
incremental common path state and max-branch cost; interpreter delegates only
selected child with existing resources/safe-stop/lifecycle. Expected failure
and original leaf identity, TCP fault/no retry and abort cleanup preserved.
Native threshold_switch codec includes depth/node/value bounds; all Core
row/sample/blackboard walkers include children. PPMS switch/select_case imports
contiguous case prefix and known axis/callback units; nested iterator contexts
through containers supported. Malformed legacy unused fields rejected (strict
import policy), valid ignored gapped/excess fields kept semantically inert.
47 new cases (8Core39Application), plus existing regression cases; repository
field fixture and nested iterator save/restart/replay/CSV/Hall evidence tested.
Native visual editor/physical acceptance and other PPMS scope remain pending.
Original29 UI drafts untouched/excluded; Rust read-only, no dependency/lab IO.
Independent C# engine; GitHub only; full goal active. See threshold-switch.md.


## Routed IV/VI meter sweeps (2026-10-12)

PPMS authoring measure_iv/measure_vi lower to scan_current/measure_voltage and
scan_voltage/measure_current. C# now inherits frozen source scan channel,
checks meter/source identity and equivalent alias routes, rejects channel
switch/bypass before IO, and supports independently routed passive voltage
reads without applying unused source preparation. Core ReadStep optional
immutable RequiredRoute and declared excitation source, Application exact-source matrix guard preflight and pre/post
read confirmation, native strict codec/budget. All read channels publish
measured blackboard values only after complete acquisition/environment/route
confirmation. No partial rows or value leaks after drift/fault; original abort,
pause/drain and retained failed stop/recovery lifetime preserved.44 new cases
(4Core40Application); focused135Application+4Core. Owned TCP VI and pure-source
IV, zero/negative points, actual fixtures, wide raw+summary groups, fresh saved
plans/archives/CSV tested. Programmed sweep axes are not actual source readback;
scalar rows have guarded plan + original quality, not extra per-row routing
provenance. Native editor/physical/parallel/background/full PPMS remain pending.
29 original UI drafts untouched/excluded; Mac lock verified this stage; no lab
IO/dependencies/Rust writes. Independent C# engine; GitHub only; full goal active.
See meter-sweeps.md.


## Live controller waits (2026-10-12)

Core WaitTemperatureStateStep observes current Reached/Stable (Reached accepts
Stable); WaitAngleStep uses fresh coherent target/readback each poll unless an
explicit Degree comparison target is authored. No target writes or synthetic
rows. Application capability/unit/availability preflight, claimed snapshot IO,
fresh temperature runtime and pause evidence reset; existing shared leases,
hold/cancel/drain/retained failure recovery. PPMS wait_temp/aliases,wait_angle/
state alias and generic wait_kind, strict aliases and source-line diagnostics;
default3600/30 seconds, poll500 raised to runtime100ms floor. Zero timeout is
truly no deadline; finite waits compose native TimeoutStep (wall time including
pause), strict expiry and root abort priority; never drop live IO tasks or mask
faults with workflow retry/decorators. Native codecs and angle value budget.
68 new cases(10Core58Application), focused182Application+10Core; actual fixture
with temperature settings/live state and angle current target, unrelated
sources/matrix disconnected, native save/archive/CSV/fresh replay. Field vendor
Holding/persistence, physical/background/parallel/native UI/full PPMS remain
pending.29 original UI drafts untouched/excluded. No dependencies/Rust writes/
physical IO; independent C# engine, GitHub only, full goal active. See
controller-waits.md.


## Native minimum window inspection (2026-10-12)

Desktop unlocked: current be8d4ca working source including29 unchanged UI drafts
packaged and actually opened.1440 default and1060x720 client native rendering
observed (Retina2120x1496 includes28 logical titlebar). Navigation/readings/chart/
controls visible; parameter panel scroll and actual name/interval edits verified.
Native drag still noWindowsAvailable and AX setValue unsupported. New optional
StartupWindowSize/Program hook and package --window-size/validated launcher make
layout checks repeatable without changing original29 drafts.14 new App tests
and7 invalid package executions. Native start attempt not proven: tool reported
app change, fresh state showed instrument page, coordination question pending;
Mac subsequently locked. No new run/pause/fault/close proof or published draft UI
parity claimed. Full native/physical/full PPMS scope remains pending. No new
dependencies/Rust/lab IO; independent engine/GitHub only/full goal active. See
native-window-sizing.md.


## PPMS nonramping field wait (2026-10-12)

Rechecked Rust wait_field_state.rs: actual predicate is NOT ramping AND inclusive
field-target tolerance, with optional duration; wait_for is display metadata,
not exact Holding-state comparison. Earlier ledger wording describing vendor
Holding as necessary for this node was too broad. Core WaitFieldStep explicitly
models actual live Tesla target/readback/nonramping predicate; fresh coherent
owned axis snapshots, compiler Tesla role gate and existing lifecycle. Matching
value during ramp does not pass; unstable observations and pause reset evidence.
Native strict wait_field codec; PPMS aliases/generic wait_kind/default3600s/
0.0005T/0duration/500ms(default100ms floor), true zero indefinite or existing
positive TimeoutStep. Actual task drain/root abort priority, exceptions never
masked/retried; refused holds retain ownership.49 new cases(7Core42Application),
focused84Application+7Core; actual0.01T fixture, native/archive/terminal BB/CSV/
fresh replay. Vendor switches/persistent/quench/physical/native UI/continuous/
full PPMS remain pending; no magnetic safety certification claimed.29 original
UI drafts unchanged/excluded; no dependencies/Rust/lab changes; independent
C# engine/GitHub only/full goal active. See field-waits.md.


## K6221 DC admission (2026-10-12)

Core nullable Delta/Pulse Delta/differential/waveform/ordinary-sweep armed facts
and operation condition evidence require all unarmed plus Idle, excluding active,
error and undefined bits. Connected snapshot capture timestamp association is
validated; adapter freshness/coherence is an obligation, not a proven physical
transaction. Simulation supplies explicit facts; only legacy Simulation may omit
them. Hardware unknown evidence blocks DC reads/config/preparation/capture/enable/
resume; mutations verify afterward. Output diagnosis remains available. Manual
output-off invokes SafeStop even when mode unknown, then verifies DC admission
and output-off. Stop/disconnect refusal retains all run claims; acquisition drift
never commits a row; verified explicit recovery releases claims.43 new cases
(18Core25Application) plus existing K6221 regression coverage. No registered real
K6221 transport: waveform/ordinary-sweep arm queries and abort/setpoint preservation
remain unestablished. Hardware-descriptor test doubles are not physical adapters.
29 original UI drafts untouched/excluded; no native inspection, lab IO, dependency
or Rust writes. Independent C# engine/GitHub only/full goal active. See
k6221-dc-admission.md.


## Desktop operator integration (2026-10-12)

Adopt original29 local drafts after byte-exact backup and acceptance repairs.
Configured composition/shared registry/run/archive; plan validation/editor/trace,
device status/settings, history/CSV, pause/resume/abort/recovery and awaited close.
Outline now traverses all supported composite families including timeout children
and measurement groups; graph editing/paged history wiring remain incomplete.
Native reproduced Avalonia11.3.8 disabled-Button accessibility exception crossing
Mac callback. OperatorButton uses standard theme and macOS-only inert disabled
invoke, preserving enabled errors and other-platform peer behavior; command bodies
recheck admission.15 new App cases plus18 existing operator-draft cases;80 App
cases passed. Actual native start/pause/locks/resume/abort/joint15-row scan before
repair, repaired disabled calls/completed21-row run and final source timeout child/
fault/one preserved row/output-off verified. Final source native history/active
close/post-repair screenshot and committed-package parity remain unverified after
desktop relock; backend unsafe close/recovery tests do not substitute native proof.
History full load/live memory/graph/physical/full PPMS remain pending. No new
dependencies or Rust edits; independent engine/GitHub only/full goal active. See
desktop-operator-surface.md.


## Paged history surface (2026-10-12)

Shell now uses existing PagedRunHistoryViewModel; removed obsolete full-load VM.
Bounded50 summary/500 sample pages, schema X/Y, records/curve tabs, source index
jump, explicit version reload and current-source500-record backward movement.
Scatter default, optional record-order lines preserve gaps/failure/kind/index
breaks; original units/quality/times/provenance and zero/constant/extreme values
retained. Expandable CSV + scrollable finite chart viewport fixes actual native
1060x720 overlap. Native reinspection:21-point curves,换轴/re-read/lines/record
values and full21 CSV; explicitly SYNTHETIC505 fixture verifies500/5 pages,
jump500–505, missing/zero values and full505 CSV from displayed6 records.
Export independently revalidates full archive, captures ID/path before callbacks,
serializes/cancels/drains with reads; shell owns shared storage disposal once.
Cancel does not promise undo of completed writes/noncooperative adapter effects.
Catalog binding reset preserves same-run trusted plot/custom export path.14 new
App cases; four prior history tests migrated;94 App pass. Native refreshed-catalog
repair/current committed-package parity pending. Full-file per-page validation
IO cost remains; live memory/indexing/multiseries/graph/physical/full goal active.
No dependencies, Core/Infrastructure or Rust edits, no physical IO; GitHub only.
See history-surface.md.

## Validated plan graph editing (2026-10-12)

Actual native tree projection/parent links, selectable nodes/active step facts,
four palette kinds, JSON node fragments and ordinary-sequence append/remove/
reorder. Optional Core document extension; Infrastructure preserves complete
envelope and native limits; Application full compiler preflight before replacing
draft. Required slots/IDs/variable/budget/group constraints remain authoritative;
editing never connects devices. Dirty fragments lock run/files/full JSON/selection;
command bodies recheck admission, discard restores state and clears errors.
Native1060 minimum layout repaired with expandable file/results controls; actual
working simulation add/move/remove/reject/discard/apply/save/reload/pause/locks/
resume/one row/output-off and idle close exit0 verified.28 Application+six App
new cases; no dependency/Rust/lab IO. Clean-package proof recorded separately.
Typed forms/device palette/wiring UX/virtualization/general expressions/parallel/
physical/full PPMS remain pending; independent engine/GitHub only/goal active.
See plan-graph-editing.md.

## Typed node forms and instrument palette (2026-10-12)

Common delay/repeat/timeout/retry/constant set/scan/output/multi-read/wait-stable
forms and five control+four instrument palette kinds. Descriptor catalog is the
compiler's registry via Application; actual access/unit/bounds resolution, address
changes clear numeric input. Typed Core port/codec append+replace, full envelope
and compiler preflight preserved. Bodies/targets/read environment/routes retained;
expression sets keep JSON. Mixed draft sources rejected; run/files/selection locks,
stale callbacks ignored and channel/command admission rechecked.24 App+nine
Application new cases;124 App/focused nine pass, saved typed instrument edit
fresh reload/one row/output-off verified. No dependency/Rust/physical IO. Native
pending: Mac locked; working launch failed in pre-UI RenderTimer -6661; PID88403
UEs, terminal exit unconfirmed, no restart on observation timeout. UI/clean-package
parity/device-specific forms/expressions/full PPMS remain pending; independent
engine/GitHub only/full goal active. See plan-node-forms.md.

## Desktop PPMS import (2026-10-12)

Configured runtime aliases and current validated preparations/budget feed the
existing importer/compiler; optional three-file profiles use explicit matrix/
wiring/source/meter selection. Runtime registry identity (not metadata equality)
and device roles validated offline; selection lists frozen. Captured pre-busy
paths/bindings/version, cancellation/actual drain and late-result rejection;
stale/reload-failed profile imports disabled. Restore retains prior validated
plan/dirty flag; native save path unchanged, no automatic source writes.
Actual inherited empty-channel meter label bug fixed without bypassing route
checks; readonly Rust scan_children evidence at unchanged35763295.14 App+14
Application new cases;138 App/focused14 pass, actual profile fixture save/six-row
execution/output-off and pure K6221/K2182 two-row run verified. No dependencies/
Rust/lab IO. Native still pending: desktop locked, old69858/88403 UEs remain
nonterminal, no new launch on observation timeout. Full preparations UI/native/
full PPMS goal active; independent engine/GitHub only. See ppms-import-desktop.md.

## Node editor native follow-up (2026-10-12)

Desktop access restored; exact44eb8bb Simulation package native1060 rejection/
discard/read-channel removal+last protection, controller PPMS import24 rows/
restore, three-file profile load/unbound matrix gating and idle exit0 observed.
G17 tails fixed via shortest round-trip text; header form/full-area JSON switch
preserves one draft, readonly locks and fallback. Import success no longer claims
not-executed after a run; empty preparation summary explicit. Corrected working
native timing0.05/space/target/full-area JSON observed; user moved to instruments,
automation yields their page and process remains live, no exit/restart claim.
Three new App cases;141 App pass. Exact committed-package parity/configured native
workflow/palette/cancel/active close/source prep forms/full PPMS remain pending.
No dependencies/driver/Core/Rust/physical IO; independent engine/GitHub only/goal
active. See node-editor-native-acceptance.md.

## Source preparation forms (2026-10-12)

Registry-backed K2450 source/sense and K6221 DC preparations now have named native
forms. Application upsert/remove previews the complete candidate, preserving root,
metadata/routing/budget/other preparations; no driver IO from editing. Source-mode
changes clear physical numerics/range, new levels/compliance blank; finite invariant
parsing and nominal choices. Optional acquisition/Source Delay and legacy fixed
sense without span preserve register request semantics. Separate preparation/node
drafts lock run/files/import/graph; mixed drafts require discard, stale callbacks
ignored, dirty device/full-document changes cannot silently discard. Command bodies
recheck busy/disposal; pending import rejects new preparation drafts.26 additional
cases(11 Application+15 App); actual fresh save/load/two-source one row and routed
sample three rows/output-off/cleanup. Native working1060 NaN rejection/locks/discard
observed; compact window left on user-selected instrument page. Successful native
valid apply/run/K6221 dropdown/final committed-package parity remain unverified;
final label/checkbox refinements only compiled/tested. No dependencies/Rust/lab IO;
independent C# engine/GitHub only/full goal active. See source-preparation-forms.md.

## Acquisition node forms (2026-10-12)

Native source/sense, integrated resistance, independent current resistance and
sample-wrapped acquisitions now use named typed forms; three direct palette kinds.
Frozen registry role resolution (K2450/K6221/K2182), explicit V/I/R IDs/units,
Ampere threshold, reference/constant expected current/retry and sample timings.
Switching independent source clears reference/constant input; K6221 programmed
reference only. Variable/qualified expectations, exact routes, environment and
sample/wiring bindings retained, with advanced JSON for rebinding. Existing full
compiler/draft/busy/readonly/stale guards remain authoritative; no driver calls.
29 new App cases: exact field retention/invalid/family/reference/mixed/busy/stale;
three palette fresh save/load/one-row simulations/output-off; frozen sample
integrated/independent3/1rows/route provenance/matrix cleanup. Native working1060
actual read_resistance negative/discard/valid apply/nine rows/Completed/output-off/
save/reload/idle close0 and archive checksum/edited threshold verified. Independent,
sample/retry/palette native actions and clean committed-package parity pending.
No dependencies/Rust/lab IO; independent engine/GitHub only/full goal active.
See acquisition-node-forms.md.

## Environment and sample routing forms (2026-10-12)

Application exposes frozen actual AxisControllerSession definitions through the
shared registry/run/workspace; App selects real readonly role/unit readbacks, not
targets or model guesses. Shared three-role form supports ordinary/paired/R/
independent/sample reads, column/inclusion and explicit unbound missing evidence;
original order/bindings retained until explicit rebind. Frozen sample/wiring/
topology controls preview all electrode crosspoints; sample wiring determines
source/meter and newly derived exact independent route. Source changes clear
reference/constant inputs; expressions/retry/timings/envelope/full compiler retained.
Nested draft/readonly/stale/mixed/busy guards integrated.22 new App cases: three
fresh environment save/reloads and actual/missing observations, catalog/no-IO,
invalid/locks/topologies/forged metadata, integrated/independent new sample/wiring/
columns5–8/current2/meter2 route execution/cleanup and unused sessions disconnected.
Native pending due unresolved same-bundle window attribution; no user window action
or rendering claim from compiled evidence. No deps/Rust/lab IO; independent engine/
GitHub only/full goal active. See plan-context-binding-forms.md.

## Typed values and workflow forms (2026-10-13)

Shared typed value editor adapts existing Quantity/Variable/QuantityFromNumber/
Bool/Text for set/wait/assign/condition; no expression string evaluation or new
interpreter. Hardware unit comes from actual parameter, address changes clear
numeric/reference input, full compiler retains type/unit/availability/range rules.
Unedited PlanValue identity retained. Condition/check/IfElse/While fields preserve
bodies/branches/IDs and optional tolerance semantics; four new palette kinds.
Nested draft/readonly/stale/mixed/busy/preparation gates remain.29 new App cases,
App236pass; numeric qualification/edited bool loop saved/fresh simulated reload,
one0.02Volt row/flagfalse/exit/output-off/released run. Scalar-only binding/static
target caption replaced, prior variable fallback test updated. Native pending:
current tool inventory has no cua control; no user windows/new process touched.
No deps/Rust/lab IO; independent engine/GitHub only/full goal active. See
 typed-value-node-forms.md.

## Variable operations and outcome forms (2026-10-13)

Readonly Rust conditions.rs confirms set/add/sub/mul, zero initialization and
ordinary outcome callbacks; no generic experiment evaluator found. C# existing
MutateValueStep/OutcomeMutationStep now have named forms/two palette kinds. Typed
units, finite parsing, operation changes clear numeric input; Multiply dimensionless.
Optional callbacks preserve body/existing IDs; new slots unique IDs and blank
physical authoring, readonly by admission/inclusion. Full compiler and original
exception/finalization semantics unchanged: workflow Failure runs failure callback
but still fails, arithmetic/hardware/deadline/abort/safety errors never invoke it.
22 App cases/App258pass, saved/fresh increment2 loop two rows/count4/output-off;
ordinary Failure count5 with original PlanFailedException; overflow count0/no callback/
original ArgumentOutOfRangeException/output-off/released run. No dependency/Core/
interpreter/Rust/physical changes. Native unavailable (no cua control), unverified;
independent engine/GitHub only/full goal active. See mutation-callback-node-forms.md.

## Scoped subtree and numeric iteration forms (2026-10-13)

Named typed forms expose existing SubtreeStep input/export maps and ForEachStep
values/unit/timing/index base; IDs/bodies/children preserved. Parent snapshot inputs,
success-only exports and failure/cancel restoration remain in the existing engine.
Readonly observable map rows, mandatory IDs, full compiler type/availability rules.
Timing/unit changes clear numbers; DelayBeforePoint binds Second, before-body delay;
finite invariant list and existing timer/point/budget limits. Two palette kinds.
Document graph permits direct subtree children append/remove/reorder; required
slots protected and whole candidate compiled.24 App cases: mapping rejection/no IO,
locks/retention/palette, removed producer rejected; fresh saved input rebind produces
one0.02Volt row/selective exports/parent0.01 preserved/no local leak; immediate/timed
saved fresh iterators three rows/index/total/units/output-off/released runs.
Native pending (no cua tools), no user window/new process; no dependencies/Core/
interpreter/Rust/physical changes. Independent engine/GitHub only/full goal active.
See scope-iteration-node-forms.md.

## Shared scan generator forms (2026-10-13)

Named shared value/range forms replace duplicated ScanStep/ForEach list parsing.
Existing Core List/Linear/Stepped/Logarithmic/Segmented rules reused; explicit lists
retain duplicates, segmented adjacent duplicates collapse. Actual registered
parameter unit/ranges and timed iteration Second/timer bounds remain authoritative.
Address/timing/unit changes clear visible and hidden range/segment inputs/preview.
Explicit readonly-guarded preview gives count/first12, no IO/draft mutation; full
compiler still protects resources/types/budgets. Save expands actual values; fresh
forms List with exact values, generator formula provenance not persisted.
New cases verify generators/descending/invalid/budget/range/locks/clearing/palettes;
four generated scans saved/fresh simulated execution/readbacks/output-off/released;
immediate/timed generated loops three rows/units/index/total/output-off/released.
Native cua inventory now callable but Mac locked/unlock failed; optional async
unlock request sent, no user window/process touched. No Core/interpreter/schema/
dependencies/Rust/physical changes. Engine independent/GitHub only/full goal active.
See scan-generator-node-forms.md.

## Sample analysis group forms (2026-10-13)

Named typed analysis form exposes four fixed-kind/unit derived IDs and ordered2/8
per-direction acquisition/routing/environment expanders, retaining independent
per-reading thresholds/timings/expressions/retry/IDs. One whole-group draft/compiler,
no flattening. Actual draft binding resolves frozen sample geometry; missing values
explicit. Four integrated/independent Hall/vdP palettes require blank derived IDs/
threshold and explicit bindings/reference. Raw columns uniquely scoped by new node
ID to prevent accidental environment-policy collisions with previous acquisitions.
24 App cases:4roundtrips/10invalid/noIO/4coordinated rebinds/4palettes/missing geometry/
locks. Saved/fresh simulated edited and new groups execute ordered raw2/8 + summary
3/9rows, exact catalog routes and derived values from actual raw inputs; outputs-off,
matrixempty/runreleased. Missing geometry retains raw evidence and measured R with
MissingGeometry/missing derived values. No Core/interpreter/schema/deps/Rust/physical
changes. Native tool available but inventory again Mac locked/unlock unsuccessful;
prior async unlock pending; no user window/new app launch. Full goal active/GitHub
only/engine independent; see sample-analysis-node-forms.md.

## Sample metadata and geometry editor (2026-10-13)

Application UpdateSampleDetails resolves actual canonical sample ID and retains
ID/topology/electrodes, routes/root/preparations/metadata/budget, full original/candidate
compiler+codec; no device IO. Named Samples tab edits name/originalId/material/
substrate/notes/optional width-length-thickness-spacing, explicit positive Meter or
null. SampleDirty joins node/preparation/run/file/import/selection/JSON admission;
stale/forged/busy/other-draft guards. Async load/save post-await checks text/path/
unapplied drafts and cancellation, retaining late sample drafts; save reports old
snapshot saved rather than clearing new local edits.8 Application+19 App cases:
invalid/noIO/preservation/codec/optional/locks/controlled late files; saved/fresh
edited Hall3bias9rows/new metadata/geometry-derived summaries/output-off/matrixempty/
released. Real archive old/new/missing-thickness runs retain original geometry/results
on fresh reopen; changed analysis uses actual geometry ratios, missing thickness
retains raw/sheet and MissingGeometry/no rho. Native again locked/unlock failed/prior
optional request pending, no window/process actions. No Core/schema/deps/Rust/physical
changes. Full goal active/GitHub only/engine independent; see sample-details-editor.md.

## Sample catalog creation and mapping (2026-10-13)

Typed new sample ID/name/topology/real electrodes + existing metadata/geometry;
topology clears mappings, old callbacks inert. Existing electrode editor preserves
ID/topology/unedited/extra rows, channel changes clear connection; actual frozen
catalog resolves choices instead of caller metadata. Application Add/UpdateDefinition/
Remove + whole compiler/codec preserve plan envelope, 1–64/case-insensitive IDs.
Runtime pure all-direction route validation checks actual matrix bounds/source guards/
meter role/links/terminal polarity. Verification wiring never rewrites execution
steps; incoherent fixed independent guards reject mapping change, atomic UI guard
coordination pending. Shared SampleDirty admission, hidden form readonly during
creation, stale/busy/selection/file/import/node/prep/JSON locks; removal validates all
references/last sample.28 App+10Application new cases; prior focused19App/8Application
also pass.7new saved/fresh simulated samples (4integrated+3independent) actual rows/
new columns7–12/exact guards/provenance/cleanup;4existing integrated map edits saved/
fresh/old snapshots retained. No Core/schema/interpreter/deps/Rust/physical changes.
Native inventory again Mac locked/unlock failed/prior optional request pending; no
window/process actions. Full goal active/engine independent/GitHub only; see
sample-catalog-authoring.md. New catalogs/channel/wiring/guard UI remain pending.

## Coordinated independent sample guards (2026-10-13)

Default-off explicit electrode checkbox coordinates only target sample's internal
independent RequiredRoutes. App service validates original guard/catalog, unchanged
source/meter, all referenced wirings/actual cards, then typed replacements and one
final compiler/codec candidate. Columns/threshold/expressions/reference/env/retry/
dwells/IDs preserved; other samples/unwrapped reads/matrix commands unchanged.
Core structure port bounded ReadSteps/ReplaceSteps; adapter one-walk batches,
ID/overlap/slot/envelope protection, no execution policy. No opt-in keeps old guard
rejection.12App+8Application new cases:6saved/fresh K6221/K2450-current+K2182 four-wire/
Hall/vdP rows/terms7–12/exact guards/cleanup; nested repeat/subtree/if plans9/27rows/
3summaries, other sample1–6 and external commands unchanged, rest JSON equal.
Batch invalid/order/envelope/required slots and checkbox stale/busy/invalid guards.
Native again Mac locked/unlock failed/prior optional request pending/no window or
process actions. No interpreter/lifecycle/transport/deps/Rust/physical changes.
Full goal active/engine independent/GitHub only; see coordinated-sample-guards.md.

## Initial routing authoring (2026-10-13)

No-catalog plan Samples editor now authors actual registered matrix/source/optional
K2182, explicit blank port card/row addresses, channel/sample IDs/names/topology
and connection card/columns plus shared metadata/geometry. Matrix definitions
frozen in Registry/proxied through Application. Pure InitializeRouting rejects
existing catalog, validates all physical port/connection bounds and sample×wiring directions against actual cards/
guards/roles then whole compiler/codec, preserving root/envelope. K6221 requires
meter in the form. Device/topology changes clear physical addresses; stale/busy/
readonly/SampleDirty/file/import/node/prep admission inherited. Two sample-read
palettes require sample/wiring/direction/threshold and independent reference;
sample catalog apply resets palette snapshot.31App+10Application new cases,
8saved/fresh sim reads including K2450-current+K2182; exact guards/provenance/
cleanup, malformed/bounds/role/forged metadata and overwrite rejection. User
answered prior unlock question “先继续代码检查”; native acceptance deferred per
user, no new inventory/window/process actions. No Core/schema/lifecycle/deps/
transport/Rust/physical changes. See initial-routing-authoring.md. General channel/
wiring edit/multi-profile creation and full goal still pending; GitHub only.

## Channel and wiring catalog editor (2026-10-13)

Samples has scoped edit/create/remove channel/wiring draft, fixed existing IDs,
explicit ports/connections/device choices, metadata preservation, 1–64 limits,
SampleDirty/readonly/busy/retired callback gates. Typed Application UpdateRoutingCatalog
admits changed physical connections and wiring roles/actual bounds/guards, all
sample links (dormant too) and every direction of referenced sample/wiring pairs;
requires structure adapter then whole compiler/codec; root/envelope retained.
Default-off coordinate option batches affected internal independent guards across
samples, proves old guard/unchanged source+meter, skips unchanged guards; standalone
reads and matrix commands unchanged.31App+16Application new cases,8saved/fresh
physical edits +7multi-profile saved/fresh dual reads +two-sample nested repeat4rows,
exact roles/guards and cleanup. Imported raw/index/displayRow/group/original metadata
retained, missing/bounds/role/clash/orphan/limit/source change and stale/busy rejection.
No Core/schema/engine/lifecycle/deps/Rust/physical changes. Native remains deferred
per user's “先继续代码检查”; no new inventory/window/process actions. See
routing-catalog-editor.md. Matrix identity fixed; whole PPMS migration still active,
including further workflow/expression/data-memory/indexing/controller/real hardware.
GitHub only per user, independent C# engine.

## Typed arithmetic values (2026-10-13)

Immutable 16-level/255-node Add/Subtract/Multiply/Divide AST; shared pure numeric
inference/evaluation/static folding/variable discovery. Same-unit +/-; declared
scaling/electrical/rate/charge/resistivity/frequency relations and explicit minute
factor; appended Watt. Logical/unknown/mismatch/unsupported/zero/overflow/nonzero
underflow reject. Compiler every-path types + folded literal device/coupled/target
limits; runtime uses existing fault/cleanup, not ordinary workflow Failure. Strict
v1 optional expression codec/recursive pre-DOM cost; all expected-current references
must be persisted axes and successful/failed row evidence reevaluates expressions.
Blackboard checks dynamic units/type, constant exact values (not historical dynamic
inputs). Recursive form blanks new operands/operator, preserves originals, explicit
units/depth/readonly/stale/address locks; typed lazy templates avoid null recursion.
32Core+35Application+12App new cases:12wrapped division faults +2overflow/underflow,
real3point example and saved/fresh scan R/P/archivedexport, current success/failure
axis evidence and no-axis rejection, static forgery/noIO/codec/budgets; managed
control construction and3saved/fresh form source reads. No script engine/deps/Rust/
physical IO; native still deferred per user. See typed-arithmetic.md. Assign math
results are terminal blackboards; generic per-row math columns, variable discovery,
PPMS scripts and broader workflow/data/controller/hardware remain goal work.

## Per-point calculated rows (2026-10-13)

CalculatedMeasurementStep wraps bounded logical read/setup, sample analysis or joint
point. Raw rows retained/delivered first, one calculated row copies actual last input
values/time/provenance and adds ordered typed results; marker prior row/step/alg1/
outcomes. Row-only inputs/axes/earlier results, no stale blackboard; optional derived
schema/dependencies/global distinct IDs. Default unavailable stops after raw; explicit
continue marks missing/div0/unrepresentable without zero fabrication. Clear result
blackboard before next read; existing pause/abort/listener/cleanup authoritative.
Shared validator one-row cache recomputes all values/outcomes/provenance and checks
schema, indices, missing marker; analysis/group windows unchanged. Strict nativekind,
optional archive2 marker/CSV calculation_json. Palette/named form body/result/recursive
expression/policy edits, original body preserved, readonly/stale/lazy controls.
9Core+25Application+14App new cases:6row saved/fresh scan/archive/CSV/example;7sample
route+4Hall/vdP analysis fresh archives and wide joint-point archive; schema/value/
marker/provenance rejection, zero/missing-env/nextclear/abort/listener failures,
source-off/releases; managed form construction/save/reload/run. No physical/deps/
Rust changes; native remains deferred per user. See calculated-measurements.md.
No nesting/inside old joint members; wrap group itself. Further statistics/workflow/
variable discovery/PPMS scripts/data memory-indexing/controllers/hardware still active.

## Bounded archived live rows (2026-10-13)

Core row-encoding port + immutable retention/proof; queue tail/count/offset/encoded
bytes, cumulative chained SHA256 and prefix checkpoints; captured rows bounded,
old snapshots unchanged. Application default4096rows/32MiB encoded footprint uses
single authoritative encoder/result listener (native FileRunArchive); withoutone
keeps accepted rows and capacity fault checked before acquisition. No silentloss.
Archive independently chains existing canonical row2 bytes, verifies prior committed
chain+tail+full digest, reconciles available undelivered suffix; full-file SHA/final
format unchanged, legacy full-snapshot proof retained. History32runs/1024rows preview
counts distinct, trims drop proof, previews cannot finalize archives. Full paged/
streaming storage remains; validators keep own bounded windows/absolute input indices.
10Core+11Application new cases:row/byte/index/capture/digest limits/forgery, fresh20row
archive with3rowtail, undeliveredlastrow, run/tail historyeviction, capacity cleanup,
6calculatedrows archive with1rowtail. No dependencies/Rust/physical/UI changes;
native still deferred per user. See bounded-row-retention.md. No exact managedheap/
wholeprocess bound claimed; subscriber queues/plot caches/blackboard/full-load/index
cost and settings UI plus broader PPMS/controller/hardware parity remain active.

## Bounded notification observers and live display (2026-10-13)

Reliable subscriptions/default legacy Reader bounded4096; overflow detaches only
lagging observer, drains prefix then explicit NotificationLagException. Display
subscription512 evicts only point/row/log/step telemetry, counts omissions; preserves
remaining critical lifecycle/safety/device/instrument/matrix order. All-critical
pressure explicitly fails observer; publishers never wait. Default retains from
construction for existing Reader compatibility, with bounded references. Shell
owns/disposes display subscription and awaits cancellable dispatcher operations
(one pending UI job); plot4096/table200/event200. Absolute acquired row counts on
row/IV/terminal facts fix totals after omission and tail eviction, including sample
analysis/calculated rows. Gap marks shell connection/run/safety stale, blocks new
work/pause/resume/edit; owned abort/recovery/close guards retained.12Application
queue cases +1fresh300row archive with stalled observer/3rowtail +3App cases and
extended plan-count regression; fresh archive preserves all300rows. No exactheap
bound, durable notification journal or auto-resubscription claimed. No dependencies,
Rust edits/physical IO; native acceptance deferred per user's code-first instruction.
See bounded-notifications.md. Full goal remains active, with wider data/cache/index,
settings, PPMS workflow/controller/realhardware parity still pending; GitHub only.

## Compiler-backed variable discovery (2026-10-13)

Application DescribeVariables validates full plan, captures only selected BeforeStep
or sequence/subtree AppendChild position; immutable ordered IDs/kind/unit/active
coordinate ID/possible first-static-entry last-writer steps. Same compiler type
flow, optional origin tracking; branch common-state unions real writers, subtree
mapping/output scope IDs, decorators no synthetic writer. Normal compile retains
no inspection maps. Shared picker for node value/condition/recursive numeric
operands; explicit ID/manual edit, hardware-unit/qualified-dimensionless/logical
filters, no UI evaluation/conversion. Palette container-end context refreshed on
selection without clearing draft; unavailable IDs retained then full appendreject,
readonly/foreign/stale callback guards.9Application+9App new cases plus prior44typed
form regressions; authored arithmetic saves/freshreload executes0.04V, sourceoff,
fresh archive retains exact row. Native still deferred per user, managed control
construction only. No Core/format/dependency/Rust/physical changes. See
plan-variable-discovery.md. Calculated-row input picker and specialized mutation/
scope mapping ID selection remain, alongside data/index/settings and broader
PPMS scripts/controller/realhardware parity. Independent C# engine/GitHub only.

## Current-row calculation draft picker (2026-10-13)

Application metadata-only draft probe first validates original host, visits current
proposed acquisition at replace/sequence/subtree append context using same compiler
rules; request-specific private exit returns only metadata, never runnable partial
compile. Logical outputs/env/active axes/elapsed only, excludes unrelated/stale
blackboard values. Frozen per-column ordered inputs, Core unit inference, blank/
self/forward/collision/type errors no binding; prior valid results only. Absence
from definite compiler availability and env semantics, not blanket schemaoptional;
prior results conservatively mayfail. Shared recursive picker/summary/error panel
refresh on body/column edits/add/remove/position, preserves stale ID text thenfull
apply rejection; readonly/retired callbacks and no initialdirty.11Application+8App
new cases: scoped/joint/optional/error/noIO queries and real picker-authored3column
power/voltage/resistance chain saved/fresh runtime3rows/input2/sourceoff, fresh
archive/CSV unit+calculation evidence. Native still deferred per user; managed
construction/compiled bindings only. No Core/schema/dependency/Rust/physical changes.
See calculated-row-variable-picker.md. Preview notadmission: full later-node/global/
budget/coupled-write checks remain normal compiler gates; synchronous refresh no
incremental cache. Specialized workflow IDs, data/index/settings and broader PPMS
scripts/controller/realhardware parity remain active; independent C# engine/GitHubonly.

## Standalone headless CLI (2026-10-13)

Separate .NET9 console composition, no Avalonia/newpackages. Mandatory actual device/
native plan paths, metadata-only validate/no fallback; run reuses registry/full compiler/
ownership/result archive, optional sample/CSV. Version1 JSON lifecycle reports include
actual model/driver, archive-ready run ID, state/totalrows/ownership; CtrlC/SIGTERM
cooperative abort drains cleanup+finalization. Unknownstop keepsprocess/archive alive;
optional bounded8KiB/100ms controlfile targets exact run ID, status/pause/resume/abort/
recover with explicitreconnect; immutable requestdedup4096 (status excluded), safety
controls retained atcapacity, malformed/stale/reused commands rejected. Storedpoll
lifetime canceled/awaited, outputfailure aborts beforedispose, useddevice disconnect.
15Application newcases; real owned CLI processes validate0/6rowcalc run0/CSV and
SIGTERM130/finalizedAborted, no desktop/hardwareIO. SelfcontainedCLI packaging script
newoutput guards/manifests; osx-arm64 runtime acceptance, otherRIDs unexecuted.
Rust readonly investigation/governance: field_set_ramp_rate current A/s twozones,
not genericT/s; naming/range ambiguity and no calibration/readbackproof -> remains
pending, nofakeconversion. See headless-cli.md. RawPPMS import stillvia workspace,
remote authentication/crossprocess leasing/startup reconciliation/native visual/
fieldprofiles and broader migration remain active; GitHub only, native deferred.

## Headless PPMS import (2026-10-13)

CLI import requiresdevices/nativecontext/rawPPMS/output, optional bounded64KiB/depth16
version1 routing selection with relative3profile paths and explicitmatrix/channelwiring/
source/optionalmeter IDs/settle. Existing workspace validatescontext, configuredalias/
roleauthority, profilebindings, full importedtree and inheritedbudget before atomic
native save; no driverIO/leases/archive/import-root execution. Namedinput/profile
output collisions reject. Argument fields staycommand-scoped; unknown/duplicate/bad
version/oversize/role/missing/unsupported/budget/cancel preserveoldoutput.18Application
newcases plus15CLI regressions: routed6rows, current samplecollection28rows (includes
joint-point summaries), fresh runtime/archive/CSV; standaloneK6221+K2182 2rows with
explicitcurrentprep/no routing. ActualCLI subprocesses import/run6and28rows. No Core/
schema/newpackages/Rust/physical/desktop changes; native deferred peruser. See
headless-ppms-import.md. Thishost reuses existing importer coverage, doesnotmake
unsupported commands work. Fieldzones/controller/measurement/workflow/crossprocess/
remoteauth/startup reconciliation/data/settings and native acceptance remain goalwork;
independentC# engine/GitHubonly.

## Gate/bias voltage command import (2026-10-13)

Readonly Rust voltage.rs evidence: same K2450; source/bias modes source-drain path,
gate mode and legacyset_bias/set_gate force gate contacts. Core frozen ResolveGate
prefers completeenabled biaspair then gatepair onlyif bothbiaskeysabsent; sourceports
only, distinctpolarity/slots, independentmeter reject. Import command/schemaaliases,
target/range aliases exclusive, fixedVoltageprep/deviceidentity; automatic2contact
requiresTwoWire, channelnone externalexplicit. Existingoff/route/zero/compliance/range/
level/on engine sequence and full card/guard/capability preflight retained. Missing
contacts failinsteadRustwarnskip; nofakeactor success.9Core+18Application newcases:
alias/mode/external/badprofile/noIO, pauseheldroute/outputoff/resume/abortbaseline,
saved/fresh archive/CSV; actualCLI3row gate/source/legacygate example. No Coreformats/
deps/Rust/physical/desktop changes. See gate-voltage-routing.md. Sourcefunctionchanges,
gate/differential/polarization scans, actualhardware and fullPPMS/user native acceptance
remain active; independentC# engine/GitHubonly/native deferred.

## Gate scan import and inherited meter routing (2026-10-13)

Readonly Rust registry at 3576329 maps scan_gate/gate and legacy scan_bias/bias
through build_gate; scan_voltage mode bias is source-drain, never gate. Native
scan import reuses command GateRoute/ResolveGate, explicit Voltage/TwoWire or
external none, exact source identity, bounded points and exclusive range aliases.
Source scope carries gate routing authority; integrated scalar children inherit
and verify gate pair without energized switching, bypass/different routes reject.
Resistance reinterpretation and same-source nested gate scans reject. Standard
iteration retains independent axes and scope releases before later source scan.
Signed/zero points, pause off/held route/resume/abort baseline, drift no row,
save/fresh native/archive/CSV tested. See gate-scan-import.md and nine-row fixture.
No dependency, Core data format, Rust write, physical IO or desktop change. Native
acceptance remains deferred per user; differential/polarization/controller/hardware/
workflow/data/settings and broader PPMS goal remain active; independent C# engine,
GitHub only.

## SR830 passive input prerequisite (2026-10-13)

Rust dI/dV and dV/dI require SR830 AC roles, not numeric DC differences. Added
Core passive input port/settings/raw frame and native read_sr830_frame; Application
owned session, resource admission/read/stop/pause/resume policies; explicit configured
SR830 simulation and PPMS four passive-setting commands with exclusive aliases.
One frame returns raw X/Y/R volts and theta plus observed four-register subset;
Current input is not presented as calibrated amps. Preserve reported magnitude,
manual SNAP pair timing does not justify reconstructing R/theta. Exact tables,
input/sensitivity gain-change guard, pre/post settings and timestamp checks,
column ownership, full-frame/archive/blackboard validators tested. No source
capability on this passive profile: acquisition idle != oscillator off; reference
and differential/composite AC routing remain pending. Existing grouped data,
archive/CSV/budgets/notifications and retained failed-stop recovery reused.
See sr830-passive-input.md and explicit CLI examples. No packages, Rust writes,
physical IO or desktop actions; native forms/visual/physical acceptance deferred.
Full PPMS measurement/controller/workflow/data/settings/remote ownership goal
remains active; independent C# engine and GitHub only.

## SR830 reference observations and nominal signal referral (2026-10-13)

Explicit passive profiles advertise eight input params or twelve including reference
source/frequency/SLVL/harmonic. Native includeReference is opt-in; metadata preflight,
actual pre/post reference equality, missing values and pause drift reject. No default
readback or oscillator/mute control. Existing normalized-voltage-equivalent port
contract remains explicit: future actual amperes wire responses require verified
adapter normalization, never blind/double division; Rust current role names/wrappers
alone do not prove units. Pure helper builds ordinary typed calculated rows referring
Current1M/100M by nominal Ohm transimpedance; preserves raw R/theta/input/reference
and gain evidence. Not calibration or full differential/sample-terminal excitation.
Siemens appended to units, typed conductance/reciprocal relations reuse finite numeric
policy. Archive rows and frame-written footer fields validate settings, reference
codes/bounds and lineage; fresh codec/archive/CSV and recomputed ratios tested.
Six-row CLI diagnostic example; history S label only, no layout/window action.
See sr830-reference-analysis.md. User mute/isolation wiring clarification pending;
independent data work proceeds, native acceptance deferred, no deps/Rust/physical IO.
Full differential/AC routing/controller/workflow/data/settings and remote ownership
remain active; independent C# engine, GitHub only.

## SR830 native frame and nominal-analysis authoring (2026-10-13)

Palette raw-frame and nominal-referral entries now initialize explicit unselected
frame fields. Dedicated profile-filtered VM/view preserves saved device/mode/prefix/
reference, requires compatible declared eight/twelve input metadata and never IO.
Unsupported reference-device switches retain flag until operator clears it or picks
compatible profile; read-only capability controls propagated. Calculated body uses
same frame form; explicit Core helper generator replaces only formula list, keeps
raw body and continuation policy, and never rewrites loaded custom expressions.
Body changes prompt regenerate/check; helper prefix alone not dirty. Existing
metadata-only row picker/full workspace compile apply/append/discard/busy rules
reused.19 new form cases, no-connection edits, real palette/graph/calc preview,
fresh saved plan/run/archive/CSV tested. See sr830-authoring-forms.md. Named vertical
components/compiled bindings; native minimum/visual checks remain deferred per
user, no native inventory/window/process action. No deps/driver/Coreformat/Rust
change. SR830 oscillator/mute and full differential/physical/controller/workflow/
data/settings/remote goal still active; independent C# engine/GitHub only.

## Dedicated rotation homing migration (2026-10-13)

Readonly Rust zero_home writes dedicated [z,1,1501] then polls strictabs<.5deg
with30s timeout/500ms poll. Added optional Degree/contains-zero homing definition
and port, native HomeRotationStep, owned Application start/fresh stationary-zero
completion and active-home pause/resume marker. Resume reissues home, never
normal SetTarget(0); unsupported advertised ports/preflight reject beforeIO.
ConfiguredROTATOR explicitly opts into dedicated immediate-home per Rustmock;
genericAngle preserves legacy false default, so wrapped ports never inherit
unimplemented capability. Aliases
zero_angle/angle_zero/rotator.zero and source-lines/nativecodec preserved.5Core+
16Application newcases: limits/capability, noordinarytargetwrite, moving/boundary/
target drift, canceledstart/disconnect/abort, pausehome restart and failedhold
retained recovery, fresh plan/archive/CSV. One-row CLI fixture; graphlabel only,
no newform/desktop/physical/dependency/Rust change. See rotation-homing.md.
Native visual/real home-sensor/protocol acceptance, wider PPMS/controllers/workflow/
data/settings/remote and SR830 mute/differential gaps remain active; independent
C# engine and GitHub only.

## K6485 passive current migration (2026-10-13)

Separate picoammeter port/settings/session and fixed1nA configured simulation;
zero check defaultsON and sample reads require explicitOFF. Exact8currentranges,
NPLC/averaging/zero registers, owned idle confirmation/pause drift/recovery.
Optional per-reading PicoammeterConfiguration preserved in archive/CSV/points;
reject absent/foreign evidence, configdrift/timestamp/unit/manualspan overflow.
Six PPMS device commands; measure_current explicitK6485 is directC#extension
(RustexperimentalcurrentK2450only; Rustmanualcurrent includesdirectK6485).
Reject matrix/inheritedsource scans; no implicitzerooff/calibration/status.
Onlysim registered, physicalcatalog fails beforeconstruction. See
k6485-picoammeter.md for50HzNPLC/filter/status/zerocorrect/physical gaps.
No deps/Rust/nativewindow changes; visualacceptance userdeferred. Broadgoalactive.

## K6514 function-aware acquisition (2026-10-13)

Four modes with independent typed selected spans/autorange flags and shared
NPLC/filter/zero registers. Explicit zeroON before mode change/zeroOFF before
sample read; changing zero check discharges real charge, never implicit.
Selected nominal span remains observed underauto, not actualconversionrange.
Session confirms mode/unrequestedregs and frameconfig/function/unit/time/range;
mixedmodeRead preflightreject. Ownedread/stopproof/pause drift/recovery asK6485.
ElectrometerConfiguration persisted/freshread/CSV/widepoint and domainchecked;
no SMU/othermeter/calibrationclaims. Sevencommands and direct4modeextensions,
no matrix/ensourcingscans. Shared chargeNPLC applies permanual insteadRustnoop;
rejectcountoutsidebounds insteadclamp, boundRustRES/CHAR TODOs. Onlysim and
sessionrejecthardware: ohmsinternalexcitation/guard needs richer stopproof.
See k6514-electrometer.md. No deps/Rust/nativewindowactions; broadgoalactive.
Full audit exposed recursive compiler stack growth after inline mixedmode check.
Moved that check to static helper outside VisitBody; preserved128depth limit,
existing127/128/importguard tests and added127/128electrometer-read probes.

## Owned polarization pulse/readout (2026-10-13)

Native ReadPolarizationStep through existing K2450 Application resources/ports;
first voltage configuredOFF beforeON, optionalinter stage, pairedactualsourceV/I
frame and freshconfig/time/route checks, independentOFF proof before anyrow/value.
Pause dwell confirmsOFF beforecapturing state; resume leavesoldstageOFF and
restartsfullattempt; boundedattemptcolumn/max/globalbudget, no stitchedpulses.
Fault/abort/deadline cleanup and refusedOFF retainedrecovery; onlyRun ownsIO.
PPMSreadoutaliases/params/sourceLines, explicitprep/channel, frozenmode/route;
rawmapped4wire needsnamedsource/sense profiles, same-source/native or anyPPMS
source-scan inheritance rejects. Nativecodec/group/calcs/fresharchive/CSV/footer
provenance integrated. ActualTCPadapter two/fourwire +refusedOFF loopback.
Recursive compiler/codec pulse handling movedoutside largevisitorframes;
127/128depth preserved (inlineswitchpattern initiallyoverflowed). See
polarization-readout.md. No deps/Rust/labIO/nativewindow change, graphlabelonly.
Host timing/physicalwaveform/controller/procedure/visual-form acceptance and
wider PPMS/workflow/data/settings/remote/SR830mute goal remain incomplete.

## Polarization point iterator (2026-10-13)

Rust actual registry maps measure_polarization/pol_voltage/scan_pol_voltage and
scan_voltage polarization modes to injection-only iterator, notsourcewrite.
C# ForEachVolt axis injects directmeasurement-child target/channel; sequence
descendants stayfixed, parentunsupportedsourcecontrols reject, parallelpending.
Dynamic ReadPolarizationStep target uses existingtypednumericvalues, mandatory
requested-V column; oldfixedconstructor/encoding/archives unchanged. Runtime
freezespoint beforeIO/pausereplay; scopedliteralForEachpoints checkedbeforeConnect
by liftedcompilerhelper,128depth retained. Rowchecks requestedlimits/axis agreement
andfooter/point/calcs optionaloutput; finalmutableglobalvars nothistoricalproof.
Bipolar/list/generator/segment/guardedchild/noIOemptyloop/nativearchive/replay,
pausedpoint andpartialabort, actualTCP3point tests. See polarization-scan.md.
No deps/Rust/labIO/nativewindowactions; dynamicgraphlabelonly. Physicalpulse/sweep
trigger/buffer/forms andbroadercontroller/workflow/data/settings/remote active.

## Native polarization authoring (2026-10-13)

Pulse forms expose registered K2450 source, typed voltage targets, dwell/read
timing, current compliance, sensing mode, NPLC, nominal range, result columns
and bounded attempts. Three named compiled-binding subviews use existing
resources and value/environment editors. Editing preserves route, acquisition
configuration, custom calculation AST and dynamic targets; saved routed pulses
reject source replacement. Palette adds pulse and Volt iterator forms; scoped
variables remain metadata-only during authoring. Nineteen App tests cover
invalid inputs, read-only/dirty/apply/discard, disconnected authoring and saved
three-point simulation through fresh archive and CSV. No dependency, hardware
IO or native window actions. Native minimum-window visual acceptance remains
deferred per user. See polarization-authoring.md; broader migration active.

## Electrode continuity diagnostics (2026-10-13)

ReadElectrodeContinuityStep models one frozen channel/pair with two-wire K2450
Force excitation, guarded route, strict settings and positive Ohm threshold.
PPMS all-channel source/meter/bias/gate order, legacy enabled aliases and
not_configured pairs lower into ordinary owned steps and terminal counters.
Each configured observation requires fresh paired/settings/route evidence and
confirmed OFF before commit. Only typed same-device overrange and zero current
become open; transport/timeout/drift/cleanup errors fault. TCP acquisition
sentinel typing is isolated from configuration parsing; SCPI/status checked
first. Native pair rows use explicit status/reason codes and omit unavailable
electrical values; strict row validator, blackboard, archive/CSV retain proof.
See electrode-continuity.md. Dedicated UI/wide-point/calculation/physical
acceptance remain pending. No deps/Rust/nativewindow/lab IO; broadgoalactive.

## Passive temperature cooling (2026-10-13)

TemperatureCoolingStep implements Rust threshold/timed/immediate cooling
observations with actual Kelvin/index0/total, descending/end-appended points,
wall-time threshold deadlines and timed cadence including pause/child time.
Independent ramp/control OFF confirmed before low target; fresh target/rate/
flags/readback evidence, read-only controller body preflight and global budget.
Normal scope restores control ON/ramp OFF. Failure/abort/logical failure keeps
OFF obligation in DeviceRunResources through bounded stops and explicit recovery;
ordinary hold success cannot release failed OFF claims. Passive readback drift
while control OFF no longer invalidates stop solely by target/readback equality.
Whole-run terminal hold still parks live temperature, differing from Rust low-
target retention. Explicit SIM-CRYOSTAT cold/fridge model; ordinary simulator
unchanged. Native/import/graph/footer/fresharchive/CSV tested;11Core+40Application.
See temperature-cooling.md. Dedicated UI, physical fridge/controller and terminal
thermal handoff remain unverified. No deps/Rust/native window/lab IO; goal active.

## Hardware continuous temperature scans (2026-10-13)

HardwareTemperatureScanStep implements hardware branch of Rust scan_temp_ramp.
Live park/first target/rate confirmation, optional consecutive strict-tolerance
stability, then hardware ramp ON/end target. Threshold and estimated-point
sampling plus timed/immediate modes store actual Kelvin/index0/progress total.
Guarded target/rate/control observations, owned read-only-controller children,
active-time threshold/endpoint guard and global budget. Estimates exclude held
pause; endpoint guard faults rather than Rust warn/success without arrival.
Ramp-OFF obligation survives failure/abort/logical failure through root stops/
recovery; successful paused holds cannot be reused as terminal OFF proof.
Explicit hardware PPMS mode only; software default remains rejected. Native/
graph/footer/fresharchive/CSV/depth verified. See hardware-temperature-scans.md.
Dedicated UI, software scans, physical controller/protocol/thermal handoff and
full PPMS remain pending; no deps/Rust/lab/nativewindow actions.
