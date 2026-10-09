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

MPA 0.2 adds Google authorization, paginated Drive folder selection/creation, account/folder-bound background backup, resumable uploads with verified remote checksums, and explicit downloads of images in the selected folder. Read [GOOGLE_DRIVE_SETUP.md](GOOGLE_DRIVE_SETUP.md) for the Android OAuth package/certificate configuration and live checks. Recursive discovery, Drive changes tokens, remote thumbnail indexing, cache-budget enforcement, and background download queues remain outstanding. The cache eviction policy is a standalone component that must only be connected to files with verified Drive copies. Face recognition, conventional editing, map browsing, albums, and video support remain planned. Imported originals are protected durable files, not evictable cache.

Next: complete live Google/device transfer and migration checks, then extend remote indexing and controlled download caching. Do not request AI keys or promise that the limited `drive.file` scope grants access to all descendants of an existing folder. The app offers an explicit broader-scope mode for existing collections.
