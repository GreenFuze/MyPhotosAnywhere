# Exact current control for independent Drive clients

## Why the protocol needs an explicit current object

A full folder listing plus the highest signed policy observed locally is insufficient
for an independent client. A missing newer revocation could leave an older authorization
visible. Conversely, treating every unknown removal in the account-wide changes feed as
a workspace deletion can permanently stall the library after unrelated Drive cleanup.
Neither ignoring those events nor clearing retained recovery evidence fixes authority.

The consumer therefore needs one actual Drive file ID, independently approved with the
root public key and workspace. A new connection format, `mpa-library-v2`, pins that ID
in its canonical document and approval digest. Existing v1 codes remain exactly readable;
they cannot infer, search for, or silently acquire current-control authority. The v2 pin
contains public setup information, never an endpoint, token, private key or write grant.

## Signed snapshot and publication ownership

The dedicated mutable file contains a root-signed `library-current-control` document,
requiring `library.current.v1`. It selects exact immutable publisher and source policies
by account, workspace, file ID, byte length, SHA256 and root stream sequence. It optionally
selects one exact immutable catalog commit. Every role has a distinct file ID, including
the current-control file itself. A null catalog means no catalog is selected; publisher
revocation must have a null catalog. Received keys never establish root authority.

Policy signatures precede the control signature in the same global root sequence stream.
Changed policy references must have a sequence greater than the previously accepted
control head; unchanged exact references can persist. This rejects reactivating an older
unpublished orphan policy through a later head. A repeated control sequence must have
identical signed envelope bytes. Rewind, fork, cross-connection comparison, role alias,
wrong root key, counter overflow and workspace/control retargeting fail closed.

The producer must serialize policy signing and control publication under its existing
durable signing owner. It must reserve the actual control ID once, seal immutable intent
before network I/O, verify exact uploaded policies/dependencies, compare-and-update the
same control object, and reconcile a lost acknowledgement by exact bytes and actual Drive
version. It must never resolve an ambiguous update by creating another current object or
selecting an equal copy. Plugin workers receive no controller key or publication owner.
The producer now has a durable local slot and shares the existing root signing journal;
the joined allocation owner is implemented, while runtime composition and mutable
publication remain pending. Pure serialization,
reservation and signing are not publication.

## Consumer sequence

1. Capture the approved connection and actual Google account. Read the pinned file by ID
   with actual workspace/corpus/parent/version/size/downloadability checks. No filename
   lookup or received ID may replace the independently approved identity.
2. Verify its root signature, exact scope, immutable role references and locally retained
   control high-water. Retain the observation before further requests. A locally retained
   head is recovery evidence, not fresh authority after restart or credential changes.
3. Fetch the exact policies; verify actual immutable bytes, root signatures and their
   selected sequences. Verify current publisher/epoch, catalog ancestry, complete graph
   and agreement with the exact approved source configuration. Stage facts privately.
4. Re-read/fence that same actual control object after all graph reads. Its version and
   bytes must agree with the captured head. A missing/changed object invalidates admission.
   Retire and join all borrowed I/O; do not perform metadata reads after this final fence.
5. In the existing serialized private SQL transaction, compare the current connection,
   account, accepted policies, head high-water and staging attempt, then atomically retain
   the accepted head and switch the complete canonical generation. An interrupted attempt
   must not partially activate. Preserve usable previously accepted content on failure.

The mutable control object must not enter the immutable candidate/read journal. Exclude
only its independently pinned ID from that channel, and retain its captured versions and
bytes in a separate bounded control observation. Do not exclude received filenames, copy
identities or other unknown metadata. This separation remains to be implemented.

After this complete integration, unrelated unknown account removals need not be treated
as hidden policy authority: the exact pinned current object determines selection. Required
missing dependencies, known access loss and changed selected objects still block admission.
The existing conservative inventory/history paths must not relax their recovery rules in
advance of that integration. Known observations and immutable pending work are preserved.

Actual Drive responses and independent enrollment remain trust assumptions. Signatures
alone do not prove wall-clock freshness or defeat an authorized Drive writer replaying
an old signed snapshot to a client that has never observed its successor. Retained
high-water rejects rollback below that client's accepted head; no stronger guarantee is
claimed by the codec. Original image/video writes still require explicit human approval.

## Implemented scope and pending work

C# and Kotlin implement identical v2 public pins, strict signed snapshot decoding,
exact policy resolution and pure successor checks. Bounded transport envelopes are
copied before hashing and signature checks, so borrowed-buffer mutation cannot mix
verified semantics with a different retained digest. Shared independently generated Python
cryptography vectors exercise both implementations, including malformed signed data and
validly hashed policies signed by a foreign root. No private key is retained in fixtures.

Android also implements a serialized captured-control reader and durable observation
journal. Metadata is retained before body I/O and complete actual read SHA/version before
semantic verification. Nonces, Root-head/global-role integrity and actual-file version/
corpus/content high-water survive failed reads and restart. Rewriting bytes under a known
Drive version is rejected. Changed final probes block the old capture. Close and caller
cancellation join actual I/O and reject late results; restarted readers capture afresh.
Private schema7 adds two tables while preserving1–6, old receipts and originals.

