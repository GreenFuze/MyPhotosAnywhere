# Catalog publication and local activation — protocol 1 candidate

These contracts complete the wire/storage design for implementation. Runtime and scale
acceptance are still required; this document is not evidence that the catalog exists.

## Immutable publication

The controller-approved Processor/authority epoch owns one generated publication stream.
Every artifact is created with a journaled Drive ID, verified by exact byte length and
SHA-256, then referenced from an immutable signed commit. A mutable head is only a hint.
Each commit retains its predecessor hash/ID for recovery. Same stream/epoch/sequence with
different commit hashes is a conflict; retain the agreed catalog and report reconciliation.
No wall-clock winner, shared writable message file, or SQLite database in Drive.

Commit `kind` is `catalog-commit`, required capability `catalog.generations.v1`. Its
payload contains `generationId` (UUID), `parent` (null only for genesis, otherwise an
immutable commit reference), `rootIndex`, `sourceCoverage`, `settingsRevision`, and
`minimumHistoryGeneration`. References identify the canonical account permission ID,
Drive file ID, containing workspace folder ID, exact decimal-string byte length and
lowercase SHA-256. Gzip chunk references additionally carry expanded length/hash.
No executable URLs, arbitrary filesystem paths, image base64, or access tokens.

Readers verify approved publisher/library/epoch, complete signed commit, source/workspace
membership, every referenced index/chunk hash and required dependency before activation.
The Drive adapter validates actual parent membership; a signed claimed folder is not
sufficient. A transport or parsing failure retains the prior complete generation.

Index nodes form a prefix tree over lowercase asset UUID hex without hyphens. Each
edge adds two hex digits. Edges are unique and ordinally sorted; maximum 256 children
per node, four levels, no cycles/shared node IDs, maximum 65,536 visited index nodes.
Leaves hold at most 1,000 asset records, 4 MiB compressed and 16 MiB expanded. A dense
leaf is split before publication; exceeding the final prefix density is an explicit
unsupported-size failure, never record truncation. Nodes/chunks have immutable hashes;
unchanged references are reused. A generation publishes only dirty leaves and ancestors,
not the full library on each progress update. A commit cannot reference itself/descendants
as predecessors. An initial consumer can walk the complete bounded tree incrementally.

`sourceCoverage` is paged when necessary and records each source's captured binding,
scan checkpoint and state: `complete`, `scanning`, `access-lost`, `offline`, `disconnected`.
Absence from an incomplete scan never implies deletion. A confirmed source deletion
becomes an explicit tombstone; restore references its current tombstone identity.

## Asset facts and generated results

Asset identity is independent of content/path. Representation records retain their own
account/file or device-volume/row identities and observed revision. Verified association
receipts can link them; content equality alone cannot merge media. Association validation
rejects cycles and conflicting preferred IDs, and keeps old IDs resolvable. Visible grid
keys and existing viewer sequences are not rewritten merely because a representation links.

Discovery can publish an honest indexed record before downloading the original. It marks
content as unverified and cannot attach an analysis result to a presumed hash. Processing
requires a verified full-content SHA-256/byte length and before/after source revision checks.
Generated work is keyed by asset + content fingerprint + capability + complete recipe,
parameters/model hashes + policy revision. Different recipes use separate namespaces.
Conflicting output for the same work key retains candidates and the previously agreed result.

UTC timestamps use signed decimal-string epoch milliseconds; null is unknown. Preserve
timestamp provenance and raw local capture/offset facts separately. Never assign upload
time to a missing capture date or rewrite metadata to obtain order. Timeline precedence
uses verified capture time, then valid source modification/import facts according to its
record type, then Unknown. Equal keys use canonical identity, independent of local zone.
The phone groups/formats dates in its current timezone; regrouping preserves identity and
pixel-offset anchors. Image coordinates are orientation-correct normalized integer PPM
in `[0,1000000]`, top-left origin; rectangles require positive width/height within bounds.
Confidence uses integer PPM, not binary floating point. Incompatible embedding models
never share a vector namespace or threshold.

