# Audit Gates

The main audit command is:

```bash
bash scripts/audit.sh
```

Use strict architecture mode when preparing a real cleanup PR:

```bash
bash scripts/audit.sh --strict-architecture
```

Use formatting mode before a commit when touching many C# or XAML files:

```bash
bash scripts/audit.sh --format
```

## What The Audit Checks

- Restore succeeds.
- Build succeeds with warnings as errors.
- Tests pass.
- Optional formatting check passes.
- Architecture scan reports known risky patterns:
  - App/ViewModel importing Infrastructure directly.
  - View code-behind referencing application or infrastructure services.
  - hard-coded placeholder plot canvas in `MainWindow.axaml`.

The architecture scan is advisory by default because the current scaffold has
known prototype debt. Use `--strict-architecture` once that debt is cleaned.

## Manual Review Checklist

- Does the change preserve `App -> Application -> Core` direction?
- Does any UI command that affects hardware have disabled/running/error state?
- Are units and IDs explicit at boundaries?
- Is cancellation passed through every async operation that can wait?
- Does abort/fault behavior prove output-off cleanup?
- Are demo fixtures separated from production state?
- Did the change avoid unrelated refactors?

