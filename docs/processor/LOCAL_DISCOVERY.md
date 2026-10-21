# Local discovery projection

This increment is a private desktop SQLite projection, not the completed portable catalog or an Android schema change. Its repository has no source-byte or Google mutation access. Source scanning, publication, acknowledged Drive operations, explicit tombstones, generated analysis, viewer sessions and UI activation remain separate required work.

Processor schema4 stores account/file-derived asset UUIDs, observed Drive revisions, bounded media metadata and per-source memberships. Overlapping folders share the same account/file representation. Different accounts/files stay distinct regardless of names or equal content. A newer observed Drive version can update facts while preserving the asset ID. Older versions cannot overwrite facts; conflicting facts for the same version raise a domain conflict and roll back the current bounded batch.

Each discovery batch is at most250 records and revalidates the captured account/source revisions in its SQLite transaction. Source policy changes fence old batches. Disabled sources and excluded descendants disappear from the projection without erasing their facts. Disconnected accounts retain indexed browsing facts while fresh reads require reconnection. Empty/incomplete scans never infer deletion.

The timeline uses an index over unknown-date status, descending effective UTC time and canonical asset ID. Capture time takes precedence over source modification time; unknown dates sort last. Keyset reads are bidirectional, at most90 returned records (60 by default), and date seeking queries the same index. Reads capture discovery/configuration revisions and rows in one short transaction. This repository is not a frozen viewer-session index or a complete UI anchor implementation.

Raw capture text is preserved. An EXIF wall time without an offset does not become the computer's local time or a fabricated UTC capture time. `DiscoveryTimestampReader` accepts an instant only from a valid explicit RFC3339 offset/Z. Later verified original/Takeout metadata can refine the facts through its own revisioned owner.

Google documents `version` as a monotonically increasing int64 string, capture time as EXIF DateTime, and warns that list pagination can change during edits. These are the reasons for version fencing, raw-time preservation and no absence-based deletion. Sources: [File resource](https://developers.google.com/workspace/drive/api/reference/rest/v3/files), [files.list](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/list).

## Provisional Windows fixture evidence

All62 Core tests passed after adding ten discovery cases (eight functional cases and two scale fixtures). Two subsequent timestamp tests passed with strict analyzers. Schema1/2/3 fixtures preserve prior accounts, sources, library identity, captured jobs and outboxes. Final native coverage must bind the committed revision.

| Fixture | Preparation | Date seek | Maximum observed60-record page | Complete paged walk |
| --- | ---: | ---: | ---: | ---: |
|10,000|2,645ms|11.1ms|26.5ms|1,134ms|
|100,000|214,581ms|11.9ms|75.4ms|17,864ms|

These are single observations on a concurrently loaded Windows computer, not latency guarantees. Preparation cost requires continued measurement as scanning is integrated. Each test walk retained one returned page; actual process memory, import responsiveness, publication snapshots, Android staging and physical-device performance are not established by this test. Full logs/TRX/JSON measurements remain in private preflight evidence. No warning gate or assertion was reduced.

## Durable discovery admission and failed observations

SourceDiscoveryRepository captures source/account revisions, canonical root/shared-drive scope, descendant policy and each folder's immutable identity/depth in schema5 jobs. Page checkpoints survive restart and reject stale attempts, late pages and repeated current tokens. Idempotent child admission retains completed work; source changes fence old work without silently retargeting it.

A malformed observation creates an atomic failed repair item in the same job, so healthy folder work can continue. A known file identity retries that exact metadata GET. If Google omitted a usable identity, the repair captures the metadata-page cursor; it never assigns the old position to a different photo. RetryFailed admits only failed repairs and leaves successful folders/items ready. Replaying the observation preserves an already queued or completed repair. Folder and repair claims have separate stage filters. This is metadata-only scheduling; runtime scanner/repair execution and user-facing progress are still pending.

All14 focused captured-input/discovery tests pass locally, including four repair cases. No schema or Android Room change beyond the already introduced schema5 item context is needed for these records. Full affected/native regression remains separate.

## Bounded runner candidate

SourceDiscoveryRunner performs explicit bounded steps using the engine's shared account read owner. Folder pages commit healthy media, child admissions and failed observations before their durable checkpoint; replay is idempotent. The checkpoint commits before yielding, and a terminal saved checkpoint completes without re-reading the folder. Network work is never inside a SQLite transaction. Unknown identities retry only a captured metadata page; known failures read the exact file and revalidate its parent/shared drive. A file that moved out of scope is operationally unavailable, not a replacement. Incomplete search creates explicit retryable work and does not erase existing facts.

Pause, cancellation and Exit join the actual read before returning its lease. Captured source/account changes fence late results. Operational failures retain healthy content and exact checkpoints; internal identity/facts conflicts throw and fault this runner rather than returning an empty/complete folder. Local AppServices19/Google24/Web11 tests pass. Periodic scheduling, exclusive-restart reconciliation, progress controls, storage-budget admission, runtime/catalog UI activation and Drive publication remain pending. This component alone is not a completed background import/processing pipeline.

## Automatic admission and dispatcher candidate

Automatic source requests now commit an idempotent verification job before any Google call. Stable request identities include the library, account and captured source/account revisions; restart or repeated browser scheduling preserves failed/completed work. Accepted root/shared-drive verification is checkpointed before root-folder admission, so replay cannot replace it with newer network facts. No schema6 or Android persistence change is introduced.

SourceDiscoveryScheduler owns the browser-independent dispatch loop and cancellation epochs. Pause acknowledges only after the active read and durable lease have joined; Resume starts a fresh epoch. Exit joins the loop, and internal failure remains visible with an unsuccessful exit. Exclusive-startup recovery is limited to the three known metadata stages, preserves pause/cancel/checkpoints, and leaves upload/native-worker fences untouched. It must run under the composition root's exclusive state lease before new reads are admitted.

Local full Core84, AppServices26 and strict Desktop compilation pass without diagnostics. Current Google24/Web11 regressions passed in the preceding runner increment. This dispatcher is not yet instantiated in the product composition root: unified web/tray/IPC pause controls, metadata storage-budget admission, Sources activity/retry/rescan and catalog UI integration must be connected before enabling it. The complete joint release remains in development.
