# MyPhotosAnywhere (MPA)

An Android photo library with offline browsing, Google Drive storage, and AI editing through the user's existing provider apps. No AI API keys or backend subscription are required.

Repository: [GreenFuze/MyPhotosAnywhere](https://github.com/GreenFuze/MyPhotosAnywhere). The owner's selected license is included in [LICENSE](LICENSE).

## Project status

The [0.4.4 release](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.4.4) includes multiple Drive folders/accounts, one chosen backup destination, shared person identities, source-scoped XMP annotation backup/recovery, and bounded phone scans with pause/resume and a charging/night window. The 0.4.3 update adds face-group crop review, possible matches under named people, local matching diagnostics and a full rescan that preserves names. Google authorization error handling and installed OAuth setup details from 0.4.2 remain included. The included 0.4.1 fixes make Scan now prominent, open photos directly with original-region zoom, and hide externally deleted Drive photos from the main gallery while retaining downloaded copies. It includes the original MPA adaptive launcher icon and preserves the 0.3/alpha debug signer.

Editing includes crop/rotate/straighten, adjustments, filters, markup, saved drafts and undo/redo. The library includes albums/favorites/archive, videos, object labels, places/maps, recursive Drive discovery, verified downloads and cache budgets. AI edits use a share-sheet handoff to the user's official provider app and subscription.

This is a development APK for phone testing. Individual pet recognition and Google Photos level recognition accuracy remain future work. Cats/dogs are searchable object labels. Production signing, broader phone coverage, adjacent/MWG sidecars, large-catalogue partitioning and automatic conflict merging remain on the roadmap.

Read [0.4 behavior and the phone checklist](docs/FEATURES_0_4.md), [validation evidence](docs/VALIDATION.md), and [Google OAuth setup](docs/GOOGLE_DRIVE_SETUP.md). XMP backup is opt-in because readable catalogues contain names and locations; each selected source receives only its own annotation projection. Shared folders expose those annotations to their readers. Embeddings and face crops stay on the phone. Existing catalogues require recovery review before this phone can overwrite them.

The [implementation stages](docs/IMPLEMENTATION_0_4.md) and [recognition design](docs/RECOGNITION_AND_SIDECARS.md) distinguish shipped features from later model evaluation and pet identity work.

## Development

To choose a clone directory and continue in a local Codex session, follow [the local setup guide](docs/LOCAL_DEVELOPMENT.md).

The next agreed milestone is reliable bulk Drive ingestion plus Google Photos Takeout and Picker imports. See [the implementation plan](docs/IMPORT_AND_DRIVE_SYNC_PLAN.md) and [the Windows Codex handoff](docs/LOCAL_CODEX_HANDOFF.md), starting with a clone into `C:\src\github.com\GreenFuze\MyPhotosAnywhere`.

The 0.4.4 increment adds durable, bounded Drive discovery and per-source progress. Read the [ingestion audit](docs/DRIVE_INGESTION_AUDIT.md) and [validation evidence](docs/VALIDATION.md) for its tested boundaries and remaining phone checks. Takeout and Picker remain subsequent stages.

Open this folder in Android Studio with JDK 17, Android SDK 35, and Python 3 installed. Gradle fetches and verifies the licensed, pinned face model automatically (set `MPA_PYTHON` to your Python executable if `python3` is unavailable). The project uses Kotlin, Jetpack Compose, Room, Coil, and coroutines. Android 8.0 (API 26) is the minimum supported version.

Build and test using the included Gradle 8.11.1 wrapper (on Windows, use `gradlew.bat`):

```sh
./gradlew testDebugUnitTest lintDebug assembleDebug
```

GitHub Actions uses the pinned wrapper and builds an installable debug APK. Signing credentials, OAuth tokens, user photos, and local SDK paths must never be committed. A Google Android OAuth client is configured by package/signing-certificate association, without an AI API key or Android client secret.

## First milestone workflow

1. Use **Add** to select images or videos with Android's system photo picker. MPA copies them into its private, durable storage and generates small thumbnails.
2. Open a photo, select an AI provider, and enter editing instructions. MPA exports a bounded-resolution JPEG without EXIF metadata. It copies the instructions to the clipboard and opens Android's share sheet; choose the provider's app if it accepts images.
3. Finish the edit in the provider's official app using your subscription. Save its output, return to MPA, and use **Import AI result**. The output is stored as a new photo linked to the original.

Sharing does not automatically attach a result to MPA, start a paid operation, or guarantee that a provider accepts the prompt text. Provider app capabilities and subscriptions control what edits are available. Claude may help with instructions and analysis without generating an edited image.

Imported originals and edited outputs are never treated as disposable cache. Enable backup in Storage to queue them to your selected Drive folder; a photo is only marked backed up after remote size/checksum verification. Storage controls may remove only verified, unprotected Drive downloads; imports and edited outputs remain durable. Uninstalling MPA or clearing Android app data deletes those device copies; keep your source photos. Android cloud backup is disabled for the photo library.

## Privacy

Face indexing and object labeling are optional and run on the phone. Face crops and embeddings remain local. Names, reviewed regions, albums, places and edit recipes are backed up to source-scoped XMP catalogues only when annotation backup is enabled. Each catalogue is readable to that Drive folder’s readers and currently has a 2 MiB limit. Online OpenStreetMap tiles are off by default; enabling them shares the viewed map area with the tile service. No background GPS tracking or online geocoding is used.

The app requests no broad gallery or location permission. Drive access is requested per connected account; the existing-library mode requests a broader Drive scope and explains it before consent. MPA accesses photo contents inside connected folders and uploads imports/edited copies to one chosen default destination after backup is enabled. Explicit offline downloads and the system photo picker import selected media; optional cloud indexing also downloads verified originals in bounded batches. Annotation backup writes a separate XMP catalogue in each connected writable folder after opt-in. AI handoff exports one chosen preview at a time, without EXIF metadata, although the visible picture can still reveal location or identity. The prompt is explicitly copied to Android's clipboard when handing off. Drive originals retain their source EXIF, including embedded GPS; only handoff previews strip it.
