# SR830 reference observations and nominal input referral

The reference readback is optional and read-only. An SR830 profile can advertise
its original eight input parameters, or those plus the complete four reference
parameters. Native `read_sr830_frame` retains the eight-column default; explicit
`includeReference: true` adds source code, frequency in Hz, sine amplitude in V,
and detection harmonic. Preflight requires the declared reference capability.
Missing observations never become default values. Reference changes between
before/after snapshots discard the frame; pause/resume also rechecks the declared
reference subset. Frequency/harmonic/product, amplitude, units and integer codes
are validated in Core and when reopening full rows. Frame-written terminal
blackboard values now validate domain bounds, exact input settings and integer
reference fields in addition to source/type/unit lineage.

These observations do not enable an oscillator or prove that its signal is
connected to the sample. SLVL readback is an instrument amplitude, not measured
sample-terminal voltage or a verified source-off condition. Reference lock,
status/overload quality, sample coupling, signal transfer, input bandwidth and
mute/isolation are not certified here. No reference write or SourceOutputStep
capability is added to the passive session.

## Explicit data contract

The existing Core port frame is a normalized voltage-equivalent contract. A future
hardware adapter must verify its actual response representation and normalize at
the adapter boundary. If the wire returns input-referred amperes, it must not
silently label those as volts and later divide them by gain a second time. Rust
currently carries x_v/y_v/r_v names through the read controller but the current
role wraps them in CurrentA without another conversion; these names are not an
adequate unit guarantee for C# hardware porting.

`Sr830InputReferredAnalysis.Create` builds ordinary CalculatedMeasurementStep
expressions over one frozen raw frame. Voltage inputs retain volts. Current1M
and Current100M divide voltage-equivalent X/Y/reported R by their nominal
transimpedance, expressed as 1e6/1e8 Ohm (V/A), to produce nominal input amperes.
The sensitivity register is referred by the same nominal model. Current results
include the transimpedance column, raw frame, observed input mode and calculation
lineage. This is nominal conversion, not calibration. It neither reconstructs
reported R/theta nor changes a driver setting.

The [manufacturer manual](https://www.thinksrs.com/downloads/PDFs/Manuals/SR830m.pdf)
provides the current preamplifier gain and reference register constraints; its
SNAP timing and mode-dependent legacy output conventions require care when
implementing a real adapter. Physical response normalization still needs actual
hardware/protocol acceptance. The explicit simulation always supplies normalized
voltage-equivalent data; it does not reproduce a sample's transfer function.

## Existing calculation and data pipeline

Siemens is appended to the unit enum without changing prior numeric values.
The typed arithmetic catalog now supports A/V -> S, S*V -> A, S*Ohm -> 1 and
reciprocal Ohm/Siemens relations. It preserves finite/range, zero denominator and
overflow/underflow policy. Existing compiler dependencies, calculation evidence,
raw/result rows, bounded capture, archive replay and CSV are reused. Referred
results retain observed reference columns and unobserved quality; they cannot
be relabeled as certified dI/dV/dV/dI measurements by this helper.

`headless-sr830-reference-analysis.plan.json` is a six-row simulation fixture:
three raw reference-enabled frames plus three nominal current/result rows. The
instrument_current_over_sine column demonstrates typed Siemens arithmetic using
that row's observed instrument amplitude. It is an instrument-referred ratio;
there is no DC bias, composite channel route, isolation capacitor assertion or
sample-terminal drive proof. Full differential commands therefore remain pending.

```sh
dotnet run --project src/InstrumentControlWorkbench.Cli -- run \
  --devices examples/devices/headless-sr830.devices.json \
  --plan examples/plans/headless-sr830-reference-analysis.plan.json \
  --archive /absolute/path/runs --csv /absolute/path/diagnostics.csv
```

Tests exercise optional capability/preflight and eight-column compatibility,
reference loss/drift, pause drift, native save/fresh archive/CSV/blackboard,
malformed reference rows/footer fields, nominal 1M/100M/current versus voltage
referral, Siemens arithmetic and recomputation of calculated results. The history
unit formatter displays S; no layout or native window action occurs. The user's
native visual acceptance remains deferred. Output mute wiring is a pending user
clarification; independent input/data work does not assume a particular relay.

## Active acquisition follow-up

An explicit, separate active simulation capability is now described in
[sr830-active-isolation.md](sr830-active-isolation.md). This passive stage
retains its original boundary; the active raw-frame stage does not establish
physical isolation or full curr_ac/differential-measurement parity.
