# Paged history plot projection

`RunHistoryService.ReadPlotPageAsync` accepts a run ID, the existing bounded
`RunArchiveRowQuery`, two schema `ParameterId` axes and a cancellation token.
It uses the archive page integrity/version gate before projecting any samples.
No instrument operation or extra plotting dependency is involved.

```csharp
var plot = await history.ReadPlotPageAsync(runId,
    new RunArchiveRowQuery(offset: 0, pageSize: 500),
    xColumn, yColumn, cancellationToken);
// Continue using plot.NextOffset and plot.Version in the next row query.
```

Axis descriptors preserve schema IDs, labels and physical units. Unknown axes
fail with an argument error. Each source row yields exactly one sample, in
record order, with its original one-based row index and timestamp. An optional
missing axis yields a nullable coordinate and `HasCoordinates == false`;
missing, zero and negative values stay distinct. Gaps count toward page size,
byte budget and next offset. There is no sorting, interpolation, conversion,
averaging, quality filtering or downsampling.

Samples preserve whole-row quality state, issue flags and observed-quality
column count. These are aggregate acquisition observations, not a claim that
only the selected axes were measured, or that absent status flags prove good
quality. Failed acquisitions remain failed, and unobserved quality remains
unobserved. The row-kind discriminator distinguishes plain/routed readings,
sample analysis and measurement-group summaries. It is not a series identity;
renderers must choose compatible data/series and retain gaps before connecting
points. Full scientific evidence remains accessible through `ReadPageAsync`.

The result retains selected-run metadata, version, observed row count, source
byte count, next offset and incomplete-tail status, without retaining source
rows or their unselected values. At most the existing query's 2,000 records
are projected. The source UTF-8 byte bound is not a managed heap bound;
metadata and transient source-page allocations have their own existing limits.

Every query still scans and validates the entire captured archive. It does not
provide constant-time seeking, a full-run decimated overview, a history index,
or a cache. The live dataset still retains its rows. Native chart integration
and visual acceptance are pending; the existing live I–V control and desktop
history draft are unchanged.
