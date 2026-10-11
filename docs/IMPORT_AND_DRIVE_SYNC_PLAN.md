# Google Photos imports and Drive storage plan

Agreed direction, 2026-10-10. This is the next implementation plan, not a list of newly shipped features. Baseline: MPA 0.4.3, version code 9, Room v5. Start with reliable bulk Drive synchronization, then Takeout import, then Google Photos Picker. The combined 0.5.0 development release implements these stages and Drive JSON conversion. Its required CI passed; the live acceptance checks below remain outstanding. Smaller verified increments can ship first; 0.4.4 shipped the bounded Drive stage.

## Storage contract

**Selected Google Drive folders are MPA's durable media storage.** Adding ordinary images/videos to those folders outside MPA must make them appear in the app automatically, including nested folders. No MPA filename convention or app property is required for discovery. Connecting an existing folder requires the existing-collection authorization mode; `drive.file` cannot read every arbitrary descendant.

Several accounts/folders form one gallery, with source filters and one phone-wide people/recognition library. One chosen default destination receives new Takeout/Picker imports, ordinary local imports and edited copies. An import job captures its destination before transfer. Changing the default cannot silently redirect an in-flight job, historical asset or upload checkpoint. Preserve the current source binding and pause/resume behavior.

The Room index and bounded caches support browsing offline. Discovery imports file records, not a permanent phone copy of every original. Originals download for viewing/editing, offline pins, or enabled bounded recognition batches. Existing face/object consent, charging/night settings, pause, thermal gates and per-source network preferences still apply. Bulk discovery must not silently enable recognition or offline pinning.

Media uploads and annotation backups have distinct completion states. Original/edited media bytes are immutable from MPA's perspective. Edits create linked copies. Portable annotations use XMP; embeddings/crops stay local. MPA does not automatically delete Google Photos, Takeout archives or Drive originals during migration.

## Current implementation and gaps

Already implemented in 0.4.3:

- `CloudSync.kt` recursively enumerates paginated folder contents, then consumes the Drive change feed. Change-page rows and tokens commit transactionally. A pre-enumeration cursor catches concurrent changes; absent rows become tombstones only after a successful traversal.
- Per-source WorkManager synchronization uses 15-minute periodic requests and each source's network preference. App startup/resume also queues refreshes; users can choose Refresh Drive. Android can delay background work, so this is eventual synchronization, not guaranteed real-time delivery.
- New cloud records appear in the combined gallery. Thumbnails are fetched on demand; originals are queued through verified downloads. External deletion/move-out hides main-gallery items while retaining downloaded snapshots/annotations in offline and saved collections. Active overlapping sources keep shared media visible.
- Phone indexing can queue verified originals fairly across connected sources. Metadata uses an opt-in source-scoped `.mpa-catalog.xmp` with revision checks and explicit recovery.

Extend these paths rather than rebuilding sync. The 0.4.4 increment now persists scan generations/folder-page cursors in Room v6, bounds discovery/change pages per worker and adds primary per-source sync progress/actions. It retains authoritative-scan removal gates and rejects incomplete listings. See [the concrete audit](DRIVE_INGESTION_AUDIT.md), [validation](VALIDATION.md) and the new bulk-ingestion phone checklist; live Stage 1 acceptance remains outstanding. Published 0.4.4 uses Room v6; 0.4.3 used Room v5.

A normal local import remains protected even after backup. The 0.5.0 increment implements a separate migration transition to Drive-backed disposable media, durable jobs/checkpoints, Takeout ZIP/folder manifests, Photos Picker and opt-in Drive JSON conversion/verified cleanup. See [implemented controls and limits](GOOGLE_IMPORTS.md) and [validation](VALIDATION.md). These implementation changes do not establish the real-account acceptance checks below. Downloads/imports currently reject files above 250 MiB; XMP catalogues are limited to 2 MiB / 10,000 assets. Those limits must be visible and addressed deliberately for large libraries.

