# AI Governance Gap Review - 2026-06-15

Scope: initial scan of `Instrument-Control-Workbench` for AI governance and
code-shape risks.

## High Priority Findings

1. `MainWindowViewModel` constructs infrastructure directly.

   Evidence: `src/InstrumentControlWorkbench.App/ViewModels/MainWindowViewModel.cs`
   imports `InstrumentControlWorkbench.Infrastructure.Drivers` and creates
   `MockKeithley2450SourceMeter`.

   Risk: future AI sessions will copy this and make ViewModels the composition
   root. This weakens testing and makes real hardware wiring unsafe.

2. The main Avalonia view is a monolith.

   Evidence: `src/InstrumentControlWorkbench.App/Views/MainWindow.axaml` contains
   shell navigation, cards, instrument detail, recipe controls, plot placeholder,
   jobs, logs, and footer in one file.

   Risk: AI will keep appending controls, making binding state, visual hierarchy,
   and review harder.

3. Safety commands lack UI command-state modeling.

   Evidence: Connect, Start Run, Abort, Output On, Safe Stop are visible in the
   shell, but only some are bound to commands and no `CanExecute` state is
   modeled for connection/running/safe-output state.

   Risk: operator-facing controls can be shown as available when the backend
   would reject or mishandle the action.

4. ViewModel owns a background notification listener without disposal.

   Evidence: constructor starts `ListenForNotificationsAsync` using a private
   `CancellationTokenSource`, but the ViewModel has no disposal path.

   Risk: leaked background work and hidden exceptions when windows are recreated.

## Medium Priority Findings

1. Demo data is mixed with production ViewModel state.

   Evidence: `BuildShellData()` fills fake instruments, nav counts, and jobs in
   `MainWindowViewModel`.

   Risk: AI cannot tell what is real state versus portfolio filler.

2. Placeholder plot is hard-coded geometry.

   Evidence: `MainWindow.axaml` uses `Canvas`, `Rectangle`, `Line`, and
   `Polyline` for a static plot.

   Risk: it looks like UI progress while no real plotting contract exists.

3. NotificationHub is single-reader.

   Evidence: `NotificationHub.Reader` exposes one `ChannelReader`.

   Risk: adding logs, UI, persistence, or tests as concurrent consumers will
   silently distribute messages instead of broadcasting them.

4. Time is read from `DateTimeOffset.Now` in services and notifications.

   Risk: tests for expiry, notification ordering, and run IDs become harder and
   more brittle as behavior grows.

## What Was Added

- Repository AI entrypoint: `AGENTS.md`
- Project constitution: `docs/CONSTITUTION.md`
- AI workflow: `docs/AI_WORKFLOW.md`
- Durable memory: `docs/AI_MEMORY.md`
- Audit guide: `docs/AUDIT.md`
- Prompt templates: `docs/prompts/`
- Local skills: `.claude/skills/`
- Audit script and pre-commit hook: `scripts/audit.sh`, `.githooks/pre-commit`

