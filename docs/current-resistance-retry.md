# Independent current resistance: bounded acquisition retry

The independent C# executor supports one optional retry for a terminal K2182
voltage-read failure. This is a verified part of the PPMS current DC migration;
explicit failure-to-invalid-point recovery is now verified separately; see
[current-resistance-invalid-points.md](current-resistance-invalid-points.md).

## Error boundary and policy

`InstrumentReadException` is a Core port error with explicit device, kind, optional
command code and original exception. A port reports it only after its acquisition
task has terminated. It does not certify idle hardware, reusable transport state
or source safety. Application classifies it only around the actual K2182
`ReadAsync`, after observing connected idle state and settings. Snapshot,
configuration, route, source readback, cancellation and validation errors cannot
enter that boundary, even if they happen to carry the same exception type.

The exact eligible cases follow the read-only Rust current DC executor:

| Failure | Single retry |
| --- | --- |
| Read timeout | Yes |
| Buffer timeout | Yes |
| Command rejected with code -221 | Yes |
| Overrange, invalid response, VISA read, other command codes | No |
| Untyped timeout/IO error, wrong device, cancellation | No |

Native `ReadCurrentResistanceStep.Retry` defaults to null and fails the run on
any error. Explicit policy carries `OwnsSourceOutput` and a bounded retry settle
interval. Managed excitation requires zero settle. Independent sample retries
must own output. Version1 documents omit absent policy; populated policy is:

```json
"retry": { "ownsSourceOutput": true, "settleSeconds": 0.1 }
```

See `examples/plans/current-resistance-retry.plan.json`, which runs with the
explicit simulated `examples/devices/k6221-meter-devices.json` configuration.
Healthy acquisition records one attempt and no fabricated retry evidence.
PPMS curr_dc import enables the policy for owned channels/samples and uses the
original acquisition settle. Parent current scans use the managed policy and
retain their inherited current/setpoint/route. Other modes and ordinary voltage
reads do not acquire this policy automatically.

## Resource lifecycle

The first read task must be terminal before cleanup begins. Owned excitation
starts fresh source-off and meter-abort tasks; managed excitation only aborts the
meter. Cleanup uses independent bounded tokens and the existing attached stop-task
ledger. A live or failed stop cannot be replaced or authorize output restoration,
a second read, relay mutation or lease release. Cleanup errors retain the original
read failure and the complete resource group for explicit safety recovery.

After successful cleanup, confirm the exact held route, source function/current/
compliance/range (and SMU sensing/terminals), and unchanged meter settings. Owned
output is restored through the existing model-specific enable guard without
reconfiguring away operator changes. Its retry dwell shares the executor's
interruptible pause/resume/abort mechanism. The resource gate is released during
that controlled dwell while run leases remain held, then reacquired for fresh
state/route confirmation. Measured-current mode reacquires actual source current
for the successful attempt; programmed current stays explicitly programmed.

A second failure never causes a third attempt. The legacy/default native policy still faults the run, preserving both
acquisition exceptions and bounded cleanup. Explicit continuation now records
eligible acquisition failures only after fresh safe confirmation. Cancellation
produces no unfinished row. Recoverable invalid rows, absent dependent values, partial Hall/vdP and scan
continuation now use the separate validated failure data boundary described in
current-resistance-invalid-points.md; no zero, NaN or healthy-quality substitutes.

## Data and verification

Successful retried resistance retains optional `CurrentResistanceRetryEvidence`:
kind/code/message, first attempt start/failure times, actual idle confirmation,
optional owned source-off confirmation, and the observed meter settings from the
failed attempt. `Attempts` is1 or2. The proof must match the frozen plan ownership,
precede the successful source-confirmation interval and match its observed meter
configuration. The final source/meter/V/I/R, independent capture times and ordinary
quality evidence remain unchanged in meaning.

Raw sample inputs, complete Hall/vdP summaries, mixed wide points, fresh archive
reload/re-execution and streamed CSV preserve that proof. New populated version1
fields may be rejected by older strict readers; legacy absent-policy/evidence
files remain supported. These are structurally validated software records, not
cryptographically authenticated physical observations.

54 new cases cover10 Core and44 Application tests: exact classification, policy/
proof bounds, owned/managed K6221 and actual source-readback K2450 loopback paths,
source/meter/route drift, both failed attempts, independent cleanup failure,
non-cooperative timed-out abort without overlapping recovery, cancellation,
interruptible pause/resume/drift/abort, four sample topologies, parent scans,
healthy repository fixture, native/archive fresh replay and mixed wide-point CSV,
plus forged ownership/kind/code/time/meter/summary and malformed policy rejection.
Physical K2182/K6221 transports and laboratory acceptance, remaining modes,
advanced modes and native UI acceptance remain pending. No dependency, Rust edit
or laboratory IO is introduced. The full migration goal remains active.
