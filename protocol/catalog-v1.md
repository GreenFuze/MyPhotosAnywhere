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


## Implemented manifest/index field encoding

`CatalogPublicationReader` has matching C# and Kotlin typed readers and shared synthetic
fixtures. This is bounded schema validation after the existing approved-actor signature
reader, not an activated publisher, trusted enrollment flow or complete tree consumer.
Actual Drive parent membership, every referenced object's hash, graph ancestry and full
staging completeness remain required before a local generation can become active.

Every object reference contains string fields `accountId`, `fileId`, `workspaceId`,
`bytes`, `sha256`. Identity fields use opaque Drive IDs (1..256 ASCII letters/digits,
`-`, `_`), never an email, path or URL. Account/workspace must match the previously
selected metadata workspace; the file cannot be the workspace folder itself. `bytes`
is a positive canonical int64 string, and `sha256` is exactly 64 lowercase hex digits.
Index, source-coverage page/root and parent-commit objects are at most 256 KiB each.
They are distinct object roles, with distinct Drive file IDs. Source coverage is paged;
its page schema is subsequent implementation, not an unbounded list in this commit.

A `catalog-commit` explicitly requires `catalog.generations.v1`. Its canonical payload
contains nonzero lowercase UUID `generationId`; nullable object reference `parent`;
object references `rootIndex` and `sourceCoverage`; canonical nonnegative decimal-string
`settingsRevision`; and positive decimal-string `minimumHistoryGeneration`. The last is
the minimum retained sequence in this publisher/authority-epoch stream, and cannot exceed
the enclosing signed stream sequence. Sequence 1 alone has a null parent; later commits
must retain a parent reference. Full ancestor verification still belongs to the consumer.

Canonical index JSON has `format: "mpa-catalog-index"`, integer `major: 1`, `prefix`
and `children`. A node prefix is zero, two, four or six lowercase hex digits and must
match the partition expected by its parent. Each of at most 256 children has `edge`
(two lowercase hex digits), `kind` (`index` or `chunk`), object reference `object`, and
`expanded`. Edges are unique and ordinally sorted. Child Drive objects cannot alias.
An index child requires `expanded: null`; a chunk child requires an object containing
positive canonical decimal-string `bytes` and lowercase `sha256` for expanded bytes.
Compressed chunks are at most 4 MiB and expanded bytes at most 16 MiB. At prefix length
six a child must be a chunk, giving at most four two-digit partition levels. Only the
root can have no children, representing a genuinely empty generation. An empty internal
branch, unknown child kind or mismatched prefix is malformed, never empty-success.

Canonical encodings are checked before schema parsing. Unknown optional fields remain
covered by canonical hashes/signatures; changed semantics require a declared capability.
The shared fixture contains 18 malformed commit and 21 malformed index cases, including
cross-workspace/account references, duplicate roles/partitions, wrong types, counter
bounds, transport/expanded bounds and depth. Fixture reference digests are synthetic
schema inputs, not evidence of any uploaded or hash-verified publication. Shared signature
tests separately exercise the approved actor boundary before the typed manifest parser.


## Compressed chunk reader acceptance

C# and Kotlin now decode the same independent Python gzip fixture and reject 16 shared
corruption/resource-limit cases. The typed index's `CatalogChunkReference` supplies exact
compressed/expanded lengths and SHA-256 values. Both readers take one bounded owned copy
of the compressed input before verification, enforce 4 MiB transport and 16 MiB expanded
ceilings (or smaller consumer budgets), reject expansion beyond the declaration, verify
both hashes and require already canonical JSON. The footer's CRC32 and ISIZE must describe
the complete expanded body; producers emit one gzip member. CRC32 checks format framing,
while the signed SHA-256 reference provides integrity. Matching declared hashes alone
cannot turn a truncated gzip footer into a valid container.

