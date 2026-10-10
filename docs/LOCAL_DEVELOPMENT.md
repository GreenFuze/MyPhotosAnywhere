# Continue locally

The code and implementation plan are in the private repository `GreenFuze/MyPhotosAnywhere`. Cloud workspace files do not automatically appear on a local computer. For the owner's requested Windows location and an actionable continuation prompt, use [LOCAL_CODEX_HANDOFF.md](LOCAL_CODEX_HANDOFF.md).

## Clone into the requested Windows directory

The project checkout belongs at `C:\src\github.com\GreenFuze\MyPhotosAnywhere`:

```powershell
New-Item -ItemType Directory -Force -Path 'C:\src\github.com\GreenFuze' | Out-Null
git clone https://github.com/GreenFuze/MyPhotosAnywhere.git 'C:\src\github.com\GreenFuze\MyPhotosAnywhere'
```

If the directory already exists, inspect and preserve it rather than cloning over it. The handoff includes an idempotent bootstrap and exit-code checks. Authenticate using Git Credential Manager/GitHub Desktop or your existing GitHub CLI login.

## Other clone locations

In GitHub Desktop, use **Clone repository**, choose `GreenFuze/MyPhotosAnywhere`, and set **Local path**. Alternatively, use Git:

```sh
git clone https://github.com/GreenFuze/MyPhotosAnywhere.git "YOUR_CHOSEN_DIRECTORY/MyPhotosAnywhere"
```

Replace `YOUR_CHOSEN_DIRECTORY` with an actual path. Sign into GitHub using your normal Git credential manager or GitHub Desktop; do not put credentials in the command or repository.

## Open and build

1. In the ChatGPT desktop app, select Codex and open the cloned local folder. If your client offers a cloud/local switch for the existing chat, select the local folder there. Otherwise, start a local chat in that folder. The repository documents preserve the project decisions even if the chat does not move.
2. Open the same folder in Android Studio. Install Android SDK Platform 35 and Build Tools 35.0.0. Use JDK 17 or newer compatible with Gradle 8.11.1.
3. Build and test with the included wrapper:

```sh
./gradlew testDebugUnitTest lintDebug assembleDebug
```

On Windows:

```bat
gradlew.bat testDebugUnitTest lintDebug assembleDebug
```

4. Install `app/build/outputs/apk/debug/app-debug.apk` on a test phone, or run the app from Android Studio. Android 8.0 or later is required.

Official desktop/local-access guidance: https://help.openai.com/en/articles/20001275-chatgpt-work-and-codex

## Context for the next coding session

Read [the agreed import/sync plan](IMPORT_AND_DRIVE_SYNC_PLAN.md), [the local handoff](LOCAL_CODEX_HANDOFF.md), [current behavior](FEATURES_0_4.md), [VALIDATION.md](VALIDATION.md), and the README before making changes. Published baseline is 0.4.3, version code 9, Room v5, with 95 unit/integration tests and nine Android emulator tests. The first unreleased local increment adds durable Room v6 bulk-discovery checkpoints and visible source refresh/progress; see [the audit](DRIVE_INGESTION_AUDIT.md) and its remaining live acceptance checks. Continue with the shared migration engine, bounded resumable Takeout import and official Google Photos Picker import in that order.

There are no AI API keys: users edit in their official ChatGPT, Gemini, or Claude app using their own subscription. Provider capabilities vary. The original is preserved, and the exported handoff preview is capped at 2048 pixels and strips EXIF metadata.

The current app includes editing, people/group review/diagnostics, places, albums, videos, multiple Drive sources, recursive cloud discovery/change checkpoints, queued verified downloads, bounded disposable caching, source-scoped XMP backup/recovery and scheduled phone indexing. Read [FEATURES_0_4.md](FEATURES_0_4.md) for actual limits and device checks; the older 0.3 document is historical.

Install Python 3 as well as the Android toolchain. The Gradle asset task invokes `scripts/fetch_face_model.py`, downloads a versioned MobileFaceNet asset, and validates both archive and model SHA-256. Its Apache-2.0 license and provenance are included in the APK assets. If your platform calls Python `python` rather than `python3`, set the environment variable `MPA_PYTHON` to that executable for the build. CI additionally executes `scripts/verify_face_model.py` using the pinned NumPy/TFLite CPU runtime.

Recognition is an experimental review aid: confirmed identities seed suggestions, uncertain faces stay unassigned, and corrections remain local. There are no AI API keys or automatic uploads to AI providers.

Published CI APKs use SHA-1 `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Inspect existing local signing configuration before creating a key. A newly generated local debug certificate cannot update that phone installation. If the matching key is unavailable, test on an emulator/separate device or publish test builds through the established CI signer. Register a different local certificate as an additional Android OAuth client in the same project. Do not uninstall the owner's app to bypass a signing mismatch. Never commit key material. Production signing remains an owner-managed setup.

Next: follow the import/sync plan in order, using local builds for development and required CI before publishing. Do not promise that `drive.file` grants access to arbitrary descendants; the existing-collection mode requests broader Drive access. Picker requires its own Google API/scope setup and supplies selected media with less metadata than Takeout.
