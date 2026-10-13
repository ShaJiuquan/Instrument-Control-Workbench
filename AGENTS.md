# Instrument Control Workbench AI Rules

> Before writing C#, XAML, tests, or build scripts in this repository, read
> [docs/CONSTITUTION.md](docs/CONSTITUTION.md).
>
> The constitution is the locked project rulebook. If it conflicts with this
> file, the constitution wins.

This project is a native Avalonia desktop workbench for laboratory instrument
control. AI assistance is welcome, but unsafe UI-to-driver shortcuts, fake
architecture, and speculative abstractions are not.

## Rule Index

| Task | Read first |
| --- | --- |
| Any implementation or refactor | [docs/AI_WORKFLOW.md](docs/AI_WORKFLOW.md) |
| C# / Avalonia / instrument-control shape | [docs/CONSTITUTION.md](docs/CONSTITUTION.md) |
| User-requested team roles and collaboration | [docs/TEAM_WORKFLOW.md](docs/TEAM_WORKFLOW.md) |
| Current durable decisions and known risks | [docs/AI_MEMORY.md](docs/AI_MEMORY.md) |
| Audit gates and review checklist | [docs/AUDIT.md](docs/AUDIT.md) |
| Copy-paste prompts for AI sessions | [docs/prompts/](docs/prompts/) |
| Current governance gap review | [docs/reviews/2026-06-15-ai-governance-gap-review.md](docs/reviews/2026-06-15-ai-governance-gap-review.md) |

## Hard Rules

1. UI never talks to instrument drivers directly.
   View -> ViewModel -> Application service -> Core port -> Infrastructure
   adapter is the allowed direction.
2. No new infrastructure dependency by instinct. For persistence, plotting,
   serial/VISA IO, logging, DI, configuration, or validation libraries, present
   mature options and wait for the user's choice unless the dependency is
   already in the project.
3. No large XAML dumps. Split screens into named views/components when a file
   stops being easy to scan, and move reusable visual values into resources.
4. Instrument safety beats demo smoothness. Abort, safe stop, cancellation, and
   output-off behavior must be testable.
5. Units and IDs must be explicit domain types at boundaries. Avoid naked
   strings and doubles in long-lived domain APIs.
6. Tests must cover unhappy paths for instrument state, leases, validation,
   cancellation, and notification ordering.
7. Do not refactor unrelated code while fixing a bug. Every changed line should
   point back to the request.

## Local Commands

```bash
bash scripts/audit.sh
bash scripts/audit.sh --format
bash scripts/audit.sh --strict-architecture
dotnet run --project src/InstrumentControlWorkbench.App
```

The git hook is stored in [.githooks/pre-commit](.githooks/pre-commit). Enable
it once per clone:

```bash
git config core.hooksPath .githooks
```