## Stage 1: reliable Drive ingestion and reconciliation

1. Persist per-source initial-scan generation, folder queue, folder-page cursor and pre-scan change cursor. Process bounded pages/time per worker; resume after process death without restarting the whole tree. Prefer per-source coordination and bounded network concurrency; avoid holding a cache lock during network enumeration.
2. Make rows browsable as pages arrive. Commit tombstones only when every folder/page of an authoritative scan completes. Interrupted, unauthorized or partial scans must not hide unrelated media. Use the appropriate full-rescan path when a cursor cannot be used; verify actual Google error contracts.
3. Commit each change page and its next cursor atomically. Handle new files, new/moved nested folders, rename, metadata-only updates, content replacement, move-in/out, trash/removal, access loss and overlapping roots. Preserve unchanged-content identities and manual annotations. A byte change invalidates thumbnails/embeddings; reviewed face geometry must not transfer silently onto new content. Keep original snapshots and edit provenance recoverable.
4. After discovering new media, notify the gallery and schedule enabled indexing through its existing bounded queue. Recognition fetches only small batches and observes the phone/cache/network limits. Do not download the entire library just to discover filenames or show it in the gallery.
5. Add per-source last successful sync, indexing/transfer progress, waiting/reconnect/failure states and Retry/Refresh actions. Put primary actions above lengthy settings. A background worker reports authorization-required; consent launches only from a user action.
6. Filter metadata files and import archives out of the ordinary media gallery. Do not follow Drive shortcuts outside the selected scope; shortcut support, if added, needs explicit ancestry/cycle handling. Preserve shared-drive API flags and test supported permissions instead of assuming all shared folders behave alike.

No server is required. Foreground refresh plus Android background jobs is the default. Drive push notifications need a public receiver and expiring channel renewal; they are an optional future service, not a requirement for normal users.

Acceptance: dump a nested collection of thousands of media files directly into a connected Drive folder while MPA is closed. Reopen/Refresh and see progressive discovery without bulk original downloads. With indexing enabled, recognition eventually processes those items under its configured schedule. Restart midway, add files during enumeration, rename/move/replace/trash selected files and exercise access/network failures. Verify convergence, no duplicated local identities for overlapping copies and no false mass removal from a partial scan.

## Stage 2: shared resumable migration engine

Both Takeout and Picker feed one durable import job/item model. Suggested item lifecycle: discovered, metadata pending/review, selected, staging, uploading, remote verified, committed, complete, waiting/retry or failed. Store source provenance, durable job/item IDs, source identity/revision, selected destination binding, reserved remote ID, resumable upload session/offset, byte length/digests, metadata disposition and actionable error. Keep authorization tokens out of Room and XMP.

Preview displays counts, supported/unsupported media, size estimates, destination, metadata completeness and duplicates. Estimates remain estimates until bytes are read. Give explicit Import selected, Pause/Resume and retry controls. Cancel stops transfers but leaves already verified media and any sole unsynced originals intact; explain what remains.

Use a bounded staging area, initially one media file in flight. Check free space before staging; stream hashes and uploads rather than buffering whole media in memory. Reuse reserved remote IDs and acknowledged offsets through retries. Checksum/length/version/parent checks and an atomic database commit gate success. An upload succeeding just before a crash must be recognized on resume, without creating a second remote file.

Once a migrated item is verified in Drive, create/update its `CloudPhoto` linkage and source-backed `Photo` record, preserve its stable metadata asset ID, and make the original eligible for the normal cache policy. Do not apply this eviction transition to existing protected local-only imports/edits accidentally. Pending uploads, active readers, pins, drafts/handoffs and enabled pending scans stay protected. Protected files can exceed the soft budget; pause further staging/downloads when real free space is insufficient rather than growing without control.

