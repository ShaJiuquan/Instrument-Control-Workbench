# SR830 native authoring forms

The workbench palette now offers SR830 原始帧 and SR830 名义换算. Frame nodes use a
named, compiled-binding form for device selection, expected input mode, output
prefix and optional reference readback. It accepts only registered SR830 passive
input profiles with the exact eight/twelve-parameter contract. New templates have
no silently chosen device, input mode or prefix. Existing steps retain their IDs,
device, mode, prefix and reference flag without loading-induced edits.

The reference checkbox follows declared capability. Switching a saved
reference-enabled frame to an input-only device retains its flag and rejects
Build until the operator selects a compatible device or explicitly clears the
option. The checkbox remains available to clear that retained flag; an
unsupported profile cannot turn the option on through normal UI interaction.
Input mode is an acquisition expectation, not a device write. Use ordinary
parameter steps or separately owned device configuration to establish it.

## Nominal analysis and editable calculations

SR830 名义换算 uses the existing calculated-measurement form with an editable
SR830 body and an explicit generator. The operator completes frame fields and a
result prefix, then chooses 生成名义换算. This replaces the current formula list
with the Core helper's voltage or nominal 1M/100M current referral expressions.
The original raw frame and existing continue-on-unavailable choice remain.
Result IDs, formulas and current-row unit/dependency previews stay editable.

Loaded custom calculation expressions are never classified and rewritten as a
nominal template. Nothing regenerates on load or on device/input/prefix changes.
After body edits the form prompts the operator to regenerate or check formulas.
The generator is disabled for incomplete bodies, invalid prefixes, unrelated
measurement bodies and read-only/busy workspaces. Changing its helper prefix
alone does not dirty the persisted plan; explicit generation does.

## Application boundary and verification

Forms build typed Core steps only. Apply/append still goes through the existing
Application workspace and full compiler. Invalid prefixes, device/capability
choices or globally conflicting output columns do not replace a validated plan.
A dirty/invalid node blocks Run/Save and supports discard. Busy/read-only state
reaches nested frame controls and generator admission; no instrument operation
is issued by the form. Palette initialization, loaded graph node forms and
current-row calculation preview use the existing metadata-only path.

Tests cover exact load/edit round trips, explicit template selections, invalid
profile/prefix rejection, retained reference choices, untouched custom formulas,
explicit generation after mode/prefix changes, read-only controls, real palette
append and graph labels, rejected node drafts, busy admission and no-connection
checks. A form-authored plan saves, loads into a fresh mock runtime, produces raw
and nominal-result rows, reopens a fresh archive and exports CSV with actual
observed reference columns and nominal gain evidence. Existing acquisition and
calculation-picker tests remain part of verification.

The two forms are small, named vertical components using existing caption,
button, spacing and field styles; help text wraps and controls stretch inside the
existing scrollable node inspector. SR830 calculation bodies open initially to
expose required input fields. XAML compiled bindings/build are checked, but the
native visual/minimum-window acceptance is deferred per the user's “先继续代码检查”.
No native inventory/window action or desktop process is launched. This does not
claim visual acceptance. No new dependency, driver, Core plan format or execution
engine is introduced. Oscillator/mute wiring, full differential routing and
physical response normalization remain pending; these forms do not enable output
or certify calibration.
