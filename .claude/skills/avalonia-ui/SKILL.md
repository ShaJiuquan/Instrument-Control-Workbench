# Avalonia UI Skill

Use this skill for `.axaml`, ViewModel, command, binding, or desktop UX work.

## Required Reading

1. `AGENTS.md`
2. `docs/CONSTITUTION.md` sections 1 and 2
3. The XAML file and its ViewModel together

## Rules

- Use compiled bindings with `x:DataType`.
- Keep ViewModels free of concrete drivers.
- Bind hardware-affecting buttons to commands with state.
- Split large views into named controls when adding more surface area.
- Separate demo fixture data from production state.
- Do not add fake charts where a plotting contract is required.

## Verification

Run the app and inspect at the minimum supported window size after UI changes.

```bash
dotnet run --project src/InstrumentControlWorkbench.App
```