Deduplicate exact verified bytes within the selected destination/import scope, using streaming SHA-256 and length with remote verification. Names and dates alone are not identity. Album duplicates may share one stored asset with several memberships. Keep edited variants, RAW/JPEG pairs and motion-photo/video components distinct. Retried/repeated imports reuse provenance checkpoints. Do not merge unrelated accounts' media or annotations merely because names or hashes match; existing cross-account copies remain independent unless the user explicitly chooses reuse.

Large videos require a streamed disk/transfer path with explicit supported limits; do not silently omit them or buffer them into RAM. Test the existing 250 MiB boundary and introduce any changed limit with bounded staging and low-space behavior. XMP catalogue partitioning/backward-compatible recovery is a scalability gate: stop with a clear, recoverable state at the current limit until partitioning is implemented. Never complete a large migration while silently dropping its annotations.

## Stage 3: Takeout import

Support Google Photos Takeout ZIP parts first. Start with one small selected-album export. Export delivery by download link avoids placing another complete archive in the same Google Drive quota. Smaller ZIP parts reduce the size of an individual archive, not the total exported library.

- Read existing ZIPs without extracting the whole archive into a second directory. Build a compact durable manifest of entries/metadata, then stream selected media through bounded staging. Support multiple parts added over time; JSON and corresponding media can arrive in different parts. Missing metadata stays pending and can be attached later.
- Initial source support: a selected local/USB ZIP or already extracted folder with durable Android document access. Detect whether the document provider is seekable and whether it downloads a whole archive internally. Do not promise bounded total phone storage for every cloud provider. A dedicated authenticated Drive archive reader with checked range requests/revisions can be a subsequent source adapter; keep it separate from ordinary media discovery. TAR/TGZ can wait.
- Pair JSON sidecars using full archive-relative context, title and supported Takeout naming variations, including `.supplemental-metadata.json`, truncation, numbered names and Unicode. Ambiguous matches are a preview/review issue, never resolved by filename alone. Recognize album metadata separately from per-media metadata and retain duplicate-entry provenance.
- Read original EXIF plus exported capture-time/location/title/description fields when present. Record provenance and precedence: retain existing reviewed MPA values, show conflicting incoming values, and distinguish absent GPS from valid zero coordinates. Treat imported descriptions and people-name hints as separate fields, not object labels or confirmed face assignments.
- Extend XMP and Room compatibly for the portable fields actually supported. Preserve bounded unknown Takeout metadata as provenance if useful; document exported versus local-only fields. Keep original media bytes untouched. Existing XMP backup opt-in remains in effect; show when annotations are local-only and when they have been verified in Drive.
- Exported people names, if present, are photo-level hints. Takeout does not supply a guaranteed usable Google recognition model or face-region-to-name mapping. Run MPA's phone inference across all sources and require review before promoting hints to confirmed references. Multiple people in a photo must not receive arbitrary face assignments.
- Add metadata-only recovery for verified existing Drive media. Preview candidate matches, require a content/provenance-verified match and review conflicts; otherwise leave unresolved. A fresh Takeout export can still contain all media even when MPA elects to import only metadata. Do not claim a Google metadata-only export option.
- If the user uploads an unpacked Takeout collection directly into a connected Drive folder, discover its media through normal sync and offer a Takeout-metadata preview for its sidecars. Reuse verified existing media rather than uploading it again. Sidecar discovery must remain scoped and user-reviewed; JSON files are metadata, not gallery photos.
- Bound entry count, JSON size, expanded bytes and media dimensions; reject path traversal, unsafe archive entries, corrupt/encrypted/unsupported archives and decompression abuse without affecting completed work. User exports and evaluation photos remain outside Git.

Acceptance: import a small real album, then synthetic multi-part fixtures covering late/missing JSON, Unicode/truncated/colliding names, album duplicates, edited variants, GPS/EXIF conflicts and mixed images/videos. Pause/network failure/process death at transfer boundaries, repeat the same export and confirm no additional remote copies. Verify byte hashes, metadata/XMP round trip, cache release only after verified remote storage, and visible unresolved/unsupported items.

