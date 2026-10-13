# Electrode continuity diagnostics

The PPMS `electrode_test`, `test_electrodes` and `continuity_test` measurement
imports into the existing C# engine. All frozen channel profiles are ordered by
group index and channel key. Each channel checks Source, Meter, Bias and Gate
pairs through K2450 Force HI/LO rows in two-wire mode. Display rows, raw address
strings and Sense rows never substitute for the authored Force ports. Enabled
legacy aliases are accepted; missing contacts or a card-slot mismatch produce
`not_configured`. Opposite polarities on the same sample terminal are rejected
rather than shorted. An explicit two-wire voltage/current source preparation,
channel catalog and guarded matrix are mandatory.

Parameters match the Rust reference: voltage_v (default 0.1 V), compliance_a
(100 microampere), max_connected_ohm (1 megaohm), nplc (1) and settle_s (0.1 s).
The typed step validates source limits, mode, timing and a positive resistance
threshold up to 1e15 Ohm. An enclosing source scan cannot surrender its source
to this diagnostic. Every configured pair uses OFF, verified route, configure
while OFF, ON, settle, paired actual V/I, fresh configuration/route checks, then
independent confirmed OFF before recording any outcome. Matrix changes use the
existing guarded resource owner. Pause parks outputs and resumes only verified
original configuration; abort discards unfinished observations. Refused OFF
retains all claims until explicit recovery.

Connected means abs(actual V / actual I) <= the threshold. Zero current is open
with no invented resistance. Only a terminated typed K2450 overrange read may
also become open; timeouts, malformed replies, other-device errors, transport
errors, drift and failed cleanup remain faults. The TCP adapter now identifies
its existing finite >=9e36 acquisition sentinel as typed overrange after checking
both status words and the SCPI error queue. Malformed and nonfinite replies
retain their error behavior; ordinary acquisitions still fault on overrange.

Storage adapts the Rust curve to one native row per pair. Column descriptors
define status codes (0 connected, 1 open, 2 not_configured) and reason codes
(0 resistance, 1 zero_current, 2 overrange, 3 not_configured). Available electrical
values retain original paired quality and acquisition settings. Unmeasured
pairs omit electrical values and quality. Frozen native steps retain channel,
pair, exact route, settings and threshold; row validation rejects inconsistent
thresholds, missing evidence and fabricated values. Counts pair_count,
tested_count, connected_count, open_count and not_configured_count are updated
after each pair completes result delivery and retained in terminal blackboard
history. A listener fault can preserve a committed row before its counters
advance; pair rows are authoritative in that case. Partial counts never claim
a completed diagnostic.

Native plans, fresh archives and CSV preserve these observations. The CLI
example uses headless-electrode-context.plan.json, headless-routing.devices.json,
headless-import-routing.json and ppms-electrode-test-import.json. Two channels
produce eight pair rows: five connected and three not configured in the supplied
deterministic simulation. No physical sample or relay behavior is certified.

A dedicated graphical diagnostic editor and wide-point/calculation integration
remain pending; the imported steps are visible in the existing graph and editable
through native JSON. Whole PPMS migration and hardware acceptance remain active.
Native desktop checks remain deferred per user; this stage uses code, simulation
and local TCP loopback only. No dependency or Rust modification.
