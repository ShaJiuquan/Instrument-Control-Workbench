# PPMS gate and source-drain bias scans

This imports the recorded Rust runtime dispatch into native C# scan steps:

| Runtime name / mode | Route | Published PPMS variable |
| --- | --- | --- |
| `scan_gate`, `gate`, legacy `scan_bias`, `bias` | Gate/substrate contacts | `ppms.axis.gate` |
| `scan_voltage`, `voltage_mode: bias` | Existing source-drain contacts | Existing voltage/bias variables |
| `scan_voltage`, default/source mode | Existing source-drain contacts | Existing voltage/bias variables |

Evidence is the read-only Rust `scan_axis/registry.rs` at
`35763295f63b612f70af9dc55983644f437c18ea`: its `SCAN_AXES` maps all four
gate/legacy-bias names to `build_gate`. `build_voltage` routes bias mode through
`build_bias_voltage`, using the current/source keys. Names alone cannot determine
the contact pair. `scan_voltage` gate/polarization modes remain unsupported and
reject; no silent interpretation of unknown modes is introduced.

The import requires explicitly bound voltage source preparation and instrument
identity. Automatic gate routing reuses command `GateRoute` and frozen
`ResolveGate`: complete enabled bias pair, fallback to a complete gate pair only
when both bias keys are absent, actual source ports, exactly two crosspoints,
TwoWire preparation, matching device identity and full native card/guard
preflight. `channel: none` explicitly selects external wiring and retains the
selected preparation, including FourWire; missing profiles never imply external
wiring. The default gate channel is ch1.

Source range selectors accept exactly one of source_range_v, range_v and
voltage_range_v, including auto. Signed and zero points, existing bounded
list/linear/step/log/segments generation, range/compliance/unit validation and
aggregate execution budgets use the existing importer/compiler. Gate settle
is explicit seconds, default zero as in Rust; source-drain scans retain their
existing default. Generic native data axis columns retain their unique step path
and volts unit; there is no new archive or plan format.

The native sequence turns output off, reconciles the route, parks at zero,
configures compliance/range/initial level, enables output, then runs each point
and its children. Scalar integrated meter children inherit the enclosing gate
route, check that frozen route before and after acquisition, and never switch
energized contacts. Explicit child channel aliases must resolve to the same
physical gate route; bypass/different-channel requests reject before connection.
Gate scans nested under ordinary iteration retain both axis columns. A subsequent
source-drain scan does not inherit the completed gate scope.

Same-source nested gate/source scans reject because this importer cannot restore
the enclosing level and route. Existing resistance primitives reject inside a
gate excitation scope because their source-drain interpretation is inappropriate.
Scalar current/voltage readings remain available. Pause holds the matrix route
with source output off, resume restores the owned source, and abort/fault follows
the existing cleanup and retained-ownership/recovery policy.

## Reproducible simulation

```sh
dotnet run --project src/InstrumentControlWorkbench.Cli -- import \
  --devices examples/devices/headless-routing.devices.json \
  --plan examples/plans/headless-gate-import-context.plan.json \
  --routing examples/profiles/headless-import-routing.json \
  --ppms examples/plans/ppms-gate-scans-import.json \
  --output /absolute/path/gate-scans.plan.json
```

The fixture alternates gate, source-drain bias and legacy gate scans, three signed
points each. Run the saved native plan with the same explicit simulation device
manifest and an archive/CSV destination. It produces nine acquisition rows.
Tests cover alias/range/channel inheritance, no-IO rejection, nested iteration,
scope release, pause/resume/abort, route drift with no acquisition publication,
source cleanup and fresh native plan/archive/CSV round trips. Only simulation is
accepted here; real hardware, polarization/differential scans and full PPMS
parity remain pending. Native desktop acceptance is deferred per user request.
