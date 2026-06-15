# Prompt: Fix Bug

```text
Read AGENTS.md, docs/CONSTITUTION.md, and docs/AI_WORKFLOW.md first.

Bug:
[describe observed behavior]

Expected:
[describe expected behavior]

Requirements:
- Reproduce with a focused test when practical.
- Fix the smallest responsible code path.
- Preserve safe-stop/output-off behavior if measurement or instrument code is
  involved.
- Do not include cleanup or refactor riders.
- Run the narrow test, then bash scripts/audit.sh.
```

