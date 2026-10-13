# Polarization authoring

The experiment palette now provides **极化脉冲读出** and **极化电压迭代**.
Select a registered K2450 source and explicitly set sensing mode, target,
read voltage, compliance and output column names. Voltage/time, readout and
result/recovery settings are grouped into separate named views. Environment
reads use the existing environment editor.

For a sweep, add a voltage iterator with a variable name and point list or
generator; select its body and append a pulse. Choose the scoped variable in
the pulse target editor and record the requested voltage column. Saving or
editing a plan does not connect instruments. Full plan validation checks
source preparation, wiring and target scope before a run.

Existing pulses retain matrix routes, sampling configuration and target
expressions. A routed pulse cannot silently switch sources. Calculated
measurement wrappers retain custom formulas when their pulse body is edited.
Invalid finite values, duration, nominal ranges, column collisions and attempts
are rejected by the existing domain validation. Busy state disables editing
and append/apply commands.

Nineteen App cases cover these behaviors, including a saved three-point
5/0/-5 V simulation with 1 V actual readback, confirmed output-off, fresh
archive loading and CSV export. This is host-timed execution; hardware timing
and sample behavior are not established by simulation. Native visual checks
at the minimum window size are deferred by the user's instruction to continue
code checks. No real instrument or desktop application was launched.
