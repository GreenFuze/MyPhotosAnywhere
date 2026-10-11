# Google Photos imports and Takeout metadata conversion

Implemented in MPA 0.5.0 / version code 11, Room v7. Local checks and required CI passed on the immutable source recorded in [release evidence](releases/v0.5.0.json). Google Cloud Picker configuration was completed and verified on 2026-10-11 in the existing `myphotosanywhere` project. Real-account phone acceptance remains outstanding.

**0.5.2 / code 13 / Room v8** restores direct ZIP file selection and reads the Android-granted file descriptor without reopening a filesystem path. The sole connected Drive folder remains the automatic import destination. All 144 unit/integration and 36 native checks (18 each on Android 10 and 15) passed. See [validation](VALIDATION.md) and [release evidence](releases/v0.5.2.json).

## Background progress in 0.5.3

Takeout and Photos Picker media imports run through WorkManager when you leave MPA. Version 0.5.3 promotes active import work to a data-sync foreground service with the monochrome MPA status-bar icon and a silent **Photo imports** notification. It shows verified files / selected files and the current file's preparation, upload percentage or metadata commit. Uploading bytes count as imported only after remote verification and the metadata/link transaction. Tap the notification to open Storage. Completion, pause, cancellation and failures leave a dismissible summary.

On Android 13 or later, allow notifications when starting/resuming an import. Refusing notification permission does not block the import. For an already running import, open Storage and choose **Allow import notifications** or **Open notification settings**; enable MPA and its **Photo imports** channel. Android hides the status-bar/drawer notification when permission or the channel is disabled.

The foreground worker keeps one notification across files and yields between files after an eight-minute batch. Existing durable staging, reserved upload IDs and acknowledged offsets remain unchanged. WorkManager reschedules with the captured destination's network requirements. Android can stop/defer work, including foreground-service or job quotas, loss of the required connection and force-stop. This is resumable background work, not an uninterrupted-transfer guarantee. A summary is not an ongoing service after work stops. Pending/waiting jobs appear in Storage; a progress icon is shown while work is active.

The owner reports that a real ZIP import is running in 0.5.2. Completion, interrupted-import recovery, Picker and Google metadata cleanup still require phone acceptance. All 152 unit/integration and 40 native checks (20 each on Android 10 and 15) passed. See [validation](VALIDATION.md) and [release evidence](releases/v0.5.3.json).

