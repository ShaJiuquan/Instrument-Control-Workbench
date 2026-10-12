# Repeatable native window layout checks

The desktop product accepts optional `--window-size WIDTHxHEIGHT` or
`--window-size=WIDTHxHEIGHT` in logical pixels. Without the option, the view's
existing default geometry applies. An explicit request is invariant integer
geometry within320..16384 by240..16384; duplicate/missing/malformed values fail
before Avalonia startup. The MainWindow opened hook applies the request while
retaining its declared MinWidth/MinHeight. Loading windows and other dialogs are
not resized. The hook lifetime ends with the desktop lifetime. Device settings,
workflow state and driver connections are unaffected.

The existing macOS packaging script accepts the same optional geometry:

```bash
bash scripts/package-macos.sh --window-size 1060x720 \
  --output '/absolute/path/Instrument Workbench Minimum.app'
```

For an explicit size, the package contains a small executable launcher supplying
normal product arguments to the existing self-contained .NET app. LaunchServices
can open the .app directly; no OS window manipulation or external runtime is
required. Info.plist names the launcher, build-info records window_size, and all
runtime libraries remain beside the executable. Package geometry is validated
before publish; malformed characters, out-of-range/oversized numbers and duplicate
options fail without creating a bundle. Without the option, the original direct
executable launch is preserved. Signing/notarization are still pending.

## Current native evidence

The Mac was unlocked at the start of this stage. A freshly packaged local source
build at be8d4ca, including the29 unchanged pending UI draft files, actually
opened through the native computer-use tool. The 1440-width workbench and the
1060x720 client layout were observed as native rendered windows. The latter's
Retina screenshot was2120x1496 pixels, including the28-logical-pixel native title
bar:1060 by720 client pixels at2x scale.

At minimum size, navigation, three readings, actual chart area, start/stop and
pause/resume controls remain visible; the measurement parameter area scrolls.
The sample name and interval were edited through real native text controls;
scrolling exposed the lower fields and the interval summary changed to0.5s.
Four enabled devices were explicitly Simulation. No physical device IO occurred.

Native coordinate dragging still returned noWindowsAvailable; repeatable launch
geometry replaced that unavailable resize gesture. Native AX setValue was not
supported for these Avalonia text fields, but normal focus/select-all/paste was
verified. After the measurement start was attempted, the tool reported the app
had changed and the refreshed tree showed the instrument page. There is no proof
that this attempt started a run; this stage does not claim run/pause/fault/close
acceptance. A coordination question was sent; the desktop then locked again and
automatic unlock failed. Earlier draft workflow checks are separate historical
evidence, not current complete acceptance.

The observed local package includes unpublished UI drafts; it is not evidence
that the published UI has all draft screens. The29 drafts remain byte-for-byte
preserved and excluded from this commit. Clean committed-source audit, packaging
and native layout acceptance must be recorded separately. Backend migration,
native history/chart/blackboard/flow wiring, current full native interaction,
physical controllers/lab acceptance and the full PPMS goal remain pending.

Fourteen new App cases verify geometry input/default/duplicate/bounds contracts;
seven actual invalid package invocations reject before publish. Actual packaged
native minimum rendering provides the layout evidence; parser tests alone do
not establish native view correctness. No new dependencies or Rust changes.
Independent C# engine; GitHub only; full goal remains active.
