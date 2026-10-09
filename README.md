# MyPhotosAnywhere (MPA)

An Android photo library with offline browsing, Google Drive storage, and AI editing through the user's existing provider apps. No AI API keys or backend subscription are required.

Repository: [GreenFuze/MyPhotosAnywhere](https://github.com/GreenFuze/MyPhotosAnywhere). The owner's selected license is included in [LICENSE](LICENSE).

## Project status

MPA includes a native offline gallery, photo import, thumbnails, AI handoff, and linked-copy result import. The 0.2 implementation adds Google authorization, Drive folder selection/creation, resumable background backup, and explicit verified downloads for offline browsing. It preserves original files and stores a persistent transfer queue. Configure [Google Drive OAuth](docs/GOOGLE_DRIVE_SETUP.md) before a live test.

Version 0.2 passes 21 unit/integration tests, Android lint, and APK assembly. [Download the debug APK and checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/37954645025) from the workflow artifacts. A configured Google project and live-account/device validation are still needed.

Automatic Drive change reconciliation, recursive discovery, remote thumbnails and cache-budget eviction, conventional editing, face recognition, albums, and map browsing remain planned. See [the implementation plan](docs/PLAN.md) and [validation evidence](docs/VALIDATION.md). Automated checks do not replace a real-device and Google-account test.

## Development

To choose a clone directory and continue in a local Codex session, follow [the local setup guide](docs/LOCAL_DEVELOPMENT.md).

Open this folder in Android Studio with JDK 17 or newer and Android SDK 35 installed. The project uses Kotlin, Jetpack Compose, Room, Coil, and coroutines. Android 8.0 (API 26) is the minimum supported version.

Build and test using the included Gradle 8.11.1 wrapper (on Windows, use `gradlew.bat`):

```sh
./gradlew testDebugUnitTest lintDebug assembleDebug
```

GitHub Actions uses the pinned wrapper and builds an installable debug APK. Signing credentials, OAuth tokens, user photos, and local SDK paths must never be committed. A Google Android OAuth client is configured by package/signing-certificate association, without an AI API key or Android client secret.

## First milestone workflow

1. Use **Add photos** to select images with Android's system photo picker. MPA copies them into its private, durable storage and generates small thumbnails.
2. Open a photo, select an AI provider, and enter editing instructions. MPA exports a bounded-resolution JPEG without EXIF metadata. It copies the instructions to the clipboard and opens Android's share sheet; choose the provider's app if it accepts images.
3. Finish the edit in the provider's official app using your subscription. Save its output, return to MPA, and use **Import AI result**. The output is stored as a new photo linked to the original.

Sharing does not automatically attach a result to MPA, start a paid operation, or guarantee that a provider accepts the prompt text. Provider app capabilities and subscriptions control what edits are available. Claude may help with instructions and analysis without generating an edited image.

Imported originals and edited outputs are never treated as disposable cache. Enable backup in Storage to queue them to your selected Drive folder; a photo is only marked backed up after remote size/checksum verification. This version keeps all local originals. Uninstalling MPA or clearing Android app data deletes those device copies; keep your source photos. Android cloud backup is disabled for the photo library.

## Privacy

The app requests no broad gallery or location permission. Drive access is requested when connecting your account; the existing-library mode requests a broader Drive scope and explains it before consent. MPA accesses photo contents inside the selected folder and uploads after backup is enabled. Explicit offline downloads and the system photo picker import selected images. AI handoff exports one chosen preview at a time, without EXIF metadata, although the visible picture can still reveal location or identity. The prompt is explicitly copied to Android's clipboard when handing off. Drive originals retain their source EXIF, including embedded GPS; only handoff previews strip it.
