# Google Drive setup and live test

MPA 0.2 uses Google Identity Services authorization on Android and the Drive v3 API. It does not use AI API keys, a backend, Firebase, a client secret, or stored refresh tokens. Android authorization associates the package and signing certificate with your Google Cloud project.

## Configure the Google project

1. Create or open a Google Cloud project and enable **Google Drive API**.
2. Configure Google Auth Platform branding, audience, and data access. For development, keep the app in testing and add the Google accounts you will use as test users.
3. Add the scopes needed for the access modes you want to test:
   - New MPA folder: `https://www.googleapis.com/auth/drive.file`.
   - Existing collection: `https://www.googleapis.com/auth/drive`.
4. Create an **Android** OAuth client for package `com.myphotosanywhere.mpa` and the SHA-1 fingerprint of the signing certificate used by the installed APK.
5. For a local debug build, run `./gradlew signingReport` (`gradlew.bat signingReport` on Windows) and use the debug variant's SHA-1. The actual keystore stays on your computer.
6. Build and install that APK. No Android client ID or client secret needs to be pasted into the app or this chat for this on-device authorization flow.

GitHub's debug APK normally uses a generated signing key that can differ between runners. The successful workflow's **MPA-checks** artifact includes `signing-report.txt`; register that APK's debug SHA-1, or use a locally built APK with your stable local debug certificate. Use the Play app-signing certificate for a future production build. Never commit a signing keystore or password.

Google's broad `drive` scope is restricted. Production publication may require Google's OAuth verification and other requirements depending on the deployment. Test users and testing mode are for development, not a substitute for production review. The `drive.file` scope does not grant automatic access to every file inside an existing selected folder; the app's new-folder mode intentionally shows only app-accessible folders/files.

Official references:

- [Android authorization and OAuth setup](https://developer.android.com/identity/authorization)
- [Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)
- [Resumable uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads)

## Use the app

1. Import a few disposable test photos locally.
2. Open **Storage**, choose the access mode, then **Choose Google account**. Accept Google's consent screen.
3. Browse folders, or create a folder named MyPhotosAnywhere. Open it and select **Use this folder**. Root My Drive cannot be selected as the app folder.
4. Choose **Enable backup for this library**. This queues the existing local library and future imports. Backup uses unmetered networks by default; changing the toggle changes WorkManager constraints.
5. Check the backed-up/pending counts. The worker reserves a Drive file ID, persists it, uploads in resumable 1 MiB chunks, and verifies the remote size, MD5, and parent folder. It never overwrites an existing mismatched file or deletes the original.
6. To read an existing image, use **Browse Drive photos** in the selected folder and **Make available offline**. The app downloads to a temporary private file, verifies checksum and revision, then imports it into the local gallery with its Drive filename. Only direct-child images of the selected folder are offered for import in this version.

## Required live checks

- Enable backup for several images, turn connectivity off mid-transfer, restore it, and verify each photo has one remote file. An expired session must reuse the persisted reserved file ID.
- Change the selected folder or disconnect during an upload. Old tasks must retain their old account/folder, pause, and never be retargeted. Switching destinations intentionally queues a new backup for the selected destination after you enable it.
- Revoke permission in Google settings, then reconnect in MPA. Background work must not launch an unsolicited consent screen. Verify authorization errors are visible and local files remain.
- Try a read-only folder and a deleted folder. The app must explain the failure and preserve the library.
- Download an existing image, restart in airplane mode, and browse it. Modify it remotely during download and confirm MPA rejects a checksum/revision mismatch.
- Upgrade a 0.1 install with local photos and a pending handoff. Both must survive the Room v1-to-v2 migration.

## Current limits

The selected folder is managed directly: uploads carry private `mpaAssetId`/`mpaParentId` properties, and original files and edited outputs are separate immutable assets. Subfolder organization, recursive discovery, automatic Drive change-feed reconciliation, remote thumbnail indexing, background download queues, storage-budget eviction, and remotely reconstructing album/edit relationships are later work. Folder listing is paginated; image downloads are explicit foreground actions with a 250 MiB limit. No remote deletion is implemented. All local originals remain protected, even after successful backup.

Disconnect pauses/removes the local connection and cancels workers. It does not revoke Google grants, remove backup-history rows, or delete any local/remote photo. The user can separately revoke grants in Google account settings.
