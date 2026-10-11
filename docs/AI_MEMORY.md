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
