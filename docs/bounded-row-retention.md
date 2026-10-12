# Bounded live result retention

Archived runs now separate acquired row count from the retained live tail. The
execution service uses a default 4096-row/32 MiB encoded-frame window. It selects
the single configured persistence listener exposing the Core `IRunRowEncoding`
port; the native app already supplies `FileRunArchive`. Core owns no files or JSON
codec. It retains immutable rows plus cumulative count, offset and a SHA256 chain
checkpoint. Capture copies only the bounded tail, preserving earlier snapshots.
The byte bound covers canonical encoded frames, not exact managed heap size.

## Complete storage and evidence

Every raw/calculated row is still delivered under the existing listener backpressure
and flushed by the archive. Producer and archive independently chain canonical
versioned row bytes. Finalization verifies the retained prefix checkpoint, committed
archive prefix, entire tail and final chain, then reconciles a valid undelivered
suffix after safe stop. The existing complete-file SHA256 and terminal count remain
the persisted format. No archive data or version was changed. A partial history
preview carries offset/count metadata and cannot stand in for canonical proof.
Old callers supplying complete snapshots still use complete prefix verification.

The canonical encoder is an actual configured result listener; multiple encoders
reject to avoid ambiguous proof authority. Wrapping an archive in a custom listener
must forward `IRunRowEncoding` to opt into tail retention. Without a durable encoder,
results retain all accepted rows up to the configured count limit and then fault
with cleanup. Capacity is checked before the next acquisition. This prevents silent
row loss; the native app's archive permits longer runs without retaining every row.

## History and boundaries

In-memory history retains at most 32 finalized run previews and at most 1024 rows
per run. Preview metadata distinguishes total count from captured rows; trimmed
previews discard archival chain proof. Full storage history remains available via
existing paged reads and streaming CSV, and explicit full-load APIs retain their
requested row limits. Runtime analysis/group/calculation validators keep their
existing bounded evidence windows independently of live-row eviction.

10 Core +11 Application new cases cover row/byte policy bounds, byte eviction,
immutable capture/indices, digest/count forgery, full fresh archive restore after
live eviction, missing-tail reconciliation, preview limits/run eviction, preview
rejection for archive finalization, non-durable capacity cleanup and calculated
rows replayed after a one-row live window. All data retained by the archive is
reloaded in these cases. No physical IO or new dependency.

Native UI acceptance remains deferred per user. This does not bound notification
subscriber queues, all plot/control caches, blackboard snapshots, whole archive
full-load requests or indexing/scan cost. Those broader data/workflow/controller/
physical requirements remain active goal work; no whole-process memory bound is
claimed. The live window policy currently has an Application constructor option;
a native settings editor for it is still pending.
