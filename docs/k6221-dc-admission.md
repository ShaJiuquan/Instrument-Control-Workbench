# K6221 DC mode admission

This stage strengthens the existing source session contract. It does not add a
TCP/VISA driver or enable configured K6221 hardware. The test double carrying a
Hardware descriptor exercises admission policy without instrument IO.

## Observation boundary

`Keithley6221ModeObservation` carries nullable Delta, Pulse Delta, differential
conductance, waveform and ordinary sweep armed facts plus a nullable operation
condition word. An absent fact is unknown, not false. All five armed facts must
be false, Idle must be set, and calibration, sweeping, wave-started, trigger-wait,
arm-wait, RS232-error and undefined condition bits must be absent. Terminal
sweep-done/aborted, wave-stopped and filter-settled bits are allowed.

The snapshot rejects disconnected mode evidence and a different capture
timestamp. This proves association only: the adapter remains responsible for
fresh coherent device observations. The model cannot detect a dishonest adapter,
guarantee simultaneous SCPI reads, or prevent a physical front-panel change after
observation. No wall-clock freshness threshold or atomic instrument transaction
is claimed.

The explicitly simulated port now supplies DC facts. Legacy snapshots lacking
mode evidence remain usable only for Simulation descriptors; invalid supplied
evidence blocks simulation too. Hardware descriptors require complete evidence.

## Session and run behavior

- Connection and output-register diagnosis remain possible with unknown mode;
  status reports Faulted and cannot confirm safety.
- Configuration, non-output parameter reads, run preparation, source capture,
  output enable and resume require DC admission. Configuration and output writes
  confirm mode again afterward; a mode change faults the operation.
- Manual output-off calls the port's safe-stop operation, even when mode is
  unknown. Stop and disconnect require a subsequent admitted, connected,
  output-off observation. A returned stop command alone is insufficient.
- Existing acquisition checks reject source mode drift before committing a row.
  A failed stop retains every run claim; explicit recovery needs new verified
  stop evidence before release. Pause cannot restore output from unchanged
  settings alone when mode has become unknown or armed.
- Safety notifications are emitted only after successful stop confirmation.

43 new cases (18 Core, 25 Application) cover armed/unknown flags, status bits,
capture association, manual diagnosis, mutation checks, stop/disconnect refusal,
preparation, acquisition, retained leases, recovery and pause/resume. Existing
K6221 execution tests also run. No physical acceptance or new native UI inspection
is performed in this stage; original UI drafts remain untouched and excluded.

## Protocol registration still pending

The [manufacturer reference manual Rev. C, sections 4 and 11](https://download.tek.com/manual/622x-901-01%20%28C%20-%20Oct%202008%29%28Ref%29.pdf)
documents `SOUR:SWE:ARM` and `STAT:OPER:COND?`. An idle observation alone does
not establish the complete contract above. Query forms for waveform and ordinary
sweep armed facts have not been established in this investigation; no invented
queries or cached requested-mode substitutions are implemented. Abort effects on
the current setpoint also need reconciliation with pause configuration checks.
See [TCP investigation](k6221-tcp-investigation.md) for the remaining requirements.
