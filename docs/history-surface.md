# Paged desktop history and scientific plot surface

The shell now uses `PagedRunHistoryViewModel`; the previous full-catalog/full-run
screen has been removed. Catalog pages retain at most50 summaries and plotted
pages at most500 source samples, subject to the existing byte budgets. Search,
next catalog, X/Y selection, next data page, jump, explicit reload and cancellation
use the existing version-pinned Application reads. The backward control moves the
start offset back500 source records; it is not an inferred prior byte-limited page.

HistoryDataView separates curve and current-page records. Every coordinate keeps
its original units, record index, time, row kind and acquisition quality. Scatter
is default. Optional lines connect recorded order only: missing coordinates,
failed acquisitions, row-kind changes and discontinuous indices break segments.
They do not claim continuous physical measurements or a fitted trajectory.
Markers distinguish reading/routed/analysis/group provenance; failures are red
crosses, warnings amber, incomplete/unobserved quality gray. Constant/zero pages
retain their points; empty pages invent no coordinates. Scaled bounds avoid
overflow for finite extreme values. Hover exposes original values and provenance.

Native1060x720 inspection found chart/title overlap in the initial layout.
The right panel now scrolls, export is expandable and the chart has a finite
viewport. Native reinspection verified corrected layout,21-point curves, axis
change invalidation/re-read, record-order lines, source values/units/times/quality
in the record tab, scrolling and complete21-row CSV export.

A505-row explicitly named SYNTHETIC archive fixture, written through the real
FileRunArchive without instrument IO, verified native1–500/501–505 navigation,
backward movement, jump500–505, zero and absent Y values, and complete505-row
export while only six records were displayed. Catalog-binding null resets now
preserve the same selected run, trusted plot and custom export destination.

Export remains a full-run, freshly validated archive operation independent from
the displayed plot version. The UI says this explicitly. Requested ID/path are
captured before busy callbacks; changing selection cancels presentation and
cannot redirect the in-flight export. The screen serializes/drains export with
reads. Cancel does not undo an already committed file or guarantee rollback for
a noncooperative archive adapter. Closing the shell waits for the actual pending
screen operation before disposing shared storage once. Browsing never commands
devices or infers physical safety from a historical record.

14 new App cases cover geometry, gaps, provenance/quality, finite extremes,
actual shell paging/export, cancellation/drain/storage ownership and catalog
binding resets. The four prior history acceptance tests were migrated rather
than dropped. All94 App cases pass.

Remaining: integrity validation still scans the archive per page; bounded UI
retention is not indexed/random-access IO. Live acquisition memory, overview/
decimation, multi-series and analysis tools, graph editing and physical acceptance
remain incomplete. Repaired catalog-refresh native behavior and clean committed
package parity require their own inspection; earlier screenshots are not proof.
