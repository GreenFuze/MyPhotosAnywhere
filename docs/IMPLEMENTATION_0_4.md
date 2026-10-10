# Multiple sources and recognition implementation

## Decisions

MPA connects several Google Drive folders across Google accounts. All connected sources appear in one gallery, with source filters. The user chooses **one default backup destination** for newly imported media and edited copies. Other sources remain available for browsing, downloads and recognition; imports are not broadcast to every account.

There is one phone-wide identity library and one shared set of inference models. Naming Sam in account A supplies a reference for Sam in account B. Human and pet encoders can differ because their tasks differ; the same human encoder and species-appropriate pet encoders apply across every source.

## Stage 1: multiple sources (0.4 development implementation)

- Persistent account/folder sources, source management and gallery filters.
- One backup destination, selected atomically. Upload checkpoints retain their original account/folder. Switching the destination pauses old tasks; they resume only if that destination is selected again. Previously assigned assets are not automatically copied to the new destination.
- Independently scoped sync, change tokens, thumbnails, downloads, network preferences and cancellation. Removing a source preserves other sources, local photos and identity corrections.
- Migration of existing 0.3 connections without losing media, reviewed names, upload sessions or change tokens.
- Overlapping parent/child folders in the same account can share one local photo when remote file ID, checksum and size match. Matching IDs or hashes alone do not join assets across accounts. Separately uploaded copies are not automatically deduplicated.
- Existing opt-in human matching uses all locally indexed sources. Tests exercise references across accounts and preserve rejected matches. These functional tests do not measure accuracy.

This stage does not implement individual pet recognition, stronger human models, XMP synchronization or overnight scheduling. The installed 0.3 release remains unchanged until a newer APK is published.

## Stage 2: portable annotations and stable regions

Implement XMP before changing detection models, so a rescan cannot silently replace reviewed decisions. See [the detailed design](RECOGNITION_AND_SIDECARS.md).

Use global asset, identity and region UUIDs independent of Drive IDs. Replace detector-order IDs with stable regions, normalized geometry, oriented image dimensions, model/preprocessing versions and checksum-bound provenance. Match new detections to existing regions; ambiguous remapping becomes a review task. Preserve assignments, rejected links, merge/split history and imported hints.

Write adjacent `<full-original-filename>.xmp` files and a `.mpa/catalog.xmp` **inside each source**. A source catalog contains only assets and identities referenced in that source, sharing global UUIDs with other catalogs. Never export unrelated accounts' photos, names, albums or references into a source. Read-only sources use local annotation overlays until a permitted export destination is explicitly selected.

Embeddings, crops and vector indexes stay local and rebuildable. Sidecars store confirmed names, regions, albums, GPS annotations and edit recipes. Add an offline outbox, revision-aware updates, conflict handling and recovery previews. Test round trips, interrupted writes, external edits, conflicting names and reconstruction after database loss. Preserve original media bytes and EXIF; XMP is the portable sidecar format.

## Stage 3: phone indexing and scheduling

Use one resumable global queue with per-asset checkpoints and model-version invalidation. Include photos from all enabled sources; fetch bounded previews where suitable and verified originals when necessary. Fair scheduling prevents one source starving others.

Provide Scan now, selected-photo scans, charging-only and a user-defined overnight window. WorkManager triggers jobs; workers also check time, battery, charging, thermal state and preferences between bounded batches. Handle daylight-saving changes, windows crossing midnight, process death, retry and model replacement. Show progress and pause/resume. Respect network preferences and avoid retaining large bitmaps between batches.

## Stage 4: measured people and individual pets

Evaluate human detection, alignment and identity embeddings separately. Use a consented held-out set with groups, small/occluded faces, profiles, aging children and similar relatives. Measure retrieval, false merges and missed detections; calibrate thresholds and ambiguity margins. Preserve manual references through model updates. Benchmark actual phone latency, memory, heat, battery and model size before shipping.

For pets, start provisionally with cats and dogs, suitable face/body regions and individual identity embeddings. Evaluate similar breeds/coats, siblings, growth, grooming, poses and occlusion. Object labels are not pet identities. Uncertain pets remain unassigned. Confirm exact checkpoint licensing before distributing weights; a code license alone is insufficient. Add species and identity review to the global library.

Later Google Photos import supplies provenance-tagged hints and candidate references, not assumed ground truth. Exports may not contain all recognition data. Full model training on the phone is not initially required; reference examples improve personal matching without retraining weights.

## Stage 5: optional companion

After the phone pipeline works, offer a paired desktop/NAS companion with QR setup, local-network discovery and visible consent. Use the same IDs, model versions, job protocol and XMP rules. Pairing should not require internet server administration. Phone-only operation remains complete; paid cloud training or hosted services require a separate explicit decision.

## Validation

0.4 adds regression coverage for the released v3 upgrade, one destination, immutable upload checkpoints, source removal, cross-account suggestions, rejected matches, overlapping-folder identity reuse, account separation and changed-byte snapshots. GitHub Actions runs these with the existing unit/integration suite, Android lint, APK assembly, the pinned model-contract probe and native emulator smoke tests. Live multi-account OAuth/sync and recognition accuracy still need real-device evaluation.
