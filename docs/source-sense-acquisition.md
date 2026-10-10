# Coherent source/sense acquisition

`ReadSourceSenseStep` acquires actual voltage and current through one
`ISourceMeterPort.ReadSourceSenseAsync` frame. It requires an explicit matching
source preparation and supports both voltage-source/current-sense and
current-source/voltage-sense operation on the integrated K2450 profile. The
frame's typed values and timestamp become one row, with active scan axes and
elapsed time. Both measured values are available as typed plan variables.

New IV plans use this step. `k2450.source_voltage` remains the requested scan
axis; `k2450.measured_voltage` and `k2450.measured_current` are actual readbacks.
IV plot points use those actual values and the same frame timestamp. A requested
voltage must not substitute for the actual voltage when an instrument limits
its output. Zero current is valid here because this step does not divide V/I.
Resistance acquisition retains its separate minimum-current rejection policy.

The version-1 document kind `read_source_sense` contains `device`, `function`,
`voltageColumn` and `currentColumn`. Unknown fields remain rejected. Saved plans,
full run archives and CSV retain the separate requested and measured columns.
Older `read` plans keep their independent sequential channel-reading behavior.
They do not gain simultaneous sampling through loading or executing them.

The runnable [field/current example](../examples/plans/field-source-sense.plan.json)
uses `field` and `k2450`, waits for each field target and acquires three current
points at each of three field settings. It produces nine actual V/I rows with
both nested axes. Tests load this actual file; fault fixtures deliberately
return actual source values below their setpoints to verify the data path.

## Configuration and lifecycle checks

Acquisition requires connected, enabled output and the requested source function
and sensing mode. A post-read snapshot must still have the same source settings,
compliance, sense configuration and terminals. A mismatched frame or detected
configuration change faults the run before committing that row; previous rows
remain available and normal group cleanup runs.

Pause captures the full source configuration before stopping output. Resume
checks every captured source and held matrix route before restoring axes. It
checks each previously enabled source again immediately before output enable.
A disconnected or enabled source, or changed source/sense/terminal configuration,
rejects restoration rather than silently adopting the new settings. The run then
finalizes through its existing cleanup and quarantine policy. Sources that were
already off remain off. Output enable also checks the preparation's sensing mode.

These are software snapshot checks and a port-level coherent-frame contract.
They cannot prove hardware atomic sampling, prevent front-panel changes between
IO calls or act as a physical interlock. The mock does not model actual compliance
limiting or provide compliance-quality flags. Physical SCPI/transport adapters,
separate source/meter synchronization, range profiles and hardware acceptance
remain migration work. No new dependency is introduced.

`SourceSenseExecutionTests` covers both functions, actual IV curves, zero current,
preflight rejection, changed acquisition/pause configuration, wrong frame
functions, partial transport failure, abort, fresh archive/CSV and the nested
current-source file. Existing resource, matrix, sample and analysis tests remain
part of the full audit.