Stage outcomes: `ready`, `partial`, `blocked-consent`, `blocked-auth`, `blocked-storage`,
`unsupported`, `failed-retryable`, `failed-input`, `canceled`. Error code/message/context
and failed stage remain durable. Expected malformed-item/network/auth/storage failures
are isolated by stage/item; internal invariant violations fail the coordinator promptly.
Resume selects only unfinished/failed stages and revalidates captured inputs/approval.
A verified original upload is not repeated because metadata/OCR/preview later failed.
An import finishes `partial` when recoverable failed items remain; it cannot say complete.

## Phone materialization and leases

Use local transactions and foreign keys for these logical tables/indexes. Actual Room
schema/version and migration/clean-install UI are implemented and tested separately.

| Entity | Key / indexes / lifetime |
| --- | --- |
| generation | library/epoch/generation ID, commit hash, predecessor, staging/validated/active/retired state; unique stream sequence/hash agreement |
| catalog_chunk | immutable compressed/expanded hashes and lengths, verified flag; shared across generations |
| generation_chunk | generation/prefix -> chunk hash; foreign keys; changing this small mapping stages a generation |
| catalog_asset | chunk hash/asset ID; covering date/ID and feature lookup indexes; source facts remain chunk owned |
| active_generation | one accepted pointer per library, changed only after complete validation |
| human_operation | unique operation ID and unique actor/sequence globally, causal parents and canonical-body hash; conflict/tombstone data retained |
| human_outbox | operation ID, reserved Drive ID, immutable bytes/hash, captured workspace, retry/checkpoint; same transaction as the human change |
| device_representation / transfer_receipt | device facts and verified links; independent of generated chunk lifetime |
| identity_alias | original ID -> preferred ID and validated receipt; unique preferred association, canonical lookup index |
| reader_lease | gallery/viewer generation/chunk references; rotation/session retention; reclaimed after actual reader release |

Stage changed chunks off the main thread in bounded transactions (initial budget 250
records), validate coverage/hash/authority, then switch the active pointer in one short
transaction. Do not clear/refill live tables. Old/new generations share unchanged chunks;
reclaim a retired generated chunk only when no generation/reader refers to it. Human
decisions, local-only originals, pending jobs/outbox and failed evidence are not caches.
Long-offline clients below `minimumHistoryGeneration` fetch a verified snapshot and replay
their local outbox; compaction retains unresolved conflicts and required acknowledgements.

The gallery reads active generated facts plus local representations/transfers and human
overlays. Stable keyset ordering, frozen tile geometry, identity/offset anchors and a
New photos action prevent import refresh from repacking the current viewport. Viewer
sessions keep their originating ordered identity index; unavailable entries do not turn
into another image. Loading/empty/partial/error are distinct states with prior content retained.

## Approved original actions

`original-action` is allowed only from the paired controlling phone. Its v1 payload is
closed: `approvalId`, `action`, `source`, `destination`, `recipeSha256`, `tombstoneId`.
All keys must exist, with explicit null for inapplicable fields. Unknown action/source/
scope options fail before execution. Supported actions are `backup`, `import`,
`save-edited-copy`, `trash`, `restore`; no overwrite-original or permanent-delete action.

Device sources capture asset ID, owner actor, media-key fingerprint, original SHA-256
and exact length. Drive sources additionally capture account/file, revision, strong ETag
and expected trashed state. A destination captures account/folder/workspace identities;
changing the default never retargets an existing job. Edited copies bind the exact recipe.
Restore binds the current tombstone. Import binds the immutable input-manifest and
selection-policy hashes; new ZIP parts or changed revisions require a new approval scope.

Parsing a signed scope does not execute it. The action owner checks approved actor/epoch,
revocation and durable replay journal, validates current source and actual destination
membership, verifies conditional-write behavior, and writes only the approved operation.
If Drive cannot enforce a required precondition safely, that action remains blocked.
Generated catalog/human annotation operations never imply permission to change originals.
