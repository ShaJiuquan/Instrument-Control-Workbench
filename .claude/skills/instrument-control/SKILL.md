# Instrument Control Skill

Use this skill for instrument ports, drivers, leases, measurement runs,
notifications, and safety behavior.

## Required Reading

1. `AGENTS.md`
2. `docs/CONSTITUTION.md` sections 3 through 8
3. Existing port, service, mock driver, and related tests

## Rules

- Acquire exclusive leases before enabling output.
- Safe stop must run on completion, abort, and fault.
- Keep protocol quirks inside Infrastructure adapters.
- Publish operator-meaningful notifications.
- Validate instrument limits before driver state changes.
- Tests must cover at least one unhappy path for safety-sensitive changes.

## Verification

```bash
dotnet test tests/InstrumentControlWorkbench.Core.Tests
dotnet test tests/InstrumentControlWorkbench.Application.Tests
bash scripts/audit.sh
```

