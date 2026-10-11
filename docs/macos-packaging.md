# macOS application bundle

`scripts/package-macos.sh` creates a Release, self-contained `.app` from the
existing .NET/Avalonia projects. It includes the .NET runtime and the native
libraries produced by `dotnet publish`; the recipient does not need the SDK or
a separately installed .NET runtime. It adds the native bundle identity and
executable manifest needed for macOS application discovery. The Avalonia
application name matches the bundle display name, `Instrument Workbench`.

```bash
bash scripts/package-macos.sh
bash scripts/package-macos.sh --output '/absolute/path/Instrument Workbench.app'
bash scripts/package-macos.sh --runtime osx-x64 --output '/absolute/path/Intel Workbench.app'
```

Run the script on macOS with the repository's .NET SDK installed. ARM and Intel
runtime identifiers are accepted; only ARM has been exercised on this machine.
The default runtime follows the host architecture. An existing output path,
including a dangling symbolic link, is refused. Publishing happens in a fresh
temporary directory and is moved to the chosen destination after manifest,
executable and bundled-runtime checks. Failures remove that temporary directory.
The script does not install the application or change system settings.

The bundle declares macOS 15 as the minimum supported version, corresponding to
the oldest macOS version in the current [.NET 9 support matrix](https://github.com/dotnet/core/blob/main/release-notes/9.0/supported-os.md)
(checked 2026-10-11). It was run on macOS 15.7.3 ARM. This is a development
application bundle; signing, notarization, installer/distribution and Intel
native acceptance remain separate work.

`Contents/Resources/build-info.txt` records revision, dirty-worktree status,
runtime, configuration and self-contained status. A downloaded/exported source
tree without `.git` can also be packaged: revision and dirty status are then
explicitly unknown. This metadata distinguishes an artifact built with local
drafts from a package built from committed source. It is not a signature.

Startup behavior remains the authored application behavior: an existing device
configuration wins; invalid configuration does not silently fall back to
simulation. Do not infer driver support from successful packaging. Application
data remains in its existing local-data directory and is not embedded in the
bundle. Configuration changes apply on the next start. A package does not
grant permission to connect to physical laboratory instruments.

## Native acceptance evidence

A separate local bundle containing the unchanged pending UI drafts was actually
opened through CUA. Native operations confirmed:

- Four explicitly configured simulators appear in the operator interface.
- A selected rotation simulator connects and reports target/readback/rate.
- The device document validates without changing active device instances.
- The two-device field/voltage plan validates, pauses with resource retention and
  source output off, resumes, and completes with 15 rows and output off.
- Manual device operations are disabled while the plan is paused.
- History opens that completed run, validates its archive and exports a CSV;
  the exported file was separately checked for run ID, 15 rows and units.
- A newly launched self-contained Release process reopens the same 15-row archive.
- Closing that Release window during a second active simulated plan finalizes an
  Aborted archive with 10 partial rows and terminates the original process. The
  subsequent AX observation automatically opened a new process; that new window
  is not evidence that the original run continued.

Those operations verify the local draft UI artifact; they do not make the
uncommitted UI drafts part of the published branch. The original 29 draft files
remain unchanged and excluded. CUA's coordinate window-resize operation reported
`noWindowsAvailable` while AX observation/input and the process remained live;
the window was reattached by its known running bundle ID without restarting it.
Minimum 1060x720 layout and failed-safety recovery/close acceptance are still
pending. Whole PPMS migration and hardware acceptance remain active work.