## Stage 4: Google Photos Picker import

Add a second source adapter using the official Photos Picker API and on-device Google authorization. Enable the Picker API and its `https://www.googleapis.com/auth/photospicker.mediaitems.readonly` scope in the existing project with the local/published Android package/certificate association. Keep Drive and Photos authorization distinct; switching the default Drive account does not choose the Photos account automatically.

Create a selection session, open Google's picker in its supported UI, poll within the returned polling/timeout guidance, paginate selected items and capture persistent picked-media IDs. Cancel/deny/revoke/session timeout returns to the app without damaging other jobs. Preserve durable source/item identifiers; refresh session listings/download URLs while valid and request reselection when access can no longer be restored. Do not treat temporary base URLs as permanent storage.

Feed selected items into the same preview, deduplication, destination, staging, resumable upload and cache-promotion pipeline. Picker downloads require the documented authorization and download parameters. Image downloads retain EXIF except location metadata; video downloads can be transcoded. Preserve the bytes actually obtained with provider provenance, and do not claim byte-identical camera originals or invent missing GPS. Picker does not replace Takeout's richer JSON/album metadata recovery or provide background access to the whole Photos library.

Acceptance: select a few photos/videos, cancel selection, select the same item again, interrupt transfer, expire a URL/session, switch Photos/Drive accounts and revoke authorization. Check correct destination, no token exposure or duplicate uploads, clear metadata limitations and resumed work/reselection behavior.

## Implementation sequence and release gates

1. Audit and strengthen Drive ingestion with bounded durable scans and meaningful reconciliation tests. Add external bulk-ingestion phone checks.
2. Implement the shared job/staging/verification/cache-promotion path, with Room migrations preserving all existing IDs, uploads and corrections.
3. Implement Takeout preview/parser and a one-album end-to-end import; then multiple parts, deduplication and metadata recovery. Address or visibly gate catalogue/large-file limits before full-library use.
4. Add Picker using that proven engine. Complete real consent/import tests on the Samsung S23 Ultra.
5. Publish only a tested immutable-source APK with evidence, preserved signer/icon and accurate limitations. Never relabel a partial prototype as a complete migration feature.

Use local JDK 17 / SDK 35 / Python 3 development checks. Retain CI checks before releases. User photos, exports, credentials and signing keys must remain untracked. Update [validation](VALIDATION.md) and the phone checklist with concrete evidence. No new server, paid AI API integration or face-threshold change belongs in this milestone.

## Storage accounting and references

Final Drive copies consume storage in addition to Photos copies while both remain. Adding archives to Drive consumes additional quota too. Streaming avoids a second full extracted phone library; it cannot remove cloud duplication or the storage needed by the user-supplied archive. MPA reports these separately and never deletes the source to make a size estimate appear lower.

Official documentation checked during planning:

- [Google Takeout](https://support.google.com/accounts/answer/3024190): product data selection, archive delivery/size, Drive quota and supplemental JSON metadata. Arbitrary export date ranges are not a general Takeout feature; use available album selection for batches.
- [Photos API changes](https://developers.google.com/photos/support/updates): broad existing-library access was removed from the Library API in 2025; use Picker for user-selected existing items.
- [Picked media schema](https://developers.google.com/photos/picker/reference/rest/v1/mediaItems): persistent item identifiers, creation time and media-file metadata.
- [Picker media downloads](https://developers.google.com/photos/picker/guides/media-items): temporary authorized download URLs, location exclusion for images and video download behavior.
- [Drive changes](https://developers.google.com/workspace/drive/api/guides/manage-changes) and [resumable uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads).

Recheck API/OAuth/metadata contracts at implementation time, especially from an actual small Takeout export. Do not rely on old broad Photos scopes or undocumented provider cookies.
