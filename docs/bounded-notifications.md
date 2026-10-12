# Bounded notification observers and live display

The notification hub broadcasts facts; archive/result listeners remain the
complete data path. A slow observer must not block acquisition, cancellation,
output-off, or finalization.

## Queue policy

`Subscribe()` and the legacy `Reader` keep at most 4096 facts by default (caller
capacity 1–100000). The legacy reader still observes from hub construction for
compatibility. Overflow detaches only that observer, drains its accepted prefix,
then throws `NotificationLagException` through reading and completion. Other
observers and publishers continue. Reliable subscriptions are suitable for
short-lived ordered observers; they are not a durable event journal.

`SubscribeDisplay()` defaults to 512 pending facts. On capacity pressure it
removes the oldest pending measurement point, data row, log, step-start, or
step-completion display fact. It preserves the relative order of all remaining
facts. If all queued facts are critical, an incoming display fact is omitted.
Run lifecycle, safety, instrument/device snapshots, and matrix route facts are
never silently evicted. If another critical fact cannot fit, the observer fails
explicitly rather than claiming its state is complete. `OmittedDisplayFacts`
counts omitted incoming and evicted pending display facts over that subscription's
lifetime. It is not a count of lost acquired rows.

There is no producer backpressure and no notification-triggered hardware action.
The mailbox serializes publication, reads, cancellation and disposal; async wakeups
run outside the locks. Capacity bounds queued fact references, not exact managed
heap or process RSS. Wide row payloads and other caches still need separate limits.

## Desktop consumption

The shell owns a display subscription and awaits each cancellable dispatcher
operation before taking the next fact. Thus it has at most one scheduled UI
operation, in addition to the bounded mailbox. Closing cancels that scheduled
operation and disposes the subscription after existing safe-stop/finalization.

The IV plot keeps 4096 delivered points; its recent table and event log keep 200
items. The monitor states the retained counts and omitted update count. Complete
records remain in the configured archive. Curves show delivered recent samples,
which can be sparse when display updates are omitted.

Row and terminal notifications carry the application's absolute acquired row
count. IV and plan totals use this count, independent of displayed tail size or
skipped updates; older synthetic facts without it retain increment compatibility.
This field changes notification metadata only, not stored plan/archive formats.

On critical overflow the shell marks connection, run and safety displays stale,
shows an actionable error, and blocks new work, edits, pause and resume. Owned-run
abort, safe-stop/recovery and close remain available through their existing
application guards. Reopening the workbench after safely stopping is currently
required; automatic observer resynchronization is not implemented.

## Verification and limits

Tests cover reliable observer isolation, ordered critical preservation, display
fact omission, explicit critical overflow, cancellation/disposal wakeups,
concurrent publish/read/dispose, plot/table limits, absolute totals, and abort
while the observer is stale. A real simulated 300-row run completes with a stalled
reliable subscriber and slow display; a fresh archive loads every original row
while the live data set retains only three.

Build and managed ViewModel tests do not establish rendered layout or minimum
window acceptance. The user requested code checks first; native UI checks remain
deferred. No physical instrument IO, new dependency, or Rust change is involved.
