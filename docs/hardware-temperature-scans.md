# Hardware temperature ramp scans

HardwareTemperatureScanStep migrates the hardware branch of Rust
scan_temp_ramp. It uses the existing independently implemented C# executor,
controller port and run-owned device group. Controller configuration must
explicitly support independent ramp/control flags. This is protocol-level
orchestration verified through simulation; no physical temperature-controller
adapter or calibration is claimed.

The session parks a freshly observed live temperature before enabling control,
programs the first point with ramp OFF and verifies the requested rate. Optional
first-point stability requires consecutive fresh observations with strict
absolute error < tolerance. Excursions and pause reset the streak. Only after
this preparation succeeds does it enable hardware ramp and program the final
point. The hardware port owns actual motion; experiment children run between
observations instead of waiting for each setpoint ramp to finish.

Four modes are supported:

- Threshold samples when actual temperature crosses each point in the declared
  direction; no tolerance silently substitutes for a crossing.
- EstimatedSetpoint samples when nominal rate and active ramp time cross each
  point, but records the actual observed temperature, even if it lags.
- Timed samples at a minimum wall-clock interval between sample starts, including
  child acquisition time. Total is ceil(nominal duration / interval) + 1, an
  estimate that can be exceeded during settling.
- Immediate samples back-to-back, publishing unknown total 0.

Points require >=2 Kelvin values in one nonzero direction, including compatible
segmented/generator inputs. Actual Kelvin, zero-based index and progress total
are axes for ordinary child measurements. Children cannot reconfigure this
controller or take over another active scan of it. Physical bounds, rates,
units, strict mode fields and full hidden child branches validate before IO.
Dynamic modes use the existing global step budget.

Every observation confirms connection, exact programmed target/rate and both
ramp/control flags around the readback. First stability and threshold waits
have explicit deadlines. Timed/immediate endpoint guard uses nominal duration
plus stable timeout. Unlike Rust's warning-and-success fallback on guard expiry,
C# faults if the endpoint was not actually observed. The guard also bounds
waiting for a long timed interval. Arrival observed in that wait satisfies the
guard while the minimum interval remains respected, with continued fresh
confirmation. Late reads cannot claim on-time completion.
It is checked around sampling waits/reads; use an outer TimeoutStep to bound
child acquisitions themselves. Pause holds current position under the lease, preserves
configuration, and resumes the original end target. Estimates and deadline
progress exclude held pause time so no fabricated catch-up is recorded.

Normal scope completion confirms ramp OFF and control ON. Failure, cancellation
or logical failure retains a ramp-OFF obligation through root cleanup and
explicit recovery; a successful position hold cannot erase it. Paused hold
proofs are tracked separately and cannot be reused as terminal ramp-OFF proof.
An electrical source may already be OFF while the full group stays leased
because controller ramp OFF failed. Recovery must establish fresh confirmation.
The whole run keeps the existing terminal live-position hold policy, rather
than claiming continuing thermal motion after releasing ownership.

Native version 1 adds hardware_temperature_scan with its complete executable
body. PPMS scan_temp_ramp/temperature_ramp/temp_ramp, or scan_temperature/scan_temp
with a ramp scan_mode, require explicit ramp_mode=hardware. The default software branch now uses SoftwareTemperatureScanStep; see
software-temperature-scans.md. Hardware mode is never inferred from the default. Native plan, graph traversal, terminal blackboard, fresh archives
and CSV retain settings, mode and actual sample axes. A shared native form and separate hardware palette now support authoring; see
temperature-scan-authoring.md.

The headless example uses headless-cooling.devices.json (SIM-CRYOSTAT),
headless-hardware-ramp-context.plan.json and
ppms-hardware-temperature-ramp-import.json. Tests cover both directions/four
modes, first-point streak/reset/timeout, stationary sensors, pause/abort/deadline,
held-time estimates, drift, source/controller retained ownership and recovery,
result failures, strict codec/import, 128-depth and archive/CSV/graph boundaries.
No dependencies, Rust edits, native window actions or laboratory IO. Broader
PPMS and physical thermal acceptance remain active.
