# C# Quality Skill

Use this skill for C# edits, tests, build files, or refactors in this repo.

## Required Reading

1. `AGENTS.md`
2. `docs/CONSTITUTION.md`
3. `docs/AI_WORKFLOW.md`
4. The closest existing implementation and tests

## Rules

- Preserve nullable correctness.
- Pass `CancellationToken` through async operations that can wait.
- Do not use `.Result` or `.Wait()`.
- Keep domain types in Core and IO adapters in Infrastructure.
- Add focused xUnit tests for behavior changes.
- Avoid new helpers unless at least three call sites use them immediately or
  they define a real architecture boundary.

## Verification

```bash
dotnet test InstrumentControlWorkbench.sln
bash scripts/audit.sh
```

