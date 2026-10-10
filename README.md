# MyPhotosAnywhere (MPA)

An Android photo library with offline browsing, Google Drive storage, and AI editing through the user's existing provider apps. No AI API keys or backend subscription are required.

Repository: [GreenFuze/MyPhotosAnywhere](https://github.com/GreenFuze/MyPhotosAnywhere). The owner's selected license is included in [LICENSE](LICENSE).

## Project status

The 0.3 implementation adds photo editing (crop/rotate/straighten, adjustments, filters, markup, saved drafts, undo/redo), albums/favorites/archive, video import/playback, optional on-device faces and object labels, place search and a clustered map, recursive Drive discovery, change reconciliation, queued downloads, and storage budgets. AI editing still uses the user's official provider app and subscription through a share-sheet handoff.

Download [v0.3.0](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.3.0), an experimental test prerelease. Its source passed 49 unit/integration tests, lint, APK assembly, and two Android emulator smoke tests. Configure [Google Drive OAuth](docs/GOOGLE_DRIVE_SETUP.md) for the exact APK signing certificate you install. The user has confirmed the initial Google setup works; new feature behavior still needs phone testing.

Read [0.3 feature behavior and the phone checklist](docs/FEATURES_0_3.md), [the roadmap](docs/PLAN.md), and [validation evidence](docs/VALIDATION.md). Full Google Photos parity is an ongoing roadmap, including video editing, calibrated recognition, production signing, and optional encrypted metadata recovery.

## Development

To choose a clone directory and continue in a local Codex session, follow [the local setup guide](docs/LOCAL_DEVELOPMENT.md).

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

Face indexing and object labeling are optional and run on the phone. Face crops, embeddings, names, albums, and edit recipes remain local. Online OpenStreetMap tiles are off by default; enabling them shares the viewed map area with the tile service. No background GPS tracking or online geocoding is used.

The app requests no broad gallery or location permission. Drive access is requested when connecting your account; the existing-library mode requests a broader Drive scope and explains it before consent. MPA accesses photo contents inside the selected folder and uploads after backup is enabled. Explicit offline downloads and the system photo picker import selected images. AI handoff exports one chosen preview at a time, without EXIF metadata, although the visible picture can still reveal location or identity. The prompt is explicitly copied to Android's clipboard when handing off. Drive originals retain their source EXIF, including embedded GPS; only handoff previews strip it.
