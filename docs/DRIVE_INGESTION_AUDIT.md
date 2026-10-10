# Drive ingestion: first local increment

The 0.4.4 release candidate follows `IMPORT_AND_DRIVE_SYNC_PLAN.md`, Stage 1. Audited baseline:
`15589ca` (main at the Windows clone on 2026-10-10). This is not a Takeout or Picker release.

## Baseline findings

- Folder listing already requested ordinary image/video files, paginated recursively, with
  shared-drive flags. Archives, JSON, XMP and shortcuts are outside that query. Recognition
  and original downloads are separate from metadata discovery.
- Each page made rows browsable through Room. Full scans captured a pre-enumeration change
  token, marked absent records only at traversal completion, then replayed changes. Change
  rows and their next token already committed together. Folder changes triggered a full scan.
- Both traversal implementations kept their folder queue, visited set and page token only in
  memory. A single global mutex serialized sources. A worker could enumerate the whole tree
  and replay an unbounded change feed in one invocation.
- `files.list` did not request/check `incompleteSearch`. A successful HTTP response alone
  did not establish that a listing was complete enough for absent-record reconciliation.
- `lastScan` described full traversal, not successful change-feed completion. Refresh and
  sync errors were below the long Storage settings. No durable per-generation progress existed.
- Byte replacement detached the current cloud row from the old local snapshot, preserving
  reviewed faces. It reused the old thumbnail path until refresh, which could show stale content.

## Implemented boundaries

Room v6 adds the source's scan generation, pre-scan change token and committed page count,
plus a durable folder/page queue retaining completed folders until that generation finishes.
Each page transaction saves records, child folders and its next cursor together. A fresh
worker/Room connection resumes from that cursor. Only an exhausted queue permits tombstones
and folder pruning. Incomplete searches, authorization failures, interrupted transactions
and canceled work do not complete a traversal. A missing nested folder abandons the generation
without removals; the next refresh enumerates the root again.

One pass allows 20 folder/change pages or two minutes, checked between requests using a
monotonic clock. An in-flight HTTP request can extend that time; the HTTP client keeps its
existing timeouts. The worker appends a constrained continuation to the per-source unique
work chain. If it dies before scheduling, periodic or foreground refresh resumes the saved
checkpoint. Source mutexes serialize each source; at most two sync passes run concurrently.
Continuations read the source's current network preference. Changing that preference cancels
queued work and schedules it again with the new constraint, retaining the saved checkpoint.
The cache mutex covers database mutations, not listing requests. Cache eviction remains in
download/indexing completion and explicit Storage controls so it cannot extend discovery.

Folder changes start a new durable generation instead of recursive scans. File-list cursor
rejection (400/410 on a continuation request) restarts the generation without tombstones.
Change-page rows/tokens remain atomic and bounded. `lastSuccess` updates only on the last
change page. Primary per-source Refresh/Retry actions, discovery pages, last success, download
counts and reconnect/failure/waiting states appear above lengthy Storage settings. Existing
Scan now progress stays at the top. Background workers never launch consent.

Changed media gets a separate thumbnail path and loses its current downloaded-photo linkage;
old snapshots, portable IDs and reviewed faces remain attached to the old content. Unchanged
bytes retain the existing identity. Sync notifies Room observers page by page and schedules
the existing indexing worker after each pass, respecting opt-in, pause, schedule and bounded
download behavior. Discovery itself never downloads originals or enables recognition.

## Contracts rechecked

- [Drive files.list](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/list):
  incomplete results are explicit; rejected pagination restarts from the beginning; file-list
  page tokens are usually valid for hours and listings can change during enumeration.
- [Drive changes.list](https://developers.google.com/workspace/drive/api/reference/rest/v3/changes/list):
  change tokens do not normally expire. HTTP 410 remains a conservative fallback from the
  existing client, rather than a claimed normal expiry mechanism.

## Remaining acceptance and next work

The 10,000-folder safety bound is retained and reported, not silently truncated. Android
background execution is eventual. Large-library UI queries still materialize lists; this
increment bounds network traversal and checkpoints, not every existing gallery query.
Provider-specific shared-drive permissions and real concurrent changes still need live tests.
No server, model or matching-threshold change is part of this increment.

Use the bulk-ingestion phone checklist in `FEATURES_0_4.md` before claiming Stage 1 live
acceptance. Continue next with the shared migration job/staging/verified-cache-promotion
engine, then a small-album Takeout round trip, then Picker. Current 250 MiB media and
2 MiB / 10,000-asset XMP limits remain. No full-library migration is implemented here.

CI build/model and device workflows are retained. Publishing still requires their passing
results for immutable source and the established signer; do not invoke the existing 0.4.3
publisher for this increment. Local validation and outstanding device checks are recorded
in `VALIDATION.md`.
