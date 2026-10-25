# MPA 0.6.0 implementation ledger

## Discovery SQL belongs to one bounded batch

Each discovery transaction now prepares its prior-row, upsert and source-membership
statements once and reuses them only for that captured batch. Every nullable and
item-specific parameter is reassigned; account/source parameters belong to the batch.
Readers and commands retire with the serialized database operation. SQL constraints,
identity/hash/version rules, one revision advance, atomic rollback, the250-row cap,
FULL-WAL durability, schema and SQLite cache size are unchanged.

All367 Core cases pass, preserving every365 preceding identity, together with all12
discovery cases and a zero-warning/error strict Desktop build. Two new cases cover
null/value transitions, stale versions and overlapping/different-account sources,
and a final membership SQL failure that rolls back earlier upserts/memberships and
allows the same batch to retry. Frozen source/report/full-log hashes and unchanged
10k/100k observations are in preflight-060/discovery-prepared-commands-validation.json.
Concurrent host workload makes those observations unsuitable for a controlled speed
claim. The preceding Windows30-minute contract deadline failure is still unresolved
until a complete new exact-source run passes. No deadline or fixture is relaxed.
Full393 ordinary AppServices, mandatory scale/reference and final joint acceptance
remain required; this optimization does not complete the0.6 product.

## Complete selected graph stays under the joined current reader

The producer now walks only the Root-selected catalog ancestry and its source-covered
facts, under the same reader/account/native-key/observation nonce. A higher uploaded
commit remains retained pending work. Every selected commit signature and predecessor
is checked before facts, and source coverage is checked before staging. A borrowed
stage receives bounded leaves with awaited writes. Partial failures cannot fence an
older completed graph, and close/cancellation join actual bodies and stage callbacks.

The immutable adapter checkpoints actual metadata before and after each body and
complete actual bytes before expected-digest or semantic admission. Durable paired
observations preserve expected references separately from actual body hashes, full
Int64 version floors, actual encoding/corpus/complete parents and failures across a
new Root nonce. Existing legacy inventory/completion records are not rewritten.
No original body, new network path, schema change or larger bound is introduced.

Initial-policy evidence is verified once in each individual serialized SQL snapshot.
An opaque borrowed proof rejects another database/transaction and use after commit or
rollback. It never caches verification across network or transaction boundaries;
all Root history, signatures and current account/nonce checks remain mandatory.
This removes repeated identical verification inside one observation operation while
keeping the existing ten-second joined-callback rendezvous tests unchanged.

The final current focus passes all52 cases, preserving the preceding33 and adding19;
all116 Google cases preserve the preceding108 and add8. Three new Core cases verify
snapshot ownership and retired/corrupt evidence. All365 Core cases pass, preserving
every362 preceding identity; the strict Desktop build has zero warnings/errors.
Exact source/report/full-log hashes are frozen in
preflight-060/current-selected-graph-validation.json. Full393 ordinary AppServices,
mandatory scale/reference jobs and fresh complete CI remain required. No final
joint-release claim is made here.

The returned graph/fence is still pending. Actual private SQLite canonical staging,
local human/authority/history admission, same-ID Root transition/CAS/lost-ack recovery,
runtime handoff/export and atomic activation remain unfinished. The Windows contract
job on7704924 exceeded its unchanged30-minute deadline: its362 Core cases passed,
but the full job/workflow did not. Its100k fixture seeding dominates the run and is
being investigated without raising deadlines or removing scale assertions.

## Immutable object reads fence actual corpus and complete parents

The generic immutable, exact-JSON and listed-candidate read routes now retain actual
encoding and shared-drive identity. Their before/after equality includes the complete
sorted parent memberships and corpus, in addition to version/type. A changed additional
parent or moved corpus cannot hide behind unchanged bytes/version; harmless parent
ordering remains valid. Malformed present corpus identities stop before a body. The
existing five-field constructor/deconstruction and all JSON/binary byte caps remain.

All108 Google cases pass, retaining every97 preceding identity. Eleven new cases cover
all three routes, malformed corpus before body, changed/removed corpus and extra parents,
stable parent order and actual binary encoding/corpus without implying gzip validity.
Strict Desktop builds with zero warnings/errors; frozen source/report/full-log hashes
are in preflight-060/immutable-scope-fences-validation.json. Fresh exact-source full CI
remains mandatory. The future same-lifetime graph owner must still compare actual corpus
against its approved Root capture and verify signed history/coverage/facts before final
fencing/atomic admission. No original MIME, write authority, schema change, relaxed cap
or completed0.6 product is introduced.

## Selected producer policies share the current-reader lifetime

The captured producer reader now fetches only the exact Root-selected publisher and
source references. Actual metadata/type/length/parent/corpus and known immutable
version/digest floors precede the body; complete bytes/SHA and Root signature/sequence
checks precede typed return. Invalid publisher signatures stop before source I/O.
Revocation works without an active publisher. No scan, history or current authority is
inferred from returned semantics; full graph and atomic admission remain unfinished.

Each callback/return rechecks one reader/nonce/pin/account/native-key lifetime. Foreign,
superseded or fenced captures deny I/O. Close/cancellation join actual uncooperative
policy bodies and retain borrowed owners. Envelopes return owned copies. Repeated reads
do not modify legacy inventory or its completion receipt. Source/history admission must
still be composed before final fencing and canonical activation.

The initial new test compile failure assumed a source configuration had a Root Sequence
property; actual source semantics expose Revision and the selected head owns Sequence.
The corrected assertion preserves both distinctions. Final source passes all33 focused
cases with every19 previous current-reader identity retained and a strict Desktop build
with zero warnings/errors. The ownership assertion mutates the actual returned-memory
backing arrays, then requires later reads to preserve the original envelopes. Source,
TRX and full-log hashes are frozen in preflight-060/local-current-policies-validation.json.
Fourteen new cases cover exact references, MIME/parents/corpus,
length/minimum versions, changed bytes, invalid Root signatures, revocation, ownership,
nonce/disconnect races, final fencing and joined physical cancellation/close. Full374
ordinary AppServices and mandatory scale/reference categories remain required in fresh
exact-source CI. Room12/Processor13/native private7 and budgets are unchanged; no runtime
composition, original write, canonical activation or final0.6 release is enabled.

## Producer Root observations persist before semantic admission

The new producer reader begins a nonce before network work and composes actual Google
metadata/body callbacks with bounded local persistence. Actual metadata commits before
the body; exact bytes/SHA/version commit before Root parsing. Malformed content,
same-version rewrites, Root rollback and missing older signing history fail closed.
Only already-retained native Root envelopes may advance observed high-water; valid
foreign-to-local history requires recovery instead of counter adoption. Publisher
revocation does not prevent Root observation. Captures/fences bind one live reader,
nonce, pin/slot and account revision. Close/cancellation join physical I/O and retain
borrowed owners. Changed final metadata blocks before another body download; no I/O
follows the final byte checkpoint.

Private history references4096 signing intents through canonical pages of at most1000,
without enlarging the wire codec cap or duplicating Root bodies. Load verifies every
actual Root envelope and successor. Full Int64 versions use canonical strings; SQL
bounds actual UTF8 bytes. Five new codec cases include1001/4096 identities, malformed
splits, duplicates, bounds, defensive ownership and required rejection of the old
single-array representation. Initial local-current test compilation/analyzer failures
are retained and corrected without suppressions or weakened prior assertions.

The full earlier candidate passes97 Google,357 Core and360 ordinary AppServices cases
with a strict zero-warning Desktop build. That candidate predates the private-history
paging correction and is frozen separately, never relabeled as final-source proof.
Final paged source passes all362 Core,97 Google and19 affected AppServices cases,
with every preceding identity retained and a strict zero-warning Desktop build. Frozen
source/log/TRX evidence is in preflight-060/local-current-reader-paged-validation.json.
Fresh full exact-source platform CI remains required, including all360 ordinary
AppServices and mandatory scale/reference categories. Runtime composition, selected-policy/
full-graph admission, canonical activation, fixed Root CAS/recovery and the complete0.6
product remain unfinished.
No live application current file/original is written. Schemas and budgets are unchanged.

## Network admission closes before asynchronous token delivery

Exact43a4c30 CI exposed a Linux shutdown race: the actual conditional write joined and
its late result was rejected, but two readback requests started after close because
CancelAsync had not yet delivered cancellation to linked tokens. The unchanged test
required exactly3 metadata reads and observed5. That failed run is not accepted or
rerun into a pass; its logs and artifact remain preserved.

The Google connection now starts credential acquisition, refresh and HTTP tasks under
the same gate that closes admission. Its closed flag immediately denies later steps,
independently of cancellation callback timing. Identity reads, bounded media reads,
create-only publication and conditional updates use this shared owner boundary. Work
already admitted still joins before the connection/credential owners retire. Successful
write completion checks the boundary before any readback can start.

All90 Google cases pass locally, including every89 preceding identity and the unchanged
exact3-read assertion. An additional case closes the owner from inside its admitted
write callback and requires joined retirement, no readback and disposed responses.
Strict Desktop builds with zero warnings/errors. Exact0aa7a0c then passes all four
push workflows, including every contract, scale and dependency-reference job on all
four desktop platforms. Independently downloaded contract artifacts verify95 Protocol,
357 Core,90 Google and341 ordinary AppServices cases per platform, plus LAN/web; the
previous exact failed assertion and new close-inside-write case execute on each.
No assertion, cancellation deadline, warning gate or resource-ownership rule is relaxed.
These preceding artifacts do not validate later source changes or final0.6. The broader
current/CAS runtime and full product remain unfinished.

## Actual strong-condition JSON transport

The captured Google connection now supports bounded actual v2 JSON observations with
a strong provider entity tag. Owned body copies, exact size, JSON MIME, parent/corpus,
positive canonical version and download/edit capability precede write admission. Only
a snapshot from that same connection can supply a condition; callers cannot construct
one from a file name or raw tag. Changed corpus/parents, stale tags, version rewind,
original MIME and retired edit permission stop before a PUT. Numeric versions are
never sent as conditions; monotonic movement between separately fenced reads is allowed.

An inconsistent length under the same tag also rejects before write. Updates use only
same-ID v2 media PUT with the captured strong If-Match. Server412 and
network uncertainty propagate without allocation, unconditional retries or v3 fallback.
A200 response body is disposed unread; exact before/body/after observation must prove
the new bytes, advanced version/tag and retained scope. Late physical writes join on
cancellation/close and cannot return a successful result. An observed snapshot grants
no Root authority, permission to modify originals or canonical activation.

All89 Google transport cases pass, including all72 previous test identities and17 new cases for
owned input/output, scope/MIME/size/tag guards, shared drives/corpus moves, stale and
rewound conditions, actual412, discarded completion, readback mismatch, foreign owners
and joined cancellation/close. First83-test run retained two new assertion-type errors:
InvalidDataException does not inherit IOException. Exact typed expectations fix them;
no production guard or prior assertion is weakened. Final strict Desktop build has zero
warnings/errors. Frozen source/log/TRX evidence is in
preflight-060/conditional-json-transport-length-validation.json. The earlier88-case
candidate proof remains frozen separately; final code includes the additional length guard.

No live application JSON update is enabled. Earlier isolated synthetic v2 media evidence
is separate from live acceptance through this new adapter. A Root-signed durable intent,
semantic current-head/graph reconciliation, same-ID cross-process lost-ack recovery,
runtime handoff and final control publication remain required before composition.
Schemas, dependencies, existing operation deadlines and byte/history caps are unchanged.

## Create-only initial-current publication and joined recovery

The new initial-current owner composes the actual durable allocation/native Root signer
and create-only Drive transport. Both exact initial policies are verified before/after
create, including Root signatures, retained versions and current native/account/source
approval. Actual sharing/corpus is checked; the last network operation fences that same
current-file ID. Uncertain completion reuses the original ID and sealed bytes. Conflicts,
substituted results, replaced policies, lower versions and equal copies never authorize
an overwrite or replacement. It never restarts the two-object bootstrap inventory.

A bounded historical receipt atomically saves body/hash/claim after the exact read and
local fences. It binds the fixed slot/v2 pin, Root envelope/sequence, original policies,
account revision and minimum Drive version. Missing/corrupt records stop recovery;
SQL and low-storage failures roll back all receipt rows and preserve the actual cloud
ID/signature for retry. Cancellation and close join physical writes. Failed allocation
retirement leaves exit unconfirmed and resources retained until that same ID persists.

Nineteen focused cases pass, including concurrency/reopen, actual lost-completion
recovery, policy replacement/rewind, sharing, source/account changes, substituted return
identity/body, joined cancellation/exit, failed retirement, atomic SQL rollback and
receipt corruption/storage recovery. The initial new-test compile failures (internal
connection access and repeated-array allocation) are retained; an existing SQL helper
and one owned expected array fix them without weakening assertions or analyzers.
The final source passes all357 Core and341 ordinary AppServices cases, zero failed or
skipped, followed by a strict Desktop build with zero warnings/errors. The full run
includes all19 new cases after final retirement-failure recording; physical test owners
have joined. Source/report/log hashes are frozen in
preflight-060/initial-current-publication-validation.json. Existing scale/reference
categories remain mandatory separate CI jobs; no deadline, budget or gate is weakened.

Exact preceding8a16076 passes all four push workflows. Independently downloaded native
artifacts verify177 unique passing cases, complete process-bound allocation tracing,
108 mapped valid PNGs and exact SDK receipts on each API29/35/37. The selected-policy
native case actually executes on all three APIs. Native screenshots are decoded,
not visually reviewed. All process warning/error records are retained for separate
attribution/release review; no earlier version-specific diagnostic exception is extended.
These preceding artifacts do not validate the new C# publication owner or final0.6.

Runtime composition/export is intentionally still pending. The legacy reconciler needs
a complete current route, rather than ignoring the new Root kind or selecting the
highest unselected commit. Current CAS must precede finished-generation admission; old
immutable pending work and conservative unknown-removal recovery remain intact. This
receipt grants no fresh authority/readiness/activation. No live application current file
or original is written here. Room12, Processor13 and native private7 remain unchanged.

## Exact bounded JSON observations on the desktop transport

The captured Google connection can now read one independently selected JSON file ID
without listing, searching for a copy or knowing its body length in advance. Actual
metadata must report a concrete positive bounded size, JSON MIME, matching identity,
workspace membership and download permission before any body request. Complete owned
bytes and matching before/after revision metadata are required. Images, shortcuts,
documents and compressed/binary objects are rejected before content download.

All72 Google transport cases pass, including seven new exact-read cases; the previous
65 assertions remain. Tests cover unknown lengths, short/oversized bodies, actual scope,
changing metadata, cancellation and provider errors. Desktop builds with zero warnings
or errors. Source/log/report hashes are frozen in
preflight-060/exact-current-json-validation.json. No schema, dependency, warning gate,
deadline or byte budget changes. Live acceptance through this new method is pending.

This optional transport interface is not yet composed into current publication/runtime
admission. Returned bytes are untrusted observations: Root verification, durable head
high-water, same-owner final fencing and atomic canonical admission remain required.
No current file, original, readiness grant or final0.6 release is published here.

## Selected native policies share the captured control lifetime

The native current reader now fetches only the exact publisher/source IDs, lengths and
SHA references selected by its captured Root head. It checks actual account/workspace,
JSON type, positive version, length, parent/corpus, downloadability and optional provider
checksum before a body, then checks complete owned bytes and Root signatures/sequences.
A bad publisher stops before another role request. No listing, changes-feed scan, equal
copy lookup, original or credential read is introduced.

Policy semantics and defensively copied envelopes remain PENDING. They share the same
serial reader/nonce as the control head: final fencing prevents later policy I/O; close
and cancellation join an uncooperative physical body and reject late results. Another
valid head cannot be attached to the captured nonce: identity, connection, sequence,
actual content SHA and metadata version/corpus must agree with durable observations.
Full graph, approved-source/current-publisher/retired-epoch admission and atomic canonical
activation remain required; this policy holder is not a readiness/activation grant.

Validation passes713 Android unit cases across117 suites, zero lint findings and both
APK builds; only the approved exact AGP diagnostic remains. Eight new cases cover exact
shared vectors, ownership, metadata/corpus/MIME/digest guards, replacement bytes/foreign
signatures, selector changes, joined close/cancellation and substituted heads. The first
new-test compile failure assumed metadata was a data class; explicit immutable-object
construction preserves every negative assertion and passes29 focused cases before the
full713. Reports and sources are frozen in preflight-060/native-selected-policies-validation.json.
One additional actual-EC native policy/byte/fence case compiles for API29/35/37 execution,
expected177 cases per API; it has not run locally. Existing native assertions remain.
Private schema7, Room12 and Processor13 are unchanged; no actual current file, original,
canonical generation or final0.6 release is written/published.


## Joined current-file identity allocation

The new serial allocation owner captures ready native keys, current local publisher/
source/account authority and the immutable control plan before provider I/O. The actual
account lease is checked again before GenerateIds. One returned ID/original account
revision is retained and checkpointed even when cancellation or selector retirement
arrives during the provider call. Post-I/O authority changes deny successful result
admission after retaining that historical identity. No file is created or signed.

Concurrent calls and a reopened owner reuse the same durable ID. A failed SQL/storage
checkpoint keeps the known live receipt for unchanged retry before another provider call.
Stop denies new admission, joins physical I/O and persists the pending receipt. Failed
retirement leaves exit unconfirmed and borrowed resources retained until the same receipt
can persist. GenerateIds creates no file: unobserved unused IDs after a hard crash are
not claimed exactly-once, and signature/export/create remain forbidden before durability.

Validation passes13 focused cases and all322 ordinary AppServices CI-subset cases, zero
failed/skipped; Desktop builds with zero warnings/errors. Nine new allocation cases
cover concurrency/reopen, low storage, joined cancellation/exit, stale authority before
and during I/O, and failed-retirement recovery. Frozen source/log/TRX hashes are in
preflight-060/current-allocation-validation.json. Schemas and existing tests/budgets/
warning gates remain unchanged. Runtime composition, actual signed-current publication,
initial-phase handoff, v2 export and canonical activation remain pending; a returned slot
is historical identity evidence, not cloud availability or setup readiness.


## Actual Drive media conditions and retained-identity recovery

The isolated Windows feasibility probe now verifies conditional v2 media writes on a
small owned synthetic fixture in the approved disposable root. Captured tags permit200;
formerly valid stale tags reject412 and preserve complete bytes/content tags. A real200
completion is durably journaled and disposed before the caller consumes its body. The
same generated ID then yields exact pending bytes. A new process reopens that same
journal/fixture, allocates no replacement ID and verifies the retained stale condition
still rejects while preserving bytes, actual scope and exact read fences.

Checksum-bearing v3 metadata is captured before the exact v2 body-read window. Five
earlier exploratory failures remain archived. Numeric versions moved across operations,
and an initial create/read also moved1->2 without a prior rejected upload; causality is
unverified. Same-ID replay falsifies the stronger exact+1 hypothesis with delta0. The
correct contract records movement between separately exact captures and still rejects
rollback, changed content tags/bytes, wrong status/scope and moving within-capture fences.
Numeric versions never substitute for actual entity tags. No application assertion,
existing deadline, request budget or warning gate was relaxed.

Final isolated build has zero warnings/errors. Existing protected-store/loopback/transport
checks and new media/recovery receipt, alias, condition, rollback, tag, byte-bound and
path guards pass without Google access. One complete live sequence and independent
same-ID recovery pass using the existing protected grant; all six synthetic fixtures
and private journals are retained, and no existing original changed. Evidence is frozen
in preflight-060/drive-conditional-media-validation.json, with private IDs/credentials
excluded. Full sequence and final recovery span probe candidates; this is not one final
application revision or production publication acceptance. Runtime current ownership/CAS,
additional accounts/shared drives, actual socket/process loss, independent Android access
and exact joint release remain unfinished. See the detailed feasibility record.


## Native-key initial current-control signing

The initial no-catalog current snapshot now signs through the actual native key owner
and serial durable Root journal. Its signing intent ID is the fixed control-slot ID;
concurrent retries retain Root sequence3 and exactly the first sealed envelope. The body
is reconstructed from the completed initial policy receipts and their verified signing
sequences. Both initial policy selections, original human source approval, ready native
identity, current local publisher/account authority and actual allocated control ID are
rechecked before reserving or replaying a signature. No caller-provided policy bytes or
new intent IDs can replace this initial snapshot.

Disconnect, publisher revocation and changed human sources stop fresh initial-current
signing while preserving the old sealed bytes and file identity. Publisher native keys
still cannot sign current-control documents or human operations. Signing performs no
Google request, upload, canonical activation or original write. Initial policy publication
and every previous signing/role/recovery assertion remain intact.

Validation passes38 focused native-key/signing cases and all313 ordinary AppServices
CI-subset cases, with zero failed/skipped tests; Desktop composition builds with zero
warnings/errors. Four new cases cover allocation/completion prerequisites, concurrent
exact replay, disconnection, revoked publisher and changed source approval. Frozen
source/report/log hashes are in preflight-060/native-current-signing-validation.json.
Scale/reference categories remain mandatory in their existing separate matrices.
Actual allocation ownership, create/CAS publication, v2 setup export, later catalog and
policy updates, full canonical admission and the finished0.6 product remain pending.


## Durable producer control slot and shared Root signing stream

The producer now reserves one immutable local control plan after the completed initial
policy-publication receipt and current local publication authority. A separate atomic
receipt captures one actual provider file ID and its original account revision. This
repository generates no provider ID, makes no network request and publishes no file.
Reopen/reconnect cannot replace or relabel the captured identity. Root/workspace/existing
immutable-outbox aliases, missing records, corruption, loss of the independent controller
pin and missing allocation over signed work fail closed. Historical receipt retention after
disconnect grants no new access. Low-storage and injected SQL failures roll back the whole
checkpoint, retaining the same received ID for retry. Processor schema13 is unchanged.

The outgoing canonical writer shares the signed decoder's exact shape/scope validator;
it returns unsigned bytes, never verified authority. Current-control signing intents use
the same durable Root counter and first sealed-envelope history as publisher/source
policies. The concrete allocated slot, matching Root identity, explicit capability and
exact roles/counters are required. Publisher signing remains catalog-only. Independent
Python golden vectors and payload-negative cases exercise the outgoing writer.

Local validation passes95 protocol tests,357 Core tests and the309-test ordinary
AppServices CI subset; the two existing scale/reference categories remain mandatory
separate CI jobs. Strict Desktop composition builds with zero warnings/errors. Frozen
source/report/log hashes are in preflight-060/current-control-slot-validation.json.
The first Core compile and one subsequent new-kind inventory failure are retained; a
strict local ID helper and the explicit current-control inventory kind fix those issues.
All old counter/hash/role/preservation assertions remain.

Exact parent5e18172 Android and Native workflows pass. Independently downloaded artifacts
verify176 unique passing cases, complete process-bound runtime traces,108 mapped valid
PNGs and exact SDK receipts on each of API29/35/37. Both new control/fence/reopen and
schema6-preservation cases actually execute on all three. Frozen proof is
preflight-060/ci-5e18172-native-validation.json. Screenshots are decoded/validated,
not visually reviewed; runtime diagnostics and physical-device acceptance remain separate.

Actual provider allocation ownership, native-key runtime signing composition, current
publication/CAS/lost-ack recovery, v2 setup export, complete graph/source/retired-epoch
admission and atomic canonical activation remain unfinished. The parallel synthetic-only
media feasibility probe is not production code or accepted publication evidence. No
current-control file or0.6 release was published, original changed, conservative history
cleared, warning exception added or phone/desktop link introduced.


## Native captured current control and durable read high-water

The native client now captures only the independently pinned current file, through the
actual Google metadata reader. It does not list the folder, scan account changes or search
for equal copies. File/account/workspace/corpus, JSON type, bounded length, downloadability,
version and actual SHA are checked. Actual metadata is persisted before its body request;
a completed bounded read's SHA/version is persisted before Root semantic verification.
A previously seen Drive version cannot acquire different signed or malformed bytes after
retry/restart. Invalid Root documents retain their exact read receipt and version.

New attempts retire old nonces before I/O, while preserving signed Root-head history,
actual-file version/corpus and last complete content receipt. Root sequence collisions
between controller policy and current-control roles fail in either arrival order. The
ordered immutable head digest, canonical attempt digest and approved connection share
one integrity marker; missing history, changed attempt bodies, lost markers or missing
pins fail closed. A renewed account selector retires old captures without resetting trust.
Full bounded Root verification checks cancellation between records. The existing4096
workspace/16384-event limits and64MiB write safety reserve remain; the separate current
head history has its own4096 bound. Retention and measured CPU work are still required.

A final probe re-reads the same actual file and exact version/bytes. Changed evidence
blocks the old capture and retains the highest observed version. A successful fence
prevents every later read through that capture. The reader serializes its borrowed I/O;
close denies late admission and joins a held physical read before Google/state owners
retire. Caller cancellation also joins uncooperative I/O. Fenced evidence remains PENDING:
future graph reads must join this lifetime before atomic canonical activation. A restarted
reader requires a fresh capture; persisted fenced state alone grants no current authority.

Private schema7 adds two tables and preserves1ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“6;6->7 does not rewrite old controller or
workspace bodies. Genuine migration tests retain approval, account, pending evidence and
original bytes. Historical migration fixtures drop only the new tables when constructing
older versions; every existing preservation assertion remains. Room12 and Processor13
are unchanged. Actual Google capture also rejects a file whose corpus differs from its
captured workspace before body I/O.

Validation passes705 unit cases/116 suites, zero lint findings and both APK builds.
There are21 new current-control cases and19 actual HTTP transport cases. Two additional
native signature/fence/reopen and schema6-preservation cases compile; exact-source
API29/35/37 execution is still required. Frozen source/report/APK hashes and116 unique
XML reports are saved in preflight-060/native-control-owner-validation.json. Only the
approved exact AGP diagnostic remains. First-focus failure evidence is retained: the new
negative helper originally caught IOException while protocol errors are IllegalArgument-
Exception. Adding only that expected protocol type passes all110 focused cases. Review
then adds complete read receipts and three corruption/version cases; the final705 includes
every old assertion. No invariant catch, deadline, warning gate or old bound was relaxed.

Prior exact793df09 passes all four push workflows. Downloaded evidence independently
verifies174 passing native cases, complete runtime tracing and108 valid PNGs per API;
32 passing TRX reports across12 protocol/scale/reference jobs,92 protocol cases on each
OS; and four strict desktop build reports with zero web audit findings. The10k/100k
fixtures executed, but Windows reports roughly201/216GB cumulative allocation and462/472s
preparation. These are measurements, not final performance acceptance. Complete evidence
is in preflight-060/ci-793df09-joint-validation.json. Those private6 results predate this
private7 increment; desktop reports are not installer or UI acceptance.

Producer current-ID reservation/signing/CAS/lost-ack publication, v2 setup export, actual
full-graph/source/retired-epoch admission, canonical activation and runtime gallery remain
next. No current-control file was published, unknown-removal history relaxed, original
written, phone/LAN link introduced or final0.6 release claimed.

## Explicit current-control protocol for independent clients

The next authority step now has a concrete C#/Kotlin wire contract. A v2 public connection
pins one actual mutable Drive file ID with the independently approved root key/workspace.
Its root-signed current snapshot selects exact immutable publisher/source policies and an
optional catalog commit. Signatures, scope, distinct roles, strict bounded counters and
exact policy bytes/sequences are checked. Known control heads reject rewind/fork/retarget
and changed policy references signed before the earlier accepted head. Explicit publisher
revocation cannot select an old catalog. Legacy v1 codes retain exact wire identities and
cannot infer a current-control pin. [Design and required integration](CURRENT_CONTROL.md)
explain the account-wide unknown-removal problem and the remaining Drive trust assumptions.

