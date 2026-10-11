# Google Drive setup and live test

MPA 0.4 uses Google Identity Services authorization on Android and the Drive v3 API. It does not use AI API keys, a backend, Firebase, a client secret, or stored refresh tokens. Android authorization associates the package and signing certificate with your Google Cloud project.

## Published 0.3–0.4 APK certificate

For published 0.3 and 0.4 APKs (including 0.4.1 and 0.4.2), add an Android OAuth client with package `com.myphotosanywhere.mpa` and SHA-1:

```text
A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD
```

This identifies the established release debug signer; local builds can have another signer. In 0.4.2+, **Storage → Google setup details** shows the actual installed version, package and signing SHA-1 and can copy these public setup details. Keep the old 0.2 client if you still use that build. Preserve any app-only media before uninstalling an older differently signed APK.

## Configure the Google project

1. Create or open a Google Cloud project and enable **Google Drive API**.
2. Configure Google Auth Platform branding, audience, and data access. For development, keep the app in testing and add the Google accounts you will use as test users.
3. Add the scopes needed for the access modes you want to test:
   - New MPA folder: `https://www.googleapis.com/auth/drive.file`.
   - Existing collection: `https://www.googleapis.com/auth/drive`.
4. Create an **Android** OAuth client for package `com.myphotosanywhere.mpa` and the SHA-1 fingerprint of the signing certificate used by the installed APK.
5. For a local debug build, run `./gradlew signingReport` (`gradlew.bat signingReport` on Windows) and use the debug variant's SHA-1. The actual keystore stays on your computer.
6. Build and install that APK. No Android client ID or client secret needs to be pasted into the app or this chat for this on-device authorization flow.

GitHub CI caches its private debug keystore; the 0.4 publisher refuses to release an APK whose certificate differs from the established signer. The 0.3 prerelease uses a different signer from 0.2. The successful workflow's **MPA-checks** artifact includes `signing-report.txt`; register that APK's debug SHA-1, or use a locally built APK with your stable local debug certificate. Use the Play app-signing certificate for a future production build. Never commit a signing keystore or password.

To upgrade an existing installation while keeping its private library, sign the new build with the same certificate as the installed version. A differently signed debug APK cannot update it. Keep the original source copies and a verified backup; uninstalling removes the private library.

Google's broad `drive` scope is restricted. Production publication may require Google's OAuth verification and other requirements depending on the deployment. Test users and testing mode are for development, not a substitute for production review. The `drive.file` scope does not grant automatic access to every file inside an existing selected folder; the app's new-folder mode intentionally shows only app-accessible folders/files.

Official references:

