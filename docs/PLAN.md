# Implementation plan

## Product decisions

- Name: **MyPhotosAnywhere**, abbreviation **MPA**.
- Platform: native Android, Kotlin and Jetpack Compose; minimum API 26.
- Source of truth: ordinary image/video files in a user-selected Google Drive folder after successful upload. The local Room database drives browsing and tracks synchronization.
- Account model: user's Google account for Drive; user's official ChatGPT, Gemini, or Claude app and subscription for AI handoff. MPA never takes provider passwords, session cookies, or AI API keys.
- Preserve originals. Every exported edit is a separate asset with a parent link and an edit history record.
- Next priority: measured people and individual-pet recognition quality, with durable XMP sidecars for library annotations. The owner explicitly requested portable sidecar metadata after 0.3. Read [the recognition and sidecar design](RECOGNITION_AND_SIDECARS.md) for pipeline, evaluation, storage, and release gates. Model selection remains an evaluation decision.
- Recognition runs on the phone by default. Offer manual/on-demand scans and charging/overnight scheduling with resumable batches and resource controls. A paired desktop/NAS companion is optional acceleration; it must not become an onboarding requirement. Model training and phone inference are separate concerns.
- Repository visibility: private. Preserve the owner's selected LGPL 2.1 license in `LICENSE`.

## Milestones and acceptance criteria

Progress: 0.1 and 0.2 passed unit tests, lint, and APK assembly; the user has since confirmed Google setup works on their phone. The 0.3 implementation covers image editing, local albums/favorites/archive, video import/playback, optional local people/object indexing, map/place browsing, recursive Drive discovery/change checkpoints, thumbnails, queued verified downloads, and protected cache eviction. It passed 49 unit/integration tests, lint, APK assembly, CPU model-contract checks, and two Android emulator tests. New live Drive/cache and recognition behavior still needs phone validation. Read [FEATURES_0_3.md](FEATURES_0_3.md) for exact behavior and residual work.

Current storage layout keeps uploads directly in the chosen folder for compatibility with 0.2. Recursive indexing supports existing subfolders without reorganizing unrelated files. Biometric data and user library metadata stay local. Future owned subfolder layout, encrypted metadata recovery, production signing, calibrated recognition, pet identities, and video editing remain separate roadmap work.

The next metadata milestone changes durable annotations from phone-only storage to XMP sidecars beside assets and a library XMP catalog in Drive. Confirmed names, face/pet regions, corrections, albums, location annotations, and recipes become recoverable; embeddings/crops/vector indexes remain local and rebuildable by default. Readable sidecars expose stored names/locations to folder collaborators. This design is not implemented in the 0.3 APK. Start with stable region IDs and portable corrections before model upgrades so rescanning cannot lose reviewed identities.

### 1. Offline gallery and AI round trip (first implementation)

- System photo picker imports selected images to durable private storage.
- Room records IDs, hashes, timestamps, MIME types, optional GPS, parent links, and original paths.
- Bounded thumbnails and Coil's memory cache enable offline grid browsing.
- Detail view can initiate a handoff to an official provider app via the share sheet. Show the image/prompt sharing notice; keep the user in control.
- Bounded JPEG exports strip EXIF. FileProvider exposes only the handoff cache directory with temporary read grants.
- Import a saved AI result and link it to its source. No assumption of an automatic provider callback.
- Core cache eviction rules and prompt construction have unit tests; CI runs unit tests, lint, and APK assembly.
- Acceptance: import, restart offline, browse, hand off, and import an edited copy without changing the original. A real-device test is required before declaring the entire round trip verified.

### 2. Google Drive folder and reliable sync

- Google Identity authorization, account switching, sign-out, and a native Drive folder browser. Persist account and folder ID together.
- The user can pick an existing folder or create an MPA folder. Manage an owned subtree such as `Originals/`, `Edits/`, and `.mpa/` without modifying unrelated files.
- Validate folder permissions, support pagination and shared-drive differences, and display the selected folder clearly.
- Scope decision: `drive.file` only exposes app-created or explicitly granted files. Selecting a folder must not be assumed to authorize all existing descendants. Arbitrary existing-library discovery can require broader restricted scopes and Google's verification process. Prototype the exact picker/authorization flow before freezing the scope. A SAF folder URI is not a Drive file ID and Drive providers do not universally support tree picking.
- WorkManager persists an upload/download queue with network constraints, retry/backoff, explicit authorization recovery, idempotency keys, and visible per-item failures.
- Use resumable uploads, revision/checksum verification, paginated initial scans, then Drive changes tokens. Commit a token only after the corresponding database transaction succeeds.
- Acceptance: interrupted uploads resume without duplicates; expired authorization needs user action; external changes reconcile; a folder/account switch cannot upload pending items to the wrong destination.

