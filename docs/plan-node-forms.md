# Typed plan node forms and instrument palette

Common nodes now have structured forms: delay, repeat, timeout, retry,
constant-valued parameter set, scan, source output, multi-channel read and
wait-stable. Sequence and other complex nodes retain advanced JSON. Variable/
expression-valued set nodes are deliberately not converted into constants.
Wait-stable preserves its original target expression while editing tolerance,
timing and readback. Repeat/retry/timeout/scan retain their original body.
Read forms retain all environment reads, required route and excitation source;
channels can be edited, added and removed while retaining at least one.

The palette now offers five control nodes plus instrument read/set/scan/output.
Instrument templates require explicit device/parameter/values before full plan
validation can append them. A template is not part of the running or saved plan
until Add succeeds. Each successful append creates a fresh template and ID.
Scan templates have a real empty sequence body that can accept child steps.
Output templates default off and still require a registered output device and
the existing compiler's source/preparation checks.

Parameter selectors use the immutable descriptors from the same DeviceRegistry
as the execution compiler, via Application. Unit, access and nominal bounds are
displayed. Reads and writes have separate eligible parameter lists. Resolving a
selection uses the catalog's actual descriptor, not caller-supplied metadata.
Changing the device or parameter clears value/scan/tolerance fields and requires
new explicit numeric input; no unit conversion or physical relabeling is inferred.
Numbers use invariant decimal/scientific notation and reject NaN/infinity.
Numeric/quantity/timing validation supplements, rather than replaces, complete
compiler preflight (including coupled settings, IDs, resources and variables).

Core's structure port now reads typed steps and accepts typed append/replace.
Infrastructure encodes them with the existing native codec and applies the same
complete-envelope transaction. App never constructs JSON from form fields or
sends driver commands. Application returns a validated candidate before the UI
replaces its draft. Original preparations, routing, metadata and budgets remain.

The form and advanced JSON are mutually exclusive draft sources. A form draft
makes advanced JSON read-only; a JSON draft makes the form read-only. Both lock
run/files/other node selections. A conflicting programmatic dual draft is rejected
without replacing the plan. Discard rebuilds the original form and fragment.
Old form callbacks are ignored after selection/replacement, and command bodies
recheck run/document admission. Palette templates have separate draft state and
are explicitly described as not yet included in the plan.

24 new App cases and nine Application cases verify invalid/nonfinite durations,
access/range/unit behavior, body/target/environment/route retention, read-channel
constraints, stale/mixed drafts, palette selection/typed append, real full-plan
semantic rejection and fresh saved-plan reload/execution/output-off. Existing
graph tests now also cover retry. All124 App and focused nine Application cases
pass; full audit is recorded with the publication artifacts.

Native inspection is pending: the Mac was locked, and the launched working
package failed before UI initialization when Avalonia.Native could not start its
RenderTimer (native error -6661). Its process remained in UEs with no terminal
exit observed. This is startup/environment evidence, not successful UI acceptance
and not proof of a form binding or layout failure. No physical lab IO occurred.
The failed process is not restarted merely because observation yielded/expired.

Remaining: native minimum-window/layout/selector/channel action and committed-
package parity checks; more device-specific settings/preparation, routing,
resistance/analysis and environment forms; expression authoring, richer scan
generation, drag/reparent and virtualization; physical and full PPMS acceptance.
No new dependency. Independent C# execution/GitHub only/full goal stays active.

Native follow-up on the published44eb8bb package and corrected editor now confirms
the common rejection/discard/channel/import path and improved form/JSON space.
The earlier lock/startup failure remains historical evidence. See
[node-editor-native-acceptance.md](node-editor-native-acceptance.md) for the exact
checked subset and still-pending configured native scenarios/package parity.