- [Android authorization and OAuth setup](https://developer.android.com/identity/authorization)
- [Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)
- [Resumable uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads)

## If authorization closes or fails after reinstalling

The former “Google authorization canceled” message was too broad: it discarded non-OK activity results before the Google SDK could extract an actual error. Version 0.4.2 parses all returned Google result intents and distinguishes cancellation, consent-launch failure, missing data, OAuth configuration error (code 10), network error (code 7) and other status codes. This improves diagnosis; it does not repair an incorrectly configured Cloud project or bypass consent/account restrictions.

1. Open **Storage → Google setup details** and copy the installed package/certificate details. An unsuccessful upgrade can leave an older APK installed; check the actual version. A reinstall requires reconnecting sources.
2. In the Google Cloud project used by MPA, check **Google Auth Platform → Clients** (or APIs & Services → Credentials). Add an **Android** client with the installed package and SHA-1. Do not replace the old client if another build still uses it.
3. Check **Audience**: keep development in Testing and add the exact Google account you select in MPA under Test users. Organisation/account policies may still prevent access.
4. Check **Data Access**: existing collections need `https://www.googleapis.com/auth/drive`; the new-folder scope `drive.file` cannot browse arbitrary existing photos. Also confirm Google Drive API is enabled in that same project.
5. Retry and complete the Google screen. Record any Google error such as `access_denied`, `invalid_client`, or blocked access and MPA’s numeric code. A closed screen without result data cannot reveal the root cause by itself.

### Prompt for an agent on your computer

> Help diagnose MyPhotosAnywhere Android Google Drive authorization. The release package is `com.myphotosanywhere.mpa`, and published 0.3–0.4 APKs use Android signing SHA-1 `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Verify these against Storage → Google setup details on the installed APK or its signing report. In the existing Google Cloud project, verify Drive API is enabled, an Android OAuth client matches the installed package/certificate, the development consent screen is in Testing and my selected Google account is a test user, and data access includes both `https://www.googleapis.com/auth/drive.file` and `https://www.googleapis.com/auth/drive`. Preserve existing OAuth clients and signing keys. Existing-folder access requests the full Drive scope. Diagnose any Google-screen error and MPA status code; do not assume “canceled” proves I pressed Cancel. Explain any account-policy block. Do not create a client secret, put API keys into MPA, regenerate signing keys, or change publication status. Report the project and configuration changes, then help retry on my phone.

## Use the app

1. Import a few disposable test photos locally.
2. Open **Storage**, choose the access mode, then **Choose Google account**. Accept Google's consent screen.
3. Browse folders, or create a folder named MyPhotosAnywhere. Open it and select **Use this folder**. Root My Drive cannot be selected as the app folder.
4. Choose **Use as default backup destination**. There is one default destination across all sources for new imports and edited copies. Historical assets with an existing destination are not retargeted. Backup uses unmetered networks by default; changing the toggle changes WorkManager constraints.
5. Check the backed-up/pending counts. The worker reserves a Drive file ID, persists it, uploads in resumable 1 MiB chunks, and verifies the remote size, MD5, and parent folder. It never overwrites an existing mismatched file or deletes the original.
6. Selecting the folder starts recursive image/video discovery. Browse cloud previews in Photos, tap an item to queue its verified download, or pin an album for offline use. Refresh and download status appear in Storage. Downloads are staged privately and checked against size, checksum, revision, and folder ancestry before appearing locally. Wi-Fi-only network constraints apply by default.

## Required live checks

- Enable backup for several images, turn connectivity off mid-transfer, restore it, and verify each photo has one remote file. An expired session must reuse the persisted reserved file ID.
- Change the selected folder or disconnect during an upload. Old tasks must retain their old account/folder, pause, and never be retargeted. Changing the default applies to unassigned assets and new imports/edits; existing assigned tasks remain attached to their original destination.
- Revoke permission in Google settings, then reconnect in MPA. Background work must not launch an unsolicited consent screen. Verify authorization errors are visible and local files remain.
- Try a read-only folder and a deleted folder. The app must explain the failure and preserve the library.
- Download an existing image, restart in airplane mode, and browse it. Modify it remotely during download and confirm MPA rejects a checksum/revision mismatch.
- With a matching signing key, upgrade a 0.1/0.2 install with local photos and a pending handoff. Both must survive the Room v1/v2-to-v3 migration. A differently signed APK requires preserving media before uninstalling; an uninstall deletes the database.
- Exercise a nested folder with more than 100 files. Move/delete files remotely and modify a downloaded file. Refresh must update the cloud index without deleting preserved local snapshots.
- Set a small cache budget and pin an item/album. Clear downloads with and without connectivity. Only unprotected, remotely reverified downloads may be evicted.

## Current limits

Uploads remain directly in the selected folder and carry private `mpaAssetId`/`mpaParentId` properties; originals and edited outputs are separate immutable assets. Existing nested folders are indexed without reorganizing unrelated files. Discovery and change-feed reconciliation are automatic; thumbnails are requested as needed and verified downloads are queued in the background. Imports/downloads have a 250 MiB per-file limit. Interrupted downloads restart from staging, while uploads resume from acknowledged offsets.

The opt-in source-scoped XMP catalogue can back up and restore album/name/recipe/place annotations and reviewed face regions for checksum-matched media. Face embeddings and crop files remain on the phone and are rebuilt locally. See [FEATURES_0_4.md](FEATURES_0_4.md) for recovery/privacy/size limits. Catalogue encryption and adjacent per-image sidecars are future work. No remote deletion is implemented. Imported originals and edited copies remain protected even after backup; only safely verified, unprotected Drive downloads are disposable. Offline verification failures preserve files beyond the soft cache budget. See [FEATURES_0_3.md](FEATURES_0_3.md) for exact behavior.

Disconnect pauses/removes the local connection and cancels workers. It does not revoke Google grants, remove backup-history rows, or delete any local/remote photo. The user can separately revoke grants in Google account settings.

## Google Photos Picker in 0.5.0

Photos authorization is separate from Drive. Enable Google Photos Picker API and its `https://www.googleapis.com/auth/photospicker.mediaitems.readonly` consent scope in the same existing project, preserving Android clients, Drive scopes, testing status and test users. See [import setup and controls](GOOGLE_IMPORTS.md). Browser automation could not initialize during development, so no Cloud configuration changes were made or confirmed.