### 3. Storage controls and search

- Default disposable-original cache budget: 2 GiB, configurable. Keep thumbnails separately (default 256 MiB), subject to measured device capacity and visible disk-pressure handling.
- Evict least-recently-used downloaded originals only after a matching remote revision is verified. Never evict an unsynced original, a pending edit, an active read/export, or a user-pinned offline item.
- Thumbnails are rebuildable; imported originals live under `filesDir`, not `cacheDir`. Preview generation is bounded in resolution and serialized to avoid concurrent bitmap spikes. Stage writes and atomically rename complete files.
- Show storage use, Wi-Fi-only backup, pinned offline albums, and queue status. Clearing disposable cache must preserve original files and the metadata index.
- Distinguish remove local download, remove from library, and trash remote asset. No bidirectional delete propagation without a separate, deliberate trash action.
- Text/date/type/place/people search uses the local database. Never upload faces or metadata to AI providers during background indexing.
- Acceptance: browse offline; preview larger files on demand; fill storage without losing pending uploads; changing cache budgets does not remove protected data.

### 4. Conventional editing and geographic browsing

- Crop, straighten, rotate, exposure, contrast, saturation, warmth, filters, and markup first. Extend to video after image behavior is stable.
- Persist edit recipes; render output copies and preserve parent relationships. Include undo/redo and export controls.
- Read EXIF capture time, orientation, and GPS; retain absence of GPS as unknown. GPS reads from selected media and optional location tagging are separate permission flows.
- Map clustering and place search; choose map/geocoding licensing before implementation. Offer strip-location on export and explicit opt-in to any online reverse geocoding.
- Acceptance: edited output is correctly oriented, originals remain byte-identical, map pins use embedded coordinates, and photos without GPS remain searchable by date.

### 5. Faces, people, pets, and advanced discovery

- Opt-in on-device face detection. Detection alone is not recognition: tracking IDs must not become person identities.
- Evaluate a licensed on-device embedding model and preprocessing pipeline before selecting a recognition engine; calibrate matching thresholds on a consented evaluation set.
- Local clustering with unknown identities, user names, merge/split, wrong-match correction, and deletion of biometric indexes. No promise that embedding similarity proves identity.
- Face embeddings and crop/vector caches stay local by default. Per the owner's sidecar request, confirmed names/regions and human decisions belong in readable XMP metadata with clear storage controls. Optional encrypted inference-cache recovery needs a separate key-recovery design.
- Separate pet recognition feasibility from human face recognition. Add scene/object search only after memory, battery, and indexing measurements.
- Acceptance: correction persists across rescans, deleting indexes removes embeddings and labels, offline recognition works, and uncertain matches remain unassigned.

## Sync and recoverability model

Assets have local IDs independent of Drive IDs. Originals/edits are immutable; remote ID + revision identifies a verified backup. Queue states include pending, running, backed-up, authorization-required, conflict, and failed. Handoffs have an originating asset ID and persist through process death. Retry-safe operations use stable IDs recorded in Drive app properties. Device-local access times are not shared metadata. Account/folder namespaces apply to all remote state and worker inputs.

Drive access tokens are short-lived, held in memory, and obtained through supported Google authorization APIs. Public Android OAuth client identifiers are configuration, not AI keys. Configure Google Cloud project, consent screen, signing certificate/package association, and test users in milestone 2; never request secrets in chat.

## Delivery and validation

Keep code, this plan, build instructions, tests, CI, and later database migrations in the owner's GitHub repository. Each milestone should result in a buildable commit and a debug APK. Do not label planned capabilities as working features. Use a test Google account and disposable folder for destructive sync tests. The first release stays read/import/upload-oriented until deletion recovery is proven.
