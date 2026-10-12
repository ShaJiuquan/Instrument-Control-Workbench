# Atomic sample mapping and independent acquisition guard coordination

## Behavior and boundaries

Existing sample electrode forms now offer a default-off explicit checkbox to
coordinate their internally managed independent acquisition guards. Creation forms
hide it, and codecs without structure editing do not offer it. The checkbox joins
the existing dirty/readonly/stale/busy admission and resets on fresh form reload.
Without explicit selection, the previous strict mapping/guard rejection remains.

Application UpdateSampleDefinition constructs the candidate sample definition and
optionally coordinates guards before the final whole-plan Inspect. It reads the
version-1 independent sample wrappers in a batch, selects only the target sample,
proves each existing guard equals its original derived catalog route, verifies
source/meter identity, checks all directions of every referenced wiring against
actual registered matrix definitions, and constructs typed replacement acquisitions.
Only RequiredRoute changes. IDs, binding, columns, minimum current, expected value,
reference, environment, retry and route/acquisition dwells are retained.

Unwrapped independent acquisitions, other samples and standalone matrix routing/
verification steps are never rewritten. The original plan must already compile;
coordination is not a repair mechanism for an invalid existing guard. No interpreter,
resource lifecycle, transport or execution safety policy is weakened. Changed
routes still pass ordinary whole-plan validation and use normal run-owned cleanup.

Core's existing structure document port adds ReadSteps and ReplaceSteps for bounded
batch editing. Infrastructure serializes/walks once per batch operation, validates
IDs/membership, rejects duplicate or overlapping parent/child replacements, then
parses the complete final envelope. No intermediate candidate escapes and no
execution workflow moves into the adapter. Application continues to own selection
and guard policy; UI constructs no JSON or concrete driver.

## Evidence and remaining work

Twelve new App cases cover explicit coordination for K6221+K2182 and current-mode
K2450+K2182 with four-wire/Hall/van der Pauw samples, plus nested repeated Hall/vdP
analysis, invalid mappings and checkbox admission. Six edited cases save/reload on
fresh simulated devices, use terminals7–12 and confirm actual R/provenance/cleanup.
Nested repeat/subtree/if-else plans produce9/27 rows and three analysis summaries.
Other sample terminals1–6 remain; inactive standalone independent read, matrix
verify and route steps retain their guards. After removing only intentionally
changed electrode/guard fields, the complete before/after JSON envelopes match.

Eight new Application cases verify batch read order, sibling replacements and
whole envelope retention, unknown/duplicate/empty/overlapping mutations and required
measurement acquisition slots. The focused runs also include previous catalog and
structure cases. Original plans remain immutable, invalid candidates stay local,
and all simulated runs confirm output-off, empty matrix and released ownership.

Native cua inventory again reports the Mac locked with automatic unlock unsuccessful.
The earlier optional unlock request remains pending; no user window/new app process
was manipulated. Native checkbox/layout interactions and committed-package parity
remain unverified; build/test evidence is not native visual acceptance.

Initial routing catalogs/channel/wiring authoring, remaining workflow/variable forms,
arithmetic expressions, bounded data memory/indexing, real transport and physical/
full PPMS acceptance remain unfinished. Native version-1 structure kinds are the
current adapter contract; other formats require matching structure support. No new
dependencies/Rust edits/physical IO. Independent C# engine, GitHub only per user,
Gitee deferred; overall migration goal active.
