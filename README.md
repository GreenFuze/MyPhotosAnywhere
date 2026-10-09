# MyPhotosAnywhere (MPA)

An Android photo library with offline browsing, Google Drive storage, and AI editing through the user's existing provider apps. No AI API keys or backend subscription are required.

Repository: [GreenFuze/MyPhotosAnywhere](https://github.com/GreenFuze/MyPhotosAnywhere). The owner's selected license is included in [LICENSE](LICENSE).

## Project status

Milestone 1 is under development: native gallery, durable local import, thumbnails, AI handoff, and importing the resulting edit as a linked copy. Google Drive synchronization, conventional editing, face recognition, and map browsing are planned milestones, not finished features. See [the implementation plan](docs/PLAN.md).

## Development

Open this folder in Android Studio with JDK 17 or newer and Android SDK 35 installed. The project uses Kotlin, Jetpack Compose, Room, Coil, and coroutines. Android 8.0 (API 26) is the minimum supported version.

Build and test using Gradle 8.11.1:

```sh
gradle testDebugUnitTest lintDebug assembleDebug
```

GitHub Actions installs the specified Gradle version and builds an installable debug APK. Signing credentials, Google OAuth credentials, user photos, and local SDK paths must never be committed.

## First milestone workflow

1. Use **Add photos** to select images with Android's system photo picker. MPA copies them into its private, durable storage and generates small thumbnails.
2. Open a photo, select an AI provider, and enter editing instructions. MPA exports a bounded-resolution JPEG without EXIF metadata. It copies the instructions to the clipboard and opens Android's share sheet; choose the provider's app if it accepts images.
3. Finish the edit in the provider's official app using your subscription. Save its output, return to MPA, and use **Import AI result**. The output is stored as a new photo linked to the original.

Sharing does not automatically attach a result to MPA, start a paid operation, or guarantee that a provider accepts the prompt text. Provider app capabilities and subscriptions control what edits are available. Claude may help with instructions and analysis without generating an edited image.

Imported originals and edited outputs are never treated as disposable cache. Until Drive synchronization is implemented and confirms uploads, the private app files may be the only MPA copies. Uninstalling MPA or clearing Android app data deletes those copies; keep the source photos. Android cloud backup is disabled for the photo library.

## Privacy

The initial app requests no broad gallery, location, or network permissions. Only explicitly selected images are imported. AI handoff is user-initiated and exports one selected image at a time. GPS metadata remains local; the handoff image has no EXIF metadata, although the visible picture can still reveal location or identity. The prompt is explicitly copied to Android's clipboard when handing off.
