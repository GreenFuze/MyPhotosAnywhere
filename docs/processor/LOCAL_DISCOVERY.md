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