Official Android contracts: [long-running WorkManager workers](https://developer.android.com/develop/background-work/background-tasks/persistent/how-to/long-running), [notification permission](https://developer.android.com/develop/ui/compose/notifications/notification-permission), [Android 15 data-sync limits](https://developer.android.com/about/versions/15/behavior-changes-15).

## Takeout and Drive connection failures in 0.5.4

Reading the Takeout ZIP does not call the Google Photos API. MPA imports selected media into the captured **Google Drive destination**, so authorization, destination checks and verified uploads require the Drive API at `www.googleapis.com`. A hostname-resolution error means MPA cannot reach that server; it does not establish that the MP4 is unsupported or corrupt. A saved `staging` row can remain from an interrupted attempt while a new attempt fails its initial Drive check.

Version 0.5.4 / code 15 keeps Room v8. DNS, socket timeout and interrupted-connection errors have a clear connection message and receive up to **three automatic retries**, with exponential WorkManager backoff beginning at 30 seconds and respecting the source's network requirements. Waiting/retry messages appear in Storage and a dismissible notification. After the retry limit the job fails visibly; restore connectivity and choose **Import selected / Resume**. Existing reserved IDs, acknowledged offsets, staged files, verified files and original ZIP grants are retained. Pause/cancel cannot be undone by a late connection failure. HTTP permission/authorization, malformed data and local storage errors remain fail-fast rather than being classified as connection failures.

For a current DNS failure, try another working Wi-Fi network and inspect any VPN or Private DNS configuration. If using mobile data, the destination's Wi-Fi-only preference still applies. Resume the same job instead of creating a replacement import. MPA cannot repair phone/network DNS, and retrying does not bypass permissions or guarantee uninterrupted background work. Staging byte progress and read deadlines remain separate follow-up work; this release addresses the reported Drive-resolution error, without claiming a reproduced MP4 decoder defect.

All 160 unit/integration and 42 native checks (21 each on Android 10 and 15) passed. See [validation](VALIDATION.md) and [release evidence](releases/v0.5.4.json). The owner supplied an MP4 `staging` screenshot and a Google API hostname-resolution error on 0.5.2; real-phone recovery remains to be checked.

## Dismissing previous import errors in 0.5.5

Stopped imports with saved job, file or historical download errors offer **Dismiss errors** in Storage. This clears those saved messages and the import's summary notification, including messages in its preview. The dismissal survives app restarts. It retains import history/status, selection, destination, source ZIP access, staged files, reserved IDs, upload sessions, acknowledged offsets and verified media. A failed import stays failed and **Import selected / Resume** remains available; dismissal does not retry, cancel or delete anything. A later failure is shown again.

Active imports and downloads keep their current messages; pause first to dismiss them. The database transaction rechecks the state so a job resumed since the button appeared cannot have its current errors cleared. Drive metadata-conversion review messages are separate from import-job errors.

Development and CI checks are pending; no 0.5.5 release is claimed yet. Real-phone acceptance remains pending.

## Import destinations and controls

In Storage, connect a writable Drive folder. When exactly one folder is connected, it is automatically the default import destination, even if ordinary backup is paused. With several connected folders, select **Use as default backup destination** on the intended folder. Automatic import destination selection does not enable ordinary local-media backup. Each new import captures this account/folder permanently. Changing the default affects future jobs only. Disconnecting its source pauses an import; reconnect the original source to resume it.

**Import Takeout ZIP parts** opens Android’s Files picker directly. Select one or more downloaded ZIP files from Downloads or USB storage. **Import extracted Takeout folder** accepts an Android document tree. MPA retains read access, builds a durable manifest, and previews media, supplemental metadata, unsupported items and unresolved matches. Choose **Add ZIP parts to this import** on a preview, paused, failed or completed Takeout job to attach later parts explicitly. Keep parts of unrelated exports in separate jobs. Late JSON can attach to completed media without uploading another copy.

Review selection and destination, then choose **Import selected / Resume**. **Pause import** preserves checkpoints. **Cancel remaining import** keeps verified Drive media, source archives and any unfinished staging bytes. Cancel does not remove uploaded media. Retry uses the reserved Drive ID and server-acknowledged upload offset. A process restart resumes running jobs; paused jobs stay paused.

The staging path holds one active media transfer. SHA-256 and length identify exact duplicates inside the captured Drive source; the current remote checksum/length/parent are rechecked before reuse. File names alone never deduplicate media. New uploads must pass checksum/length/destination verification before the database atomically links the Drive asset, metadata and completed import item. Migration bytes become ordinary source-backed media; local staging is released after this commit. Protected pre-existing local imports and edits are not converted into disposable cache.

## File access and recovery (0.5.2)

Android grants read access when you select a document using Files; ZIP imports do not require a separate broad storage permission prompt. MPA checks the returned read/persistable flags and retains only read access for resumable jobs. If a provider refuses lasting access, save the ZIP in Downloads or USB storage and select it there. After moving/deleting a source or revoking access, select the unchanged ZIP again with **Add ZIP parts to this import**, then resume. Keep your ZIP until the import finishes.

The reader uses the granted descriptor directly, including when it belongs to another app. It no longer reopens `/proc/self/fd`, which can fail with access denied under Android scoped storage. Archives stay in place; MPA stages one media item at a time. Providers must support seeking; if they return a pipe instead, the app explains how to save/select a local copy. Encrypted ZIPs and unsupported compression fail before importing.

The URL entry option has been removed at the owner's request. Existing 0.5.1 download history, encrypted checkpoints and app-owned cleanup remain compatible in Room v8. Old downloads can be paused/cancelled; existing app-owned archives can be discarded explicitly or cleaned after verified imports. Browser-downloaded/local/USB ZIP files are always preserved.

Phone acceptance: update the installed app, open Storage → Import Takeout ZIP parts, choose a small real ZIP from Downloads, check its destination and metadata preview, then import and verify the Drive result. Pause/reopen/resume and reselect the unchanged ZIP if access was revoked. Test USB storage if used. Keep the downloaded ZIP until all files are verified. Real Google-account/S23 Ultra acceptance remains outstanding.

## Google Photos Picker setup and use

Verified on 2026-10-11: Google Photos Picker API is enabled, the Picker scope below is saved, and the existing Android client matches the released package/certificate. The app remains External / Testing with its existing test user. Drive scopes and both Android clients were preserved. This setup applies to the existing 0.5.0 APK.

For another project or a future setup:

1. Enable **Google Photos Picker API** alongside Drive API.
2. Add `https://www.googleapis.com/auth/photospicker.mediaitems.readonly` to the app's consent/data access setup. Keep development users in the test-user list.
3. Preserve the Android OAuth client for package `com.myphotosanywhere.mpa` and the actual installed signing certificate. Published CI builds use SHA-1 `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`; local builds can differ. No API key, client secret or server is required.

In Storage, choose **Choose from Google Photos**, select the Photos account, grant Photos access, and finish selection in Google's picker. Return to MPA and choose **Check completed selection**, preview, then **Import selected / Resume**. Photos and Drive account authorization are separate. The selection session is durable; expiring media URLs and authorization tokens are not stored in the database. MPA fetches fresh selected-item URLs before downloads. Expired/revoked sessions require selecting the items again; verified prior imports are preserved and exact bytes can be reused.

Picker supplies selected existing library items, not background access to the whole Photos library. Image downloads exclude location metadata; videos may be transcoded. MPA retains provider/item/account/capture-time provenance and the bytes actually delivered. It does not claim byte-identical camera originals or invent GPS coordinates.

## Unpacked Google metadata discovered in Drive

Ordinary Drive refresh also discovers JSON files in selected folders/subfolders, separately from gallery media. In Storage, **Google metadata found in Drive** shows pending/review/completed counts and errors. Enable XMP annotation backup, then opt into **Convert and clean up Google metadata** for each source. Subsequent synchronization queues conversion for that source.

Conversion accepts recognized Google Photos media sidecars only. The filename, Google title and same-folder media context must agree, with supported supplemental/truncated/numbered naming variations. Missing, duplicate, conflicting, unrelated and album-level JSON remain visible as review items and are not removed. MPA never guesses face-to-person assignments. Descriptions, capture time, location, people-name hints and the full bounded Google record are retained. Existing MPA annotations take precedence; conflicting reimports are preserved for review rather than overwritten. For a newly imported Takeout asset, exported capture time/location take precedence over embedded EXIF; both the original bytes and full provider record remain available.

For a cloud-only asset, conversion first queues one verified media download. It rechecks source scope and both media/JSON revisions, commits metadata locally, uploads the opt-in MPA XMP catalogue, then reads and verifies the complete Google record in the correct source's catalogue. Only after that gate does a revision-conditional request move the JSON to **Drive trash**. Media originals and Takeout ZIP archives are never removed. The JSON can be restored from Drive trash. Failure, backup conflict, size limits, cancellation, disabled backup or changed bytes preserve the JSON.

## Visible limits and live acceptance

- Media: 250 MiB per file. Larger items and unsupported formats are visible; source bytes remain intact.
- ZIP/folder manifest: 10,000 entries; declared expanded archive size at most 1 TiB; excessive compression ratios and unsafe paths fail. Archive access must be seekable. A cloud document provider may download the archive internally; MPA cannot promise bounded provider storage. Use local/USB storage if necessary.
- Supplemental JSON: 128 KiB per record, depth 32. Album-level and unrelated JSON remain in the source; complete recognized media records, including unknown fields, are retained in MPA provenance/XMP. Folder names supply album memberships; album-level JSON conversion is not supported.
- XMP: 2 MiB / 10,000 assets. Catalogue growth fails recoverably rather than silently dropping annotations. Catalogue partitioning and large-video support remain future work.
- Picker: at most 2,000 selected items per session. API/consent/certificate configuration and real account checks remain required on the owner's phone.

The 0.5.0 release checks passed: 125 unit/integration tests, zero lint errors, CPU model checks, APK assembly and 12 Android emulator tests. Device tests include a synthetic ZIP round trip and both import controls. Live acceptance uses a small real album, Photos consent/selection, interrupted transfer, repeat import, late JSON, changed default destination, revoked authorization and a Drive sidecar conversion/cleanup round trip. User exports/photos are never committed.

Official contracts rechecked during development:

- [Picker sessions and polling](https://developers.google.com/photos/picker/reference/rest/v1/sessions)
- [Picked media schema](https://developers.google.com/photos/picker/reference/rest/v1/mediaItems)
- [Picker media downloads](https://developers.google.com/photos/picker/guides/media-items)
- [Drive file updates](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/update)

- [Takeout archive help page and browser sign-in](https://support.google.com/accounts/answer/3024190?hl=en-rd)

- [Android document access and persistent read grants](https://developer.android.com/training/data-storage/shared/documents-files)
- [Apache Commons Compress ZIP descriptor/channel support](https://commons.apache.org/proper/commons-compress/zip.html)
