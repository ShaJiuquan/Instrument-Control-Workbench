# Run archives and export

The result storage adapter is optional and injected into the existing measurement
service as an `IRunResultListener`. IV recipes and generic typed plans both use
it. `RunHistoryService` exposes catalog, open and export use cases without device
access. Core owns the immutable data contracts; Infrastructure owns file IO and
serialization. The published desktop composition has not yet enabled this API;
plan/history UI integration is awaiting native acceptance.

## Version 1 layout

```text
<archive root>/<run Guid in N format>/
  metadata.json
  rows.ndjson
  final.json        # present only after successful archive finalization
```

Metadata has `format: instrument-workbench-run` and `version: 1`. It records run
ID/name/start time, result column descriptors (units, access, bounds, dependencies,
optional columns and source addresses), numeric recipe metadata, instrument
parameter/configuration snapshots, the versioned plan document and sample
ID/name/notes/attributes. `driver_kind` and `stop_policy` distinguish source
simulation and cleanup policy. Device snapshots describe preparation-time state,
not current device state or the final physical condition.

Every row is one UTF-8 JSON object followed by LF: 1-based consecutive index,
ISO timestamp and a typed quantity map. Schema validation applies both while
writing and reading. Optional absent measurements remain absent; no NaN padding
is added. Rows are flushed before their written count advances. If cancellation
interrupts a write, finalization truncates unconfirmed bytes and reconciles rows
that were already committed to the application data set. It rejects altered
previously delivered rows or mismatched run provenance.

The separate atomic terminal marker records acquisition/cleanup state, finish
time, row count, data byte length and the SHA256 of the exact NDJSON bytes.
Existing run folders are never overwritten. Metadata and terminal frames reject
unknown versions/fields, duplicate fields, missing required fields and invalid
domain values. Each metadata object and row is bounded at 8 MiB. Plan documents
also retain their own size/tree limits.

## Reading after restart

Opening a new adapter over the same directory preserves result history. Catalog
listing validates metadata/terminal records and reports damaged folders alongside
healthy ones; it does **not** read every data row or verify the entire checksum.
Open and export perform that complete row validation. A finalized archive must
match its declared length, count and checksum. The checksum detects data changes;
it is not authentication or a checksum over metadata.

Without a terminal marker the UI must label a record **unfinalized** even though
the stored data-set state is `Running`. Complete rows can be inspected. Only an
unterminated final frame is excluded with an explicit incomplete-tail flag;
corruption in any complete row fails the operation. A reader captures a byte
boundary when it opens an active stream, so it sees a prefix of that run.

These observations never connect, stop or resume instruments. An old data record
cannot establish current physical safety. Startup device discovery, verified
stop/reconnect and checkpoint-based execution resume remain separate work.

Full reload defaults to 100,000 rows and accepts an explicit limit from 1 to
10,000,000. For larger data use streamed CSV export. Acquisition and
`InMemoryRunHistory` still retain all rows in RAM, so this adapter does not by
itself make very large running experiments memory-bounded. Queries, pagination,
retention, history plots and settings persistence remain pending.

## CSV version 1

CSV uses invariant numeric formatting and quotes all fields. Preamble records
carry format/version, ID/name/state, start/finish timestamps, sample metadata,
recipe, instrument snapshots, column schema/units and full plan JSON. The table
header starts with `row,timestamp` followed by column ID/unit labels; missing
optional values are blank. Unfinished exports use the state `Unfinalized` and
include an incomplete-tail notice when applicable.

Parse **CSV records** until the first field equals `row`; do not skip a fixed
number of physical lines. A quoted plan or metadata field may contain embedded
newlines. CSV is an exchange format, not a plan/import or execution-resume format.

Export streams rows to a sibling temporary file, validates the complete stream
and replaces the requested destination only after success. Cancellation, invalid
data or a write failure preserves a previous destination. Destination paths
inside the source archive are rejected using normalized lexical paths; this is
not a sandbox or protection against filesystem aliases/symlinks.

## Finalization limits and evidence

The engine confirms device stop policies before delivering terminal snapshots.
Data-set terminal state describes acquisition/cleanup. `RunOutcome` additionally
records listener failures, so a completed data set may have a faulted overall
outcome if another listener fails. A missing terminal marker is never reported as
successful archive finalization. Finalizers currently have no timeout or durable
retry queue. Independent cleanup runs without the acquisition cancellation token.

Flush and rename reduce partial writes but do not guarantee survival of power
loss; no fsync/disk-controller guarantee is claimed. SHA256 does not replace a
backup. No new package or database dependency was added.

`RunArchiveTests` exercise fresh-instance reload, aborted partial acquisition,
storage admission failure before output enable, cancellation reconciliation,
unfinished tail handling, corrupt records/versions, CSV metadata/units and
optional columns, unchanged exports after failure, row limits, wide buffered rows
and damaged-directory catalog isolation. `RunMetadataTests` check immutable
copies and field limits. Hardware and desktop visual acceptance are separate.

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
