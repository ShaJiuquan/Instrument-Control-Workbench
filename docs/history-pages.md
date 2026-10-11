# Paged archive history

Core defines immutable catalog/row queries, cursors, read versions and results.
Application `RunHistoryService` exposes `BrowseAsync`, `ReadMetadataAsync` and
`ReadPageAsync` through `IRunArchive`; Infrastructure performs file IO and uses
the existing shared schema/provenance validators. No new packages or device
operations are involved. Existing full open, catalog and CSV APIs remain.

## Catalog summaries

`BrowseAsync(RunArchiveCatalogQuery, token)` returns only a newest-first page
of lightweight summaries: ID/name/start/finish/state, optional terminal row and
byte counts, sample ID/name, instrument count, plan availability and optional
blackboard entry count. Null blackboard count means unknown; zero means an
observed-empty snapshot. It retains no full plan/schema/notes/attributes, row
list or variable payload in the result. Select a run with `ReadMetadataAsync`
for its complete frozen metadata and variable history.

Filters compose: ordinal case-insensitive run-name substring, exact sample ID,
inclusive start/exclusive end time, state and finalized-only. Unfinalized
storage state does not mean live hardware. Sort order is StartedAt descending
then Guid.CompareTo descending; the typed continuation cursor uses both keys.
Newer runs appearing between calls do not shift an existing cursor's next
page. Refresh starts a new listing. This is a per-call scan, not a transaction
over all pages: edits/removals/backdated additions may change later results.

Catalog query page size is 1–200 (default50). Summary text has a separate
1–8,388,608 UTF-16 character budget (default65,536), counting name/sample name/
sample ID. The implementation keeps only the newest matching prefix and
remembers the first excluded position, so a smaller older name cannot jump
past an intervening excluded run. If the first matching summary cannot fit,
it rejects rather than skip/truncate that run. MatchingRuns counts healthy
matching records after this query's cursor; ScannedRuns counts recognized
run directories. Metadata failures contribute to IssueCount, with at most
PageSize issue details under another independent text budget; HasOmittedIssues
reports omitted details explicitly. Detailed metadata and terminal variable
checks still happen for each scanned record. Catalog rows are **not** inspected
until a data read/export is requested.

## Row pages and consistency

`ReadPageAsync(id, RunArchiveRowQuery, token)` uses a zero-based offset and a
1–2000 row limit (default500), plus a 1–67,108,864-byte source-record budget
(default16 MiB). It returns a contiguous prefix under both limits. A single
requested record that cannot fit rejects; no record is silently skipped or
truncated. ReturnedDataBytes counts exact UTF-8 NDJSON frames including LF,
not UTF-16 characters or managed allocation size. NextOffset is absent at
end; empty or out-of-range pages still validate the captured archive.

Only requested rows are retained for the result. The reader streams and
validates **every** record before returning: sequence, required fields, units,
routing, environment, quality, independent-source and sample/wide summary
evidence, partial-tail policy and terminal count/length/checksum. Existing
bounded provenance windows cover raw records outside the selected page. A
corrupt later record or rehashed derived mismatch rejects even the first page.
Header and terminal record fingerprints are rechecked during the read.

Every page returns a RunArchiveReadVersion containing run ID, raw header and
optional final SHA-256, captured data length and full captured byte SHA-256.
Pass it as ExpectedVersion when requesting the next page. Changed metadata,
rewritten data (including equal-length edits), appended data or finalization
then reject with a refresh message, instead of mixing versions. A refreshed
query without ExpectedVersion observes current storage. These fingerprints
are consistency evidence, not signatures/authentication or device safety.

For unfinalized runs, each read fixes the file length once and only returns
complete LF-terminated records within that byte boundary. An incomplete last
record is excluded and flagged; its bytes remain part of the version hash.
Later appends/finalization do not retroactively promote the returned view.
ObservedRowCount is that boundary's complete row count, while metadata terminal
counts remain unknown. Both legacy archive version1 (unknown variables) and
version2 (explicit snapshot capture state) remain readable.

## Verified scope and remaining work

Tests cover restarted 100,005-row storage beyond the default full-open limit,
contiguous size/byte pagination, wide UTF-8 records spanning IO buffers,
deterministic tied timestamps and randomized text-budget prefixes, filters,
new-run insertion, bounded corrupt catalog details, source/version/cancellation
guards, equal-length edits, append/terminal transitions, incomplete tails,
legacy v1, full typed/empty variable metadata, and cross-page derived evidence.

Memory is bounded by the selected page, source record/frame, frozen metadata
and existing provenance windows. Source-byte limits do not measure managed
object overhead. Catalog scans all recognized metadata for ordering/filtering;
each row page performs a full integrity scan. These are bounded-retention APIs,
not an indexed database or constant-time seek. Live datasets/in-memory history
still retain their rows. Durable indexing/query cache, lazy/paged plots/native
history integration and UI visual acceptance remain work. No UI drafts, Rust,
physical instrument drivers or package dependencies were changed.
