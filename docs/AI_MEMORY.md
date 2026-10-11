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
