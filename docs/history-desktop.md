# Paged desktop history state

`PagedRunHistoryViewModel` is the App-layer consumer of `RunHistoryService`.
It has no driver/Infrastructure dependency and creates no storage service. The
composition root owns the shared history service; the screen owns its reads.

The current native history draft has not been replaced or wired to this model.
Mac lock prevents window inspection and visual acceptance. This stage verifies
page/command/lifetime behavior through the real file archive and controlled
noncooperative read adapters; it does not claim a rendered history chart.

## Commands and bounds

- Refresh: first 50 lightweight summaries, optionally filtered by name.
- NextCatalog: next keyset page, replacing the visible summaries. Editing the
  search invalidates the old cursor until refresh. No catalog-page stack kept.
- Open: selected metadata and first verified XY page. Default Y uses the first
  column with dependencies; default X uses its first dependency. Otherwise the
  first two schema columns are used, or the same sole column for both axes.
- ReadPlot: selected axes, first page. Axis changes clear the previous plot and
  pin; unknown axis IDs cannot enable this command.
- NextPlot / Jump: preserve the captured archive version. Jump input is a
  one-based source-row number, invariant integer 1 through 2147483648. Gaps
  count as source records. A valid out-of-range jump displays an empty page.
- Reload: explicitly capture a fresh version while preserving chosen axis IDs.
- Cancel: cancel the current read; keep already verified data.

The row query uses the existing 500-record / 16 MiB source UTF-8 defaults.
Only one catalog page and one plot page are retained. Full selected metadata,
managed overhead and transient allocations have separate existing bounds.
Every data page still validates the entire captured archive; there is no
index/cache/constant-time seek or full-run overview/downsampling.

## Publication and lifetime

The model belongs to the UI thread. Commands reject overlapping reads even if
forced through `ExecuteAsync` while disabled. Editing selection, axes, search
or jump input cancels pending work and supersedes its revision. Results from
old reads cannot replace the new selection. Busy lasts until the actual read
ends, including a port that ignores cancellation.

Archive replacement or corrupt records preserve the last trusted plot, report
error and disable pinned navigation pending explicit reload. Reload failure
also keeps that trusted page and its version. Changing axes starts a new view;
this is not continuation of an older version. The page's metadata and axis
descriptors are the authority for the displayed plot.

One sample represents one source record, with original units/order/indices,
nullable coordinates, provenance kind and whole-row quality. Missing values
remain gaps; unknown quality is not treated as observed-good. Display summaries
distinguish missing coordinates, failed acquisition, warnings and unobserved
status. Historical completion is never proof of current device state.

Read/disposal tasks are reserved before busy/close binding notifications,
so synchronous closing and reentrant disposal cannot report completion before
a reserved read drains. Concurrent disposal calls await the same pending read. No late publication is
allowed after disposal starts. The model releases retained pages/axes when the
read drains and leaves shared storage usable. Native screen wiring, curve
rendering/series partitioning, minimum-window and fault/recovery visual checks
remain pending. Original29 UI drafts are preserved and excluded from this stage.