The shared truncated-footer case reproduced acceptance in the prior desktop decoder even
with the matching expanded SHA-256. Explicit complete-body footer validation now rejects
it. Kotlin's gzip reader already rejected that case; its explicit footer check also keeps
the agreed complete-body profile consistent. No diagnostic was suppressed or failed case
removed. Android performs decoding on its background dispatcher and checks cancellation
between bounded reads, checksum batches and completion. Desktop propagates cancellation
through reads/checksums and checks again after canonical validation. These codecs neither
write originals nor activate a generation. Asset schemas, actual Drive membership, graph
completeness, authority enrollment and transactional activation remain separate checks.

## Observed Drive facts leaf

`catalog.drive-facts.v1` identifies the matching C#/Kotlin observed-metadata profile.
Canonical JSON contains `format: "mpa-drive-facts"`, integer `major: 1`, nonzero canonical
`libraryId`, the exact parent-expected `prefix` and nonempty `assets`. A leaf prefix has
two, four, six or eight lowercase hexadecimal digits. At most 1,000 records are sorted
by canonical asset UUID; identities must equal the existing library/account/file-derived
UUID and belong to this prefix. Equal names or content do not merge distinct identities.

Each record explicitly contains `id`, opaque `accountId`/`fileId`, positive decimal-string
int64 `driveVersion`, `name`, `mimeType`, nullable integer `width`/`height`, nullable
`capturedUtc`/`modifiedUtc`, nullable `rawCapture`, and `sources`. Timestamps are signed
canonical decimal-string UTC milliseconds in the year 1..9999 range. Missing capture
time remains unknown; valid Drive modification time is a separately identifiable fallback.
Offsetless raw capture text stays literal. Null fields are explicit rather than omitted.
Names are nonblank, at most 4,096 UTF-16 units and contain no control characters. MIME
types start with `image/` or `video/`, contain no controls and have at most 256 units.
Dimensions, when known, are 1..1,000,000; raw capture text is at most 128 units.

One to 256 captured source memberships contain canonical nonzero UUID `id`, positive
decimal-string int64 `revision`, and integer folder `depth` in 0..256. Memberships are
unique and sorted by source UUID. The complete-generation validator must separately bind
each membership to the correct captured account/source revision and source coverage.
This syntax parser cannot prove that a source owns a file or a Drive original is available.
No thumbnail, processing result, original-verification receipt, human edit or embedding
is smuggled into these observed fields. Those facts retain separate authority and owners.

The desktop writer sorts without modifying its input records and serializes one bounded
record at a time. It requests a partition split before exceeding either the record count
or 16 MiB expanded-byte bound; it never truncates records. Invalid Unicode fails before
the serializer can replace it. The shared independent fixture includes 38 malformed cases
and mixed-account Hebrew/unknown-date records; tests also exercise writer byte exhaustion
below 1,000 records. Full graph validation, publication and activation remain subsequent
integration work. This leaf does not change either client's database schema.

## Desktop workspace-object transport

The desktop Google adapter now reads a captured workspace/file reference through the same
serialized, account-bound credential owner used by foreground and background readers.
Before requesting blob bytes it fetches actual `id`, `parents`, `trashed`, `mimeType`,
`version`, `size` and `capabilities.canDownload`. Parent membership must match the selected
workspace; a folder, shortcut, original image/video or Google Workspace document cannot
be used as a metadata object. Accepted transport MIME types are JSON, gzip and octet-stream.
References must provide a positive exact byte length of at most 4 MiB and lowercase SHA-256.

Blob content is read with a hard bound even when HTTP omits Content-Length, then checked
against exact length and SHA-256. A second metadata read must still match the same Drive
version, MIME, size and workspace membership, with download still allowed and trash false.
Change, missing authorization, unavailable download, malformed metadata, integrity failure
and cancellation remain errors; no live generation is switched by this transport. Credentials
and Google URLs do not leave the connection. Reads never follow a returned executable URL.