Both consumers use the same independent synthetic Python cryptography vectors:39 signed
malformed/scope/alias cases,8 invalid v2 connections,4 invalid successor transitions,
full counter range, revocation and validly hashed policies signed by a foreign root.
No private test key is retained. Initial Android10 cases pass. The first C# build rejects
new `Object` naming under CA1720; renamed to `Reference`, preserving the analyzer. The
corrected complete C# protocol suite passes91 cases, zero failures/skips/diagnostics.
The initial complete682-case Android/lint/APK run and desktop build pass and are frozen
as historical candidate evidence. Review then makes bounded envelope ownership explicit
before hashing/signature verification, preventing borrowed-buffer mutation from mixing
verified semantics and a different retained digest. An additional case per consumer
checks immutable accepted identity and empty/oversized envelope rejection.

Final validation passes683 Android cases/115 suites, zero lint findings and both APK
builds;92 complete C# protocol cases and the desktop composition build pass with zero
warnings/errors/skips. All9 new cases per consumer pass, alongside every prior assertion.
Only the approved exact AGP diagnostic remains. Frozen source/report/assembly/APK hashes
and115 unique Android reports are saved in preflight-060/
current-control-owned-validation.json. Earlier682/91 evidence remains separate and is
not rebound to the ownership correction. Exact-source cross-platform/native CI remains
required; these are local Windows/JVM checks, not completed0.6 acceptance.

This component does not publish control documents, reserve IDs, change cloud state,
relax conservative history recovery, activate catalogs or complete0.6. Producer durable
CAS/lost-ack integration, actual captured transport/final fencing, high-water persistence,
retired epochs, canonical staging/atomic activation and native gallery remain next.
Private client6, Room12 and Processor13 are unchanged. No phone/LAN connection or original
write permission is introduced.

Exact3c638a1160dc9bb2f3434d387db00399156a1a6f passes Android40171677578 and Native
40171677569 across API29/35/37. Downloaded artifacts independently verify174 unique passing
cases, complete process-bound resource traces and108 valid mapped PNGs per API, including
the two actual schema6 read-retirement/migration cases. Proof is saved in preflight-060/
ci-3c638a1-native-validation.json. This prior revision does not validate the new v2 contract,
S23 acceptance or a completed joint release.

## Observed catalog reads and an explicit final-fence phase

Catalog ancestry and covered-facts reads now use one durable prepared/replayed read set.
The adapter admits only the exact captured account/workspace/file ID/length/SHA; it never
looks for an equal copy. Version, encoding, corpus, parents, downloadability and actual
read bytes are checked before a semantic consumer receives them. A replaced attempt or
canceled physical read cannot return its late result; borrowed I/O joins before retirement.

The final unchanged check atomically records an explicit fenced phase. That phase closes
all catalog metadata reads, including attempts to create another adapter or repeat the
fence. A full validated replay context rejects active, changed or sticky-recovery state
before transport. The read set grants staging access only: current controller/retired-
epoch/head admission, approved-source/full-graph canonical staging, atomic generation
activation and gallery integration remain unfinished.

Private schema6 preserves schema1/2/3/4/5. Schema5 requires no table/body rewrite; its
legacy replay JSON defaults to pending, never an accepted final fence. A historical5
fixture preserves its complete valid old role/global event chain, raw event bytes, pin,
account, read identities and original. A separate changed-body/event mismatch fails
closed. Legacy Room12 and Processor13 remain unchanged; no existing preservation
assertion or4096-document/16384-event bound is relaxed.

Focused90 cases pass, followed by11 new catalog cases with zero lint issues and both
APK builds. The additional integration verifies real signed ancestry and the complete
coverage/GZip/facts tree through retained SQLite receipts, exact sequential reads and
retirement at the final fence. Final full regression passes674 cases across114 suites,
zero failures/errors/skips, zero lint findings and both APK builds. Only the approved
exact AGP diagnostic remains. Source/report/APK hashes and114 uniquely archived reports
are saved in preflight-060/native-observed-objects-validation.json.

Two new native cases compile for final-fence retirement and preserving schema5 migration;
exact-source API29/35/37 execution remains required. The first full-check action was not
executed when automatic approval review temporarily hit a usage limit. Once the account
reported ordinary usage available, the identical action passed the same approval path.
No approval bypass, credit reset, warning waiver, test retry or larger deadline was used.

## Reviewed SDK and policy snapshot verified across emulator APIs

Exact66e65ad1e9c374b891d6f996dc023c54212139a0 passes Android40165064081 and Native
40165064110 on API29/35/37. Downloaded artifacts independently verify172 unique passing
cases, complete process-bound runtime traces and108 valid mapped PNGs per API. The new
caller-owned SQL snapshot/rollback case actually ran. Installation records verify the
reviewed CLI1.1.16717390 and all six exact SDK/image packages on every emulator runner.
These results predate the newer schema6 read adapter and are not its native acceptance,
S23/live Google acceptance or a completed0.6 client/release.


## Reviewed Android CLI pin after upstream replacement

Exact80ed3e0f3ede63f7d134124380f9b1b6138081c8 CI Android40164405339 and Native
40164405372 stop at SDK setup before any application/native tests: Google's mutable
latest CLI endpoint changed after the preceding successful matrix. The digest/version
gate correctly rejects the unreviewed tool. Local663-case application evidence remains
valid; these failed workflows provide no new native acceptance.

