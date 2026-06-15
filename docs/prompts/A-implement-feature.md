# Prompt: Implement Feature

Use this when asking an AI agent to add a feature.

```text
Read AGENTS.md, docs/CONSTITUTION.md, and docs/AI_WORKFLOW.md first.

Task:
[describe the feature]

Constraints:
- Keep the dependency direction App -> Application -> Core.
- UI must not call drivers directly.
- If this touches persistence, plotting, serial/VISA IO, DI, logging,
  validation, configuration, or another infrastructure concern, list mature
  package options first and wait for my choice.
- Add or update focused tests for behavior.
- Do not refactor unrelated code.

Before editing, state:
1. likely files
2. layer-boundary risk
3. verification command
```

