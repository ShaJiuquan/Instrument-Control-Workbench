# K6221 TCP protocol investigation

No K6221 hardware adapter is registered by this stage; configured hardware still
fails validation without a simulated fallback. Existing pure-DC simulation and
independent acquisition remain available.

The subsequent [DC admission stage](k6221-dc-admission.md) adds typed nullable
mode evidence and session/run checks. It does not supply the missing hardware
queries or change registration status.

Primary manufacturer sources checked on2026-10-11:

- [622x driver release notes, section4.4.3](https://download.tek.com/software/supporting_files/622xB01.1DriverReleaseNotes_0.pdf)
  establish a socket resource and port1394 for6221.
- [Keithley IO Layer release notes, section4.4](https://download.tek.com/software/supporting_files/kiol-850c10_release_notes_KIOL-850C10.pdf)
  distinguish6221 raw sockets from VXI-11.
- [6220/6221 reference manual RevC](https://download.tek.com/manual/622x-901-01%20%28C%20-%20Oct%202008%29%28Ref%29.pdf),
  sections4,5,7,8 and11, describe autonomous source modes, aborts and status.

The existing BCL `TcpScpiTransport` can carry raw SCPI without a package decision.
Transport availability does not establish a pure-DC safety contract. Generic
`ABOR` can leave a sweep armed. Dedicated sweep/wave abort commands exit modes
but can zero the current setpoint; naive substitution would break existing pause
configuration proof. Operation-condition bits distinguish running/trigger/arm
wait states and idle. Delta, pulse-delta and differential-conductance arm queries
are documented, but wave/sweep arm query forms are not established here. An idle
bit alone cannot prove all autonomous actions unarmed.

Before registration: establish supported fresh DC/mode/armed evidence and stop
semantics across firmware versions; prove safe output-off and setpoint behavior;
verify range/compliance/delay/error queue/identity queries with independent local
peers; then exercise lifecycle and archive evidence. Do not invent query forms,
clear errors/reset the instrument to hide rejection, or treat cached mode as fresh
hardware proof. Physical acceptance remains a separate required stage.
