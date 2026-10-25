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
actual provider allocation and mutable publication remain pending. Pure serialization,
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
