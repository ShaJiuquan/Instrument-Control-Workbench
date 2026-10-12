# Channel and instrument wiring catalog editor

The Samples editor now exposes **编辑通道与接线** for plans with an existing
routing catalog and a typed structure document adapter. Create, edit or remove
channel and wiring profiles in one draft, then use the shared apply/discard flow.

Channels expose name/alias/notes, connection IDs, explicit card/column addresses,
enabled state and connection label/notes. Existing IDs are fixed. Original ID,
group index, connection index/display row/raw text and untouched records remain
unchanged. New enabled connections need physical addresses; unchanged imported
unbound presentation records can remain in the catalog.

Wiring profiles expose description, actual registered K2450/K6221 source, optional
K2182 meter, and four card/row ports. New selections start blank; changing device
roles clears the affected port addresses. K6221 needs an independent meter. The
matrix definition comes from the frozen runtime registry.

## Application admission

`UpdateRoutingCatalog` preserves samples, matrix identity, preparations, metadata
and execution budget. Root steps remain unchanged unless explicit guard
coordination replaces their route expectations. It validates bounded distinct catalog IDs, new or
changed enabled connection addresses, changed wiring roles/guards/port bounds,
and all sample links, including dormant samples. It checks every required
measurement direction for each referenced sample/wiring pair via typed structure
reads, then compiles/serializes the whole candidate. No hardware command is sent.
Removing a referenced wiring or changing source/meter identity in a used wiring
fails the complete candidate; a new wiring can be added and selected explicitly
in the existing acquisition node form.

The default-off guard coordination option updates affected internal independent
sample acquisitions across the plan in one batch. It proves original guards and
unchanged source/meter identity, preserves acquisition settings, and leaves
standalone reads and explicit matrix commands untouched. Without opt-in, stale
fixed guards reject the catalog change. Existing single-sample coordination shares
this implementation. Unsupported structure adapters fail explicitly.

Catalog drafts share the established SampleDirty run/save/import/JSON/node/
preparation/selection gates. All nested forms and commands become readonly while
busy; removed and retired row/form callbacks cannot dirty the current plan.
Limits remain one to 64 channels/wirings/connections, and referenced entries
cannot be orphaned by deletion. The matrix identity itself remains fixed.

## Verification

31 new App and 16 new Application cases. Eight saved/fresh physical edits cover
integrated two/four-wire/Hall/vdP, independent K6221+K2182 four-wire/Hall/vdP and
K2450-current+K2182 four-wire. Seven saved/fresh multi-profile cases create an
additional channel/wiring/sample and append a second read, proving old and new
row routing separately. A nested repeat with two independent samples produces
four rows using coordinated guards. Tests include inactive Hall direction clashes,
bad bounds/roles/IDs, orphaned dormant samples, limits, metadata preservation,
source identity rejection and busy/retired callbacks. Cleanup verifies output-off,
matrix-open and released runs. Native visual/interaction acceptance stays deferred
per the user's request to continue code checks; no physical instrument IO.