Google's [official release notes](https://developer.android.com/tools/agents/android-cli/release-notes)
identify stable1.1.16717390. The exact new Linux ELF was downloaded from the
[official endpoint](https://developer.android.com/tools/agents/android-cli/download),
SHA25643204dcc42955f4698b7a7ca945f26c26f594f9239f5f8e11b3b73d4ceaa11ae.
That same binary reports1.1.16717390 in the existing Ubuntu runtime and its SDK-install
help retains exact package specs. Its review cache stays in the task workspace;
no SDK package was installed and no user computation stopped for this probe.

CI now pins that reviewed SHA/version. All SDK/platform/build-tools/NDK/emulator/image
pins and verification remain unchanged. Eight SDK regression cases pass, including new
changed-download/no-execution and wrong-version/no-installation checks. Artifact, logs,
headers and source references are recorded in preflight-060/android-cli-1-1-review.json.
Full SDK installation and exact-source build/native execution remain required. This is
a tool pin update, not a warning waiver or a completed0.6 release.


## Native signed ancestry and transaction-bound local policy reads

The native consumer can now enumerate one independently approved publisher's entire
signed predecessor chain. Actual account/workspace, bounded envelope length/SHA,
ECDSA/library/actor/epoch/capability, contiguous stream sequence, generation identity
and commit/dependency aliasing are verified before yielding each entry. The4096 bound
matches the existing primary consumer. No read prefetches past the consumer's persistence;
cancellation joins an uncooperative read and rejects its late result. Exposed envelope
bytes and verified capability lists cannot rewrite the retained observation.

The local controller journal exposes the exact selected source-envelope SHA and a read
inside its caller's existing serialized SQLite transaction. It requires that transaction,
opens no second owner, verifies the same pin/signatures/selected maxima and participates
in caller rollback. It remains LOCAL accepted history: neither API certifies cloud-current
authority, retired-epoch admission, full graph/coverage, canonical activation or gallery
readiness. Those integrations are still unfinished.

Final strict regression passes663 cases across113 suites, zero findings in lint and both
APK builds. Eight new real-signature ancestry cases and two actual SQLite policy cases
pass; one new native snapshot/rollback case compiles but needs exact-source API29/35/37
execution. Private client5, legacy Room12 and Processor13 remain unchanged. Complete
source/report/APK hashes and113 archived XMLs are recorded in preflight-060/
native-ancestry-policy-validation.json. Only the approved exact AGP diagnostic remains.

The first new-test build caught a Long/Int comparison. A later32-case focused run passed
31 but rejected a260-character Windows native-SQLite fixture path. Isolated reproduction
retained that failure; shortening only the new test method name produces215 characters
and preserves every replay/reopen assertion. All663 final cases pass. Failed compile/log/
XML evidence is retained; no deadline, warning gate, history cap or old assertion changed.

## Exact screenshot correction verified on every emulator API

Exactb174e3835450b7bd2b9c00a7a4efb75aa7d8518b passes Android40159949284 and all three
Native40159949290 API29/35/37 jobs. Downloaded artifacts independently verify171 unique
passing cases, every process-bound runtime event and108 complete mapped PNGs on each
API. The previously incomplete dark/RTL search capture is now a valid97134-byte image
on API37, from the original single capture. No recapture or assertion retry qualified it.
These are emulator checks; final S23/live Google/complete0.6 acceptance remain pending.


## Search screenshot publication and exact history execution

Exact29e2c34608e9bd7f92c48fc9f0435a15cd0d8441 passes Android40150551312 and Native
40150551282 API29/35. Downloaded XML independently confirms171 cases, zero nonpass and
complete process-bound runtime tracing on all three APIs; the three new recovery/fence/
schema4-migration cases actually ran. API29's capture-order correction is verified by
all171 real started/finished identities. API29/35 each have108 valid mapped PNGs.

API37's job remains FAILED: one actual device search screenshot is61bytes containing
IHDR/sBIT/sRGB only. Pull, device hash and same-file reread agree; this is not transport
corruption. Its other107 mapped PNGs validate. The encoder/provider cause remains
unverified, and passing XML/runtime alone does not qualify that CI run for release.
The source failure and independent checks are recorded in preflight-060/
ci-29e2c34-native-png-failure-observation.json and ci-29e2c34-native-validation.json.

SearchChrome now passes its same single captured frame to the existing owned ARGB,
bounded complete-encoding, synced-descriptor and readback-verified publication helper.
All setup,48dp, clearing, line-count, filter/video and scrolling assertions are unchanged.
Strict lint has zero findings and both APK builds pass with only the approved exact AGP
diagnostic. Production and JVM-test sources and the application APK are byte-identical
to the653-case revision; those frozen unit results are reused, not falsely reported as
a new run. Source/APK/report hashes are saved in preflight-060/
native-search-chrome-evidence-validation.json. Fresh exact-source emulator runs remain
required. No recapture, test retry, exception or weakened capture gate is introduced.


## Native actual Drive history replay and immutable recovery

The native Google-only reader now captures account/corpus-bound change cursors and
reads bounded actual changes pages. Replay retains the earliest unresolved interval,
atomically journals every relevant observed identity and supported candidate before
advancing a page or reporting operational failure, and keeps removal/access/move
recovery sticky across successful listings and coalesced later entries. A128-page
batch is resumable across restart; it is not a permanent total-history limit. A rejected
final fence retains evidence without advancing its cursor. Private schema5 preserves
schema1/2/3/4, including exact older journal bytes and legacy global-corpus cursors.

Seventeen new actual SQLite history cases and five additional real HTTP boundary cases
pass. The corrected focused run passes78 cases, zero lint findings and both APK builds;
final full regression passes653 cases across112 suites, no failures/errors/skips, zero
lint findings and both APK builds. The only build diagnostic is the already approved
exact AGP setVisible deprecation. Immutable source/report/APK hashes and112 archived
reports are recorded in preflight-060/native-workspace-history-validation.json.
Three additional emulator cases compile for sticky removal, a rejected final fence and
real schema4 migration preserving raw event bytes, the original and approved account.
They have not run locally; exact-source API29/35/37 execution remains required. The first
focused run passed57 cases but caught an invalid Kotlin HTTP test declaration (non-Unit
return). Its reports are archived; explicit Unit corrects the declaration without
removing an assertion. No new warning exception, larger cap or weaker gate applies.

This still returns PENDING evidence. Actual current controller/retired-epoch admission,
ancestry/full graph, canonical staging/activation and runtime gallery integration remain
unfinished. No0.6 release or completed client is claimed.

## API29 runtime-capture preparation ordering

Exact3f9f8e9312e48415ed7ea971c636289eda835ce6 passes Android40148403416 and the
API35/37 jobs in Native40148403400. API29's job fails its uninterrupted runtime-capture
gate after the native tests and108 screenshot collection: its continuous log ends before
the tests because the confirmed API29 preparation restarted adbd/framework after the
stream had been opened. The retained heap profile proves the narrow16mÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢configured576m
zygote repair, without raising the existing AVD heap. This is incomplete runtime
evidence, not an accepted warning or passing API29 release result.

The CI owner now retains separate before/after preparation diagnostics and starts its
uninterrupted TEST stream only after preparation joins. The existing dead-reader check,
exact started/finished/resource-tracing verifier and every native assertion remain.
No reconnect, report substitution, test retry, increased deadline or exception is added.
Nine runtime-capture and nine heap-owner regression tests pass; Git Bash syntax passes.
Fresh exact-source emulator execution must verify this ordering correction. The sandbox
could not initialize Git Bash's DLL; its authorized read-only external syntax check
succeeded. This does not qualify the earlier incomplete API29 stream retroactively.

## Native pending inventory and durable recovery

The native client now commits an incomplete attempt before its first Google request,
binds it to the independently approved connection, and retains the actual start and
earliest unresolved cursor. It captures every eligible metadata identity across all
pages before downloading the first body. Unknown-length, empty and oversized metadata
remain durable failed identities; their disappearance cannot turn a later scan into
success. Learning a previously unknown length preserves identity. Original media is
not downloaded or changed by this owner.

Actual read hashes persist before semantic verification. Failed verification, canceled
I/O, storage failure and stale-owner completion preserve pending recovery. A successor
fences the exact old attempt. The entire coordinator and callback run off the UI thread;
recovery loops check cancellation, and source retirement joins before returning.
Private schema4 preserves schema1/2/3 pins, selectors and accepted controller evidence;
legacy Room12 and Processor13 remain unchanged. Missing accepted state fails closed.

A first passing628-case candidate exposed superlinear full-inventory hash rebuilding:
64/256 synthetic metadata records took297/2456ms on the shared Windows host. Immutable
global/per-role event heads now protect atomic row/event/attempt updates without full
body rescans per mutation. Startup, recovery and final enumeration verify the entire
contiguous chain and every materialized body/identity. The4096 distinct-document limit
is unchanged; the new private journal admits at most16384 events. Retention/ack compaction
is still unfinished, and these host measurements are not10k/100k media or S23 acceptance.
A54-case focused chain run passed with64/256 samples723/1490ms; different JIT/load conditions
mean the timing alone is not proof of a speedup. The algorithm removes full-dataset
mutation hashing. The focused XML was subsequently overwritten; final reports will be
archived under a unique name. No integrity check or assertion was relaxed.

New cases cover actual SQLite recovery/migration, unreadable identities, stale owners,
cancellation, atomic rollback, event/row corruption, page cycles, storage and principal
binding. Two additional emulator cases compile for interrupted recovery and preserving
schema3 migration. The first631-case run caught nullable lengths encoded as text instead
of JSON null; its failure report is retained and encoding is corrected. Final full
regression passes all631 cases across111 suites, zero lint issues and both APK builds.
The only build diagnostic is the already approved exact AGP setVisible deprecation.
Complete logs,111 uniquely archived XML reports, source/APK hashes, host-cost samples
and explicit limits are recorded in preflight-060/native-workspace-observation-validation.json.
Two new native cases compile into the test APK; execution still requires exact-source
API29/35/37 CI because no local AVD is available. No old assertion or warning severity
was weakened.

The result is explicitly PENDING evidence. There is no completed/current-authority or
catalog activation API. Real Drive history replay, current controller/retired-epoch
admission, ancestry, full-graph staging and atomic gallery activation still come next.
This owner is not yet wired into runtime gallery browsing. No full0.6 product/release
completion, live Google consent or S23 acceptance is claimed.

## Native screenshot completion and exact journal CI

Exact9b609eb217812c451247d41873cd95ec581329a8 passes Android40141224428 and all
three Native40141224425 API29/35/37 jobs. Downloaded XML independently confirms166
passing cases and107 mapped PNGs per API. The two actual journal/migration cases ran;
the previously missing nine setup images are now archived and validated by the collector.
This is emulator evidence, not S23 or live Google consent acceptance.

Visual review of three API35 setup frames found that captures could record the enabled
button's color animation before settlement; the isolated content fixture also lacked the
system insets supplied by the real application scaffold. Test fixtures now advance the
virtual animation clock before their single capture and provide safe drawing insets.
Existing enabled/approval/48dp/retained-geometry assertions remain active. A tenth required
image captures the actual MainActivity Shared library route after draft restoration;
108 screenshots are now mandatory. No production UI change, assertion retry or test
budget increase is used to obtain these frames. Corrected exact-source native execution
and visual inspection remain required.

Strict lint and both APK builds pass with only the approved AGP diagnostic. The application
APK and all production/JVM-test sources are byte-identical to8d71913, whose full614-case
regression remains valid. Native fixture changes compile; they were not locally executed
because no AVD is installed. Hashes and limits are retained in
preflight-060/native-library-screenshot-settlement-validation.json. Full0.6 catalog
activation, processing/client/installers and final joint acceptance remain unfinished.

## Native bounded Google Drive workspace reads

The catalog consumer now has a Google-only transport over the captured account gateway.
It lists all metadata workspace children, including binary compressed chunks, with
explicit bounded page tokens and incomplete-search rejection. Owned immutable metadata
observations retain actual account/workspace/parent/version/MIME/length/checksum and
availability. An old observed candidate cannot be silently substituted before its read.

Reads verify actual Google identity and workspace membership before/after transfer.
Original media MIME types, unavailable download permissions, foreign references, workspace
aliases, changed revisions/principals, partial responses, truncation, overflow and wrong
hashes fail while preserving the caller's previous content. Every request is GET-only.
Account/file replies are bounded at 16KiB, pages at 1MiB/100 rows, JSON metadata at 256KiB
and compressed objects at 4MiB. Provider error bodies are not consumed without bounds.
Metadata JSON parses on I/O; body hashing runs off the UI dispatcher. No local original
copy, image mutation or phone-to-desktop endpoint is introduced.

The candidate's ten real HTTP fixture cases passed but review found caller-dispatcher
parsing and account-body bounds gaps. The corrected twelve cases also verify denied
reads, oversized account responses and ownership of immutable page data. Cross-thread
fixture changes use safe publication; their HTTP pools/dispatchers actually retire.
Full corrected regression passes all614 cases across110 suites, zero lint issues and
both APK builds. Complete source/report/APK hashes and retained logs are recorded in
preflight-060/native-workspace-objects-validation.json. The only upstream diagnostic
remains the approved exact AGP setVisible call; no old assertions or warning policy changed.
Legacy Room12, Processor13 and private native client schema3 are unchanged.

This boundary supplies transport evidence and is not yet wired into runtime catalog
activation. The next consumer owner must durably fence each incomplete inventory and
retain candidates before downloading, reconcile changes, validate ancestry/coverage/full
facts and atomically activate a staged generation. Live Google/native consent, API latency,
S23 acceptance and the full joint release remain unverified. Per-object principal checks
still need measured API-latency acceptance; correctness is not a performance claim.

## Native controller authority history

The native Drive-only client now has a private signed-controller journal alongside its
approved connection and credential selector. Its serialized state owner has a preserving
schema 1/2-to-3 migration; legacy Room12 and Processor13 remain unchanged. Migration
requires the existing storage safety margin before schema writes. Existing pins/accounts
survive migration, and missing selectors require fresh account approval before reception.

Only the independently approved controller key/library/epoch can authorize publisher and
source policies. Actual signed bytes and their digest are immutable, with one global
sequence across both roles. Exact replay is idempotent; older arrivals cannot rewind a
selection, including a publisher revocation. Current selections must name each role's
highest accepted sequence. Missing anchors/evidence, changed digests, invalid saved
signatures and rewound pointers stop the client. Neither parsing nor this journal grants
device signing authority, original-file permission or catalog activation.

Source history accepts either arrival order: neighboring signed revisions must be
nondecreasing, and equal revisions require exactly the same canonical payload. A changed
source binding cannot reuse a revision. Conflicts, failed SQLite transactions, cancellation
and insufficient space preserve accepted state. History admits at most 4096 bounded signed
documents, with no truncation or increased protocol limits. Retention/ack compaction and
complete/current Drive observations still need implementation; this local journal alone
cannot establish current cloud authority or activate a received catalog.

The first candidate passes 25 focused SQLite cases, including eleven new signed-history
cases. Corrected final validation passes all602 JVM/Robolectric cases across109 suites,
zero lint issues and both app/native-test APK builds. Thirteen journal cases include fresh-
account and cancellation regressions. Two additional actual-emulator journal/migration
cases compile into the APK; no local AVD is available, so their execution remains required
in exact-source CI. Eight screenshot utility tests also pass. Complete hashes, archived
reports/logs, source/APK identities and explicit limits are retained in
preflight-060/native-controller-journal-validation.json. The only diagnostic is the already
approved exact AGP setVisible deprecation. No old assertion or warning policy changed.

Exact parent3315b6d23e359bdd0d7c39a4da1c5ee784d810a1 now passes Android40130917688,
all three Native40130917780 API29/35/37 jobs (164 cases each), and all12 cross-platform
Protocol40130917706 jobs. Native setup/UI cases actually ran. Its nine new screenshots were
written app-private and escaped the existing artifact collector, so no visual acceptance
is claimed from those missing images. The corrected helper exports the same captured
frame through the existing bounded MediaStore writer/readback; all nine images are now
required by the native evidence map. Exact-source CI must prove that corrected collection.

## Native shared-library setup and foreground consent ownership

Settings now includes a native Shared library destination without changing existing
navigation IDs. Its account picker and Google consent flow use Drive directly; they do
not enter legacy folder browsing or schedule legacy imports. The screen displays actual
account/folder review and the controller fingerprint, then requires an explicit source
acknowledgment and approval. Process recreation restores bounded draft/pending context,
never a token, pending intent or human approval. Current accepted context stays visible
during loading and operational failure; saved-state reads can retry without replacing trust.

Private connection schema 2 adds a credential selector through a preserving 1-to-2
migration. It stores the actual Google permission ID/email atomically with approved trust;
the email only selects Google authorization and grants no authority. Actual ID and folder
checks still run before every approval. Changed selectors need a fresh reviewed approval.
Low-storage and failed selector updates preserve the previous pin/account. Corruption
stops this client; unexpected internal errors propagate. Legacy Room12 is unchanged.

Each native operation owns its temporary SQLite handle and joins close in finally.
Google SDK tasks retire before cancellation; access grants have redacted string output.
Command ownership transfers before activation, and successor commands join predecessors,
so an earlier finally cannot clear a newer busy operation. A foreground lifecycle owner
registers the consent launcher, obtains its exact identity before activation, detaches on
stop/destroy/close and fences replacement hosts. Pending consent waits in memory for a
started screen. A retained ViewModel never calls an obsolete composition's launcher.

Review found real rotation/foreground and reentrant-close defects despite passing
candidate tests. Full584 pre-host results remain explicitly candidate evidence. Twenty-
eight host cases passed before transfer-order review; a deliberate late-transfer control
then failed the new actual closed-host assertion, expected one callback but receiving two.
Restoring registration/ownership/activation ordering passes all29 focused cases. No old
assertion, stop budget, warning severity or exception policy was weakened.

The UI uses merged checkbox semantics, 48dp targets and the existing progress animation
inside a stable reserved slot. Three additional emulator cases cover eight theme/RTL/font
presentations, retained-context geometry and actual Settings/ViewModel/draft rotation.
They are not locally executed: no local AVD is available. The corrected full run passes
all589 JVM/Robolectric cases across108 suites, zero lint findings and app/native-test APK
builds. The sole upstream diagnostic is the already approved exact AGP setVisible call.
Room12 bytes are unchanged; source/resource UTF-8 and all three native test classes in the
APK are independently checked. Complete logs/counters/source/report/APK hashes are retained
in preflight-060/native-library-ui-validation.json. Fresh exact-source native CI, live
Google/native consent, independent catalog adoption and final S23/installer/release
acceptance remain required.

A default Windows CP1252 read temporarily damaged two unrelated MainActivity glyphs and
new resource punctuation. MainActivity was restored from HEAD's strict UTF-8 with only
the intended destination change; new resource arrows/dash were restored. The validation
record independently compares both. Subsequent file operations use explicit UTF-8.

The exact parent55a04545155874c2a3fc6c544c7546db7ff76ed4 passes Desktop40125462374
on four platforms, Android40125462365, all three API29/35/37 jobs in device40125462290,
and all12 Protocol40125462307 regression/reference/scale jobs. Archived results match its
SHA. Those results include the prior two real pin SQLite cases, not these new UI cases.
This remains unfinished joint 0.6 work, not a completed native/desktop product.


## Drive-only connection export and Android public-pin approval

Both clients consume the same bounded public connection code: the library, approved
controller/epoch/P-256 public key and opaque Drive account/workspace identities. Three
valid and 52 invalid independent wire fixtures exercise canonical bytes, hashes, native
key validation and bounds. Parsing carries no authority, Google token, endpoint, device
grant or original-write permission.

An authenticated desktop host-owner browser can display/select the code from the exact
approved local pin. Anonymous and LAN sessions cannot export it. Exports never approve,
publish or enroll anything. Browser reads cancel on departure, reject malformed response
fields and show the controller fingerprint separately from publication progress.

Android now has a separate serialized public-pin owner under noBackupFilesDir. Its
private SQLite schema starts at 1; legacy Room schema 12 is unchanged. Exact approval
and its hash marker commit atomically. Different accepted roots/epochs/workspaces cannot
be replaced. Missing accepted pins and corrupt markers fail fast. Existing-pin reads
remain available under low storage. Actual transactions arbitrate competing owners;
close joins I/O even from a canceled caller before fencing later access.

The read-only DriveGateway adapter observes actual permission ID and exact folder
metadata. Review checks the account before/after that read; approval repeats those reads
and requires the exact human-approved document hash and unchanged folder revision. It
creates no phone-to-desktop connection, device signing grant or catalog activation.
Android UI wiring and independent published-catalog adoption remain unfinished.

Seven strict .NET Release builds have zero findings. All 855 full cases pass on matching
immutable application components: Core349, AppServices322, Web27, Protocol83, Google65
and LAN9. Complete logs/TRX/component hashes are retained in
preflight-060/library-connection-dotnet-validation.json. The browser build passes strict
TypeScript/esbuild and seven API cases. Ten new Android native-SQLite/actual REST owner
cases pass, including rollback, competing owners, low storage, cancellation, canceled
cleanup, corruption fencing, exact recreation and GET-only account/folder requests.
The final expanded Android run passes all 570 tests across 107 suites, with zero lint
findings, debug app and native-test APK builds. Its only upstream diagnostic is the
already approved exact AGP setVisible deprecation. Room12 bytes match the preceding
revision exactly. Complete counts, source/APK/report hashes and limits are recorded in
preflight-060/library-connection-android-validation.json. Two new actual emulator cases
are compiled into the test APK but have not run yet. Fresh exact-source CI and final
acceptance remain required; no release readiness is inferred.

Failed new setup evidence is retained: an unsupported generic argument to Robolectric's
getApplication prevented the first compile. The corrected run exposed a test-only
260-character Windows native SQLite path before its first open. Shortening the new
method name keeps its temporary path below that limit; native SQLite and every assertion
remain active. Cleanup review also added the canceled-caller regression before accepting
the owner. One reused Protocol TRX filename generated an overwrite diagnostic and replaced
the earlier Debug report. The unchanged frozen Release suite was rerun with a fresh unique
name; the diagnostic is retained and no warning was suppressed. Two pre-existing ledger
Windows-1252 dash bytes were converted to the same characters in UTF-8.

Full connection UI, independent catalog adoption, operation grants, processing/plugins,
complete clients, installers and the remaining joint release work are still unfinished.


## Native worker exit and startup-recovery ownership

A deterministic real-child test now holds event dispatch beyond the old ten-second
join deadline: the original event-based waiter stays pending while the separate real
native wait and its actual thread join complete. The test retains and joins the original
waiter after releasing its barrier. This reproduces the observable failure class; it
does not establish the historical host's exact delay trigger.

FramedWorkerProcess now creates no event-based exit waiter. A dedicated owned thread
per direct worker performs the actual blocking native wait. Its signal alone cannot
permit retirement: supervision, bounded stop, recovery and resource release require
the real thread join plus every existing pipe/write/monitor join. The existing five-
second grace and ten-second post-kill budgets remain unchanged.

Framed ownership transfers before thread startup. If no native thread starts, cleanup
must perform a real bounded process wait itself; a timed-out cleanup cannot complete
the pending signal or permit a later thread start. Failure retains the existing worker
owner. Diagnostics distinguish started/joined/native-retired state. No global pool,
heap, native-handle cache or new original-mutation interface is introduced.

All twenty focused actual-worker/supervisor/installed-worker cases pass, including four
new native-wait cases, held-event timeout reproduction, forced stop, startup cleanup,
input leases and first-failure preservation. Strict focused builds have zero findings.
The first new harness attempt correctly rejected the not-yet-allowlisted exact scenario;
that failed setup log is retained.

Review found and corrected two additional startup/recovery edges: no-thread recovery
must return an asynchronous pending task so the existing supervisor timeout remains
effective, and disposal must branch on actual retirement rather than a successful
native signal alone. Two real-child cases now prove bounded timeout preserves the live
child/lease and that the same no-thread recovery task returns promptly and joins only
after actual exit. All 22 affected cases pass with the corrected source.

The earlier candidate passed 848 full cases, but those results are retained explicitly
as candidate evidence, not validation of the corrected source. The corrected immutable
run passes all 850 cases: 348 Core, 322 AppServices, 26 Web, 80 Protocol, 65 Google Drive
and nine LAN. Seven strict Release builds have zero warnings/errors, complete logs and
TRX counters are clean, and application component hashes match every test/Desktop output.
Evidence is retained in preflight-060/native-exit-owner-corrected-validation.json.

Exact 29312f971fc7e0bb5ae6ebc47347153cb2b0be59 passes Desktop40121750611 on
all four platforms, Android40121750563, device40121750631 on APIs29/35/37 and all
12 Protocol40121750582 regression/reference/scale jobs. Archived results independently
match that SHA and every actual job succeeded. New connection changes still need fresh
exact-source CI and final acceptance. The historical host trigger remains unidentified;
this change removes its event-owned wait mechanism
and tests that failure class without claiming a diagnosis of the old host. No release
readiness or physical-device result is inferred from these local tests.

## Immutable cross-generation dependency references

The current owner can select a byte-identical completed dependency only under its own
actual current scan. A separate immutable local reference journal binds the receiving
generation's deterministic dependency ID to the exact original outbox/file, role,
encoding, size, hash and fully validated completed provenance. Selection commits before
the actual joined read; failed reads retain that choice. Existing pending upload IDs
always win, and an upload cannot later claim a selected reference ID.

Prepared-graph and native plan checkpoints accept an earlier-generation dependency only
through that exact bound record. Full current source, signed history, graph and final
completion checks remain mandatory. Completion checks current observations for all
selected references. No old scope is reopened for writing, no payload is duplicated and
no permission to collect metadata or mutate originals is introduced. Complete historical
native provenance is verified before selection, then its exact immutable records are
fingerprinted to avoid recursive generation walks. Private schema 13 is unchanged.

The initial new full-next-generation test correctly failed the old same-generation
prepared gate. That failed development result remains retained. With the explicit
journal and gates in place, all five initial actual-owner cases pass, including full
second-generation signed completion and retirement. Expanded validation passes actual
third-generation completion, failed-read/recreated-owner recovery, low-storage and atomic
checkpoint rollback, damaged evidence rejection and missing-original HTTP 404 without
substitution. Two new fixture mistakes (invalid SQL digest length and dictionary lookup
instead of the actual transport's HTTP error) remain recorded as failed development runs;
their setup was corrected without weakening any pre-existing assertion.

Seven strict frozen Release builds have zero warnings/errors. All six full suites pass
on the same immutable application components: 342 Core, 321 AppServices, 26 Web, 80
Protocol, 65 Google Drive and nine LAN cases (843 total). Complete logs, TRX counters and
matching hashes are recorded in preflight-060/dependency-reference-validation.json.

An additional mixed-generation case passes full publication of new facts while reusing
the exact two unchanged compressed partitions. All thirteen new reference cases pass
again in that test assembly (844 unique local passing cases in total, not 856).
Its eighth strict build preserves every application DLL byte-for-byte; no pre-existing
test assertion changes. The supplemental evidence records both test assemblies, complete
logs, zero bad outcome counters and application component continuity.

CI puts only the thirteen newly added cases in a separate mandatory four-platform
matrix with strict reports/counters. The existing regression matrix and its 30-minute
budget are preserved; its preceding Windows run took 28m13s. YAML and the exact new CI
report parser pass locally against the actual thirteen-case TRX. Fresh exact-source
CI, cross-generation large-library measurements and release acceptance remain required.
This is unfinished work, not release acceptance. The historical worker join blocker,
large-library costs, retention and the remaining 0.6 feature backlog remain unresolved.

The preceding exact revision 93a4b621 passed Desktop40106241655, Android40106241654,
device40106241664 on APIs29/35/37, and Protocol40106241772 with all four regression and
all four scale jobs. Archived reports independently match that SHA. Synthetic preparation
on those runners took about 161ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“421 seconds for 10k and 171ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“380 seconds for 100k; this
does not replace current-source validation or live-network/UI acceptance.

## Measured large generation preparation and immutable public keys

Actual 10k/100k fixtures now run real retained SQLite capture, compressed dependencies,
the concrete current/private upload owner and full prepared-graph verification through
a paged synthetic Drive adapter. A new photo arriving during upload remains visible
in the current gallery while the frozen generation retains exactly its captured photos.
Every native paging, import, graph and preservation assertion passes.

Initial preparation measured 1,316,897ms/1,242,460ms. Profiling found repeated native
public-key import/export/shape validation. An internal immutable canonical-key value
now owns only validated public bytes and their computed key ID. Its sole factory keeps
the native P-256, complete-consumption and canonical-export checks, exports once and
closes every failed native owner. An approved actor retains that value; every received
signature still uses a fresh owned native verifier and real VerifyData. Controller and
publisher key IDs remain distinct. No signature, authority, scope, epoch, capability or
high-water conclusion is cached, and no native key pool/global trust cache is added.

The repeated run measured 1,063,887ms/996,760ms (about 19.2%/19.8% lower) and native gallery
p95 205ms/188ms. Both fixtures generated 261 metadata objects, made 785 typed metadata
reads and six inventory pages. Retained snapshots are 6.4MB/63.8MB. Cumulative shared-process
allocations remain about 191GB/205GB: this is transient work, not retained memory, and
remains a performance concern. Shared-host timings are observations rather than a
controlled causal speed claim; baseline also had two bounded diagnostic samples.
Neither run establishes live Drive, native UI, full processor or final-commit acceptance.

Final immutable validation passes 342 Core, 309 existing AppServices, 26 Web, 80 Protocol
and both new scale cases. Five locked strict builds have zero warnings/errors; complete
logs/counters and matching component hashes are retained in preflight-060/
canonical-key-validation.json with the earlier scale baseline. New tests preserve
caller-array isolation, concurrent independent real verification, tampered/wrong-key/
keyId/epoch/capability rejection, malformed/trailing/private/RSA/other-curve/size bounds.
Every pre-existing assertion remains active.

CI runs the two new measurements in a mandatory four-platform scale matrix with strict
diagnostics, exact fixture-size/outcome checks, source/hash reports and archived evidence.
The existing regression matrix retains its tests and budget. Each new fixture's original
30-minute cancellation remains unchanged. This is not a timeout waiver or a warning,
history or inventory-cap relaxation. Remaining CPU/allocation costs, reuse/retention and
the distinct unresolved historical worker join still block declaring the release ready.

## Private incremental dependency reads

The concrete generation owner now derives dependency inventories from its own actual
completed scan and a fully drained Drive change feed. Original capture identities and
private one-upload admissions remain fixed. Ordinary public, startup, renewed-access,
prepared-commit and completion reconciliation still use full actual inventories.

Baseline startup atomically checks the exact completed scan ID, checkpoint, native
signing/observation inventories and current original generation. An identical later
checkpoint cannot substitute for that owned attempt. Current account, native keys,
controller/source policy, complete outbox and root access are checked again. The one
expected upload must appear in actual changes if absent from the baseline; missing
notifications and unexpected changes require a new full read. Raw removal/ancestor
observations stay durable before fallback and cannot be erased by coalesced responses.

Unchanged local metadata bytes come from one integrity-checked native outbox record at
a time, bound to the actual baseline version/hash/type/size and fresh changes. The new
expected object and all nonlocal metadata use actual transport. No original/media path
or aggregate payload cache is involved. Full signature, ancestry, coverage and facts
verification still runs; cancellation/Exit joins the real account, I/O and native work.

Four focused native cases and nineteen focused incremental/actual-owner cases pass,
including original-ID recovery after allocation without creation, real frozen snapshot
preparation with an import arriving during upload, full graph publication/completion,
private unlink, next-generation catch-up, superseded identical checkpoints, unrelated
native work, nonlocal outages, ancestor/removal recovery and joined held-feed Exit.
The preceding 184 existing account/catalog regressions also pass. Final immutable
validation passes all 342 Core, 309 AppServices, 26 Web and 77 Protocol cases.
Five locked strict builds have zero warnings/errors; complete final logs are clean
and component hashes match every test/Desktop output. Evidence is retained in
preflight-060/incremental-owner-validation.json. The shared-host AppServices run took
26m38s; a ten-second managed stack sample showed active native/cryptographic work.
That diagnostic observation is not a performance benchmark or final release acceptance.

A sixteen-dependency structural fixture uses eighteen metadata downloads and one full
inventory after its basis, while public reconciliation rereads all eighteen documents.
This is not a live-Drive or 10k/100k benchmark. Native inventory hashing/semantic walks,
large-fixture measurements, cross-generation reuse and bounded history retention remain
unfinished. No inventory cap, assertion or warning policy was relaxed. Schema13/Room12
are unchanged. The distinct historical worker join failure remains a release blocker.

## Complete shell screenshot publication

API37 CI40067938335 passed all 159 native tests but correctly failed screenshot validation.
The shell image saved on the device and both independent transfers have the same 16,469
bytes/hash, two complete IDAT chunks and no PNG ending. This is a bad saved artifact,
not repaired evidence or a passing native bundle. The exact encoder/storage cause is
unproven. Other matrix jobs canceled by fail-fast are not passes.

ShellNavigationDeviceTest now uses the existing CompletedScreenshotEvidence owner:
one bounded captured frame is encoded and checked before publishing; pending MediaStore
output is synced, closed and read back byte-for-byte before becoming visible. Names,
capture points and every UI assertion stay unchanged. There is no replacement frame,
assertion retry, collector relaxation or new warning exception. The failed bytes remain
preserved in preflight-060/ci-8588701-api37-failed-evidence with their independent review.

Strict application/test APK assembly and lint pass with zero findings under the existing
exact AGP gate. The application APK is byte-identical to the Gson-validated build; only
the screenshot test changes. Exact5844d09 passes every required Desktop/Protocol
job across four platforms, Android checks and native API29/35/37 jobs. Their actual
results are archived in preflight-060/ci-5844d09-*-status.json. New final-revision
checks remain required; S23 stays reserved for final acceptance. Evidence:
preflight-060/shell-completed-evidence-validation.json.

## Final Drive changes remain durable before rejection

Reviewing incremental-read safety found a gap in the current full read: inherited
folder permissions can change without child events, and a final rejected change page
was not retained as complete history before failure. General metadata final fences now
close conservatively on any folder change in their captured corpus and persist every
relevant record in the actual rejected page before returning the existing operational
failure. A later coalesced response cannot erase an observed removal.

The native journal atomically retains history, supported candidates and an incomplete
replay fence without advancing the original replay cursor. Even non-recovery changes
require a new actual full read; removals remain sticky across restart and empty later
responses. Failed writes and low storage roll back together. Actual callback work is
joined before cancellation is acknowledged. Processor schema13 and Room12 are unchanged.

Three new native cases and seventeen focused actual-source/inventory cases pass,
including original inventory/history assertions, concrete current-owner ancestor and
removal rejection, full refetch after an ancestor event, native restart, atomic failure,
and joined uncooperative evidence persistence. Full immutable validation passes all
338 Core, 290 AppServices, 26 Web and 77 Protocol cases; five locked strict builds have
zero warnings/errors, complete final logs are clean and component binaries match every
output. Evidence is retained in preflight-060/rejected-final-validation.json.
This is a correctness prerequisite; incremental catalog reads are not implemented yet.
Permission-inheritance behavior was checked against [Google's change documentation](https://developers.google.com/workspace/drive/api/guides/about-changes).

## Explicit human reconnect scheduling and late-access rejection

Successful host-browser Connect/Resume now synchronously schedules engine-owned retry
before browser cancellation or credential cleanup can lose that wake-up. Only the
workspace's account and recoverable access attention qualify. Reconnect cannot infer
workspace approval, dismiss sharing/history review or restart an internal invariant.
Every admitted retry still executes actual current reconciliation and original-work
recovery. Ordinary source notifications continue to preserve attention.

Seven focused actual cases pass, including the existing no-inferred-approval case,
successful OAuth recovery, foreign connection filtering, sharing/invariant attention,
and another real reconnect during allocation, create and readback. Stale completions
are rejected; original allocated target, scope, bytes and signature remain available.
A successful create receipt from before a later access loss remains historical, while
head completion is blocked until fresh admission. Renewal retries reuse the target.

The new routing fixture initially expected a single generation after reconnect.
Actual source coverage correctly changes its account revision, so the test now checks
the original sequence-one frozen generation and the sequence-two coverage catch-up,
then proves repeated reconnect notifications allocate no third generation. Its failed
development log remains failed; no production fence or existing assertion was weakened.
Complete frozen validation passes all 335 Core, 287 AppServices, 26 Web and 77 Protocol
cases. All five locked strict builds have zero warnings/errors, complete final logs are
clean and every output has matching component binaries. Evidence is retained in
preflight-060/generation-reconnect-routing-validation.json. This does not resolve the
historical worker blocker or establish live Google/physical acceptance.

## Release blocker: intermittent worker exit-task join

One complete native run failed the actual stubborn-child disposal case: the OS reported
exit while the original asynchronous exit, diagnostic reader and supervision tasks still
had not joined within the existing ten-second post-kill deadline. Ownership was retained;
the test's recovery owner subsequently joined cleanup. The cause remains unidentified.
No deadline, ownership assertion or warning gate was relaxed, and no exception is approved.

Observation-only instrumentation now records the first native signal, actual Exited-event
timing, diagnostic read/write/flush/disposal progress and thread-pool counts on failure.
These observations cannot complete an exit task or release a resource. The instrumented
full 328-case Core run, ten concurrent lifecycle cases and forty further cases from a
separate immutable output copy pass. This does not establish a fix. Publication remains
blocked while independent feature work continues.

## Renewed access preserves original captured publication

The actual reconciler now consumes renewed credentials through an opaque run-owned
LOCAL context, while every upload still requires its independent private current
cloud admission. Default original-scope APIs remain strict. The context borrows one
actual native database owner, fences current semantic policy, controller/publisher,
credential revision, original immutable attempt and active generation, and retires
only after joined operations. No original scope, snapshot, commit ID, target file,
payload, signature or previously recorded verification receipt is rewritten.

A lost outbox checkpoint after native sealing has a narrow private read-only recovery
path for that exact captured intent. Ordinary reads and write grants still reject a
missing signing history. A different pending sealed intent cannot be grandfathered.
After renewal, the exact original outbox is reconstructed before a new ordinary full
current read and private one-object admission. Local capture callbacks never reenter
the account/network owner. Historical journal records alone cannot grant a write.

Six actual integration cases pass for interrupted dependencies, sealed-without-outbox
and uncertain commit replies, unrelated signing history, recreated engine catch-up
through later imports, and Exit joining uncooperative actual I/O before context/key
retirement while rejecting late receipts. Seven native local-context cases and the
full 335-case Core suite pass. Complete immutable validation also passes all 281
AppServices, 26 Web and 77 Protocol cases. The five locked strict builds have zero
warnings/errors, complete final logs are clean and component binaries match every
output. Evidence is retained in preflight-060/generation-local-access-validation.json.
This is not independent-client activation or release acceptance; the worker blocker
above remains unresolved.

The preceding 1da6539 revision passed all four desktop and protocol platform jobs and
all Android device jobs on APIs 29/35/37. Its Android build correctly failed the strict
dependency gate after Gson 2.15.0 was released on October 24. The exact stable pin is
updated. All 557 Android unit tests pass, lint has zero findings and the debug APK builds
through the existing strict gate, accepting only the already approved exact AGP warning.
The [official release](https://github.com/google/gson/releases/tag/gson-parent-2.15.0)
was checked; no check was weakened. This is not new device or signing acceptance.

## Separate renewed-access journal

Processor schema 13 adds a separate immutable access-attempt journal with original
capture/snapshot/commit identities, actual renewed account revision, pinned native
controller/publisher identities, completed observation and both inventory digests.
It never rewrites the original scope or remints pending work. Native transactions fence
current semantic policy, completed scans and complete inventories before recording.
Retries retain the first attempt's provenance; later reconnects use separate records.
Low storage and failed checkpoints retain original claims. Corrupt records are preserved
and rejected, and bounded history fails explicitly rather than truncating earlier attempts.

The concrete reconciler now owns actual current access/history reads before journaling
an attempt. It checks original parent or exact own visible commit, original allocated
membership, immutable pending work, native keys, current policy and both inventories.
Its returned record is historical evidence, never a transferable write grant. Existing
strict original-scope upload/signing/completion fences remain the default; the explicit
local-context integration described above supplies renewed credentials separately.
Successful human reconnect now has the explicit guarded scheduling described above. All seven native journal/migration
cases and six actual reconnect cases pass, including failed native checkpoints, low
storage, bounded history, restarts, original allocation membership, foreign heads and
joined uncooperative reads. Complete instrumented immutable suites pass: 328 Core,
275 AppServices, 26 Web and 77 Protocol cases; strict test/Desktop builds have zero
warnings/errors and component hashes match every output. Earlier old-schema fixture
omissions and one stale latest-version assertion were corrected while preserving their
original preservation/rollback assertions. Their failed logs remain failed. The distinct
historical worker timeout above remains an unresolved release blocker.

## Automatic engine generation catch-up

Production DesktopWebRuntime now composes CatalogGenerationRuntime into the engine-owned
publication runtime. Original active captures resume before any new input selection.
Bounded local input hints compare indexed discovery/configuration revisions, source
coverage/progress and semantic authority without enumerating photo facts or subscribing
to the whole library. Published hints reconstruct the exact retained signed coverage
root/pages from original outbox bytes after private snapshot deletion. Hints only schedule:
even unchanged-ready admission performs actual cloud/history reconciliation again.

Changed observations run real preparation/publication/completion/retirement, then compare
again so imports arriving during an upload receive their next generation. Committed source
steps emit owned notifications, including coverage-only completion/failure without a changed
photo revision. The publication runtime coalesces request versions and joins predecessor
tasks, preserving late wake-ups and explicit attention retries. Browser cancellation cannot
own admitted work. Generation borrowers retire before their current reconciler and keys.
No notification silently dismisses approval or recovery attention.

Six focused integration cases pass for unchanged suppression with an actual remote outage,
imports during a commit, recreated-owner frozen recovery, browser-independent engine setup,
real coverage-only source progress, and joined engine shutdown. Existing setup/history/retry
regressions also pass. One new fixture initially omitted its empty source-page response;
the corrected typed fixture passes without changing production checks or old assertions.
All 321 Core, 269 AppServices, 26 Web and 77 Protocol cases pass on frozen binaries.
All five strict test/Desktop Release builds have zero warnings/errors; complete final
logs are clean and component hashes match every output. Exact parent 0ac9ca3 also passed
every required CI job across four desktop platforms and Android APIs 29/35/37.
Per-object full scans still need scale optimization; renewed-access recovery and the
remaining release features are not claimed complete.

## Verified publisher completion and private snapshot retirement

Processor schema 12 transactionally rebuilds both capture and its FK publication child,
preserving original scopes/IDs/descriptors/plan hashes. Actual native migration tests
verify success and full rollback after a deliberate final-table conflict. New immutable
verification receipts bind the original native-signed commit, outbox identity, scope,
captured count and actual completed observation. Verification and publisher-head completion
are separately fenced transactions; a failed head write retains commit-verified and the
same immutable receipt. The exact head pointer and complete phase commit atomically.
Historical receipts never replace fresh private actual graph/ancestry/current admission.

Completion keeps the exclusive snapshot claim through complete and snapshot-retiring.
Only the real private snapshot-store owner can return an opaque retirement token after
checking the original generated regular file/descriptor/hash and joining native SQLite
ownership before unlink. The token retains a native-use/directory lease through the
retirement checkpoint. No media path is accepted. A missing file is recoverable only in
persisted snapshot-retiring, covering a lost unlink checkpoint. Actual confirmed retirement
then releases admission for the next generation. Corrupt replacements remain untouched;
an open native reader prevents retirement. Retired history/receipt corruption cannot hide
an invalid claim from a new admission. Completed private cleanup needs no Google access.

CatalogGenerationCompletionService now joins exact publication, private actual completion
and native-file retirement, preserving original phase/IDs across failures and cancellation.
Five actual lifecycle cases pass for empty/nonempty successive generations, disconnected
cleanup/corrupt-file protection, lost native checkpoint/low storage and borrowed-reader
ownership. Two more pass for an actual failed head transaction resumed with new owners
after later discovery, and Exit joining uncooperative account I/O without retiring borrowed
owners. Twelve focused native migration/checkpoint cases pass, including old migrations,
low storage, failed head writes, incomplete scans, corrupt receipts and invalid later phases.
Earlier development setup errors remain recorded, including a missing captured revision
argument in the new disconnect test; existing assertions and warning gates are intact.

All 321 Core, 263 AppServices, 26 Web and 77 Protocol cases pass on frozen binaries.
All five strict test/Desktop builds have zero warnings/errors, complete final logs are
clean and the actual component binaries match across every output. This publisher
completion is not independent-client decoded catalog activation. Automatic engine scheduling,
measured incremental admission/retention, renewed-access recovery, independent enrollment,
processing plugins, full Android/web features, installers and exact release remain pending.

## Captured commit publication and actual current readback

The concrete reconciler now publishes only the durable capture's original commit.
An original sealed signature whose outbox checkpoint was lost can prepare only those
same exact envelope bytes under the fixed CommitIntentId metadata-object identity.
An original unsealed reservation first undergoes actual full current history and prepared
graph reconciliation, then resumes the same native signing intent. New signing/outbox
transitions are followed by another actual read before a private one-commit admission.
Account-lease validation remains local and never reenters network/account ownership.

The write grant uses the existing exact one-row monotonic inventory guard, binds the
sealed generation/plan/body/native context, and rejects unrelated pending signing or
publication work. Its current head must be the captured predecessor or the exact own next
envelope after an uncertain reply. Another head cannot replace the parent or burn a new
sequence. Original allocated-file membership remains required even when identical copies
change which envelope is selected by listing order; missing originals cannot be retargeted.
Current exact readback precedes grant retirement, then full signed ancestry/coverage/facts
and final access/change/local/native/inventory checks precede returning the original
reference. Failures close the private proof and preserve original journaled work.

Nine focused integration cases pass, covering first and later generation ancestry, exact
retry, sealed signature without outbox checkpoint, unsealed original recovery, lost actual
create reply, identical visible copies, missing original allocation, foreign head rejection
and joined Exit while a real readback adapter ignores cancellation. All 315 Core, 256
AppServices, 26 Web and 77 Protocol cases pass on frozen binaries. Five strict test/Desktop
builds have zero warnings/errors, complete final logs are clean and all outputs share
identical component binaries. No Core verified/complete/retirement phases,
head activation, automatic engine scheduling or renewed-credential attempt is claimed yet.
This transport integration is not a completed release workflow and does not mutate media.

## Integrated retained generation preparation

CatalogGenerationPreparationService now owns a joined serial run from actual current
reconciliation through durable capture, the real private frozen SQLite reader, dependency
building/uploads and full actual graph readback. The concrete reconciler chooses or
resumes the journal's original generation/snapshot/signing identities under its private
current proof. Local/native permission to copy a private snapshot is kept separate from
the private per-upload Drive grant. Neither caller read records nor local receipts supply
publication authority. Later discovery cannot replace a retained snapshot on retry.

An explicit outgoing dependency traversal validates the same bounded schemas, complete
coverage and facts tree before signing, without fabricating a verified incoming document.
Inside the actual full workspace scan, every prepared reference must also belong to this
generation's exact verified outbox role/MIME/bytes/hash and actual observed membership.
The source-policy digest, coverage discovery/configuration and complete asset count bind
the captured basis. Existing final changes/access/local/native/signing/outbox fences run
after the full graph read. Only that private completed preparation proof may seal the
immutable dependency plan. A retained ready plan is fully reread on retry.

Six actual integration cases pass: empty/nonempty capture and recreated-owner reuse,
lost compressed-object completion followed by new discovery and exact frozen retry,
missing dependency rejection, a real changed-version event during the final graph read
with same-capture retry, and joined Exit with an exclusive retained SQLite handle check.
All 315 Core, 247 AppServices, 26 Web and 77 Protocol cases pass on frozen binaries.
All five strict test/Desktop builds have zero warnings or errors; complete final logs
are clean and every suite output shares the same actual component binaries. Development
failures remain recorded: initial
JSON MIME comparison did not normalize the established null-as-JSON representation;
a new sequence fixture accidentally supplied a valid first sequence; and a new final
change fixture first used the wrong constructor, then an unknown missing identity that
correctly stayed blocked. The final fixture changes a real existing object's Drive
version and leaves its immutable bytes unchanged. No existing assertion or gate changed.

This owner stops at dependencies-ready. Current commit signing/upload/readback/completion,
completed snapshot retirement, renewed-access recovery and engine scheduling remain
unfinished; it is not wired as a completed publication workflow or claimed as 0.6 release.

## Private current admission for one generation dependency

The concrete reconciler now keeps its completed pin/native/signing/outbox/observation
evidence private. Each dependency performs a fresh actual Drive reconciliation, obtains
one internal admission while holding the owner's serial lease, and retires that admission
after the joined create/readback. Validation inside the protected account lease is local;
it cannot reenter account or network operations. Public read records remain evidence only.

The grant binds one exact durable generation basis, role, MIME type, payload hash/size and
derived outbox identity. All unrelated outbox and signing records must remain unchanged.
Only that expected row may appear and monotonically acquire its allocated ID and verified
receipt. Once observed, removal, rollback or retargeting closes the grant. The original
parent must still be an actual observed member even when identical envelope copies change
the selected listing identity. Caller payload bytes are owned before the first await.

After retiring the write grant, another full actual reconciliation and local/native fence
must observe the exact dependency before it is returned. Lost create replies retain and
retry the same allocated identity. Exit joins uncooperative requests and rejects late
results before borrowed accounts, native keys or state can retire. Unsupported roles,
noncanonical JSON, stale sources, unrelated pending work and missing durable bases fail
closed. No originals, signed commits or activation pointers can use this dependency path.

Ten focused integration cases pass, including source changes during allocation, an actual
new signed head during create, gzip MIME retention, identical-head-copy parent continuity,
lost replies, unrelated outbox mutations, caller-byte replacement and joined Exit. All
315 Core, 241 AppServices and 26 Web cases pass; strict Desktop Release has zero warnings
or errors. Complete logs are clean and the suite outputs share the same actual Core and
AppServices binaries. Core was rerun after detecting that assembly version metadata had
changed its binary hash following the prior commit. This is not yet the full
exclusive generation scheduler, commit publication/completion or completed-file retirement.
Full before/after scans per object still require scale measurement and a verified
incremental strategy before release; no performance claim is made.

## Joined worker retirement and native exit verification

A full Core run exposed a diagnostics-file cleanup failure in the existing stubborn-child
test. Joined test ownership subsequently exposed a real termination timeout. A controlled
concurrent replay captured Windows reporting native exit while the async exit task remained
pending, with diagnostics still owned. The exact original cleanup event was masked and
cannot be conclusively attributed from that first run alone; neither failed run is a pass.

Shutdown now retires the resource monitor before native exit queries, checks the actual OS
exit signal within the original five/ten-second deadlines, and still joins the original
async exit owner. Native exit never substitutes for that join. Every confirmed-exit path
joins actual writes, frame/diagnostic pumps and supervision before releasing resources,
including paths following earlier cancellation/stop errors. Unconfirmed exit retains
ownership. Native primary and secondary shutdown failures remain available; structured
timeouts retain nonsecret native/task-state evidence instead of an uninformative deadline.

ProcessorWorkSupervisor now requires RetirementConfirmed, not process exit alone, before
releasing operation input/budget/state leases, including recovery after a bounded join.
The test fixture owns even failed startup attempts, joins retained workers before directory
cleanup, and preserves the actual failure. Existing process/input assertions remain, with
additional actual Completion, exclusive diagnostic-file and retirement checks. A real
before-handshake cancellation case exercises joined shutdown; the stalled-child scenario
exists only in the synthetic fixture, never the product plugin registry.

Five paired lifecycle replays now pass under the same concurrent conditions that reproduced
the pending-exit failure. All 315 Core, 231 AppServices and 26 Web cases pass, plus the strict
Desktop Release build. Complete final logs are clean and all four outputs use the identical
Core binary SHA-256 eeb72ec22e814a5759120bea4433907baba2014b5dadc6071e792e81e7f71043.
The mechanically idle-guarded frozen test build has zero warnings/errors. Earlier full
passes belong to the intermediate correction; their shared Core hash and failed independent
replay remain separately retained as intermediate evidence, not current acceptance.
Build/test overlap caused separate local copy diagnostics; guarded, serialized rebuilds
resolve that scheduling cause, without suppressions or deadline extensions. No historical
warning waiver was extended. Fresh exact-commit cross-platform checks remain required;
no APK, installer, live Drive operation or final acceptance is claimed here.

## Durable generation publication plan and native signing

Processor schema 11 explicitly migrates the capture table, preserving original scopes,
snapshot/commit identities, descriptors and active claims. A separate bounded canonical
publication journal fixes the frozen descriptor, selected parent/sequence, source-policy
digest and human settings before the first dependency write. Its publication-planned phase
detects a missing basis even before any upload exists. Dependency references become one
immutable commit plan only after their exact generation/role/bytes and retained verified
outbox ownership match. The plan checkpoint and dependencies-ready phase commit atomically.

JournaledProtocolSigner.SignCapturedCatalogAsync now reads that durable plan under the
serial state owner, checks current local authority and native identity, and signs only the
fixed commit intent at the captured next sequence. Actual journal signature/body verification
precedes commit-sealed. A lost phase update recovers the same sealed envelope. No caller
body, new discovery snapshot, newer settings or replacement parent may change an existing
intent. Claimed native snapshot files remain readable through the new phases and cannot
be abandoned after dependency preparation or signing.

This is local recovery/signing integration, not a cloud write grant or activated catalog.
The production engine still performs its current read; full exclusive generation scheduling,
private single-upload current admission, actual commit readback/verified/complete phases,
head activation, completed snapshot retirement and renewed-credential access attempts remain
the next owner work. No new network/publication path, media mutation or cleanup permission
was enabled. Android Room 12 and the upgrade signer are unchanged. The full local suites
listed above include schema-11 migration/rollback, actual retained-file lifetime, immutable
plan recovery, low-storage and native signing. Preceding plan commit 3728fa0 independently
passed Protocol40019053591 and Desktop40019053696 on all four operating systems, Android
40019053732 and native40019053893 on APIs29/35/37. These do not substitute for the newer
worker correction's exact-revision CI or the remaining joint release acceptance.

## Engine-owned current catalog reconciliation

The Desktop composition root now supplies a concrete LocalCatalogReconciliationService
to its engine-owned setup runtime. After initial policies exist, startup and explicit retry
perform a fresh complete metadata/history scan rather than treating initial completion as
current authority. Longer change feeds automatically continue their committed 128-page
batches without a browser request or restarting bootstrap. Exit joins both the actual
catalog attempt and its owner before keys, accounts, vault and state retire.

Every attempt starts closed. Actual typed observations are durable before verification;
JSON and gzip encoding comes from captured Drive metadata. Owned envelopes must match
the complete local sealed journal. Unknown/cloud-ahead signing history cannot advance
counters. Missing sealed work without a matching pending outbox record requires recovery;
unsealed intents and captured pending publications remain explicit with their original
identities and scopes. Selected revocations remain durable even when they close the scan.

The owner selects the current owned publisher's signed head independently of listing
order, verifies its complete predecessor chain, and reads the covered index/coverage/facts
graph through observed identities only. Each actual reread is retained and checked before
decoding; foreign/unlisted references and changed bytes fail. Historical coverage must
match an actually observed controller-signed source policy. All graph/access checks precede
the final actual change fence. Local authority, native ownership, signing and outbox
inventories are checked again before accepting the completed read.

The returned record is read evidence, not a transferable publication or activation grant.
No Drive allocation, create, update, delete, original download or original modification is
performed by this owner. Generation publication/activation, renewed-access upload admission,
independent enrollment, retired epochs and the remaining joint product remain unfinished.
The earliest unresolved cursor and sticky recovery evidence are not reset. Processor
schema 10 at this preceding increment; the newer durable-plan increment above explicitly
migrates it to 11. Android Room 12 and the established upgrade signer remain unchanged.

All 228 AppServices cases and all 26 Web cases pass. The strict Desktop Release build has
zero warnings/errors, complete final logs are clean, and all three use the same service
binary SHA-256. The 15 new integration cases include actual startup/retry, 129-page automatic
continuation, native signed heads, compressed facts/source coverage, cloud-ahead history,
missing/pending work, revocation, changed reads, final-fence changes and joined retirement.
Complete ancestry now fails before expensive covered-facts decoding. Existing assertions
remain intact. New fixture mistakes were corrected using the real reservation API and
the configured completion reader, without changing production visibility or relaxing checks.

The preceding 4424637 passed Protocol
40005087999 and Desktop40005087979 on Windows/Linux/macOS ARM and Intel, Android40005088033,
and device40005087969 on API29/35/37. The earlier PNG-evidence failure remains a failed run;
its actual correction passed this later exact-revision native matrix. No warning exception,
assertion, severity or evidence gate was weakened.

## Signed catalog predecessor traversal

CatalogCommitHistoryTraversal now walks the actual immutable parent references to the
first commit, verifying every envelope against one independently enrolled publisher and
its captured library/epoch. Every read checks workspace, bounded length and SHA-256 before
signature/schema admission. Stream sequence gaps, repeated generation identities, cycles,
commit/dependency role aliasing, foreign keys/epochs and oversized histories fail. The
4,096-commit bound does not relax document or protocol array limits.

Traversal retains only bounded identity sets and one envelope at a time. It does not
prefetch while a consumer persists a yielded observation. Missing/changed predecessors
can fail after an earlier yield; only normal full enumeration proves the chain completed.
Uncooperative reads remain joined, and cancellation rejects their late results. This is
a reusable verification component, not a current-policy/high-water reconciliation owner,
covered-dependency validation or activation grant. Those integrations remain unfinished.

All 74 Protocol cases pass, including real ECDSA chain signatures and the new late-failure,
sequence/identity/role, foreign authority, bounded-history and cancellation regressions.
The first development run rejected incorrectly ordered capabilities in the new synthetic
fixture; that fixture was corrected without relaxing any assertion or decoder. The strict
Desktop Release build has zero warnings/errors, and complete final logs are clean.
Fresh exact-revision CI validation remains required.

## Details screenshot publication correction

Exact 3669c8c native CI passed all 159 API37 assertions (151 ordinary, seven device-library,
one revoked-access), but its evidence gate correctly rejected the light/large Details PNG.
API29 and API35 passed. The failed API37 run remains failed: its device, pulled and reread
bytes have the same SHA-256 and 8,265-byte size. The file contains one complete 8,192-byte
IDAT chunk but no PNG ending, so this was already an incomplete saved capture, not a
transport error. Full failed evidence is retained; no recapture or assertion retry is used.

The older Details test now uses the existing NativeScreenshotEvidence owner. It copies
the same captured pixels, bounds and validates PNG encoding before publication, flushes
and syncs the owned descriptor, verifies exact bytes before and after publishing, and
removes rejected entries while retaining cleanup failures. Original layout, accessibility
and close assertions are unchanged. Strict local lint (zero findings) and instrumentation
APK compilation pass with only the existing exact AGP deprecation. All eight PNG evidence
regressions pass. Fresh exact-revision native evidence is still required. No warning
exception or evidence gate is relaxed.

## Retained change-history replay through the protected account owner

Complete metadata scans now replay the retained unresolved Drive interval before document
downloads, using the same joined account owner and state-only callbacks. Every relevant
page, supported candidate and exact continuation commits atomically before the next read
or a recovery error. New metadata requires a fresh inventory; historical versions cannot
rewind current candidates. Unknown removals, access changes and unsupported metadata are
retained as sticky recovery evidence, rather than inferred safe from missing parents.

Replay batches are bounded to 128 pages and 100 events per page. Longer feeds resume at
their last atomically committed cursor; a new batch starts closed. The earliest unresolved
cursor never advances merely because transport, listing or replay succeeds. All retained
identity evidence is bounded to 4,096 entries and hash protected. Missing, corrupted,
foreign or overflowing records fail without truncation. Cancellation joins actual account
work; stale access cannot admit its late end cursor. No original is written.

Scan completion and current admission require completed replay, unchanged history and no
sticky recovery flag. Old optional-field encoding remains readable and private schema 10
is unchanged. Raw replay is not signature, predecessor, covered graph or head authority;
the general generation owner and renewed-access admission remain unfinished. Initial
policy-only bootstrap retains its narrower source mode.

All 298 Core and all 213 AppServices cases pass, including page rollback/low-storage,
restart, superseded callbacks, large-feed continuation, 4,096 identities, maximum opaque
cursors, protected-account integration and disconnect during an uncooperative read.
The strict Desktop Release build has zero warnings/errors. Complete final logs are clean;
both test suites and Desktop use the same Core binary SHA-256. Fresh exact-revision CI
remains required. No live Drive write, new device acceptance or release is claimed.

## People readiness regression found by exact-revision native CI

e8f36ae passed the complete Protocol, Desktop and Android build workflows, but its API37
native invocation failed one existing People diagnostic assertion (150/151 ordinary cases
passed). The retained semantics explicitly reported `Loading people` when the test tried
to scroll to asynchronously computed diagnostics. API35 passed; matrix fail-fast canceled
API29. That run is not accepted as passing, and no warning exception is added.

People review results are now bound to their exact observed input, so a previous/default
result cannot declare replacement data ready. Review loading has real progress and
accessibility state. The existing native test awaits the seeded Room row and the matching
rendered face count/current review before scrolling; all original diagnostic, rescan,
pause and data-preservation assertions remain intact. There are no sleeps, whole-test
retries or relaxed assertions.

Local full unit tests, lint and both debug APK builds pass the strict gate, with only the
already approved exact AGP deprecation. Fresh native checks remain required. This is a
readiness correction in the current Android baseline; the 0.6 thin-client activation and
the complete joint release remain unfinished.

## Complete signing inventory and scan admission

ProtocolSigningJournal now captures all local actors and every reserved/sealed intent
in one transaction, including idle actors and unsealed work. It validates real payloads
and signatures one at a time and retains only bounded immutable descriptions. Framed
hashing supports more than the protocol's 1,000-item array limit without relaxing that
limit. Explicit 64-actor/4,096-intent bounds reject incomplete inventories; orphaned,
corrupt or missing counter history cannot be filtered out. Original IDs, bodies, exact
envelopes, actors, counters and native keys are never replaced or advanced by inspection.
An empty journal remains insufficient proof of native ownership or remote history.

Every new actual workspace scan captures the complete signing digest before network
inspection. Completion checks it after the checkpoint and attempt writes, within the
same transaction, so concurrent reservation/sealing cannot leave a successful checkpoint.
Current completed-scan admission also checks the digest. Historical checkpoint reading
still works after later signing, while a fresh scan must reconcile the changed work.
The optional omitted field preserves older canonical attempt bytes for historical reading;
those attempts cannot complete or grant current admission without a fresh scan. Schema 10
is unchanged.

The initial setup runtime reports an actual changed signing inventory as needs-attention
and preserves the pending work. Missing initial completion over later unsealed work keeps
its explicit recovery error before any retryable scan error, rather than restarting
bootstrap. No general publication/head authority is created. Initial setup still checks
ready native ownership, exact original plans, current policies and actual cloud reads.
General failed-history replay, complete predecessor/covered graph verification, renewed
access and generation publication/activation remain unfinished.

All 291 Core cases passed before the final legacy-proof guard. Final affected-Core128
and all-AppServices204 cases pass with that guard; the Core binaries used by those suites
and the strict Desktop Release build have the same SHA-256. The build has zero warnings
or errors, and complete final logs have no unresolved diagnostics. The initial development
run caught an exception-priority regression; explicit missing-stage recovery was restored
without changing its assertion. A new synthetic orphan-row fixture was corrected to
disable foreign keys only on its corruption-injection connection. Production constraints
are unchanged. No live Drive operation, new UI/device acceptance, installer, APK or joint
release is claimed. Warning gates and existing assertions remain unchanged.

The preceding 731fb9d passed Protocol39983066115 and Desktop39983066176 on all four
native platforms, Android39983066016 and device39983065976 on APIs29/35/37. Fresh
exact-revision CI is required for this increment and the eventual joint release.

Updated: 2026-10-22. **Stage 0 validation continues; contract, core and catalog integration are in progress. No 0.6 application or installer is released.**

## Authority and baseline

The owner approved implementing the complete joint release unattended, reported switching to High effort, and subsequently directed emulator-first development with the S23 reserved for final tests. The historical plan-only stop in the plan documents has been satisfied. Preserve fast failure, object ownership and original-media protections.

Baseline: `6c9ead7b1d4446dc819dee71974aa7b68614f232` (0.5.33 publication revision), application source `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Work branch: `feat/processor-060`. Native evidence records the exact clean committed SHA; local experiments are explicitly provisional. Android has portable protocol consumers and bounded legacy XMP output; UI composition, version and Room schema remain at baseline.

## Stage ledger

| Stage | State | Evidence / remaining work |
| --- | --- | --- |
| 0: feasibility/baseline | In progress | Production OAuth, full desktop grant, fresh-process refresh, bounded synthetic Drive round trip passed. Manual post-consent source discovery, actual-expiry refresh and injected lost-completion reconciliation passed. Four-target two-model/decoder native matrix passed on 67607b1; expanded object model matrix is running on 9bae0a3. Actual cross-client/crash/multiple-account/duration and remaining codec/OCR/package gates stay open. |
| 1: contracts | In progress | Matching identity/facts-tree/coverage/source-policy/publisher/signature fixtures pass: 67 C# Protocol and 557 Android unit tests with zero-findings lint. Complete live publication/activation and operation integration remain. |
| 2: core/plugins | In progress | Local SQLite ownership, durable stage/checkpoint/receipt outbox and failed-only retry foundation under test. Resource reservations, actual process ownership/recovery, complete plugins and tray/Exit remain. |
| 3: accounts/coordination | In progress | Production protected account/refresh, native signing ownership, authenticated loopback web host and reviewed local workspace setup implemented. Cloud library discovery, browser LAN onboarding and Drive-only mobile enrollment/acknowledged controls remain. No phone LAN connection is permitted. |
| 4: catalog | In progress | Bounded WAL facts snapshots, verified immutable objects/tree traversal and source coverage are implemented. Publishing, approved-configuration/ancestry agreement, staged phone activation and human synchronization remain. |
| 5: imports/previews | Pending | All three Takeout paths, per-stage retry, previews/video. |
| 6: recognition | Pending | People/object/OCR workers, consent, quality and native coverage. |
| 7: Android | Pending | Complete remote workflows and remove batch inference. |
| 8: installers | Pending | All required native install/upgrade/uninstall paths. |
| 9: acceptance | Pending | Frozen full matrix, scale, live Drive/LAN, duration and S23. |
| 10: release | Pending | Exact tested APK/installers, publication and public verification. |

## Completed observations and experiments

- Existing app: 0.5.33/code 44, min SDK 26, compile/target 37, Room 12. New release target remains 0.6.0/code 45, protocol major 1; no premature version bump.
- Chrome is signed into the owner's intended Cloud project; Drive API enabled. A new empty disposable test folder was created separately from the existing library. Private identifiers/screenshots remain in the local preflight evidence, not this repository.
- Initial OAuth inspection found External/Testing status and two Android clients and configured `drive.file`, `drive` and Photos Picker scopes. The owner explicitly approved creating a separate Desktop client named MPA Processor. Config is stored outside tracked files. Creating the client did not itself grant account access or expand configured scopes; subsequent explicit grants are recorded below.
- Bounded Windows authorization/refresh probe prepared with official Google library, PKCE/state and DPAPI. Synthetic storage round trip, expiry persistence, plaintext-marker exclusion, type isolation, corruption rejection and deletion passed with zero build warnings/errors. The actual loopback server also passed synthetic valid-state, mismatched-state and cancellation/temporary-request cleanup checks; concurrent probes are excluded by a private file lease. On October 20, narrow desktop consent and new-process immediate refresh passed. A read-only folder probe observed the existing manual folder as inaccessible under the returned drive.file scope. Full scope and immediate refresh subsequently passed, followed by bounded synthetic uploads, status/resume, hash/range reads, reserved-ID replay and nested paged discovery. Cross-client/manual-source and duration acceptance remain pending. See `validation/drive-feasibility.md`.
- The initial branding blocker was resolved. The owner supplied their existing website and reviewed and approved MPA-specific homepage/privacy/terms plus the domain/production configuration. Website main was fast-forwarded to the reviewed pages; Pages deployment completed successfully and all three public pages were verified. Google confirmed the branding save and **In production** status after a fresh action-time owner confirmation. Verification is still incomplete and the unapproved-scope 100-user cap is visible. Chrome control recovered after transient disconnects. The owner approved the narrow desktop grant, and immediate fresh-process refresh passed. The owner explicitly approved full Drive and completed the safety interstitial personally. The callback returned exactly drive at 2026-10-20T04:33:16Z. New-process refresh and folder reads passed; synthetic-only fixtures were created beneath the disposable root. Real beyond-seven-day observation is due after October 27 04:33:16 UTC. Existing website and domain ownership verification files are preserved.
- Installed .NET SDK 10.0.303/runtime 10.0.13 works outside sandbox; the sandbox SCM error was an execution restriction, not an SDK defect.
- Separate headless API 35 development emulator booted; checksum-verified 0.5.33 baseline installed and launched there. No physical device changed. The old global emulator is development-only; final tests still require the pinned matrix.
- ONNX Runtime 1.31.0 + SkiaSharp 4.153.1 build with zero compiler/analyzer warnings/errors on Windows. Native execution runs YuNet/SFace and an in-memory JPEG round trip.
- Original SFace export emitted initializer-input warnings. A checked graph-declaration correction removes them without changing operations/weights. Both models passed all-output comparisons for three deterministic inputs; largest measured SFace absolute difference was `5.662441253662109e-6`. Corrected Windows runtime log has zero warnings/errors. This is feasibility, not recognition-quality acceptance.
- Native probe steps on CI Windows and Apple Silicon passed on `b29fb84c6a43d8737a239861157d8779bd3ac8f7`, but their jobs were canceled by matrix fail-fast after Linux correctly blocked a hardware-discovery warning. Preserve that distinction; the run did not pass.
- Official ONNX Runtime NuGet lacks osx-x64. The revised matrix builds that exact runtime source on Intel and Linux with unused accelerator discovery and telemetry disabled. The Linux diagnostic was `device_discovery.cc/GetPciBusId` rejecting a synthetic VM path. No diagnostic suppression or exception is introduced. On revision `7bf2141ec5d24ef1c42914f27e04d8808124cdf6`, complete Windows and Apple Silicon jobs succeeded and their downloaded report hashes/source binding were verified. Linux compiled but failed strict upstream diagnostic review (76 warning lines); Intel was canceled by fail-fast with partial diagnostics retained (87 warning lines). Neither source-build target is accepted. No diagnostic exception is approved for these builds.

## Commands and local evidence

```text
dotnet restore probes/NativeFeasibility/NativeFeasibility.csproj
dotnet build probes/NativeFeasibility/NativeFeasibility.csproj --no-restore -c Release --verbosity minimal
python processor/probes/fetch_models.py
<isolated-onnx-python> processor/probes/normalize_models.py
dotnet <probe-dll> --compare <original-model-dir> <normalized-model-dir> <new-report>
dotnet <probe-dll> <normalized-model-dir> <new-report>
```

Local evidence outside Git: `preflight-060/native-build-normalized.log`, `native-equivalence.json` and its complete original-reference log, `native-windows-x64-normalized.json`, `native-normalized.log`, `baseline-emulator.json`, baseline install/launch logs and browser screenshots. NuGet lockfile and candidate model hashes are tracked. CI evidence must bind an immutable commit; it has not passed merely because its workflow is written.

## Open gates / next packet

Latest October 20 packet: `67607b119d7e1c6b89dcbafec67c95da72964e6c` is the current pushed native compatibility increment. CI run 39373514740 completed Windows/Apple Silicon successfully; downloaded four-file evidence inventories for each were independently hash/source verified. Linux/Intel subsequently passed too. Complete source-build logs, five source inventories per target, append checks and all four native report hashes were independently verified; no unresolved diagnostics. This is the two-model feasibility increment, not product acceptance. Authorization run 39373514936 passed strict build and synthetic checks without using any Google grant in CI.

Live full-Drive recovery also passed an injected I/O failure after the server completed a synthetic upload but before the uploader received its response: reconcile the persisted ID/hash, obtain 409 on replay and verify exact downloads. Separate Chrome-created nested metadata was discovered and byte-verified without another consent. Real socket/process interruption, Android cross-client and elapsed-time gates remain open.

The next local candidate adds pinned YOLOX, embedded SFace modification notice, exact build-tool wheel/license hashes, Windows synthetic OCR and nine offline downloader-ownership tests. Local strict builds and corrected native smoke are clean; all nine model-output comparisons passed. This is provisional Windows feasibility, not a completed recognition plugin or final native matrix. See `validation/object-ocr-feasibility.md`. No product Android files/schema/version changed.

1. Website and OAuth Production configuration are complete. Preserve the existing approvals and the personal-test/unverified-app limitations; do not ask again for the completed grant.
2. Complete independent Android/Desktop access, manual-source discovery, actual expiry and operational-recovery tests. Configuration/full access/short-refresh and the personal-use path are observed; real elapsed-time observation started October 20. Native feasibility remains open before Stage 0 completes.
3. Execute native feasibility on Linux, macOS ARM64 and Intel; resolve all warnings and freeze dependency/model/license/tool evidence. Check object/OCR/video backend redistribution and runtime support too.
4. Finish signing/native-test-access inventory. Missing paid publisher signing uses the already approved personal-test path; missing native execution does not.

Do not stop at routine stages or ask for another Continue. Stop only for required human access/consent, unresolved diagnostics, safety or a material scope decision, while continuing useful independent work.

## October 20 verified follow-up

- Native CI 39373514740 on 67607b119d7e1c6b89dcbafec67c95da72964e6c passed all four OS/architecture targets. Independent downloaded evidence/report hashes and source binding verified; complete Linux/Intel build logs have zero diagnostics. Earlier failed/canceled runs above remain historical failures, not current unresolved source defects. See validation/native-compatibility.md.
- A manually uploaded synthetic nested file added through Drive UI after consent was found and read byte-for-byte by the existing desktop credential. A real-server upload completion response was discarded by the test transport before consumption, and reserved-ID reconciliation/replay passed. This is injected I/O failure, not a real process-crash claim.
- At 2026-10-20T06:22:43.0693717Z a fresh process refreshed the stored access token after its recorded expiry 06:03:57.8061269Z, without interactive authorization. The actual beyond-seven-day observation remains due after October 27 04:33:16 UTC.
- Android CI on 9bae0a3 correctly failed its strict lint gate after Gradle 9.8.2 became stable. The exact official checksum is pinned in the candidate update; no check is disabled. The existing AGP setVisible diagnostic remains separately checked by owner, label, ID, origin and version.

## Protocol and conditional mutation follow-up

- Revision 8e50af7ab98d3ef74ba7c9a3efe6a3de8b82fc88 passed all four desktop contract jobs (CI 39385167894); these contain the first 17 C# tests. Subsequent local candidate has 25 passing C# tests; its final cross-platform run is still required. Targeted Kotlin 11 tests and strict lint passed; no product navigation/storage behavior was enabled.
- Gradle 9.8.2 pin passed full Android build/unit/lint CI 39385167843 on 8e50af7, in addition to local targeted checks. New source/fixture LF rules resolve Git conversion warnings.
- Expanded native run 39380066718 on 9bae0a3 passed Windows/Linux/Apple Silicon; their downloaded evidence hashes, source/model binding and logs independently verified. Intel failed because the hosted runner lost communication; no completed log/artifact was available. That failed job was rerun on the same source, not waived.
- Read-only v3 synthetic metadata returned no ETag. A separate bounded synthetic-only probe confirmed v3 accepted an invalid If-Match (HTTP200). v2 exposes the ETag and rejected formerly-valid stale ETags (HTTP412), including Trash/restore, while matching ETags permitted those changes. Original bytes stayed exact. No existing originals changed; fixture retained. A deliberately arbitrary opaque ETag caused HTTP500 in v2; this negative observation is retained and is not evidence of successful conditional enforcement. Only the valid/stale captured-tag cases establish that capability. Product mutation adapters must use the independently verified conditional path and preserve replay/source/destination checks. See validation/drive-feasibility.md.

## Durable core foundation

- Microsoft.Data.Sqlite 10.0.13 with exact compatible SQLitePCLRaw 2.1.14 pins; the Windows test runtime reports SQLite 3.54.0. The 2.x patch branch matches Microsoft.Data.Sqlite's native provider dependency family; no preview/dynamic selectors. Lockfiles capture all transitive bytes. Actual native core testing on the other platforms is still required.
- The new core owns a dedicated marked local state directory, single-instance file lease and SQLite connection. Unknown directories/databases/future versions are preserved and rejected; no silent reset. WAL/FULL durability, foreign keys, parameterized statements and bounded transactions are explicit. SQLite calls belong to the serial background coordinator, not the UI thread; async wrappers do not make SQLite disk I/O asynchronous.
- Job contexts are immutable captured JSON/hash, including destinations. Stage attempts have fresh lease IDs; checkpoints survive retries. Completion and the publication outbox are one transaction. A failed item does not block independent work, and failed-only resume never requeues verified ready stages. Pause/cancel fence late results, wait for the worker owner to confirm exit, and retain completed receipts. A reopened running attempt stays fenced until the actual worker recovery owner resolves it.
- Seven local core tests passed. A repeated TRX filename caused a test-harness overwrite warning; the verification rerun uses a fresh unique result file. Native child/process-tree cleanup and crash recovery are NOT established by these repository-level tests; they are the next owner/service work.

## Storage budget and Linux codec follow-up

- Core candidate now has 11 passing local tests with no diagnostics. Storage reservations are disposable, account for other writers/cache use, recheck before writes and retain reservations/checkpoints on low-space failure. Default reserve remains max(2 GiB, 5% volume); explicit acknowledged settings may change the percentage while retaining the 2 GiB floor. No settings were silently changed and no file-deletion path was added. Actual volume adapters/cache eviction and worker resource enforcement remain.
- Corrected local Linux FFmpeg 9.0.2 decode-only source build and synthetic AVI pixel comparison passed with zero diagnostics. NASM 3.02 was extracted from its official package and executed without system installation. The additional strict native regression passes both six-symbol 8-bit and three-symbol 16-bit VLC recursion against independent expected bit patterns. Checked patches add union bounds assertions, scope the WMV2-only label and make existing unsigned comparison conversions explicit. No diagnostic flags were removed. This is provisional local feasibility, not a frozen cross-platform codec package or redistribution closure.

## Managed worker ownership increment

- Expanded three-model native matrix39380066718 passed all four targets after the same-source Intel infrastructure retry. Independently downloaded evidence verified. Mac peak measurement remains unavailable; no memory waiver or zero-memory claim.
- New private controller-lifetime channel uses an authenticated loopback connection with a one-launch key, explicit five-second handshake bounds, rejection of invalid peers, protected diagnostic serialization and joined concurrent disposal. It is internal IPC, separate from LAN pairing/Google credentials. Partial ownership handshakes close accepted sockets.
- The first-party framed worker owner verifies/holds its executable, clears inherited environment, captures bounded stderr, limits pending frames, fences new writes during shutdown, and joins process/pumps/writes before releasing resources. A failed bounded stop retains ownership and exposes a recovery join, rather than declaring clean Exit. Unexpected exit/frame failures remain errors after cleanup.
- Twenty local core tests pass, including actual child echo, malformed framing, unexpected exit, stubborn child termination, held-file release and deliberate controller termination while its worker ignores stdin EOF. The worker observes its parent channel closing and exits; synthetic source bytes remain unchanged. This is a real managed-process fixture, not validation of native OCR/video tools or Windows job objects/macOS linked guards. Those and actual tray/Exit integration remain.

## Dependency scheduling and Linux crash-test identity

- Persist blocked dependency stages outside the ready-work queue. Completion promotes only the same item after every prerequisite is ready; failed-only retry, reopen, and cancellation preserve that boundary. This avoids repeated scans of all blocked stages during large imports. Candidate Processor database schema 1 is still unpublished; Android Room remains unchanged.
- Windows strict Core tests now pass 22/22, including actual worker/controller-loss tests and new multi-prerequisite/reopen/cancellation cases.
- Four-platform run 39397061782 correctly caught Linux process-start wall-clock estimates differing across observers. The test now compares kernel boot ID plus raw start counter on Linux, preserving exact process-instance ownership instead of adding a timing tolerance. Other platforms retain exact process start ticks. Cross-platform verification of this correction is pending.
- Expanded native model run 39380066718 on 9bae0a3 passed all four targets after the same-source Intel runner retry. Independently downloaded artifact inventories, source/model bindings and full native logs verified; no diagnostic exception was used.

## First executable product worker

- Current local Protocol tests pass 29/29 and Core tests 27/27 with no build diagnostics. The actual ByteFacts worker performs framed role/session/sequence negotiation, verifies an immutable assigned input, returns a separately hashed output, cancels prepared work without output, and isolates a changed snapshot before processing another item. It is a harmless host acceptance worker, not face/object/OCR feature completion. Cross-platform verification for this new host remains pending.
- Linux codec CI 39400442460 on f98d083 passed publisher signature/pinned compressed CPIO and executable validation, strict restricted FFmpeg build, recursive VLC regression and exact synthetic AVI frame decoding. Downloaded report confirms SHA aed3580452b8440323740cb07e8a4f9369c1d92a36dd3ab15b0221c0e7c033e2. Complete diagnostic verification and other native codecs/format/license closure remain open. Two local WSL configure runs hit their bounded 300-second deadline; their owners killed/joined compiler groups. These timeouts are not accepted product evidence or warning waivers.

## Desktop controls and verified ownership follow-up

- Protocol/Core CI 39431189286 on 8a0ecca087718107e958a5c19e0e04374fde8d66 passed all four supported desktop targets: Protocol 29 tests and Core 31 tests, including actual operations/readers/reservations and rapid process exits. Downloaded final evidence verification remains required.
- Candidate desktop shell uses exact stable Avalonia 12.1.4 and its locked compatible SkiaSharp 3.119.4 graph. This renderer is separate from the 4.153.1 worker process; it is not forcibly upgraded across an unsupported Avalonia ABI. Original Android vector branding supplies the icon; derivation hashes are tracked.
- Local candidate Core tests now pass 36 tests, adding actual current-user local control, malformed client isolation, explicit handler-failure propagation and partial-frame shutdown. Final local re-run after the process-ID reply addition passed all 36 tests with no diagnostics; complete the native matrix before accepting it.
- Local Windows native UI check against isolated workspace state passed close-to-background, same-instance reopen, actual UI Pause/Resume independently confirmed via IPC, and actual Exit: process 245380 exited with code 0 and 0 stderr bytes. These are intermediate shell tests, not installer or final release acceptance. Tray menu entries exist, but direct system-tray menu clicking was not exercised by the available target-window tool.
- Native review found and corrected repeated Window.Opened database initialization, and a concurrent-launch race. Program now acquires durable state ownership before constructing a window; a competing launcher waits for the current owner's endpoint. Either concurrent launcher may become owner; control replies expose the actual local PID. An isolated concurrent launch selected one owner and accepted repeated show/pause/resume/stop commands with the installation marker unchanged.
- Initial exploratory test harness had a non-terminating PowerShell date-conversion error; scoped process/path evidence identified only the isolated fixture. Subsequent guards use terminating errors and exact process start ticks. Earlier failed native probes remain retained evidence, not accepted results.
- No 0.6.0 Android UI/runtime activation, signed joint installers or publication yet. Accounts/pairing/catalog/plugins/phone thinning and full acceptance remain open.

Desktop CI39443445552 on75dd7d10c4e2b40ce8b4fbc5fadea1fe69b03faf passed strict builds on all4 targets. Core IPC CI39443445237 correctly failed on macOS: the standard per-user TMPDIR plus the long endpoint name exceeded Unix socket path limits. Candidate shortens only the label from MPA.Processor.Control.v1 to mpac1-, preserving the same128-bit state digest and CurrentUserOnly checks. Actual Mac IPC must pass before acceptance; no exception requested.

Native IPC run39444762318 onbbd346a got past the socket-path issue, then caught an immediate-disconnect race in .NET's explicit Unix socket buffer setup on Apple Silicon. Use default transport buffers while preserving the independent8KiB protocol frame cap. Add20 repeated disconnect/status exchanges; native revalidation pending. No native failure was waived.

## Product credential adapters

- New native credential owner passes8 local tests with zero diagnostics: real Windows DPAPI round trips/restart/overwrite/exact removal/128KiB binary records/ciphertext corruption, plus held-call cancellation/disposal and diagnostic serialization checks. No Google grant was moved or logged. Mac Keychain and direct Linux SecretService adapters are implemented but not accepted until native CI passes. Their four-target workflow uses synthetic secrets, a private Linux D-Bus/keyring and owned daemon shutdown. Locked-store/native installer acceptance and actual account UI integration remain open. See NATIVE_CREDENTIALS.md.
- Immediate-disconnect IPC regression also found the Windows Broken state needs Disconnect even though IsConnected is false. Explicit reset now passes37 local Core tests; native matrix is running on11598ec. No production failure was hidden or waived.

Native credential run39446619902 one94259d passed all4 platforms (8tests each). Independent artifact verification remains. IPC run39446618889 caught a Linux shutdown race: after cancellation the loop tried to accept again before checking the stop token, with a connected pipe intentionally retained for disposal. Check cancellation before entering the next accept; no exception is hidden. Targeted local control checks passed6/6; native revalidation remains.

Downloaded all4 native credential artifacts for run39446619902 and independently checked sourcee94259d, report/assembly-hash syntax,8passing/0failed/0skippedTRXresults each, full restore/test/keyring logs and artifact-file hashes. No diagnostics found. Private inventory is native-vault-ci-e94259d/independent-verification.json; assemblies themselves were not distributed, so report binding is not claimed to independently hash absent assembly bytes.

Product Google connection now builds without diagnostics and passes9local synthetic tests. It owns PKCE/state/system-browser loopback cancellation, scoped native-vault token records, preserved refresh credentials, read-only stable permission-ID checks, and joined Exit. HTTP/account-mismatch failures preserve credentials; reconnection must promote a new verified pending connection rather than overwrite an existing grant. No live grant moved yet; desktop account UI and native CI remain. See GOOGLE_ACCOUNT_CONNECTION.md.

Core run39447816313 caught a Windows synthetic-fixture race: the resource limiter could stop the test child between creating its input file and flushing byte42. The source-preservation assertion remains unchanged; instead the test owner now creates/flushes the input before launching, and the child opens it read-only. Separate readiness marker proves invalid launch policies/hashes did not start the worker. All37local Core tests pass with this correction. Linux listener tests passed in39447816313; full native revalidation remains required. This did not involve real source media or a product input-write path.

## Durable account setup and native follow-up

- Native run39448589289 on67e69272d1884670431295e9342a3da24101e712 passed all4 targets: Protocol29, Core37, Google9. Downloaded all targetTRX/full restore-test logs; independently verified exact counters, zero diagnostics and file hashes. Earlier socket/disconnect/shutdown/fixture failures remain retained, corrected rather than waived.
- Candidate Processor SQLite schema2 adds pending/verified Drive account references and a durable credential cleanup journal; Android Room12 remains unchanged. Stable Google permission IDs survive reconnection. Wrong-account, duplicate-account and stale-revision attempts cannot promote; disconnect preserves account identity and unrelated saved jobs. Installation ID survives reopen. Candidate schema1 migration preserves captured import destinations and existing jobs. Local Core suite now43passing; final focused account/migration checks6passing with zero diagnostics. Native schema2 revalidation remains pending.
- Shared ProcessorStateCoordinator serializes repository calls off the UI thread and joins active state work. Program/database ownership and desktop account UI integration are the next increment.

## Desktop account setup candidate

- Desktop exposes Google connect/check/reconnect/disconnect, resumable pending setup and retryable credential cleanup. Program retains the shared SQLite coordinator and database through work/account/vault/control shutdown. Account setup I/O stays off the UI thread. Background failures retain evidence and an unsuccessful exit rather than becoming empty/ready state.
- Private Windows product acceptance reused the already authorized full-Drive grant in a new isolated Processor build state. The access token was already expired; product refresh, protected storage and read-only about identity verification passed at2026-10-20T15:41:55Z. No new consent or Drive file write occurred. Original private probe grant remains intact. This is not the real beyond-seven-day test.
- Actual Windows account UI displayed the protected account and completed Check connection, Pause/Resume and Exit. Original process289008 exited0 with0stderr bytes. A follow-up corrected the misleading Ready to connect heading after account connection. Actual corrected UI227528 showed Ready to pair your phone, close-to-background/reopen preserved the exactPID/account, and native Exit again returned0 with0stderr bytes. All private evidence retained in preflight-060/desktop-account-ui*.json/log. Direct tray-menu clicking and native installer acceptance remain unverified.
- An initial account-UI status harness used a GUI executable without explicitly waiting, so its inherited LASTEXITCODE falsely reported failure despite a successful reply. Corrected harness waits the command process and checks its actual ExitCode plus retained UI PID. This was test-driver behavior, not an MPA failure or exception waiver.
- Final local affected regression passed Credentials8, Google9, Core43 and AppServices5 with no diagnostics. Desktop and private acceptance probe strict builds have0warnings/0errors. Native matrix is extended to AppServices and acceptance-probe compilation; final current-source evidence remains required. Android still baseline0.5.33/code44/Room12; no0.6.0 APK/installers/release. Pairing/catalog/source/import/plugin/Android/installer/final acceptance work remains open.

Native current account/setup run39452233450 ond73f686 passed all4 targets: Protocol29/Core43/Google9/Setup5. Desktop+acceptance-probe build39452226434 passed all4. Downloaded reports/TRX/full logs and independently checked source/counters/zero diagnostics/file hash inventories under account-native-ci-d73f686 and account-desktop-ci-d73f686. No absent assembly-byte hashing claim.

LAN identity candidate passes7local tests: separate protected P256 TLS/document keys, no silent rekey on corruption/identity mismatch, renewal with stable pins, joined startup cancellation, actual mutual TLS and actual wrong-pin/name rejection. Windows native Schannel rejected ephemeral keys; OS-managed temporary CurrentUser key container solves that without a PFX file, PersistKeySet, Exportable or trusted-root change. Actual test proves temporary CNG container removed on disposal. Native run39454956807 on1928b54 passed all4 targets; download verification for this increment remains separate.

## Owner-approved desktop web revision, 2026-10-20

`DESKTOP_WEB_REVISION.md` now governs the desktop UI and communication changes. Desktop is a full photo client with a locally bundled React/TypeScript web UI and an independent .NET background engine. Other computers access its web UI on the same LAN. **Android communicates only with Google Drive**; all earlier phone LAN/API/pairing/discovery plans are superseded. Mobile feature settings/requests require immutable Drive delivery and applied/conflict receipts. No phone LAN permission, direct connection or fallback is authorized. Use Chrome for web acceptance; native UI automation is reserved for genuinely native behavior.

Candidate code implements the engine-owned web host, bounded read-only Drive discovery/thumbnail adapter, private browser session authority, account setup/check/reconnect/disconnect/recovery in the web interface, native Open MPA/background lifecycle wrapper and exact-version locally bundled frontend. The rich Avalonia account panel is removed. Existing Processor schema2 and Android Room12 remain unchanged; one stable local library ID is retained in existing metadata before keys are created. Closing a browser does not own/cancel durable processing work.

Local Web10 tests pass, including actual HTTPS/Kestrel with the authenticated server pin, Secure/HttpOnly/SameSite cookies, remote browser forbidden from host Google authorization, single-use/endpoint-bound invitations, monotonic expiry, CSRF, Origin, rebinding rejection, operational errors distinct from empty data, session isolation and joined request shutdown. This is transport/native-loopback acceptance, not proof of browser certificate onboarding or separate-machine LAN operation. Desktop currently starts loopback only; LAN enablement/onboarding remains unfinished. Frontend strict TypeScript/esbuild build passes; locked npm audit reports0vulnerabilities. Native matrix now includes Web tests and desktop frontend compilation; current-source matrix must still run.

The first actual Chrome gallery correctly read60real Drive entries but failed thumbnails because its adapter incorrectly assumed a `/d/` URL path. Failure remained visible in host attention/diagnostics. Correction retains the exact HTTPS `lh3.googleusercontent.com` origin, standard port and no-userinfo/fragment constraints without imposing an undocumented path shape; foreign-origin rejection remains tested. A new read-only ProductMediaAcceptance probe reused the existing approved protected product account and verified a60-entry page plus a7120-byte JPEG thumbnail at2026-10-20T18:49:02Z. No new consent, original download or Drive write; private media names/IDs/URLs are not in its report. This is not full catalog/original-viewer acceptance. Earlier failed UI evidence is retained, not waived.

Actual corrected Chrome review is pending browser automation reconnection. The corrected background engine is under a retained process-handle acceptance driver; its stderr is currently empty. Do not infer complete visible gallery/viewer success from the adapter result. Full catalog/Drive-operation enrollment, source policies, imports, plugins, all desktop photo functions, Android adaptation, LAN browser onboarding, native installers, scale/physical/final checks and joint publication remain required. No0.6APK/installers/release exists.

## Source configuration and development listener follow-up

- Committed web foundation aaf59e0ff8c60fa2927f29e0c5b31a46de714f28 passed desktop run39479597044 and contract run39479597002 on Windows/Linux/macOS ARM/Intel. Downloaded full logs/TRX; independently checked source, Protocol29/Core45/Google13/Setup5/LAN7/Web10 per target, zero diagnostics, zero npm audit findings, lock hash and agreement of four reported frontend inventories. Artifacts contain reports/logs rather than application binaries; absent assembly bytes were not independently hashed.
- Processor local schema3 adds source bindings, revision fences and an atomic canonical configuration outbox. Disable/reconnect preserves identities, captured jobs and original media. Schema2 upgrade preserves installation/library/accounts/jobs. Android Room12/product behavior remain unchanged. Local Core52/Google16/Web11 tests pass, including bounded folder reads, wrong-parent rejection, CSRF denial, malformed IDs and stale policy conflicts that preserve the usable session.
- Sources UI adds bounded folder navigation, explicit selection and source-policy switches with account-change cancellation and retained errors. Configuration is durable locally; Drive publication, indexing and actual Chrome acceptance remain unfinished. Discovery Photos is not yet the selected-source catalog.
- Owner requested localhost-only development because of repeated firewall prompts. Every development HTTP/HTTPS listener, including TLS fixtures, now binds 127.0.0.1; startup fails if actual endpoints differ. Real listener tests preserve HTTPS/session checks. No firewall rules, trusted roots or OS security settings changed. Rebuilt Windows engine observed listening only on127.0.0.1:22416 (ephemeral port); strict build0warnings/0errors and empty stderr. This does not establish remote LAN acceptance or prove no future Windows prompt.
- Chrome automation disconnected during retry. A prior tab retained old Failed to fetch content from its stopped server and is not acceptance of the latest build. Browser reconnection requested while independent implementation/checks continue. No visible-success claim or new Google consent request.

## Local discovery projection candidate

Processor schema4 adds bounded account/file identity facts, source memberships and local discovery revisions. It preserves sources/accounts/jobs and keeps AndroidRoom12 unchanged. Keyset timeline/date-seek queries retain provenance and unknown dates; stale source/account revisions fence batches, older file revisions cannot overwrite facts and same-version conflicts roll back. Incomplete scans do not delete data. This repository is not yet connected to the source scanner, portable publication or UI. See LOCAL_DISCOVERY.md.

Local Core62 passed including10,000/100,000 fixtures; subsequent2timestamp tests passed after resolving the static-member analyzer finding without suppression.100,000 preparation214.6seconds/date-seek11.9ms/max60-item page75.4ms/fullwalk17.9seconds on concurrently loaded Windows. These are provisional single observations, not full import/memory/UI acceptance.

Android run39479596925 on aaf59e0 failed API35 draggingAndReversingAcrossAllLayoutsPreservesEarlierSelection: selected identity-set assertion passed, then selected tile008 was not composed in the reduced viewport. API29 passed; API37 was canceled by fail-fast. Test-only correction aae2757 scrolls every originally asserted tile into view and keeps every selection/non-selection assertion plus database-preservation checks. Native revalidation remains required. No product selection code changed or failure waived.

Read-only source-page adapter candidate passes20Google tests (all existing plus4source cases). It limits listings to60items/512KiB, validates canonical parent/shared-drive membership, retains page cursors/incompleteSearch, isolates malformed individual metadata, rejects repeated identities and never follows shortcuts into additional source scope. It makes only identity/metadata GET requests. Runtime source scheduling/checkpoint integration is still required; no background scan or original download is claimed. Initial fixture-allocation analyzer finding was fixed with a reusable immutable fixture array; no suppression.

Catalog revision e70dda564b79d422d96c04ffb89c2ca3a2671f83 passed native contracts39487318191 and desktop39487318199 on all4 targets. Downloaded and independently verified132tests/target (Protocol29/Core64/Google16/Setup5/LAN7/Web11), complete clean logs/source bindings and both scale measurements per target. Windows contract job took about10minutes; it passed within the existing timeout. Android native39487318259 remains separate and pending. No native exception was introduced.

Processor schema5 candidate adds immutable per-item captured context to durable jobs, atomic job+initial-item admission, exact replay checks over input/context/recipe graph, and kind-scoped claiming so a metadata scanner cannot claim uploads. Empty context is supplied during upgrade without changing existing jobs/checkpoints. Existing schema1/2/3 fixtures now recreate their actual historical item columns. Local66non-scale Core tests passed; after adding replay comparisons all5focused captured-item tests passed. Final native69-test Core coverage remains required. This is scanner admission groundwork, not a running source scanner or a completed import engine.

## 2026-10-21 source and read-owner follow-up

- Source/credential revision82bee80557c4596243c611e3a28dcadfa3f89e53 passed contracts39546071800 and desktop39546071747 on all four desktop targets. Downloaded and independently verified155tests/target (Protocol29/Core78/Google20/Setup10/LAN7/Web11), complete clean restore/test/build logs, both scale measurements, zero npm audit findings and agreeing reported frontend inventories. Application binaries were not uploaded, so their reported hashes were not independently compared.
- Earlier e70 Android native39487318259 passed159tests each on API29/35/37. Independent verification matched every unique runtime start/finish identity and full log hash, plus98screenshot file hashes per API. This verifies evidence completeness, not a new blanket Android diagnostic waiver.9d Android39543721409 also passed all three APIs; its downloaded evidence verification remains separate. Android still uses the0.5.33 product runtime;0.6 activation remains open.
- Actual Chrome review briefly verified all60gallery previews plus branding loaded, viewer open/next/details with honest thumbnail status, and grouped Settings. Sources then met an expired local browser session; a fresh CLI invitation was issued without Google reauthorization. The Chrome extension subsequently became unavailable, including after a fresh automation session. No whole-PC/browser fallback or firewall/security change was used. Development listeners remain127.0.0.1only.
- Candidate SourceDiscoveryRunner now executes one bounded metadata step through the shared read owner, captures verified source scheduling, preserves usable catalog facts, records per-item/page repairs, yields durable page checkpoints, and joins cancellation/pause/Exit before returning leases. Known failed files use an exact captured-file GET; moved-out-of-scope items remain explicit operational failures, while identity substitution and internal conflicts fail visibly. Incomplete search retains healthy facts plus a retryable observation. A terminal checkpoint can finish without another network read.
- Local current AppServices19/Google24/Web11 tests pass without diagnostics. These cover healthy-folder continuation, failed-only retry, interrupted page/authorization recovery, held cancellation/Exit, concurrent pause/source changes, incomplete search, inaccessible-child recovery and invariant failure. The runner is not yet activated by a periodic engine scheduler or Sources UI; progress/recovery controls, metadata storage budgets, publication, Android consumption and all remaining joint-release work remain open. No original bytes are read or mutated by this metadata increment.

## Automatic admission and dispatcher candidate

Automatic source requests now commit an idempotent verification job before any Google call. Stable request identities include the library, account and captured source/account revisions; restart or repeated browser scheduling preserves failed/completed work. Accepted root/shared-drive verification is checkpointed before root-folder admission, so replay cannot replace it with newer network facts. No schema6 or Android persistence change is introduced.

SourceDiscoveryScheduler owns the browser-independent dispatch loop and cancellation epochs. Pause acknowledges only after the active read and durable lease have joined; Resume starts a fresh epoch. Exit joins the loop, and internal failure remains visible with an unsuccessful exit. Exclusive-startup recovery is limited to the three known metadata stages, preserves pause/cancel/checkpoints, and leaves upload/native-worker fences untouched. It must run under the composition root's exclusive state lease before new reads are admitted.

Local full Core84, AppServices26 and strict Desktop compilation pass without diagnostics. Current Google24/Web11 regressions passed in the preceding runner increment. This dispatcher is not yet instantiated in the product composition root: unified web/tray/IPC pause controls, metadata storage-budget admission, Sources activity/retry/rescan and catalog UI integration must be connected before enabling it. The complete joint release remains in development.

## Shared work controls candidate

IProcessorWorkControl gives native worker supervision and metadata dispatch a common pause/resume surface. ProcessorWorkGroup serializes web/tray/IPC control intent, starts and joins both owners even when one throws synchronously, preserves component failures, and quiesces a successful half of a failed resume. Its control gate has explicit joined disposal: active commands finish, queued commands are rejected before gate release, and no borrowed work owner is disposed implicitly. The composition root still owns actual work shutdown.

Local AppServices31 and Web12 tests pass, including five shared-control cases and a real HTTP409/session-preservation check for operational work-state conflicts. The three existing native worker-supervisor lifecycle tests also pass after the interface addition. Initial ownership analyzer finding was resolved with joined async disposal, without suppression. Product composition-root, native tray/IPC and browser integration remain pending; the dispatcher is still not enabled in the running product.
# Metadata storage admission follow-up

The full affected AppServices suite now passes36tests with strict analyzers and no compiler warnings. Five new tests cover actual state-volume measurement, exclusion of unowned files, missing/replaced state files, low-space admission, concurrent disk use after a read, and background pause/recovery/Exit. Existing scheduler/read-owner/failed-only repair tests remain intact. This storage component and scheduler attention are not yet instantiated by the product composition root; activation still requires shared web/tray/IPC controls, joined lifecycle and visible progress. No original bytes or Drive content are modified by this increment.


## Activated discovery, catalog and frozen viewer follow-up, October 21

The historical pending/runtime statements above are superseded for the implemented slices in `LOCAL_DISCOVERY.md`: the product now activates joined metadata discovery, shared storage admission, Activity/retry/rescan and a bounded selected-source gallery. Runtime revision 82b480a passed all four native targets and all three Android API jobs. Independently downloaded protocol/desktop evidence verifies 203 tests per desktop target, complete clean logs, source bindings and scale reports; absent application binaries were not independently hashed. Catalog revision b1567c1 also passed all four workflows, with its independent artifact review still pending.

The next slice adds browser-owned disposable whole-query viewer identity indexes, tracked read-only source snapshots, storage reservations and joined cleanup. Local Core 96, AppServices 41 and Web 24 tests pass, with strict Desktop/TypeScript builds free of compiler/analyzer findings. The real-account native HTTP acceptance probe passes against the isolated synthetic-source state: bounded preview, exact selected identity, same-browser reopen, cross-browser denial and sign-out/Exit cleanup. No source original is downloaded or modified. The probe is compiled, never account-executed, in CI.

Chrome reappeared without user action during the latest retry, then its automation connection dropped again when discovering the newly opened page. Current frozen-viewer visual acceptance remains unverified. The prior native GUI engine did exit 0 with empty stderr; the latest run has a retained bounded-wait process owner. The unattributed browser receiving-end diagnostic is still open. No warning is waived by these passes. Continuous gallery virtualization/anchors, full media functions, portable Drive publication/operations, Android thin activation, plugins/imports, LAN/headless/installers and final joint acceptance/publication remain unfinished. Processor schema 5 and Android Room 12 remain unchanged.


## Viewer native evidence and browser recovery

Viewer revision 47bdb2a passed all four native desktop and contract targets. Downloaded complete TRX/log/report artifacts independently verify 221 tests per target (884 total), zero failures/skips/unresolved compiler diagnostics, both scale fixtures, source bindings, lock hashes, zero npm audit findings and agreeing reported web asset inventories. Actual application binaries were absent, so their reported hashes are not independently verified. Android build passed; its initial native attempt failed before tests because Google's API 29 image download closed. The same immutable device run was retried, with no assertion/gate change; final results remain to be checked.

Chrome reconnected from Codex without a user restart. Actual review then verified immediate preview during session preparation, ready viewer/Details, returning through Settings, reopening in a second tab without breaking earlier controls and sign-out propagation to both tabs. Sign-out left zero SQLite viewer indexes. Native engine 336980 exited 0 with empty stderr and removed its generated viewer directory. A static SVG with zero scripts independently reproduced the receiving-end console record; it is outside MPA application JavaScript, with its originating browser component still unidentified. No user browser/firewall/security configuration was changed.

The next local contract candidate adds matching C#/Kotlin catalog manifest/index readers and shared malformed cases (18 commit, 21 index). C# Protocol 32 and Android JVM Protocol 14 tests pass, including real approved-actor signatures before typed parsing. Complete Desktop strict build has zero warnings/errors. All 519 Android unit tests passed with no failures/skips; lint has zero findings. The existing exact AGP deprecation exception is the only accepted Gradle diagnostic, independently checked by the unchanged gate. This does not activate Drive publication, an enrolled authority or a complete graph/chunk consumer. See `protocol/catalog-v1.md` for exact bounds and the remaining activation checks. No persisted schema or Android product behavior changes.


Existing model/codec feasibility evidence was also independently downloaded and checked: all four desktop targets on ecd4262 executed YuNet, normalized SFace and YOLOX with exact expected hashes, nonempty outputs and clean complete logs; native JPEG feasibility passed. Linux f98d083 passed its restricted FFmpeg synthetic rawvideo probe. These are native feasibility observations, not face/object quality, full media-format acceptance, installer payload verification or codec redistribution closure. The codec report explicitly retains `redistributionClosureAccepted: false`.


## Joined local-control shutdown correction

Contract run 39601550164 on dc4e20d exposed a macOS ARM null reference inside the runtime's Unix `WaitForConnectionAsync` during shutdown. The MPA owner canceled and disposed the pipe before awaiting its actual server task. It now cancels the asynchronous accept/read/write token, joins `Completion`, then disposes the transport and cancellation owner. Microsoft documents cancellation support for pipes configured with `PipeOptions.Asynchronous`, which this endpoint already uses: [WaitForConnectionAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.waitforconnectionasync?view=net-10.0). No null-reference catch or warning exception was added.

All eight focused control tests pass locally. New coverage holds a real admitted handler after cancellation and verifies that the peer does not receive premature EOF, then releases it and verifies joined closure. Forty idle accept/shutdown cycles reuse the same pipe identity after actual completion. Existing malformed/disconnected-client, partial-frame, concurrent disposal and unexpected-handler-failure assertions remain. Native macOS and complete matrix revalidation are required for the correction; the failed earlier run is retained.

The same dc4e20d Android build and all API29/35/37 device jobs passed. Its enhanced restricted Linux codec probe also passed actual synthetic H.264 frame decoding. Independent compressed-codec report review remains separate; codec redistribution, other formats/platforms and the complete joint product remain open.


## Bounded compressed catalog readers

The next increment adds Android's background catalog decoder and a typed-reference overload for the desktop decoder. Both consume independent Python gzip/hash fixtures. Sixteen shared cases cover transport/expanded hash changes, lengths, CRC corruption, a truncated footer with a matching transport reference, noncanonical/duplicate/invalid-Unicode JSON, excess entries, smaller consumer budgets and expansion beyond the declared 16 MiB ceiling. Existing tests remain intact.

The footer fixture exposed a real desktop acceptance gap: the runtime returned decoded bytes for a truncated gzip footer. Explicit complete-body CRC32/ISIZE validation now rejects that input, alongside both SHA-256 checks. It does not replace cryptographic integrity with CRC32. Android runs decoding off the main thread and propagates cancellation; neither codec mutates files or activates a catalog.

Local acceptance passes all 35 C# Protocol tests, 17 Android Protocol tests, all 522 Android unit tests and lint with zero findings. Complete Desktop strict compilation passes with zero warnings/errors. The unchanged Gradle gate accepts only the exact already approved AGP deprecation. Current-source native matrix results and full staged-generation integration remain required. No persisted schema or Android product behavior was changed by enabling these isolated codecs.

Independent H.264 evidence review also verifies run 39601550192 on dc4e20d: pinned 2,272-byte source, H.264/320x240, exactly 230,400 decoded RGB bytes, expected green center, unchanged input and clean complete logs. Actual application/codec binaries were not present in these report artifacts. Other formats/platforms, recognition quality and redistribution closure remain open.

## Originating cancellation before identity promotion

The local-control correction at 4720b6a passed all four native contract and desktop jobs,
plus Android APIs 29/35/37. Downloaded native artifacts independently verify 226 tests per
desktop target (904 total), complete clean logs and both scale fixtures. The next decoder
revision f8595a5 passed its desktop and Android build jobs, but Linux contract validation
exposed an existing identity-startup cancellation race. Its failed run 39634600821 is kept.

`LanIdentityOwner` previously checked only a linked token after a delayed vault read.
`CancelAsync` marks the originating source synchronously and dispatches callbacks
asynchronously, as documented by [Microsoft](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelasync?view=net-10.0).
A read could finish before the callback marked its linked token. The owner now checks the
original shutdown/caller sources after borrowed operations and before a protected write,
and promotes the identity under its lifetime gate only after a final cancellation check.
Unresponsive writes remain owned and joined; a completed write cannot publish a canceled
startup identity. Candidate keys and serialized private bytes retain their existing cleanup.

All nine LAN tests pass locally. A deterministic test blocks caller-cancellation delivery
to the linked token, then completes the vault read and requires cancellation without a key
write. Another holds a write through shutdown and requires joined cancellation without an
exposed identity. Existing assertions remain. Native revalidation is required; no warning,
test failure or cancellation was converted to success.

## Shared observed Drive facts

Matching C#/Kotlin `DriveCatalogFacts` readers now validate the same bounded leaf schema,
including library/account/file-derived identities, source revisions, timestamp provenance,
unknown dates and literal metadata. An independent fixture contains 38 malformed cases.
The desktop encoder sorts without modifying input records, rejects invalid Unicode and
requests a partition split before exceeding count or expanded-byte budgets; it never drops
rows to fit. Metadata observations are explicitly separate from verified original receipts.

Local acceptance passes all 39 Protocol tests, 20 Android Protocol tests and the complete
525 Android unit tests (98 suites), with zero failures/skips and zero lint findings. The
unchanged gate accepts only the already approved exact AGP deprecation. Complete Desktop
strict builds have zero warnings/errors. `protocol/catalog-v1.md` records the exact profile.
These are schema/encoder checks; complete graph/source coverage, enrolled authority, real
Drive publication, transactional activation and Android product integration remain open.

## Parent handshake socket ownership

The snapshot scale-suite run exposed an unchanged parent-lifetime test failing at peer
closure: 101 tests passed and one partial-handshake socket remained readable until its
deadline. A controlled negative replay of the added race tests against the previous
09db04b listener reproduced the same peer-closure timeout. Its log/TRX and exact source
hashes are retained; the corrected source was byte-for-byte restored afterward.

The listener now pre-owns the destination socket before asynchronous accept, using the
supported [Socket.AcceptAsync destination overload](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.acceptasync?view=net-10.0).
This removes the unowned accept-completion handoff. Acquiring the borrowed NetworkStream
also shares the shutdown gate; a replay exposed `GetStream` racing `TcpClient.Dispose`
before that second correction. Reads and acknowledgments reuse the admitted stream.
Neither a null-reference/argument catch nor a longer deadline was added.

All seven focused parent-lifetime cases pass, including 72 rapid real loopback shutdowns
with zero, one and 31 handshake bytes. They require both an unsuccessful partial handshake
and actual peer EOF/reset, and do not accept a read deadline as closure. Existing four
cases retain their assertions. Complete Core/scale and native matrix validation remain
required before publication. This private worker IPC is unrelated to phone connectivity.

## Bounded facts snapshot projection

`CatalogFactsSnapshot` now projects the real SQLite discovery data into the portable
observation models. Its owned read-only WAL snapshot captures library, discovery and
configuration revisions together. The coordinator opens it briefly; its background reader
then uses indexed identity ranges and keyset pages of at most 250 assets, with bounded
source memberships. Imports remain writable. Enabled-source/depth visibility, overlapping
folders, distinct accounts and retained disconnected metadata follow the existing gallery
rules. Disposing the main state owner while a snapshot lives still fails fast.

All 105 Core cases pass after the parent-socket correction, including four focused snapshot
tests and the existing 10k/100k fixtures extended to read every projected identity once.
The latest shared Windows test process measured approximately 0.20s/12.13s for complete
10k/100k projection walks, with maximum 250-asset page observations of 8.1ms/74.8ms.
These are concurrent-host-load observations, not UI responsiveness, compression or Drive
publication acceptance. Complete Desktop strict compilation has zero warnings/errors.
Initial SQL-separator and test-cleanup mistakes were corrected with their failed evidence
retained. No database schema or source media was changed, and no generation was activated.

## Workspace artifact read adapter

The Google connection now has a bounded, account-owned metadata workspace read operation.
It checks actual folder membership/type/download permission before fetching bytes, verifies
exact size/SHA-256, and rechecks Drive revision and scope afterward. Original images/videos,
shortcuts and Google Workspace documents are rejected before blob download. The shared
reader interface exposes the operation without creating a second credential owner.

All 31 Google adapter tests pass, including explicit expected error classes, moved/replaced
and restricted objects, corruption/truncation, unknown HTTP lengths, cancellation during
blob reading and network errors. AppServices 41 and Web 24 regressions also pass; their
preview fixtures explicitly reject unexpected workspace reads. Complete Desktop strict
compilation has zero warnings/errors. Test allocation and ValueTask cleanup analyzer
findings were fixed; no check was suppressed. These HTTP fixtures contain only synthetic
metadata and do not establish real publication, enrolled trust or active catalog staging.

Shared-facts revision 09db04b has now passed every native contract/desktop target and Android
API 29/35/37. Independently downloaded complete native artifacts verify 235 tests per target,
940 total, clean logs, both scale fixtures, lock/source bindings, zero npm audit findings and
agreeing reported web inventories. Application binaries are absent from these artifacts and
their reported hashes are not independently verified. The subsequent parent/socket and
snapshot work still requires current-revision native validation.

## Joined parent acceptance follow-up

Native run 39642356278 on 2f80af3 found the pre-owned socket correction insufficient:
macOS ARM timed out at peer closure in all three new race cases while 102 other Core
cases passed. That failed log is retained. A pending accept destination was still being
disposed before the native operation settled. Underlying runtime causality beyond that
ownership gap has not been established.

The parent now owns a cancellation source and tracks the actual accept/handshake task.
Shutdown cancels its linked operation, closes the listener/admitted client, joins acceptance,
then releases cancellation/key ownership. Only the completed accept loop closes a pending
destination. Async worker startup and failed-stop paths await this join; synchronous disposal
uses the same completion. Unexpected internal exceptions remain failures. The race tests
exercise both synchronous and concurrent asynchronous disposal without changing deadlines
or treating a peer-read timeout as closure.

Local validation passes all 15 focused parent/worker cases and the complete 105 Core cases,
including both scale fixtures, followed by a zero-warning/error Desktop rebuild. These
results supersede the prior local result but do not prove the macOS correction; a new
immutable cross-platform run is required. No diagnostic exemption was introduced.

## Actual protected-account workspace read

The manual desktop acceptance probe adds a `--workspace-read` mode restricted to the
established isolated account state, its single explicitly identified disposable source
folder and the exact pre-existing 92-byte synthetic JSON. It uses the production native
credential vault/Google adapter, starts no listener and cannot substitute arbitrary input
content. CI compiles this mode without account access.

Actual Windows acceptance passed at 2026-10-21 17:20:52 UTC with process exit 0: exact
fixture bytes/SHA-256, wrong workspace, changed size, changed digest and original-image MIME
rejections. Connection/vault/state owners joined before the sanitized report was written.
No original was downloaded, no Drive file changed, no browser/consent flow ran and no account,
file identity, URL or credential was logged. This uses the synthetic source as a transport
fixture; it does not establish managed workspace enrollment, a signed generated catalog or
the actual beyond-seven-day refresh observation, which remains pending.

## Sequential verified facts-tree traversal

Matching C#/Kotlin traversal readers now stream bounded identity partitions from a
previously approved signed manifest. Every index/chunk hash and leaf identity is checked;
object reuse/cycles, foreign workspace/account references and consumer-budget exhaustion
fail explicitly. Reads are sequential without buffered leaf prefetch. Android parsing
and checksums run off the main thread. A later failure remains a failed traversal even
after earlier valid leaves were emitted into staging; it is never empty/ready success.

Independent Python fixtures exercise 21 malformed/late-failed/over-budget graphs. Real
signature tests tamper with valid settings JSON and require rejection before object reads.
Local acceptance passes all 43 Protocol tests, 24 focused Android Protocol tests and all
529 Android unit tests in 99 suites, with zero failures/skips and zero lint findings.
Complete Desktop strict compilation has zero warnings/errors. Repeated test-array analyzer
findings were fixed through shared fields, not suppression. The unchanged Gradle gate
accepts only the previously approved exact AGP deprecation.

This completes facts-tree traversal, not complete-generation activation: coverage, signed
ancestry/authority agreement, policy fences, durable staging and the active-pointer switch
remain required. No live catalog, persisted schema or Android product behavior changed.

## API 29 startup mismatch and bounded XMP output

The native/Desktop runs for `645441e7a59140678565f3da07388a8a93b56ca2`
passed on all four desktop targets. Independently downloaded reports contain 996
passing tests (249 per target), no failed/skipped tests and no warning findings;
reported web inventories agree and npm audits report zero vulnerabilities. These
artifacts do not contain application binaries, so their binary hashes are not
independently verified. Android build passed, but API 29 device acceptance failed:
two thumbnail quality assertions returned 724 instead of 768 pixels, and optional
XMP serialization exhausted a 16 MiB runtime heap. API 35/37 were canceled.

The preserved boot log shows the 64-bit zygote starting with `-Xmx16m` before
qemu-props delivered the AVD's existing `dalvik.vm.heapsize=576m`; the 32-bit
zygote subsequently received 576m. Compiler heap arguments in the same log are
distinct from the runtime heap. The CI preparation now checks the actual primary
64-bit startup arguments. Only that exact API 29/16m-versus-576m mismatch permits
a framework restart before tests, followed by a new PID and matching heap check.
It changes neither heap configuration nor assertions, never clears logs and
rejects physical devices, other images and unexplained mismatches. Six Python
tests pass, including failed repair and physical-device rejection. The next immutable
run on `bcae057bbb3ea45282d83b9a9e8af372e977ee00` passed all three API targets.
Independent verification confirms 477 passing tests and all matching runtime start/finish
events/report hashes. API 29's actual initial runtime heap was already 576m, so no
framework restart occurred; the repair branch remains isolated-test coverage only.

XMP encoding now checks a minimum UTF-8 catalogue budget before constructing its
DOM and uses a byte-bounded serialization stream for escaped and retained external
XML. Overflow remains an actionable `IllegalArgumentException`, not a swallowed
OOM, and the existing optional-XMP import recovery contract is preserved. Fourteen
focused tests pass, including early rejection, escaping, external annotations and
supplementary Unicode. All 533 Android unit tests and zero-findings lint passed locally;
Android CI build and API 29/35/37 device suites passed on the same pushed revision.
Thumbnail memory limits and all existing quality/recovery assertions are unchanged.

## Borrowed workspace transport integration

`CatalogWorkspaceObjectSource` implements the catalog traversal's transport using the
existing engine-wide `ProcessorAccountService` reader owner. It rejects foreign or
unbounded references before borrowing a reader, carries the selected account revision,
checks actual returned object identity and rechecks connected/revision state before
returning bytes. Connection replacement or disconnection cannot promote late results.
It neither owns credentials nor starts a second connection/worker/listener.

All six focused transport tests and all 47 AppServices tests pass, including credential
retirement waiting for a held read, joined Exit, late cancellation, wrong identity,
reconnection and scope rejection. Complete Desktop compilation passes with zero warnings
and errors. The native Google object reader previously passed actual synthetic Drive
acceptance; this new application-service bridge currently has synthetic integration
coverage. Complete-generation validation, durable staging and activation remain separate.

Independent four-target verification on `bcae057` confirms 259 tests per target,
1,036 total, complete clean test/build logs, captured 10k/100k measurements and matching
reported web inventories; npm audits remain zero. These report artifacts contain no
application binaries. Device runtime diagnostics still require final 0.6 review; passing
test reports alone do not establish a warning-free release or S23 acceptance.

## Bounded source coverage and membership validation

C#/Kotlin readers now validate immutable coverage roots/pages and assemble a bounded
mapping of at most 256 sources. Captured configuration/discovery/account/source revisions,
ordered ranges, exact page bytes/hashes, complete page inclusion and unique bindings are
checked explicitly. Stale/incomplete scans cannot claim complete coverage; asset source
memberships must match current captured account/policy/depth. Retained facts from honestly
partial/offline/disconnected sources remain valid; missing observations never imply deletion.

The independent shared fixture exercises all eight states, empty and 256-source libraries,
and 44 malformed graphs/membership cases. All 47 C# Protocol tests, 28 focused Android
Protocol tests and all 537 Android unit tests across 101 suites pass. Lint contains zero
findings and strict complete Desktop compilation has zero warnings/errors. The Gradle
gate accepts only the unchanged exact AGP exception. An analyzer finding prompted a real selected-workspace check in page reading;
the new foreign-reader regression passes. No severity/suppression change was made.

This establishes portable validation, not live publication/activation. Capturing actual
current scan coverage from the same durable snapshot as facts, complete graph-role and
predecessor/authority validation, journals, dirty-prefix publication and transactionally
staged activation remain. No persisted schema or product UI behavior changed in this slice.

## Atomic current-scan selection

Processor schema 6 adds an explicit current-scan reference per source. The reference,
captured source/account revisions, job and initial work item commit in one transaction.
No timestamp, UUID comparison or row order determines the selected scan. Automatic
scheduling retains the current admitted request, including failed/paused work. A new
explicit scan selects its own job; replaying an older HTTP request returns that job's
original identity without rewinding a newer selection. Missing/corrupt selection records
fail visibly instead of resetting work or returning an empty/completed source.

Schema 5 upgrades add the new empty selection table and preserve every preexisting row,
installation identity, captured context, checkpoints and receipts. A fresh metadata-only
automatic request establishes selection without guessing the order of old jobs. Historical
work is preserved. Older-schema test setup now removes the new table when constructing
an authentic earlier schema; all existing preservation assertions remain intact.

All 112 Core tests, including seven new selection/migration/fault cases and the 10k/100k
fixtures, pass. All 47 AppServices tests, 24 Web tests and strict complete Desktop build
pass with zero compiler/analyzer warnings/errors. Native CI for this change is pending.
Android Room remains 12. Current scan selection is a prerequisite for snapshot coverage
export, which is not yet wired to publication or live generation activation.

## October 22 dependency and registry diagnostics

CI for `8194f0c` discovered newly published stable OkHttp/MockWebServer 5.5.1 after local
lint had passed against earlier repository metadata. Both exact pins are updated together.
The upstream tag and changelog identify a certificate-blocklist fix. A full fresh-dependency
Android invocation passes all 537 unit tests and zero-findings lint; the unchanged AGP
exception is the only accepted diagnostic. Immutable revalidation of this pin is pending.

The same revision's Intel macOS frontend audit failed at npm's retiring Quick Audit
endpoint; no clean audit result was assumed. An independent fresh audit of the unchanged
lockfile returned zero vulnerabilities. Retrying the failed CI job then produced a clean
four-target Desktop run. The original failure remains recorded; its preceding bulk-request
failure cause is not independently established. Audit severity, dependencies and checks
were not relaxed. Independent reports verify 1,052 native tests (263 per target) and all
four Desktop builds on `8194f0c`. No application binaries were included in these reports.

API 29/35 passed on that revision, but API 37 never reached MPA tests. The SDK CLI printed
`No url ... Ignoring` for pinned system image revision 6 while returning success. The
emulator reported a broken system path; cleanup then blocked in device-waiting logcat
until the 35-minute job cancellation. The full logs and small artifact remain preserved.
This is neither an application-test pass nor an identified MPA crash/ANR.

Google's current primary repository lists stable API 37.0/google_apis/x86_64 revision 8
(channel 0); that exact image pin replaces 6. SDK setup now verifies every requested
installed package's exact identity/revision, stable status and required nonempty payload
files and archives a metadata-hash inventory. A skipped package fails before compilation
or emulator startup. Six SDK verification tests pass and run in CI. Emulator startup also
checks image files; boot probes and failure diagnostics have bounded ADB waits. Existing
test assertions, native counts and acceptance requirements are unchanged. Actual new-image
CI execution remains pending.

## Captured coverage and scan-selection validation (2026-10-22)

The trusted push checks on `63b19e3` passed Android build/unit/lint, the four-target
Protocol/Core/Google/AppServices/LAN/Web matrix, and the four-target Desktop matrix.
Independent downloaded reports verify 270 native tests per target (1,080 total),
complete clean logs, 10,000/100,000-item fixtures and agreeing reported web inventories.
Application binaries are absent from those report artifacts; their binary hashes are
not independently verified. Independently downloaded API 29/35/37 reports and complete
runtime invocation records verify 159 passing tests per API (477 total). The captured
API 37 inventory verifies the stable revision-8 system image. API 29's actual initial
heap is 576 MiB; the conditional repair branch was not exercised. This establishes a
passing device-test matrix, not a completed runtime-warning review or S23 acceptance.

`CatalogFactsSnapshot.ReadCoverageSources` captures source coverage using the same
owned read-only WAL transaction as portable photo facts. It reads at most 256 source
bindings, then streams selected scan stages without buffering library-sized journals.
No writable discovery repository or network operation is constructed inside the snapshot.
Source selection, policy, account availability, immutable scope, recipes, item identities,
bounded checkpoints and accepted completion receipts are validated. Missing/corrupt work
fails explicitly instead of claiming empty or complete coverage. Shared journal decoding
retains the existing discovery semantics with an explicit 32-KiB input byte bound.

Current failed stages determine outstanding failures; historical folder failure counters
cannot keep an accepted repair permanently failed. Counts describe processing observations
and steps, not unique photos. Incomplete Drive searches remain partial. Disabled,
disconnected, obsolete-scope, pending, scanning, access-lost, offline, partial and complete
states retain their distinct meanings. A framed journal SHA records observation provenance;
raw pagination tokens, error messages and local paths are not exported. Facts/coverage stay
stable while live imports, retries, account changes and new scan selections continue.

Twenty-one focused snapshot tests pass locally, including failed-only repair, terminal
receipts, cancellation/disposal, policy/account changes, missing journals and the source
bound. The complete local Core suite passes 133 tests, AppServices passes 47 and Web passes 24;
Desktop compilation reports zero warnings/errors. This read-only export is not
an approved configuration, signed generation, publication, staging activation or release.

The matching C#/Kotlin covered traversal now verifies complete coverage before admitting
photo facts. Coverage pages share the complete tree's role/budget ledger; a required
configuration-admission callback receives the immutable ordered source policies before
any photo-tree read. Every leaf checks its memberships against that captured map.
An empty catalog still requires configuration admission; rejection, cancellation and late
failures remain explicit. The older facts-only traversal retains its isolated validation
semantics. Source enrollment, actual approved policy comparison, ancestry and transactional
activation remain mandatory integration work. The shared fixture has 22 invalid graphs.
All 52 C# Protocol tests and all 542 Android unit tests across 102 suites pass locally;
lint contains zero findings and Desktop compilation reports zero warnings/errors.
The unchanged gate accepts only the exact existing AGP deprecation. The first local Android
invocation lacked ANDROID_HOME and stopped before compilation; the corrected explicit-SDK
invocation produced the reported clean result.

The `63b19e3` runtime review remains open. One MPA-process resource-release warning occurs
on each of API 35 and 37 without an allocation stack; framework ownership has not been
established for these exact records. API 29's close warning belongs to com.android.phone.
API 37 also reports a Google Play services ANR and resource warnings in other system apps.
The bundled face detector prints its tensor-137 diagnostic on each API. Old version-specific
exceptions are not treated as approval for 0.6, and passing tests do not erase these records.

The instrumentation-only `MpaDiagnosticTestRunner` enables the supported
[`detectLeakedClosableObjects`](https://developer.android.com/reference/android/os/StrictMode.VmPolicy.Builder#detectLeakedClosableObjects())
VM policy before constructing the application, preserving existing VM checks and logging
violations through teardown. No hidden-API exemption or production hook is added.
The runtime-capture gate requires an early matching allocation-tracing marker for every
actual test process, including later isolated stages; missing, late and wrong-process
markers fail. Nine verifier tests pass. The test APK builds, zero-findings lint passes,
and independent aapt2 inspection confirms the actual runner/target package. Native traced
execution and allocation ownership conclusions remain pending. Explicit legacy inspection
mode applies only to archived pre-runner evidence; the CI entry point always requires tracing.

## Enrolled source-policy comparison (2026-10-22)

Matching C#/Kotlin `CatalogSourceConfiguration` readers accept only the selected library,
workspace and verified enrolled-controller document kind/capability. Bounded immutable
account/source lists require explicit account association and unique canonical bindings.
The coverage-admission callback can now compare every captured source against those exact
approved identities, revisions and enabled/recursion flags. Self-consistent coverage cannot
add, retarget or enable a source. Connectivity/credential revisions remain observed state;
this source policy does not grant recognition consent or original-mutation authority.
Actual enrollment, durable authoritative configuration selection, operation admission and
activation remain separate integration work. The independent fixture contains 30 rejected
documents, nine coverage-policy conflicts and empty/maximum sets; tests also verify an
enrolled signature and the actual combined traversal callback. All 58 C# Protocol tests,
548 Android unit tests and zero-findings lint pass locally. Release-mode Desktop compilation
also reports zero warnings/errors.

The complete `a7862d0` trusted push matrix passed. Independent native/Desktop reports verify
1,184 tests (296 per target), clean complete logs and agreeing reported web inventories.
Application binary hashes remain outside those report artifacts. Its device job passed
API 29/35/37; independent artifact/runtime verification and its warning review are pending.

## Windows desktop source-build entry points

The owner's requested root `build_desktop.bat` runs the locked frontend install/build,
dependency audit, locked .NET restore and Release build beneath the pinned SDK directory.
`build_and_run_desktop.bat` launches only after build success and forwards native arguments.
Neither changes firewall/security settings or selects a private acceptance profile.
The real build passes with zero warnings/errors from outside the repository. The launch
helper forwards `--status` and a profile path containing spaces to the real application;
an intentionally unconfigured profile returns the expected unavailable status/exit 3
without starting a listener. Both helpers propagate an injected npm failure as exit 23
and skip launching, including when the scripts' own path contains spaces. This validates
development helpers, not GUI/install acceptance. Batch line endings are explicit in Git.

## Immutable workspace-creation transport

The existing Google account owner now implements a separate publication interface with
single-ID allocation and bounded create-only metadata upload. It does not extend the
read-only connection interface or construct a second credential owner. The caller must
authorize and journal the chosen workspace/ID/payload before creation; retries never
allocate a replacement or update a conflicting existing file. Exact downloaded hash,
length, actual parent/type, before/after object revision and final workspace checks are
required after both success and conflict. Caller memory is copied before asynchronous
work. Cancellation/disposal joins the actual response. There is no original-upload,
update or delete entry point here, and no live publishing scheduler was enabled.

All 40 Google Drive tests pass locally, retaining the preceding 31 tests plus nine
publication tests for allocation, captured bytes, conflict, uncertain replies, scope/type/
hash failures, permissions, canceled/bad inputs, malformed IDs and actual response joins.
All 47 AppServices and 24 Web regressions pass; the strict Desktop Release build has
zero warnings and errors. Non-secret role/digest discovery hints use Drive properties
visible across authorized applications, without granting file access. They are not
trusted enrollment, identity or authorization evidence.
These are synthetic HTTP tests; actual Drive publication, durable outbox/enrollment,
generation activation and release acceptance remain pending.

## Durable immutable-object publication and account integration

Processor schema 7 adds a bounded metadata outbox, preserving the schema-6 state,
installation/library identities, source bindings, journals and local originals. Android
Room remains schema 12. Older migration fixtures explicitly remove the new table when
constructing historical databases; their original data-preservation assertions remain.

The journal captures library/actor/actor epoch/generation, account and credential revision, workspace,
configuration revision, authority digest, role, MIME and owned payload bytes. A canonical
context hash binds immutable fields to the payload hash. ID assignment precedes create
and cannot replace another allocated ID, alias a different operation in the same account
or retarget old work. Verification receipts match exact account/folder/file/hash/length.
Pending enumeration returns bounded IDs; payloads are loaded one at a time. Storage
reservations, SQLite failures and malformed records remain explicit, preserving retries.
Completed rows remain retained; no garbage collection or original deletion is added.

The uploader borrows the existing engine account lease, requires an explicit authority
validator and rechecks current account/configuration before uploads and receipt admission.
Lost replies retain allocated IDs. Concurrent callers re-read beneath that account lease
and reuse completed work. Late cancellation, disconnect, revoked authority or unexpected
transport bytes cannot confirm completion. Shutdown and credential cleanup join the
actual response. A retained receipt records an earlier verified upload, not current
remote availability or authority to activate a generation.

These components do not yet enable a web route, publishing scheduler, enrollment or
catalog-head activation. Authority is independently supplied by the future enrollment
owner; neither the journal nor Drive discovery properties create trust. Local checks
pass 141 Core, 53 AppServices and 24 Web tests, with zero Desktop Release warnings/errors.
The final receipt-fence review additionally passes all eight focused journal tests and
all 53 AppServices tests. Immutable cross-platform validation remains required.

## Shared signed-file envelope

C# and Kotlin now package the same canonical document and detached ES256 signature in
one bounded immutable Drive file. The total 256 KiB limit includes signature/envelope
overhead. Readers require a separately enrolled actor and verify its library, actor,
epoch and allowed document kind. Self-asserted keys, unsigned files, duplicate or extra
fields, noncanonical encodings and changed payloads cannot authorize a document.

The shared fixture uses an independent Python cryptography P-256 signature and retains
only synthetic public material. It includes 15 invalid/unsupported envelopes; matching
tests also check wrong enrolled scopes and the total-file boundary. The first C# boundary
test setup exceeded the existing per-string limit before exercising the envelope. The
corrected fixture distributes its padding across eight bounded strings without relaxing
any limit. All 62 C# Protocol and 552 Android unit tests pass; Android lint has zero
findings. Both initial fixture-setup failures remain in the local diagnostic history.
No enrollment, live publisher, background route or catalog activation is enabled.

## Controller-approved publisher policies

Matching C#/Kotlin readers verify a signed publisher policy against a separately enrolled
controller. The selected library/workspace cannot be changed by the received file. A
publisher has a separate actor, key and epoch and can sign catalog commits only; it cannot
use that role to authorize human operations or changes to originals. Explicit revocation
returns no publisher. The owner still must persist current policy selection, reject forks
and prevent replay of an earlier active policy; pure decoding cannot choose current state.

Independent synthetic controller/publisher signatures cover active approval, revocation,
a catalog genesis commit, and a denied user operation. Shared vectors include 26 signed
invalid/overbroad policies. All 66 C# Protocol and 556 Android unit tests pass; lint has
zero findings and Desktop Release compilation has zero warnings/errors. No live enrollment
or publishing is enabled.

## Durable local controller and publisher selection

The local authority repository records one independently approved controller/workspace
public-key pin, then verifies and atomically retains signed publisher-policy history.
Bootstrap is reserved for trusted setup after native private-key persistence; received
Drive files cannot bootstrap or replace that root. A missing/corrupt root, policy hash,
history record or selected sequence fails explicitly. Selection must equal the highest
accepted publisher sequence, so rewinding a pointer cannot reactivate a revoked key.
Delayed/repeated earlier policies return the current selection; conflicting bytes at the
same sequence preserve agreed state and report a conflict. Duplicate accepted replays do
not require a new storage reservation.

All 149 Core, 53 AppServices and 24 Web tests pass locally, including eight new tests for
restart, revocation, delayed/repeated policies, root replacement, corrupt/missing state,
low storage and failed SQLite writes. Desktop Release compilation has zero warnings/errors.
Processor schema remains 7; the public authority records use existing local metadata.
Originals, jobs and human captions remain preserved. Native-key ownership, bootstrap UI,
source-policy selection, cross-kind controller counters and generation activation remain
pending; this repository does not enable publishing.

If a newly observed revocation cannot be persisted, the repository throws and retains
last agreed state. The generation/authority owner must halt publishing until that pending
policy is reconciled and saved; it must not continue using an older policy after such an
error. This owner integration remains required before live publishing is enabled.

The signed-file revision's first macOS ARM build failed because npm's fallback audit
endpoint returned HTTP 400. The unchanged lockfile independently audited with zero
vulnerabilities, and the failed/canceled jobs passed on one retry at the same application
revision. Original failure evidence is retained; no audit check or dependency was changed.

## Native library signing-key ownership

A dedicated owner now keeps separate controller and publisher P-256 keys and actor IDs
in the existing native credential vault. Public IDs/SPKI/key fingerprints are exported;
private material uses a bounded binary record, with transient PKCS8/vault buffers zeroed
after use. New keys become available only after the real vault write and exact read-back
complete. Existing records retain actors and epochs across restart. Missing pinned keys,
wrong pins, malformed records or another library/installation fail without replacing or
deleting saved keys. Role checks bind signatures to the owner's library/actor/epoch;
publisher keys cannot sign controller or user-operation documents.

Initialization, cancellation and Exit join the actual vault operation, including a store
that ignores cancellation. A failure after persistence is treated as uncertain; restart
reads the saved identity rather than minting a replacement. Nine new tests include
blocked cancellation delivery, unavailable read-back, missing pinned keys, uncertain
writes, role isolation and secret-buffer cleanup. All 62 AppServices and 24 Web tests
pass; strict Desktop Release compilation has zero warnings/errors. The vault exchanges
in these tests are synthetic; actual setup/native-store and cross-platform acceptance
remain required. No production bootstrap route or publisher scheduler is enabled.

The setup owner must independently approve/pin the public controller and bind the chosen
publisher policy. Signing callers still must reserve durable sequences, reconcile cloud
history after restore and journal exact signed envelopes before publication; this private
key owner does not authorize a counter reset or activation of a generation.

## Screenshot completion repair after API 37 evidence rejection

Authority revision `41e2fef` passed all 159 API 37 test assertions and complete runtime
event/tracing verification, but correctly failed screenshot acceptance. One Takeout RTL
PNG had four complete IDAT chunks and no IEND. Its 32,877-byte device file and both host
copies had identical hashes, establishing that transport did not introduce truncation.
The original encoder/provider cause is not proven; all original failure evidence remains.

The test helper now encodes the single captured bitmap fully in bounded memory before
creating its MediaStore artifact, checks PNG completion, writes a pending row through one
owned descriptor, flushes/syncs/closes it, reads back exact bytes and then publishes it.
It neither captures a replacement UI state nor retries assertions. Existing UI assertions
and the full independent PNG/CRC/pixel-stream verifier remain unchanged. Local test-APK
compilation and lint pass with zero findings; all eight existing Python PNG/transport tests
pass. Their first local invocation was blocked by sandbox temporary-directory permissions;
using a task-owned workspace temp directory passes without changing test code. Full native
matrix validation of the repaired capture path remains required before acceptance.

## Canonical Unicode envelopes and durable signing intents

Revision `d5114c3` subsequently passed all four push workflows, including the complete
device matrix. Independent artifact verification is in progress; a green workflow is
not a claim that every runtime warning has been resolved.

Both envelope implementations now preserve canonical body bytes during assembly and
extraction. A shared independently signed Unicode fixture reproduces intermediate
serializer expansion that rejected valid bounded wire data. Local validation passes
67 Protocol tests, 557 Android unit tests and zero-findings lint without increasing any
wire/string/nesting limit.

Processor schema 8 adds signing actors and captured document intents; Android Room
remains 12. Fresh independently approved controller bootstrap registers its separate
publisher in the same transaction. The journal reserves canonical body/identity/sequence
before native signing and seals exact signature bytes before Drive allocation. Replays
retain their sequence and envelope; changed bytes, missing counters, corrupt history or
unknown keys fail instead of resetting or replacing identities. Controller document
kinds share one counter while the publisher has a separate stream. Low-space and SQL
failures roll back both document and counter. All 160 local Core tests pass, including
11 new restart, role, corruption and rollback cases. The first focused run exposed an
absent-versus-empty nullable buffer defect; the fix retains the original assertions.

This journal does not activate publication. The generation owner still must reconcile
cloud high-water marks and restored state, ensure current approved configuration and
publisher policy (including pending revocations), verify dependencies/predecessors,
serialize catalog heads and join real Drive work. Existing schema-7 trust records with
no signing history cannot silently acquire reset counters. No live setup or publisher
route is enabled by this increment.

The native signing boundary now combines the borrowed key owner and serial journal.
It requires an already pinned matching controller, reserves before signing and verifies
retained sealed replays against the actual native public identity. Concurrent requests
for one intent retain its first envelope; cancellation after reservation leaves the
same unsealed intent available for retry. Six integration tests exercise real P-256
signatures with a synthetic vault, including uncertain seals and restart. All 68
AppServices and 24 Web tests pass; strict Desktop Release compilation has zero warnings
and errors. Scheduling, cloud reconciliation and independent setup approval remain
separate responsibilities.

Downloaded `d5114c3` artifacts independently verify 1,400 native tests (350 per desktop
target), four clean Desktop build/audit reports and all 477 device tests with complete
traced runtime capture and valid PNG evidence. Artifacts contain reports rather than
release binaries; no independent binary-signature claim is made. Revision `0bcb814`
also passed all four push workflows; independent artifact inspection remains pending.

## Host-owned workspace setup

The desktop runtime now owns the controller/publisher key owner alongside its existing
account/vault owners. Startup verifies protected persistence; shutdown joins browser
requests before retiring keys and vault. Host-only authenticated/CSRF-protected endpoints
provide read-only folder review and explicit local workspace approval. The React Settings
screen reuses the bounded Drive picker, explains source association and shared-folder
privacy, and keeps approval distinct from the pending first shared catalog.

Approval rechecks actual folder identity, read/write capability, Drive revision, native
controller fingerprint, connected account revision and selected-source revision before
atomic pin/counter bootstrap. Source folders cannot become their own metadata workspace.
Canonical My Drive/shared-drive root IDs are rejected as well as the root alias. The
picker also disables root selection. Shared-drive items correctly require shared-folder
approval when Google's documented `shared` field is omitted; missing My Drive sharing
state is not guessed private. See the [Drive files reference](https://developers.google.com/workspace/drive/api/reference/rest/v3/files).

Local checks pass all 45 Google, 74 AppServices and 25 Web tests, TypeScript compilation,
zero-vulnerability web audit and strict Desktop Release compilation with zero warnings
or errors. Tests retain assertions for canonical roots, permission denial, shared-folder
acknowledgment, source/account races, cancellation, host-only access and operational-error
recovery. An initial test fixture used the wrong protected credential format; it now uses
the established credential-store fixture. A root-selection exception was initially caught
as malformed data; the corrected operational type preserves the explicit recoverable
error. No assertion or warning check was weakened.

Chrome review against the authorized isolated Windows profile confirms native startup,
real Drive browsing, rejection of a selected photo source, and disabled root selection.
The final valid-folder review also passes against real Drive, with approval disabled
until explicit source association and no workspace approved during the review.
No Drive file was changed or new workspace approved during these checks. Complete cloud
library discovery/enrollment, managed-workspace traversal exclusion, signed publication,
restore/high-water reconciliation and phone activation remain mandatory before enabling
the first shared catalog. This local setup screen is not the completed joint release.

Fresh local approval additionally requires a positively verified empty workspace. A
one-item bounded child query checks actual membership; nonempty, incomplete or continued
listings are never guessed empty. Malformed results fail explicitly. Existing approved
library replays remain valid after publication makes their workspace nonempty. Unit
coverage now passes all 46 Google, 75 AppServices and 25 Web tests; frontend and strict
Desktop compilation pass. A no-write native acceptance run against the exact authorized
synthetic source also verifies actual permission/revision/contents inspection and the
existing hash/size/parent/original-MIME rejection checks. All owners joined, no listener
started, no original was downloaded, and no Drive file changed. This local diagnostic is
provisional until tied to the final immutable release revision. Existing-library discovery
and source-consent persistence remain prerequisites for complete setup/publication.

## Durable initial source association

Explicit workspace setup now captures the original reviewed source IDs, account/folder
bindings, flags, revisions and shared-metadata acknowledgment in the same transaction as
controller/actor bootstrap. Its canonical snapshot and digest are immutable and survive
restart. Display labels and credential-connected state are excluded: reconnecting an
account neither grants new source permission nor invalidates that existing association.
Missing approval is not inferred from a pinned key or connected account. A source change
requires its separately reviewed later policy; setup replay cannot replace initial consent.

Readers validate canonical bytes, digest, controller/workspace scope, identities, ordering
and bounds. Missing/corrupt proof or a lost controller fails rather than recapturing current
settings. A storage reservation remains owned through actual commit. A failed source-proof
checkpoint rolls back root, actors and approval together. Processor schema remains 8 and
Android Room 12; no source or original is modified.

All 166 Core, 75 AppServices and 25 Web tests pass; strict Desktop Release compilation
has zero warnings/errors. All 14 authority cases also pass after the reservation-lifetime
review. These checks include restart, corruption, atomic rollback, low-storage replay,
source/privacy changes and preservation across disconnect. The private initial snapshot
is not a remote signature, recognition consent or permission to mutate originals. Signed
source-policy publication, subsequent reviewed operations, cloud reconciliation and
managed-workspace traversal exclusion still must be connected before catalog activation.

## Managed workspace discovery boundary

Source admission now verifies actual parent identities through its captured account and
rejects the pinned metadata workspace or its descendants. IDs, not names, define the
boundary across accounts. Ancestor cycles, changed identities and exceeded bounds fail
explicitly. Network reads remain outside state transactions, are joined on cancellation,
and a changed workspace requires fresh source admission before committing.

Discovery filters that exact workspace from ordinary pages and failed-item repairs.
Already queued work is checked before reading and again after the real read returns,
so concurrent setup cannot insert generated previews into the photo catalog. Exclusion
has its own durable receipt. If the reserved folder already has photo observations,
processing fails with those facts preserved for reconciliation rather than calling the
folder empty or deleting earlier originals. Unrelated failed originals remain retryable.

Local validation passes 169 Core, 84 AppServices and 25 Web tests, plus all three ancestry
cases after a test-fixture-only bound adjustment. Strict Desktop Release compilation has
zero warnings/errors. Processor schema 8 and Android Room 12 are unchanged. Push checks
for the preceding initial-source-association revision `453f4ec` all passed; intentionally
skipped same-repository pull-request duplicates are not counted as validation. Cloud
enrollment, reviewed later source policies, publication snapshots and high-water/restore
reconciliation remain prerequisites for the first shared catalog and completed release.

## Signed source policy acceptance and initial signing

Source configuration now has an exact signed-envelope journal and selected pointer under
the independently pinned controller. Restart verifies retained bytes, signature, scope
and sequence. Delayed records retain the selected newer configuration; same-sequence
equivocation, revision rollback and conflicting same-revision updates are rejected.
Publisher and source documents cannot reuse a controller sequence. A lost controller or
damaged source pointer/hash is an explicit invariant failure, not fresh enrollment.

Initial outgoing source signing reads the original approval and checks current settings
under the serial state owner before reserving or replaying its signing intent. Changed
sources cannot silently become the first approved policy. Account disconnect preserves
the proof and exact sealed envelope; it does not grant Drive write access. Source-policy
acceptance itself neither edits local source bindings nor uploads/activates a catalog.
Later human-reviewed policy changes and cloud reconciliation remain separate work.

All 177 Core, 86 AppServices and 25 Web tests pass, including eight new authority cases
and two initial-signing cases. Strict Desktop Release compilation has zero warnings and
errors. Low-storage admission, failed envelope/hash and failed selection-pointer writes
preserve the prior selection and roll back partial history. The first focused test had
an incorrect expectation for an unsupported capability; the corrected exact exception
assertion preserves rejection. Two strict compiler/analyzer findings in new test code
were fixed without suppressions. Processor schema remains 8 and Android Room 12.

Before catalog publication, the coverage reader still needs explicit handling and
regression coverage for intentional metadata-workspace exclusion receipts from the same
WAL snapshot. No catalog publication or activation route is enabled by this increment.

## Workspace exclusions in captured coverage

The coverage reader now recognizes intentional exclusion receipts against the pinned
controller captured in its own WAL transaction. It cannot consult newer writable state
to reinterpret an earlier observation. Common root decoding verifies the same canonical
pin, digest, library and public-key identity. Lost/corrupt authority fails explicitly.

Excluded repairs do not add a photo observation. Receipt source/folder/reason must match
their captured context and the actual reserved workspace. A folder repair additionally
requires its zero-observation parent checkpoint. Previously completed workspace reads
that observed originals block publication with those facts preserved for reconciliation.

All 186 Core, 86 AppServices and 25 Web tests pass, including nine new coverage cases
and end-to-end coverage assertions in four existing discovery/repair tests. Earlier WAL
observations remain stable while setup and work finish. Altered receipts, missing pins,
corrupt hashes and overlap with original observations remain blocking. Strict Desktop
Release compilation has zero warnings/errors. An initial new fixture used a nonexistent
claim method; it was corrected to the established `ClaimNext` API without changing the
assertions. Processor schema 8 and Android Room 12 remain unchanged.

## Frozen catalog preparation and joined lifecycle

Catalog preparation now copies the selected facts and coverage from one live WAL
observation into a private bounded-cache SQLite file, then releases that observation
before its awaited consumer begins compression or network work. Canonical per-photo
facts retain identity and a checked digest. Paging stays bounded at 250 records; all
identities, membership, dates and coverage remain fixed during later source/import changes.
No original content or protected credentials enter the generated copy.

A dedicated marked/leased directory permits two private captures, verifies its complete
bounded recovery inventory before removing stale files, and refuses unmarked directories,
links or unknown files. Storage is reserved conservatively before writing and retained
until the generated file actually closes and is removed. Cancellation, low storage,
corruption and cleanup uncertainty have explicit failure paths and retained owners.

The engine-lifetime preparation service serializes captures and awaited consumers.
Desktop shutdown cancels and joins it before account/vault/state release. Its tests keep
an actual consumer running after cancellation and verify that files remain owned until
the consumer joins. Other cases prove real WAL release, subsequent captures after source
changes, safe recovery, corruption rejection and idempotent cleanup. Publication scheduling,
authority/high-water reconciliation and activation remain separate prerequisites; no
new publication route is exposed by this increment.

Local validation passes 197 Core, 90 AppServices and 25 Web tests and strict Desktop
Release compilation with zero warnings/errors. Compiler/analyzer findings in new test
code and explicit throws inside cleanup clauses were resolved without suppressions;
cleanup preserves primary failures and independently retains unclosed resources. The
initial corruption test exposed inconsistent exception classification; the implementation
now reports invalid stored facts explicitly while preserving rejection assertions.

The 10,000/100,000-photo fixtures measured approximately 665/14,298 ms to capture,
12/11 ms to read the first page, and 8/26 ms for an import write interleaved with capture.
Their generated metadata files were 6,385,664/63,819,776 bytes. Cumulative managed allocation
was approximately 187 MB/1.87 GB; after-GC retained managed memory increased by 10,552 bytes
in each shared-test-process observation. These loaded-host measurements establish private
metadata preparation behavior. Native UI, compression/network responsiveness and dedicated
process measurements remain part of final acceptance. Processor schema 8 and Room 12 are
unchanged. All three preceding `5c7c13e` device jobs report success; GitHub's run-level
status was still stale/in-progress at the last observation and is not counted as a
completed passing workflow.

## Bounded dependency preparation

The frozen catalog now supplies indexed scalar partition bounds before materializing a
candidate chunk. The partitioner emits at most 1,000 records per chunk, applies the
consumer's 16 MiB expanded/4 MiB compressed limits and splits dense partitions through
the existing four byte-prefix levels. A capped pooled compression buffer includes gzip
framing in the bound. Exhausted depth or object budget fails without truncating photos.

Canonical index and coverage writers feed a generation dependency builder. It compares
every captured source binding and revision with the accepted signed configuration before
any sink call, validates coverage pages against their captured root, independently
decodes generated compressed facts and checks each source membership. Nested indexes
reference exact captured metadata objects; scope, digest/size, payload mutation, duplicate
role IDs and predecessor aliasing are rejected. Cancellation joins actual sink work.

The builder returns dependency references only. It creates no signed commit, library
discovery pointer or active generation. Its production sink still must persist exact
outbox scope/bytes and verify actual Drive ownership under current authority. High-water,
restore, current revocation/source policy and predecessor admission remain mandatory
before enabling live publication or phone activation.

All 104 AppServices and 25 Web tests pass, plus strict Desktop Release compilation with
zero warnings/errors. Fourteen new cases cover count/compression splits, exact decoder
round trips, empty roots, exhausted capacity/cancellation, reference rejection and full
nested graphs. The complete traversal case uses 1,001 photos forced into one initial
partition and 18 sources across two coverage pages. An isolated signed traversal fixture
does not assert production publisher authorization. Initial fixture capability ordering
and strict analyzer findings were fixed without weakening checks. Core's preceding full
197-test run passed; the new scalar partition query is exercised by these service cases.
Immutable validation for the complete increment is pending; schemas remain unchanged.

## API 29 pre-test identity diagnostic

The `e46e4b0` Desktop, Protocol and Android build workflows passed. Its device matrix
stopped before MPA installation/instrumentation: the API 29 heap checker rejected the
unrecorded `pidof zygote64` response. Fail-fast canceled the other two device jobs. The
downloaded diagnostic log shows zygote PID 1687 starting with `-Xzygote` and `-Xmx576m`.
It does not establish the raw identity response or the exact reason for its rejection.
The expected AGP deprecation was separately accepted by its unchanged exact warning gate.

The checker now saves raw named-process IDs, the PID/PPID/name inventory and complete
runtime log before identity admission. It requires exactly one init-owned (`PPID=1`)
64-bit zygote also present in the current named inventory, then checks that actual PID's
complete startup arguments. Non-primary processes sharing a transient name do not define
the runtime heap. Missing/ambiguous/malformed evidence still fails. The existing physical
device guard, configured heap, known-mismatch-only restart and app assertions are intact.

All nine Python checker tests pass, including the six existing cases. Actual emulator
validation of this change is pending. This is no exception or passing native-test claim;
the final immutable revision still requires the complete API 29/35/37 acceptance matrix.

## Current local publication-policy fence

A read-only transaction now captures the pinned controller, current signed publisher and
source policies, exact local source settings and connected workspace account revision.
Its canonical authority digest includes both selected signed-policy hashes/sequences and
actor/key/workspace/configuration identities. Assertions reject a captured scope after
revocation, source changes, account disconnect/reconnect, altered identities or corruption.
Reconnection changes the required access revision while preserving human source authority.

All 202 Core, 104 AppServices and 25 Web tests pass; strict Desktop Release compilation
has zero warnings/errors. Five new authority cases preserve rejected/corrupt state and
prove that the read-only fence works without a writable storage budget. Processor schema
8 and Room 12 remain unchanged.

This is only the durable local policy check. It does not establish native publisher-key
possession, a completed cloud reconciliation/high-water proof, an observation barrier for
policies that could not be persisted, a verified predecessor or exclusive head admission.
It cannot be used as a stand-alone production publication authority. Those prerequisites
and the immutable pending-upload access-reconciliation path must be connected before
enabling live generation publication, scheduling or phone activation.

## Bounded read-only workspace document discovery

The existing protected Google connection now lists ordinary JSON candidates in a captured
workspace through bounded 100-item/512 KiB pages, retaining continuations and incomplete
state. It does not use names or `mpaRole` properties as authority. Candidates require
actual parent membership, downloadable JSON type, canonical version/size and the separate
256 KiB signed-document bound. Reading one candidate rechecks its captured version/type,
exact byte length and parent/revision before and after the actual joined download.
Malformed, duplicate, moved, trashed or denied candidates remain explicit errors.

Candidate bytes remain untrusted: signature/library verification, complete change-cursor
reconciliation and high-water admission are still required. These read operations mutate
no source and confer no enrollment/publication authority. The shared workspace property
parser retains existing original/shortcut/Google-Doc rejection for immutable reads and
create-only publication verification.

All 53 Google, 104 AppServices and 25 Web tests pass; strict Desktop Release and native
acceptance-probe compilation have zero warnings/errors. Seven new Google cases cover
bounded inventory, real scope/revision checks, stale/malformed/denied metadata, input
admission and shutdown joining an actual held inventory read. An initial target-typed
return could not infer its async generic; its explicit page type fixes compilation.

A native no-write probe against the already authorized exact 92-byte synthetic fixture
also verifies actual inventory, candidate transport, stale-version rejection and existing
hash/size/parent/original-MIME rejection. All protected owners joined. No listener, new
consent, original download or Drive write occurred. This dirty-tree local diagnostic is
not final immutable release acceptance or signature/enrollment proof. Schema 8 and Room
12 remain unchanged.

The `b728101` native device workflow passed all three APIs. Downloaded API 29 evidence
records one init-owned zygote PID 1687 matching its named inventory and actual startup
`-Xmx576m`, with no framework restart. It does not recover the earlier `e46e4b0` raw
identity response or prove that earlier failure's exact cause. Independent full-artifact
inspection and final-revision acceptance remain separate requirements.

## Account shutdown result-admission race

The f2e4444 protocol matrix exposed a real ownership race on macOS Intel: the held
workspace inventory request could finish after DisposeAsync began but before asynchronous
linked cancellation reached that request. The unchanged shutdown regression expected
cancellation and failed (52/53 Google cases passed); this is not an accepted diagnostic.
GoogleAccountConnection now admits completed results under the same gate that starts
shutdown, checks cancellation there, and still joins the actual operation before disposing
its native owners. Once stopping wins that gate, a late transport success is canceled.
The existing inventory and change-feed shutdown assertions remain intact.

Local strict regression after this fix passes 58 Google, 104 AppServices and 25 Web
tests; Desktop Release has zero warnings/errors. Cross-platform CI must verify the fix.

## Scoped Drive change-feed transport

The existing account-owned connection now supports bounded account/shared-drive change
cursors and explicit partial/end markers. Tokens remain opaque and scoped; invalid or
foreign cursors fail before network work. Changes preserve removals, corpus/access loss,
current parent membership, MIME, version and optional size/downloadability. This is an
account feed, not a folder-filtered or atomic snapshot. An observation never instructs
MPA to delete an original. No cursor is persisted by this transport.

All 58 Google, 104 AppServices and 25 Web tests pass locally; Desktop Release and the
native acceptance probe compile with zero warnings/errors. Five new Google methods test
scoping/encoding, removals, malformed/missing/ambiguous markers, canceled actual request
joining and response disposal. No checks or warning severities were reduced.

The protected-account native no-write probe also obtains an actual start cursor for its
captured account/drive, then repeats the pinned synthetic metadata read and integrity
rejections. It does not enumerate the account's change history or log its opaque token.
All owners join; no listener, new consent, original download or Drive write occurs. This
local dirty-tree diagnostic is not final release, signature or publication acceptance.

Transport semantics follow Google's primary changes/getStartPageToken, changes/list and
changes reference documentation. Complete workspace reconciliation, signed authority,
high-water and durable cursor/observation admission remain separate required steps.

## Change-bracketed workspace inventory and account integration

WorkspaceDocumentInventoryReader captures its account/shared-drive cursor before listing
bounded ordinary JSON candidates, then drains the intervening feed. A relevant changed
ID, new JSON candidate, missing corpus/workspace access, incomplete search, repeated
continuation or unsupported bound rejects this observation explicitly. Both previous
IDs and current folder membership matter, including moved, trashed, removed and
MIME-changed documents. Missing previous IDs are retained separately and never reset
accepted policies or counters. A later change check brackets candidate verification.
Limits are 4,096 candidates/previous IDs and 128 pages per traversal; exceeding them
requires reconciliation rather than truncation. Empty/partial pages never prove authority.

CatalogWorkspaceDocumentSource runs those operations and bounded versioned document
reads through the existing account owner. Captured access revisions are checked before
reads and current connected state again afterward. It rejects late disconnected,
reconnected, canceled or substituted results. ProcessorDriveReaders now serializes
result admission against its own shutdown gate as well, retaining actual joined cleanup.
These classes expose no route, scheduling, signed-head or publication authority. Their
inventories are neither an atomic Drive snapshot nor signed evidence; durable policy,
observation/high-water, native-key and predecessor admission remain required.

All 116 AppServices and 25 Web cases pass, including twelve new inventory/account tests.
Desktop Release and native probe builds have zero warnings/errors. Tests cover known-ID
loss/moves, new policies, scope replacement, inventory/feed loops and bounds, original
non-download, held operations, cancellation, account replacement and credential cleanup.
The first focused compilation found CA1861 allocations in new tests; static fixtures
resolve them without suppression. Existing assertions and strict diagnostics remain.

The protected native no-write probe now brackets the same pinned synthetic workspace
inventory with actual change-feed reads, verifies candidate bytes/version and prior
scope/integrity rejections, then performs a final change check. It logs no account-feed
contents or opaque cursor. All owners join; no listener, new consent, original download
or Drive write occurs. Evidence: workspace-inventory-*.log/TRX, local-results.json and
actual-workspace-inventory-native.json under the private preflight evidence directory.
This dirty-tree diagnostic is not final immutable release acceptance. Schemas 8/12 remain.

## Native possession and retained cloud signing-history fences

The ready native signing owner now compares both controller and publisher public roles,
keys/epochs, library and pinned workspace against records captured from local authority.
Uninitialized/stopped keys, substituted publisher/controller roles or workspace identities
fail explicitly. This check does not mint signatures, write keys or infer cloud authority.

ProtocolSigningJournal can now verify one cloud envelope against an already registered
local actor and require the exact corresponding sealed intent. A valid remote sequence
beyond the complete local journal, or an observed locally unsealed intent, requires
explicit recovery; counters are never advanced, recreated or sealed from Drive alone.
Conflicting bodies or distinct valid signatures for the same body cannot replace the
retained immutable envelope. Invalid signatures, foreign roles and incomplete/corrupt
local histories fail before accepting an observation. This read-only check also works
without free write space and does not change the database schema.

Five new Core cases and two native-owner cases pass. Full strict regression passes
207 Core, 118 AppServices and 25 Web tests; Desktop Release has zero warnings/errors.
The signature variation case deterministically uses the distinct valid ECDSA (r,n-s)
form, verifies it, then requires the outbox's exact original envelope. No random retry,
conditional test skip, warning suppression or automatic history repair is used.

These are components of publication admission, not a complete permission grant. The
owner must reconcile ALL cloud history under the independently pinned current policies,
retain failed-observation barriers, verify predecessor/dependencies and exclusively admit
one head before live scheduling/publication. Empty/subset inventories cannot prove that
work safe. No live publication route is enabled. Processor schema 8 and Room 12 remain.

## Durable dependency outbox adapter

CatalogGenerationWorkspaceSink now connects the bounded dependency builder to the
existing immutable publication journal/uploader and joined account reader. It owns exact
payload bytes before awaiting, reserves a deterministic generation/scope/role/content
identity before Drive allocation, and reuses the retained payload/file identity after a
lost completion reply or a new adapter instance. Dependency roles/MIME, canonical JSON,
wire byte limits and complete captured scope are validated explicitly. This adapter
cannot publish catalog commits/controller policies, mutate an original or activate a head.

A stored upload receipt proves only an earlier write. Every reference returned by this
adapter requires a fresh actual workspace read and byte/hash check, plus current authority
and local access fences afterward. Missing/moved/changed remote objects remain errors;
the adapter never silently recreates a receipt's missing object or returns an old receipt
as current availability. Revocation during readback, late cancellation and changed caller
buffers cannot produce an accepted dependency reference. Original bytes are untouched.

All 125 AppServices tests pass, with seven new adapter cases covering durable pre-create
ordering, exact lost-reply retry, readback on every reuse, deleted/altered remote state,
revocation, role/generation isolation, payload ownership, held cancellation and wire bounds.
The preceding full Core207/Web25 and strict Desktop Release checks pass; Desktop has zero
warnings/errors. Test-only signed/catalog authority remains explicitly synthetic.

No production authority is fabricated and no live route/scheduler is enabled. Full cloud
reconciliation, failed-observation/history barriers, durable exclusive generation/head
ownership, immutable pending-work reauthorization, predecessor validation and signed
commit admission still precede live use. Reconnection must not remint or retarget captured
work. The adapter does not claim those unfinished generation-owner responsibilities.
Processor schema8 and Room12 remain unchanged. Local evidence: catalog-workspace-sink-*
logs/TRX under preflight-060; these are development checks rather than final-release proof.

## Canonical outgoing catalog commit bodies

CatalogPublicationWriter now serializes a bounded canonical commit payload and validates
it against the same existing typed field/role/history rules, without constructing an
unverified object disguised as VerifiedProtocolDocument. Signed incoming admission still
requires its original kind/capability checks. Existing malformed golden assertions remain
unchanged. The outgoing writer additionally rejects the Drive root alias as an object.

CatalogCommitDocumentWriter captures an immutable plan for the journaled signer's bounded
body factory. It checks publisher/library/epoch, exact approved source policy digest and
revision, workspace, prepared dependencies, genesis/parent shape and minimum history.
Canonical body creation precedes reservation; the existing journal reserves the exact
sequence/body before native signing and replays the sealed envelope exactly. A changed
plan cannot reuse the same intent, and invalid context/parent shape cannot burn a counter.
This is serialization, not current authority, remote ancestry or head activation proof.

Full local strict regression passes 69 Protocol, 207 Core, 127 AppServices and 25 Web tests.
Four new methods cover golden byte equivalence, malformed/reference/history cases, actual
journal/native signing and exact replay, scope/configuration mismatch and unburned failed
reservations. The first CA1822 finding was resolved by capturing the plan in an OOD owner;
no suppression/check removal was used. Desktop Release has zero warnings/errors. Evidence
is catalog-commit-writer-*.log/TRX in preflight-060. Processor schema8/Room12 are unchanged.

Pushed 3d72d3a completed Desktop39836408286, Android39836408404, Protocol39836408231 and
Device39836408179 workflows successfully; all three device APIs passed. The Intel native
job row remained stale/in_progress after every step including Complete job succeeded;
its downloaded six reports independently verify all465 tests and both shutdown regressions,
with no log diagnostics. This resolves the actual earlier f2e4444 cancellation-admission
regression without waiving it. The reports do not constitute final0.6 binary acceptance.

Live publication, controller policy delivery, exclusive generation/head ownership,
cloud reconciliation/history/observation persistence and pending-work reauthorization are
still required before wiring these components into production scheduling or activation.
No production permission is inferred from a constructed payload or a stored upload receipt.

## Windows rejected-TLS fixture teardown ordering

The 952c6b1 Protocol workflow failed only in its Windows LAN suite: the rejected-peer
fixture disposed NetworkStream before the canceled server authentication task joined.
Schannel's pending read threw ObjectDisposedException from that local teardown race.
The fixture now cancels and joins authentication before scoped socket disposal; its
wrong-pin/wrong-name rejection and unauthenticated-client assertions remain unchanged.
No exception type was added to the accepted outcomes. All nine local strict LAN tests
pass. Desktop, Android build and all API29/35/37 workflows for952c6b1 passed; cross-platform
Protocol verification of this fixture correction is pending. No warning waiver is used.

## Durable catalog capture and retained snapshot recovery

Processor schema9 adds one exclusive durable catalog capture per library, fixing the
original publication scope, snapshot ID and signing-intent ID before capture. Atomic
migration preserves prior jobs, authority, accounts and signing history; Android Room12
is unchanged. Captures cannot adopt prior uploads or release their claims after signing
or outbox work begins. Frozen files survive Exit and verify their context, coverage,
row count, byte length and whole-file digest before reuse. Interrupted copies remain
claimed; recovery deletes only files proved to belong to abandoned unpublished work,
after validating the entire bounded marked inventory. Unknown files remain untouched.

The retained directory is included in actual storage accounting. Native readers retain
the directory lease until joined; accounting tolerates only a concurrently completed
recognized SQLite temporary journal. No user media path is accepted by this storage.

Local strict evidence: 228 Core tests passed before the last cleanup/accounting cases;
the final affected authority, retention and migration selection passed59 tests. All128
AppServices and25 Web tests passed; Desktop Release has zero warnings/errors. Logs/TRX
are generation-retention-* in preflight-060. Cross-platform verification of this source
is pending. This is capture infrastructure, not complete generation/head scheduling,
cloud reconciliation, renewed-access admission or production publication permission.

The preceding fc39232 Windows TLS teardown correction also passed Protocol39919491932
on all four native targets, Android39919491983 and all three API jobs39919491966. No new
warning exception was required. Final0.6 installers, APK and release remain pending.

## Joined journaled-capture application owner and macOS lease path

CatalogGenerationCaptureService now serializes capture/resumption, fixes identities
through the durable journal, recovers a complete copy after a lost seal checkpoint,
and refuses to overwrite partial files. Only its reopened frozen reader reaches the
awaited consumer. It checks the supplied external authority and current local source/
publisher scope before admission, sealing and use, and after consumption. Exit cancels
and joins consumers and native file work before releasing retained directory owners;
cleanup failures latch the owner closed. This service is not wired to a production
scheduler or route and does not substitute for cloud reconciliation/head admission.

Local strict regression:59 affected Core/migration tests,135 complete AppServices tests,
then8 final lifecycle tests (including a local-policy-change rejection) pass. Desktop
Release has zero warnings/errors. Evidence: generation-owner-*.log/TRX in preflight-060.
Initial analyzer findings were fixed by forwarding cancellation and reusing a constant
account array; no suppression or check reduction was used.

Cross-platform CI for5fde308 found a genuine macOS path-alias defect: SQLite resolved
/var through /private/var, so comparing its returned path rejected the correctly leased
state directory. ProcessorStateDatabase now retains its actual constructor lease path;
the retained store compares against that trusted path, preserving exact-directory and
installation-marker checks. All nine macOS failures shared this cause. The correction
requires a fresh full native matrix; the failed run is not counted as passing.

## Durable workspace observations and native controller policy recovery

Processor schema 10 adds bounded document observations and inventory checkpoints. The
journal retains file identities, versions, byte counts and digests; an immutable file
rewrite, older version, missing known document, stale access or foreign workspace
cannot silently replace that history. Cursor advancement requires the complete exact
observed inventory. The checkpoint is historical evidence, never publication admission.
Migration failure preserves schema 9 and capture/history rows; prior upgrade fixtures now
also remove the schema 10 additions when constructing their genuine older schemas.
Android Room 12 is unchanged.

WorkspaceObservationService performs a full joined scan on every invocation, persists
each actual read before invoking its signature/history/policy verifier, and checks Drive
changes again before committing the cursor. A failed state/policy write or final check
keeps the prior cursor. New policies are still observed before reporting missing older
files. Exit cancels and joins even an uncooperative reader before account/state retirement.
No stored cursor bypasses startup reconciliation and no production scheduler is enabled.

LocalControllerPolicyObserver connects actual native identity and exact sealed signing
history to monotonic source/publisher selection. Valid cloud work beyond the local counter
or with an unsealed local intent requires explicit recovery; it cannot adopt/reset the
counter. Revocation remains selected when an older authorization is replayed. Retired
access and failed policy writes preserve the earlier accepted state. This observer handles
this installation's controller policies only; catalog ancestry and general enrollment are
separate unfinished responsibilities.

The 4096-document inventory digest streams length-framed canonical records, retaining the
shared protocol's 1000-item array rejection. A 32768-byte checkpoint cap supports valid
4096-character opaque cursors including non-ASCII text. Local final checks pass 68 affected
Core/upgrade/migration tests and all 146 AppServices tests, including real native-key and
journaled policy cases. Earlier full Core 236 and Web 25 regressions passed; the final Core
selection adds the two new bound cases and rechecks changed code. Desktop Release reports
zero warnings/errors. Final logs contain no diagnostics. Evidence is workspace-observation-*
in preflight-060. Initial analyzer findings were corrected without suppressions.

Preceding 2fe7a02 completed Protocol 39926369077 on Windows/Linux/ARM+Intel macOS,
Desktop 39926369021, Android 39926369123 and all API 29/35/37 jobs 39926369099 successfully.
Fresh cross-platform validation of schema 10/current observations is required after push.
Full cloud/head reconciliation, exclusive complete generation phases, pending-work access
attempts, production publication/activation, plugins/client workflows/installers and final
joint release acceptance remain pending. No final 0.6 APK, installer or release exists.

## Synchronous catalog retirement and result admission

The 816f36f native matrix found a real result-admission race on Intel macOS:
CatalogPreparationService could return success after Exit began because CancelAsync's
linked-token callback had not propagated before the consumer resumed. The failure was
not waived. Preparation, retained capture and workspace observation now check their
synchronous owner retirement flags before accepting results or dispatching later policy
work, while still joining actual consumers/readers before releasing borrowed resources.
The observation owner also rejects late reads before persisting or invoking a verifier.

Existing cancellation assertions remain unchanged. Both cooperative and deliberately
uncooperative catalog consumers are covered. All 20 local affected lifecycle cases pass;
Web passes 25 cases. The final integrated working-tree suite also passed all 151
AppServices cases and Desktop Release has zero warnings/errors. Evidence is
catalog-retirement-final and initial-policy-services-full logs/TRX in preflight-060.
New immutable-revision cross-platform verification is required. No diagnostic exception,
exception-type relaxation or test removal was introduced.

## Fixed initial controller-policy plan and revocation-safe signing

Initial setup now persists both publisher/source signing-intent identities before any
signature. The plan captures the pinned controller/workspace, registered native publisher
and original human source-approval digest. A lost reply/restart returns that exact plan.
Missing plan state with retained signing work or nonzero counters cannot create a fresh
setup. Partial/corrupt plan writes fail explicitly and source changes preserve the old
plan while blocking signing. The bounded singleton uses the existing authority metadata
store; Processor schema 10 and Android Room 12 are unchanged.

JournaledProtocolSigner can prepare this plan and sign its fixed initial native publisher
authorization. Initial source signing also enforces its fixed intent when a plan exists.
Both paths check current human approval, exact registered native identities and accepted
policy history before counter reservation or sealed replay. A newer publisher revocation
or source policy retires both old setup paths, including when the source payload remains
unchanged. Historical plan inspection never reauthorizes a publisher. Exact sealed-history
restoration/upload remains a separate recovery permission; these signing methods grant no
Drive upload, original-mutation or complete cloud/head admission.

All 69 local authority tests pass, including eight initial-plan cases. All 17 journaled
signer tests pass; the integrated AppServices suite passes 151 tests. The final 20 lifecycle
cases and Web 25 tests pass, and Desktop Release has zero warnings/errors. Logs/TRX are
initial-policy-* and catalog-retirement-final in preflight-060. No warning suppression,
new diagnostic waiver or weakened assertion was introduced. These changes and df7cde0's
Intel macOS shutdown-race fix require fresh native/platform CI before acceptance.

## Immutable initial policy upload context

Initial policy publication now captures the original scope/generation, both object IDs,
fixed signing intents and exact sealed envelope digests before outbox preparation. It
requires the original human source approval, locally selected initial policies and real
sealed signing history in one serial state transaction. A restart returns the same
context; reconnecting retains its old account revision without granting renewed access.
Missing context over existing outbox work cannot allocate a replacement plan. Failed or
corrupt context writes preserve both policies and existing work for explicit recovery.

The signing journal's transaction-aware integrity reader is now internal so this owner
reuses actual envelope/body/signature validation within its transaction rather than
copying a partial SQL check. Historical context inspection remains separate from current
native/cloud admission. Processor schema 10 and Android Room 12 are unchanged.

Local strict coverage passes 98 authority/outbox/signing cases, including five new upload
context cases; all 154 AppServices and 25 Web tests pass. Desktop Release is clean. Fresh
reviewed artifact names avoid the local test runner's prior overwrite-report diagnostic;
no warning was suppressed or accepted. Evidence is initial-outbox-*-reviewed in
preflight-060. One new development test initially compared memory owners; it now verifies
retained IDs/scope/hash and actual payload bytes. Cross-platform checks remain required.

## Native initial policy to outbox preparation

InitialLibraryPublicationPreparation now connects the actual fixed native signing plan to
local signature-checked policy selection, the captured transport plan and both exact
outbox records. It performs no network writes and cannot activate a catalog. Concurrent
or lost-reply retries reuse the same two signing intents/object IDs. A failed second
record resumes only that missing object. Changed account access remains blocked by the
current local scope fence; it does not silently adopt a new revision. Callers still join
preparation before retiring the borrowed native key/state owners.

The create-only outbox now supports the explicit publisher-authorization metadata role.
The facts dependency sink continues to reject publisher/source policies, commits and
originals; its existing role-separation test passes. Actual upload admission remains
closed until the production bootstrap/history owner is implemented. No HTTP route or
scheduler was enabled and no Drive files were written by this increment.

All 21 focused signing/initial-preparation/role-separation tests pass; the reviewed full
AppServices suite passes 154 cases. Reviewed Core authority/outbox/signing coverage passes
98 cases, Web passes 25 and Desktop Release has zero warnings/errors. The preceding
09a6e5a revision passed all four native runners in Protocol39932564342 and all device APIs
in39932564427; Desktop39932564472 also passed. Fresh current-source CI is still required.

The upload authority contract now explicitly states that validation runs inside the
joined account lease. Production admission must reconcile before entering that lease
and validate its run-owned proof/current local state inside it. Re-entering account
network reconciliation from ValidateAsync would deadlock and is not a valid design.

## Joined initial policy publication and current cloud admission

InitialLibraryPublicationService now drives the actual native signer, retained outbox,
account-owned folder inspection, full initial-workspace scan and create-only uploader.
Its private admission capability permits one joined upload and performs only local/native
validation inside the account lease. Actual cloud reconciliation happens outside that
lease before each write and after every receipt. Concurrent retries, lost replies and
interrupted post-write observations retain the original policy/object identities.

Initial scans enumerate every child without a MIME/name/property authority filter.
Unsupported originals, nested folders or other metadata roles stop bootstrap before their
content is downloaded. This strict scan is separate from general JSON document discovery.
Its change-feed fence also includes non-JSON children; ordinary discovery retains its
existing scope. Folder permissions, sharing approval and shared-drive context are checked
again before the final change fence/checkpoint. A verified object that was never observed
before a crash still cannot disappear and be recreated as a fresh workspace.

Actual local-controller observations are persisted and selected before initial-only
classification, so an observed newer revocation closes this owner without restoring old
permission. An unknown copy cannot be adopted even if its sealed bytes match. Complete
outbox enumeration includes verified work and fails rather than returning a truncated
subset. Reconnected access remains blocked without a separate renewed-access proof.

Exit/caller cancellation joins uncooperative uploads and final folder inspections before
borrowed keys/accounts/state may retire. Late results cannot admit receipts/checkpoints.
All 173 AppServices and 60 GoogleDrive transport tests pass locally. New tests exercise
actual account serialization, native keys/signatures, SQLite journals and simulated Drive
storage; they do not claim a live Drive publication or a physical-device result.
Affected Core authority/outbox/signing coverage passes 99 cases, all 25 Web cases pass,
and the Desktop Release build has zero warnings/errors. Full final logs and actual TRX
counters are recorded in preflight-060/initial-owner-validation.json. The initial local
development run exposed a synthetic transport that incorrectly rejected an exact retry;
the fixture now preserves create-only replay semantics and every original assertion.

This owner deliberately handles only the two initial policies. It is not yet connected
to a production route/scheduler and does not publish a generation or activate a catalog.
General history/head reconciliation, reconnect recovery, durable setup scheduling and
the rest of the joint release remain unfinished. Processor schema 10 and Android Room 12
are unchanged. The preceding 285811f revision passed native Protocol39936601289, Android
39936601321, Desktop39936601222 and device39936601180; fresh current-source CI is required.

## Durable initial publication completion

The joined native owner now records an immutable historical completion receipt after
both exact policies were uploaded, read back and included in the final current scan.
One SQLite transaction validates the fixed plan, sealed signing history, original human
source approval, verified outbox records and retained observations/checkpoint before
writing the receipt and its digest. It reuses the actual transaction-aware integrity
readers rather than copying partial SQL checks. Processor schema 10 is unchanged.

Subsequent setup stages can read this receipt after later source policies, revocations,
disconnects or additional catalog work. That is historical evidence, not current cloud
availability, head activation or renewed-access authority. Later observations do not
replace the original receipt. Missing/corrupt completion over later outbox or even
unsealed signing work stops bootstrap reconstruction. Lost approval, signing/outbox or
observation records and checkpoint revision reuse cannot be treated as a new setup.

All 108 affected Core authority/signing/outbox/observation cases pass, including nine
completion cases. Native application coverage also verifies that a failed completion
write retains both uploaded objects and resumes without reuploading them. Low storage
and interrupted receipt/hash writes leave no half-completed stage.

## Engine-owned setup scheduling and web progress

DesktopWebRuntime now owns InitialLibraryPublicationRuntime, resumes durable approved
setup at startup and joins it before retiring accounts, keys, vault or state. A committed
workspace approval queues the job independently of its browser request. Closing the
browser or canceling a progress read cannot cancel or duplicate the admitted work.
Retry joins the previous attempt, including the short interval after an error becomes
visible but before that task completes. Startup uses historical completion only to
advance this initial stage, not to grant current catalog publication authority.

Operational failures retain exact work and show safe actionable progress. Expired Google
authorization, locked credential storage, changed source authority, interrupted scans and
storage failures remain separate from unexpected invariants. An invariant reports to the
engine failure owner and cannot be automatically restarted. Full internal diagnostics
remain outside browser status responses. No original file is modified by setup.

The host-only authenticated API exposes bounded status and a CSRF-protected retry.
Remote browser sessions cannot inspect or mutate this setup. Settings shows actual
running/completed/attention/stopped progress, permits reviewing a pinned workspace that
still lacks explicit source association, and offers read-refresh after a status failure.
It explicitly distinguishes saved Drive setup from the still-pending shared photo catalog.
Progress reads stop on navigation; the engine job continues independently.

Frozen local coverage passes all 184 AppServices and 26 Web cases, 108 affected Core
cases, and four frontend API tests. TypeScript/bundling and Desktop/probe Release builds
pass with zero warnings/errors. The frontend build now runs the API tests in CI. The
read-only HTTP acceptance probe refuses a pinned-controller state before starting an
engine that could publish approved setup automatically. It was built, not executed against
Google Drive in this increment. New UI visual review and live setup publication remain
unverified; no account credentials, real Drive files or physical devices were used here.

## Partial-page history and typed metadata discovery

The actual joined account inventory now reports its start before listing and persists
each valid bounded page before requesting another page or draining changes. A later page
failure, incomplete-search flag or change-fence failure retains earlier candidates. Both
initial and final change fences retain newly seen supported metadata before closing the
scan. No signature callback or download precedes the required durable page writes.

StartInventory retains the earliest unresolved cursor across retries and even completed
listings. Completing a listing cannot erase failed history. Optional null cursor fields
preserve the previous canonical attempt encoding; the private attempt bound accounts for
three separately bounded opaque cursors without changing any signed envelope or array cap.
General history admission still must independently reconcile that retained history.

The Google account transport now provides unfiltered typed metadata discovery. JSON keeps
its 256 KiB limit; gzip and octet-stream candidates use the separate 4 MiB compressed-object
limit. Membership, exact size, MIME, Drive revision and download permission remain checked
before and after reads. Originals, folders, shortcuts and Google documents are rejected
without content download or mutation. Names and properties never select trusted roles.

CatalogWorkspaceDocumentSource supports an explicit allMetadata mode, mutually exclusive
with initial bootstrap. The shared reader and durable observation service retain encoding
as part of immutable evidence. Encoding changes conflict; binary observations cannot become
sealed JSON policy receipts. Null stored MIME preserves legacy canonical JSON bytes. Initial
publication continues using its strict initial-only scope and original account revision.

These reads do not establish valid gzip, signatures, complete graphs, current authorization
or an active catalog. Full failed-history, pending-object and predecessor reconciliation,
renewed access and complete generation publication remain required. The current 4,096-object
inventory bound is unchanged; general multi-generation retention still needs reconciliation
with the protocol graph's separate object budget. Processor schema 10 and Android Room 12
remain unchanged. No live Drive or new UI/device acceptance is claimed by this increment.

## Worker failure ownership after resource termination

Protocol CI for 37a572f exposed an existing shutdown race on Linux: a resource-limit
failure terminated its child, then a truncated pipe frame replaced that original failure
when the supervisor awaited its task group. The original exact exception assertion stayed
blocking; no rerun, exception waiver or relaxed assertion was used to accept that result.

FramedWorkerProcess now retains every observed fault, joins all owned tasks and propagates
the first observed failure through Completion and the frame consumer. Secondary diagnostics
remain available through its failure record after retirement. A pipe fault caused by owned
termination cannot replace the resource failure; an original malformed-frame failure still
remains a protocol failure. Existing release/join/input-lease behavior is preserved.

A real child fixture leaves a flushed partial frame before its lifetime stop. Regression
coverage requires the exact resource exception, retained truncated-frame diagnostic, joined
process, consistent frame-consumer error and intact/released input lease. The initial nine
worker cases pass locally; strengthened final full suites and fresh platform CI remain
required. No release acceptance is claimed from the earlier failed matrix.

## Complete pending-publication observations

Each scan now captures a complete bounded local outbox description, including unallocated,
uncertain allocated and previously verified objects. Original scopes, IDs and payload
digests remain immutable. Descriptions are framed and hashed individually; payloads are
validated one at a time without retaining the entire outbox in memory. Truncated, foreign,
aliased or unsupported work cannot silently disappear from reconciliation.

Allocated IDs participate in the change fence even before a successful read. An uncertain
allocation may be absent until retry; a verified receipt remains required even if no earlier
scan observed it. Actual bytes, encoding, size and minimum verified version must match the
captured work. Conflicting reads are retained before rejection. The scan completion and
unchanged-outbox assertion share one transaction, including rollback if a write trigger
changes a receipt. Newly admitted work survives an invalidated scan.

A changed outbox is a typed operational retry condition. Corrupted immutable descriptions
remain invariant failures. The actual setup runtime reports the operational distinction and
retries using the same captured allocation and signing plan. Inspecting older scopes through
renewed access never changes those scopes or grants renewed upload authority.

The integration passed the full 281 Core cases before the final exception classification,
then all 102 affected Core cases and all 203 AppServices cases with the final classification.
The strict final Desktop Release build has zero warnings/errors and complete logs are clean.
The preceding b915a82 worker correction passed Protocol39978807541, Android39978807619,
Desktop39978807933 and device39978807825; skipped duplicate runs are not passes. Fresh
exact-revision checks remain required for this increment. Processor schema 10 and Android
Room 12 are unchanged. General signed history/predecessor verification, generation publication,
renewed-access admission and the remaining joint release work are still unfinished.

The preceding 5d5d2ef passed Protocol39946912329 on all native platforms, Android39946912239,
Desktop39946912280 and device39946912278 on all three API levels. Fresh final-source CI is
required. Processor schema 10 and Android Room 12 are unchanged. General current-history,
head and pending-work reconciliation, renewed-access recovery, complete generation
publication/activation, processing/plugins, desktop/mobile workflows, LAN onboarding,
installers and exact tested joint release are still unfinished.

## Durable observation failure fencing and unread candidates

WorkspaceObservationService now commits an incomplete attempt before network work.
Starting another scan invalidates earlier completed evidence even if no new bytes are
successfully read. After a complete bounded inventory returns, it retains every listed
candidate identity/version/size atomically before downloading the first document. A failed
download cannot make an unread policy disappear from the next scan's known identities.
Candidate records and their framed inventory digest commit together, without expanding
the protocol's array limit or changing Processor schema 10.

Only the exact current attempt may complete. Its completion fence and actual final
checkpoint are one transaction. Failed writes, cancellation and restart retain any incomplete
work, known documents and the earlier historical checkpoint. Superseded callers cannot
replace a newer owner's completion. A digest mismatch, missing candidate, regressed
version, rewritten size, retired access or changed workspace closes reconciliation.

The actual initial-policy publication owner checks this fence before allocation/create
admission and again before accepting a receipt. Initial completion also requires the
current completed fence; it cannot reuse an older checkpoint after a newer failed scan.
Existing historical completion receipts remain readable without becoming current authority.
No network reconciliation runs inside the upload authority callback or account lease.

All 270 Core cases and all 190 AppServices cases pass against the frozen final source.
The final Desktop Release build has zero warnings/errors; complete final logs have no
unresolved diagnostics. Coverage includes unread candidate recovery, 4,096 identities,
atomic candidate/hash/checkpoint writes, stale concurrent scans and interrupted uploads
before create or after remote create. Both upload cases recover the originally allocated
identity without accepting a late receipt. No assertions or warning gates were weakened.

The preceding b47b7a8 passed Protocol39962637760 on all four native platforms,
Android39962639392, Desktop39962637709 and device39962637736 on APIs29/35/37.
Fresh exact-revision CI remains required for this increment and the final joint release.

This covers the existing bounded JSON document scan and initial setup. Full mixed-role
JSON/gzip inventory, partial-page history, general failed-observation reconciliation,
predecessor verification, renewed-access admission and complete catalog publication remain
unfinished. A stored completed attempt cannot authorize a new process or skip startup
reconciliation. No live Drive write or new device/UI acceptance is claimed here.