These are pending observations. Future graph consumers must participate in the same
reader lifetime before fenced evidence can be atomically admitted with exact policies,
retired epochs, signed ancestry and complete canonical facts. Full bounded Root-history
verification currently repeats on observation assertions; retention/ACK and measured CPU
work remain. No history budget was raised and no account-removal recovery was cleared.

The canonical outgoing writer reuses the decoder's exact payload validator, including
scope, roles, counters and aliases. It returns unsigned bytes, never a verified snapshot.
An immutable local slot requires the completed initial two-policy receipt and current
local publication authority. A separate atomic receipt captures one actual provider ID
and its original account revision; reconnects cannot relabel or replace it. Missing or
corrupt records fail closed. Retaining a historical allocation after disconnection grants
no access. The store performs no network, ID generation or cloud-file creation.

Current-control intents use the same durable root counter and sealed-envelope history as
the two policy kinds. They require the allocated slot, matching root scope and explicit
capability. The publisher role remains catalog-only. The initial no-catalog
snapshot now uses the native key owner and the fixed slot ID as its durable signing
intent. It reconstructs exact initial receipt references and rechecks both original
policy selections, source approval and current native/local authority. Concurrent replay
keeps the first sealed bytes; disconnect, revocation or source changes deny fresh signing
without erasing history. Later policy/catalog updates and full current-policy admission
still need integration before live publication.

Stage0's live synthetic metadata probe already found that v3 accepted an invalid
`If-Match`, while v2 rejected previously valid stale tags for metadata and Trash/restore.
A later synthetic-only
v2 media sequence also verifies captured/stale-tag updates and injected lost-completion
reconciliation; a new process recovers the same actual ID and exact pending bytes. Numeric
versions may move between separately fenced captures without content-tag changes, so
version is not ETag. Production ownership/admission/CAS integration remains required.
Numeric Drive versions must never be substituted for actual entity tags. See the
[existing feasibility evidence](validation/drive-feasibility.md).

Durable producer publication/CAS/lost-ack recovery and actual reserved control IDs, v2 setup
export/import UX, full graph/control lifetime integration, high-water admission together
with canonical staging/atomic activation, and native gallery integration remain required.
No current-control document is published by this change. Legacy Room12 and Processor13
schemas remain unchanged. Fenced persisted state is not fresh authority after restart.

The joined allocation service captures current native/local authority and exact account
revision before GenerateIds and fences authority again afterward. Known returned IDs
checkpoint without caller cancellation and cannot be replaced on a live retry. Source/
account changes prevent successful result admission after historical receipt retention.
Close joins physical provider I/O; a failed pending checkpoint keeps exit unconfirmed
and borrowed resources alive until retry. It creates no file and exposes no setup-ready
state. GenerateIds alone creates no file, so an unknown unused ID after a hard crash
before result durability cannot be described as an exactly-once published identity.

Native selected publisher/source reads now participate in that same captured reader
lifetime. Actual ID/length/type/version/parent/corpus/downloadability/checksum and complete
bytes precede Root policy signature/sequence verification. A bad publisher stops before
another role request. Owned policy envelopes and typed semantics remain pending; they
cannot activate a catalog. Head/capture identity, sequence, actual SHA and control
metadata must match before I/O. Final fencing retires every selected-policy read, while
close/cancellation join actual physical I/O. Full graph/staging/admission and canonical
activation remain pending; no schema or conservative legacy recovery rule changed.

## First current-file creation and historical receipt

The joined initial-current publication owner now composes durable allocation and actual
native Root signing for the first no-catalog head. It reads both exact original policy
objects before and after create, checks their Root signatures and retained minimum
versions, and rechecks current native/account/source approval. Actual folder sharing,
download access and corpus must agree. The final network operation is an exact read of
the same allocated current ID; no later policy or folder request follows that fence.

Creation is create-only. An uncertain completion reuses the slot and first sealed bytes;
an existing conflicting head, substituted result or equal copy cannot become an update
or replacement. Close and caller cancellation join an uncooperative physical write.
Failed allocation retirement retains its borrowed resources until the known returned
identity persists. SQL and storage failures preserve pending identities and signatures.

After the final actual read and local authority checks, a bounded private historical
receipt atomically retains body, digest and claim. It binds the original slot/v2 pin,
Root envelope/sequence, exact initial policies, account revision and minimum actual
Drive version. Reopening checks those same retained records and signatures. A partially
missing or corrupt receipt cannot grant completion; uncertain work rechecks the same
retained ID and sealed bytes. A lower observed version cannot replace a retained receipt.
It grants no fresh authority, current-policy selection, canonical activation or readiness.
Processor schema13 is unchanged; the receipt uses the existing private metadata store.

