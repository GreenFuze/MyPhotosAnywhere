# Continue locally

The code and implementation plan are in the private repository `GreenFuze/MyPhotosAnywhere`. Cloud workspace files do not automatically appear on a local computer.

## Clone into a directory you choose

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

Read [PLAN.md](PLAN.md), [VALIDATION.md](VALIDATION.md), and the README before making changes. The initial code implements local photo import, thumbnails, filename search, photo details including embedded GPS, AI handoff using the Android share sheet, and persistent handoffs for importing linked edited copies.

There are no AI API keys: users edit in their official ChatGPT, Gemini, or Claude app using their own subscription. Provider capabilities vary. The original is preserved, and the exported handoff preview is capped at 2048 pixels and strips EXIF metadata.

MPA 0.3 extends the original gallery and Drive transfer foundation with editing, people, places, albums, video import/playback, a recursive cloud index/change feed, queued downloads, and bounded disposable caching. Read [FEATURES_0_3.md](FEATURES_0_3.md) for implementation limits and device checks.

Install Python 3 as well as the Android toolchain. The Gradle asset task invokes `scripts/fetch_face_model.py`, downloads a versioned MobileFaceNet asset, and validates both archive and model SHA-256. Its Apache-2.0 license and provenance are included in the APK assets. If your platform calls Python `python` rather than `python3`, set the environment variable `MPA_PYTHON` to that executable for the build. CI additionally executes `scripts/verify_face_model.py` using the pinned NumPy/TFLite CPU runtime.

Recognition is an experimental review aid: confirmed identities seed suggestions, uncertain faces stay unassigned, and corrections remain local. There are no AI API keys or automatic uploads to AI providers.

Keep an owner-managed local debug signing key to preserve updates during local development. Register its SHA-1 in the same Google Cloud project. The published CI APK can use a different certificate; do not uninstall a phone installation containing the only copies of media. Production signing needs an owner-managed key and appropriate secret storage.

Next: finish automated verification and real-device acceptance in the 0.3 checklist, then continue the remaining roadmap. Do not promise that the limited `drive.file` scope grants access to all descendants of an existing folder. The app offers an explicit broader-scope mode for existing collections.
