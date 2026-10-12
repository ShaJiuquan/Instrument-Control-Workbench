# PPMS voltage, bias and gate command routing

The recorded Rust voltage command uses one explicitly bound K2450 source. Route
semantics distinguish contacts, not a guessed second device:

| Imported command/mode | Automatic sample contacts |
| --- | --- |
| `set_voltage`, `voltage_mode: source` | Existing source/current path |
| `set_voltage`, `voltage_mode: bias` | Existing source/current path (source-drain DC bias) |
| `set_voltage`, `voltage_mode: gate` | Gate/substrate pair |
| `set_bias` / `bias_set` | Gate/substrate pair, regardless of recognized mode value |
| `set_gate` / `gate_set` | Gate/substrate pair, regardless of recognized mode value |

An optional instrument alias still resolves through explicit import bindings and
source preparation. Its selected device must match the bound physical gate wiring.
The default alias is `k2450`; there is no fallback to a different primary source.
Targets accept one of `target_v`, `bias_v`, `voltage_v`; ranges accept one of
`source_range_v`, `range_v`, `voltage_range_v`. Multiple aliases reject, even if
they contain the same value. Existing finite/range/compliance validation and
source range selectors/auto behavior apply. Recognized voltage modes are source,
bias, gate; unknown values reject rather than silently selecting a route.

## Frozen gate routing

`ImportChannelRouting.ResolveGate` uses the same frozen catalog/wiring authority
as channel/sample imports. It resolves channel ID/name/alias/active selections,
then selects complete `bias_plus`/`bias_minus` contacts. Only when both bias keys
are absent does it use `gate_plus`/`gate_minus`. A partial/disabled preferred pair
rejects; it does not mix vocabularies or choose an unrelated source contact.

Exactly two crosspoints connect the wiring's actual source plus/minus ports to
the selected columns. Channel presentation rows and raw address text do not
become executable instrument rows. Opposite polarities need distinct source ports
and terminals; slot differences and independent-meter wiring reject. Compiler
validation then checks the actual matrix cards, guarded source role and device
capabilities before any connection. Generic explicit current/sense channel routes
cannot stand in for a gate profile.

Automatic two-contact gate routing requires a Voltage/TwoWire source preparation.
A FourWire voltage source must not be enabled with its remote sense contacts
silently unpatched. Use an explicitly authored TwoWire preparation, or an explicit
`channel: none` for externally routed wiring. That external choice introduces no
matrix route and does not rewrite the existing sense preparation.

The source workflow remains output OFF -> reconcile guarded matrix route -> park
level at zero -> compliance -> source range -> requested level -> output ON.
Execution uses existing atomic admission, owned resources, fresh source-off checks,
break-before-make/readback, pause/resume and terminal cleanup. It adds no driver
shortcut or parallel interpreter. Routing metadata and original source-line
information remain in native plans and run archives.

The Rust helper warns/skips when neither gate contact set exists; this C# import
boundary explicitly rejects that case. The operator can choose channel none when
external routing is intentional. No absent profile is treated as a successful
hardware operation. Command-return axis labels from Rust are not new acquisition
columns; this stage follows the native plan's existing data/schema behavior.

## Example and evidence

```sh
dotnet run --project src/InstrumentControlWorkbench.Cli -- import \
  --devices examples/devices/headless-routing.devices.json \
  --plan examples/plans/headless-gate-import-context.plan.json \
  --routing examples/profiles/headless-import-routing.json \
  --ppms examples/plans/ppms-gate-bias-import.json \
  --output /absolute/path/gate.plan.json
```

The example explicitly alternates gate, source-drain bias, and legacy gate routes
and reads current after each command. Actual CLI import/run produces three rows
and CSV; native route records retain the two/four/two crosspoint selections.

Core tests cover bias preference/gate fallback, missing/partial/disabled contacts,
slot/polarity/port and independent-meter errors. Application tests cover command
aliases/target aliases/mode mapping, external wiring, TwoWire requirement, malformed
aliases/modes/ranges, source limits and no-IO preflight. Pause retains the gate
route with output OFF; resume restores the owned source; abort switches output OFF
and restores the matrix baseline. Native save/fresh load/run/archive reopening and
CSV preserve the authored gate profile.

Only deterministic simulation is exercised. Real hardware routing, source-function
switching within an imported tree, differential/polarization scans and fuller
measurement families remain migration work. No dependency, native data-format,
Rust write, or desktop UI change is introduced.

Gate/legacy bias step scans now have a separate migration record in
[gate-scan-import.md](gate-scan-import.md); hardware acceptance remains pending.