This uses Drive's documented [blob download](https://developers.google.com/workspace/drive/api/guides/manage-downloads)
and [file metadata](https://developers.google.com/workspace/drive/api/reference/rest/v3/files)
APIs. Exact signed authority, object role, complete graph/coverage and staged activation
are additional consumer checks. Actual account/Drive publication integration is separate
from the adapter's synthetic HTTP regression acceptance.

## Sequential facts-tree traversal into staging

Matching C#/Kotlin `CatalogFactsTraversal` readers now walk the facts tree from an already
verified, approved catalog commit. Its library must match the locally captured namespace
and its required capabilities must include both `catalog.generations.v1` and
`catalog.drive-facts.v1`. The existing typed manifest/index/leaf readers retain their bounds.
The transport adapter must separately enforce actual account/workspace membership; the
traversal verifies every index length/SHA-256 and every chunk's compressed/expanded checks
before parsing its facts. It never accepts a URL or credential as a transport reference.

Traversal reserves unique Drive file IDs across branches and document roles. The source
coverage root and predecessor are reserved roles even though this walk does not read them.
Aliases, cycles and role reuse fail. A consumer budget defaults to at most 65,536 referenced
objects, including those reserved roles, and can be smaller; exceeding it is an explicit
resource-limit failure. This consumer budget is additional to the wire's existing index
node/depth limits. It never truncates the walk. Four prefix levels bound the depth-first
pending stack independently of total library size.

Leaves are emitted in prefix/identity order, not display chronology. Consumers build their
chronological indexes while staging. There is no implicit leaf prefetch/buffer: stopping
after the first leaf does not request the second. Android moves parsing/checksums to its
background dispatcher without introducing a buffered flow boundary. Cancellation propagates
from reads, parsing completion and collection; no unavailable object becomes an empty tree.

A valid earlier leaf can precede a corrupt or missing later leaf. Only normal completion
proves that this facts walk finished. A caller must retain staging state until complete
source coverage, signed predecessor/authority agreement, policy fences and every required
dependency have also validated, then atomically switch generations. This reader does not
activate or discard a live catalog. Even an empty root requires a verified hash and valid
canonical index; full source-coverage validation is still necessary before declaring an
empty library.

Independent Python fixtures cover 21 malformed, unavailable and over-budget graphs,
including a failure after earlier leaves, cross-branch aliasing, cycles and foreign facts.
Real signature tests alter syntactically valid settings JSON and require rejection before
the first object read. The shared coverage placeholder is deliberately absent from fixture
transport, preventing these tree-only tests from claiming complete-generation acceptance.

## Bounded source coverage profile

Coverage is a canonical `mpa-source-coverage-root` object with numeric `major: 1`,
the captured library UUID, positive decimal-string `configurationRevision` and
`discoveryRevision`, decimal-string `sourceCount` (0–256), and `pages`. Configuration
and discovery revisions have their own owners; neither is the manifest's settings
revision. Actual selected-workspace membership and the root reference hash are checked
before interpreting it. A genuinely empty library has zero sources and zero pages.
The generation owner must separately compare the received policies/accounts with the
enrolled authority's approved configuration at that revision. A self-consistent revision
number or source list alone cannot authorize a new source or consent.

There are at most 16 page descriptors. Each declares `firstId`, `lastId`, decimal-string
`count` (1–16), and an immutable `reference` subject to the existing 256 KiB metadata
object/workspace rules. Descriptors use canonical source UUID string order, have strictly
non-overlapping ranges, distinct file IDs and counts summing exactly to `sourceCount`.
Only a one-source page has equal first/last IDs. Pages cannot silently go missing.

A canonical `mpa-source-coverage-page` repeats major/library/configuration/discovery,
then `sources`. Its exact bytes/hash, count and first/last IDs must match its descriptor.
Source identities are strictly ordered. Each source contains `id`, opaque `accountId`
and `folderId` (not My Drive's synthetic `root`), positive decimal-string `revision`
and `accountRevision`, boolean `includeDescendants`, `enabled`, `connected`, `state`
and explicit nullable `checkpoint`. Identity and account/folder binding are unique
across the complete coverage map. At most 256 small source records are retained.

States are `complete`, `scanning`, `partial`, `pending`, `access-lost`, `offline`,
`disconnected` and `disabled`. Disabled policy requires `disabled`; an enabled source
with a disconnected account requires `disconnected`. Connected/enabled sources cannot
claim either state. `complete`, `scanning` and `partial` require a checkpoint with:

- Nonzero canonical `jobId` UUID and positive decimal-string `sourceRevision` and
  `accountRevision`, neither exceeding the source's captured current revision.
- Nonnegative decimal-string `observedItems`, `pendingItems`, `failedItems` and boolean
  `incompleteSearch`, plus lowercase `sha256` for the captured scan journal summary.

Raw Drive page tokens and credentials are not portable coverage fields. A complete
source must reference its current policy/account revision and have no pending items,
failed items or incomplete search. Partial coverage must declare an outstanding gap.
Old checkpoints can describe retained observations after a policy/account change;
they cannot claim a fresh complete scan.

After every page validates, each asset membership must name a known enabled source
with the same account and current source revision, with depth 0 for a nonrecursive
source and depth 0–256 otherwise. Disconnected, offline and partial sources may retain
their previously observed photo facts. Absence from an incomplete scan never means
deletion. A checkpoint digest is observation provenance, not original-file integrity.

Matching C#/Kotlin readers and the shared independently generated coverage fixture cover
all states, empty and 256-source libraries, 44 malformed graphs and source-membership
conflicts. These readers do not perform authority enrollment, predecessor agreement,
durable staging or the active-generation switch; those checks remain mandatory.

## Covered observed-facts traversal

`CatalogFactsTraversal.ReadCoveredLeavesAsync` (C#) and `readCoveredLeaves` (Kotlin)
combine the bounded coverage and observed-facts readers. Their enclosing signed commit
must already have passed signature, actor, library and capability verification. The
read-only transport must verify actual selected-workspace membership for each object.

The reader checks the coverage root's captured length/SHA before parsing it, reserves
every page in the same object-role/budget ledger as coverage root, predecessor, indexes
and chunks, and validates all pages before reading the photo tree. Cross-role aliases
and object-budget exhaustion fail explicitly. At most 256 coverage records remain in
memory; photo leaves retain the existing sequential bounded traversal.

A required asynchronous configuration-admission callback receives the bounded complete
coverage set and its immutable ordered `Sources` list. The generation owner must compare
those source/account policies to its enrolled authority's approved configuration; matching
self-reported revisions alone cannot authorize them. The callback executes once, including
for a truly empty catalog, before the first photo-tree read. Rejection or cancellation
propagates; neither is converted into an empty successful result. Every emitted leaf's
asset memberships must agree with that captured coverage. Partial/offline/disconnected
sources can retain facts; absence still never implies deletion.

Later object/membership failures can follow already emitted staging leaves. Only normal
full enumeration proves this covered walk finished. Signed predecessor/epoch agreement,
remaining policy/consent checks and an atomic active-generation switch are still separate
mandatory generation-owner responsibilities. The older facts-only walk remains available
for isolated tree validation and cannot by itself validate a generation.

The independently generated shared `catalog-covered-traversal.json` fixture combines
partial, offline and disconnected sources with the actual bounded facts tree, an empty
catalog and 22 invalid graphs. Matching consumers cover corrupted/missing metadata,
revision changes, page/index/chunk/predecessor aliases, source/account/revision/depth
conflicts, whole-graph budgets, late failures, configuration rejection, cancellation and
abandoned enumeration.

## Enrolled source configuration

A controller's signed `library-source-configuration` document requires capability
`library.sources.v1`. Signature verification must use a previously enrolled actor allowed
to publish that kind. Decoding a self-supplied key/document never enrolls a controller.
The generation owner selects the authoritative configuration revision before admitting
coverage; a newly received larger revision is not automatically trusted or applied.

The payload contains positive decimal-string `configurationRevision`, a `workspace`
with `accountId`/`folderId` matching the independently selected workspace, `accounts`
and `sources`. Accounts are 1–128 unique ordinally ordered opaque permission IDs and
include the workspace account. Sources are 0–256 records ordered by canonical UUID,
with `id`, associated `accountId`, canonical `folderId`, positive decimal-string
`revision`, and boolean `includeDescendants`/`enabled`. Account/folder bindings are
unique; the synthetic `root` alias and paths/URLs are forbidden. Disabled policies can
remain present without erasing their identities or human annotations. These documents
retain the signed-envelope/canonical byte limits.

`CatalogSourceConfiguration.ValidateCoverage` / `validateCoverage` compares the
complete captured coverage set to those exact library/configuration/source identities,
bindings, source revisions and flags. Missing, extra, retargeted or newly enabled
sources fail, including when the received coverage graph is otherwise internally
consistent. Credential revision, connectivity and scan progress remain observed state;
reconnection cannot grant permission for a different source. Partial, offline and
disconnected observations can therefore pass policy comparison while still honestly
reporting their operational gaps.

This is source association/scope approval, not recognition consent, processing settings,
original mutation permission, actor enrollment, operation admission or generation
activation. Those owners still apply their separate revision/authority/consent checks.
The immutable ordered policy lists can be used by the required covered-traversal
admission callback. The independent shared configuration fixture covers empty and
maximum source/account sets, 30 malformed/unscoped documents and nine coverage-policy
conflicts, plus matching signature and combined-traversal checks in both consumers.

## Captured workspace-object creation transport

The native account connection exposes a separate metadata-publication interface; its
existing read-only interface remains unchanged. The publication owner must authorize
the workspace and durably record its account/folder, allocated object ID, exact payload
length/SHA and generation scope before creation. ID allocation is a separate operation.
Google supports [pre-generated IDs](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/generateIds)
and documents that retrying a successful upload at the same ID returns conflict rather
than creating a duplicate. See [upload recovery](https://developers.google.com/workspace/drive/api/guides/manage-uploads#use_a_pre-generated_id_to_upload_files).

`CreateWorkspaceObjectAsync` owns a bounded synchronous copy of its supplied bytes,
verifies its captured SHA, checks the actual folder/write capability and only creates
new JSON/gzip/binary metadata objects, at most 4 MiB. It never allocates another ID during
create and has no update/delete/original-upload operation. A success or conflict is not
storage proof: the transport independently re-reads the captured object's actual parent,
type, size and revision, downloads/verifies its exact bytes/hash, checks the object again
and rechecks the workspace. Wrong types, movement, restricted access, corruption,
cancellation and uncertain replies remain explicit failures. Resumption retains the
original allocated ID; it cannot overwrite a conflicting file.

The operation borrows the existing serialized credential/HTTP owner and joins the real
response before cancellation/disposal releases it. The metadata name is derived from
the payload digest; lookup/identity uses Drive IDs. This transport primitive does not
enroll an actor, authorize a workspace, journal a generation, activate it or publish a
head. Those responsibilities remain with the durable publication owner. Its fixture
tests are synthetic HTTP exchanges; they are not evidence of a live production write.

Non-secret role/digest discovery hints use Drive `properties`, visible across authorized
applications, rather than app-private `appProperties`. This does not change file sharing.
These hints and names are untrusted: actual workspace membership, enrolled signatures
and captured content references remain the admission authorities. See Google's
[custom property visibility](https://developers.google.com/workspace/drive/api/guides/properties).

### Durable publication owner integration

Before allocating an object ID, the local journal persists exact bytes, role/type, their
digest and captured library/publisher/generation/account/workspace/configuration authority.
It binds those immutable fields with a canonical context digest. Before create, it
persists the allocated ID; retry cannot mint a replacement or retarget a captured job.
Account/configuration revisions fence uploads and receipt admission. A reconnect does
not silently transfer old work to another credential revision. A future generation owner
must explicitly reconcile such work without replacing its captured object identity.

The account adapter requires an independently supplied publisher-authority validator,
borrows the existing serialized Google connection and revalidates before create and after
the exact stored-object checks. Its bounded local receipt does not activate a generation.
Lost replies, late cancellation, revoked authority, corruption and disposal leave the
operation available for explicit recovery. Two concurrent callers re-read the journal
under the same account lease so completed work is not uploaded twice. No live scheduling
or enrollment is inferred from these transport/journal primitives.
