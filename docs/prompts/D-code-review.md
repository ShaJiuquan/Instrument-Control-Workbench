# Prompt: Code Review

```text
Review this change for bugs, safety regressions, and missing tests.

Read AGENTS.md and docs/CONSTITUTION.md first.

Prioritize:
- layer-boundary violations
- cancellation and async bugs
- output-off/safe-stop regressions
- lease lifecycle mistakes
- unit/ID ambiguity
- brittle tests or missing unhappy-path tests

Findings first, ordered by severity, with file:line references.
```

