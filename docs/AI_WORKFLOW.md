# AI Workflow

Use this flow for every non-trivial change.

## 1. Classify

Name the task category:

- UI/Avalonia layout
- ViewModel binding and command behavior
- Application workflow
- Core domain model
- Infrastructure driver
- Tests
- Build/governance

State the files likely to change and the verification command.

## 2. Read

Read:

- `AGENTS.md`
- `docs/CONSTITUTION.md`
- The closest existing implementation
- The relevant tests

For UI work, read the XAML and ViewModel together. For instrument work, read the
port, service, and mock driver together.

## 3. Decide

Before coding, answer:

- Does this cross a layer boundary?
- Does it touch safety, output, cancellation, or lease behavior?
- Is a dependency/library decision required?
- What test would fail before the fix?

## 4. Implement

Make the smallest change that satisfies the request.

Avoid:

- unrelated cleanup
- speculative abstractions
- adding "future" interfaces with one caller
- moving code just to make a diff look architectural

## 5. Verify

Run the narrowest useful command first, then the repo audit when the change is
ready:

```bash
dotnet test InstrumentControlWorkbench.sln
bash scripts/audit.sh
```

For UI work, run the app and inspect at the minimum window size.

## 6. Report

Report:

- what changed
- what was verified
- any remaining risk or debt

Do not hide skipped checks.