This owner is not yet composed into setup. That requires a complete v2 current route:
the old reconciler currently expects sealed Root work in the immutable inventory and
selects the highest observed catalog. Simply excluding the pinned current ID or adding
another setup phase would not fix those assumptions. The new route must independently
observe/fence current Root work, select only its catalog and preserve newer unselected
commits as pending work. Verified conditional control publication must precede finished
generation status. Initial bootstrap must keep its existing two-policy completion
handoff. Later policy/catalog updates, v2 export and independent native activation remain
unfinished; this increment enables no live application current creation or original write.

## Same-ID conditional transport

The actual Google connection now captures bounded JSON with a provider strong entity
tag and exact before/body/after metadata fences. A private connection identity binds
the condition to its real observation. Body access returns owned copies. Before an
update, actual JSON MIME, byte length, parent/corpus, download/edit permission, tag and minimum
version are checked again. Original MIME, foreign connections, stale conditions and
scope changes cannot become writes. Numeric version movement between separate exact
captures is allowed; a numeric version is never an If-Match value.

Only the captured same-ID v2 media PUT is available. A server412 or uncertain completion
propagates to the semantic owner; it cannot allocate a replacement, issue an unconditional
update or fall back to v3. Successful status/body is not a receipt: the body is disposed
unread, and a complete newly fenced observation must match the intended bytes, advanced
version/tag and original scope. Cancellation and shutdown join actual borrowed I/O.

The [v2 File resource](https://developers.google.com/workspace/drive/api/reference/rest/v2/files)
defines the actual tag/version/size/scope/capability fields; the
[v2 update method](https://developers.google.com/workspace/drive/api/reference/rest/v2/files/update)
provides the media PUT endpoint. The earlier isolated live probe establishes conditional
media capability, not live acceptance of this application adapter. Its complete89-case
synthetic transport regression and strict Desktop build pass. Native Root verification,
durable signed pending-work/observed-head ownership, cross-process recovery and runtime
composition remain mandatory; this lower interface alone grants no write authority.


## Producer observation and durable Root high-water

The joined producer reader now begins a durable nonce before any Google request. Its
actual conditional transport awaits local metadata persistence before downloading the
body and complete-byte persistence before Root interpretation. Metadata, digest and
version floors survive malformed content, cancellation and restart. Another nonce
invalidates earlier captures; changed final metadata commits a blocked phase before
another body request. A successful final byte callback runs after all transport I/O.

Only exact already-retained native Root signatures can advance producer high-water.
A valid cloud signature missing from the local signing journal requires recovery; it
cannot advance a signing counter. The retained initial-publication receipt supplies a
historical floor, never fresh authority. Private history stores at most4096 distinct
signing-intent IDs in canonical pages of at most1000, under the unchanged wire codec
limits. Load checks every referenced envelope, signature and successor, including older
observed prefixes. Root bodies remain in the immutable signing journal. Full Int64
Drive versions are canonical decimal strings; SQL checks actual UTF8 byte bounds.

Capture and fence objects bind one private live-reader identity, exact nonce, approved
pin/slot, account revision and native Root possession. Closing denies admission, joins
uncooperative physical I/O and retains borrowed account/key/state owners until exit.
Observation and revocation do not depend on an active publisher. A same-owner final
fence still grants no graph completeness, canonical activation or write permission.

This producer reader is intentionally not yet composed into runtime publication. The
selected policies, complete ancestry/covered facts and final fence must share its
lifetime before atomic admission. General fixed Root transitions/CAS/lost-ack recovery
and the legacy inventory/current-selection handoff remain required. Persisted fenced
state becomes historical after restart; it cannot authorize a new process. Processor
schema13, Room12, native private7 and existing operational budgets remain unchanged.


## Producer selected-policy lifetime

The same joined reader now reads only the publisher/source IDs, byte lengths and SHA
references selected by its captured Root head. Actual JSON MIME, account/workspace,
parent/corpus, positive version and exact declared length precede a policy body. Known
immutable version/digest floors are checked without changing the legacy scan. Complete
owned bytes must match the signed reference; policy Root signatures and selected
sequences are verified. A bad publisher stops before the source request. Revocation
remains readable when no publisher is active.

Callbacks and return admission recheck the live private reader identity, nonce, account
revision, independent pin/slot and native Root possession. Foreign readers, superseded
captures and already-fenced captures cannot start policy I/O. Cancellation/close join an
uncooperative physical policy body before borrowed owners retire. Returned envelopes
are owned copies. A successful holder remains pending: it does not update legacy
inventory or mark a scan/current generation complete. Full retained policy/history,
approved-source/publisher admission, selected ancestry/coverage/facts and atomic
activation still need integration under this same lifetime before the final fence.


Immutable graph-object transport now exposes actual MIME/corpus and includes complete
sorted parent memberships plus corpus in its before/after metadata fence. Generic,
exact-JSON and listed-candidate routes share that check; a valid SHA/version alone
cannot hide a move or additional parent. Existing JSON/binary bounds and original-MIME
rejection remain. The graph owner must compare those actual returned fields against
its captured scope; this transport improvement alone supplies no graph/current authority.
