# Google Photos imports and Takeout metadata conversion

Implemented in MPA 0.5.0 / version code 11, Room v7. Local checks and required CI passed on the immutable source recorded in [release evidence](releases/v0.5.0.json). Google Cloud Picker configuration was completed and verified on 2026-10-11 in the existing `myphotosanywhere` project. Real-account phone acceptance remains outstanding.

## Import destinations and controls

In Storage, connect a writable Drive folder. When exactly one folder is connected, it is automatically the default import destination, even if ordinary backup is paused. With several connected folders, select **Use as default backup destination** on the intended folder. Automatic import destination selection does not enable ordinary local-media backup. Each new import captures this account/folder permanently. Changing the default affects future jobs only. Disconnecting its source pauses an import; reconnect the original source to resume it.

**Import Takeout ZIP parts** offers **Choose local / USB ZIP files** or **Download ZIP from URL**. Local selection accepts one or more ZIP files. **Import extracted Takeout folder** accepts an Android document tree. MPA retains read access, builds a durable manifest, and previews media, supplemental metadata, unsupported items and unresolved matches. Choose **Add ZIP parts to this import** on a preview, paused, failed or completed Takeout job to attach later parts explicitly. Keep parts of unrelated exports in separate jobs. Late JSON can attach to completed media without uploading another copy.

Review selection and destination, then choose **Import selected / Resume**. **Pause import** preserves checkpoints. **Cancel remaining import** keeps verified Drive media, source archives and any unfinished staging bytes. Cancel does not remove uploaded media. Retry uses the reserved Drive ID and server-acknowledged upload offset. A process restart resumes running jobs; paused jobs stay paused.

The staging path holds one active media transfer. SHA-256 and length identify exact duplicates inside the captured Drive source; the current remote checksum/length/parent are rechecked before reuse. File names alone never deduplicate media. New uploads must pass checksum/length/destination verification before the database atomically links the Drive asset, metadata and completed import item. Migration bytes become ordinary source-backed media; local staging is released after this commit. Protected pre-existing local imports and edits are not converted into disposable cache.

## Takeout ZIP download URLs (0.5.1)

Choose **Import Takeout ZIP parts → Download ZIP from URL** and paste a direct HTTPS ZIP link. **Download and import** captures the destination immediately, downloads into MPA private storage, validates the ZIP, pairs supplemental metadata, and automatically runs the existing verified migration pipeline. Uncheck **Import automatically after download** to stop at preview instead. Progress and Pause/Resume are in Storage; Android runs the ZIP download as a foreground data-sync worker. The destination source's network preference applies. Running work resumes after reopening MPA; paused work stays paused.

A download link must return ZIP bytes. Google's emailed Takeout link normally opens its export page and may require browser password/2-step verification; MPA does not read browser cookies or automate that sign-in. Download through your browser and choose the local ZIP in that case. Expired links and HTML sign-in pages fail visibly; **Paste a fresh download link** restarts the archive safely before its manifest is committed. Links can contain temporary credentials: they are encrypted with an Android Keystore key before persistence, never included in XMP, saved UI state or arbitrary network error messages. Redirects are limited and must remain HTTPS.

Downloads stream to disk, fsync progress before saving checkpoints, and resume only with a matching strong ETag, total and exact HTTP byte range. Missing/weak validators or a server ignoring Range cause a full restart rather than appending potentially different bytes. No second extracted library is created. Phone storage must fit the entire archive plus a 314 MiB reserve for one media file and working space. Downloaded ZIPs are limited to 50 GiB; smaller Takeout parts are preferable on phones. Existing 250 MiB media, 10,000-entry and metadata catalogue limits still apply.

After every media item is committed to verified Drive storage, MPA deletes only its own downloaded ZIP; supplemental JSON remains in the durable import manifest. Crash recovery and **Retry phone ZIP cleanup** finish interrupted cleanup. Skipped, failed or unsupported media/other files keep the ZIP for review. **Delete downloaded ZIP from phone** explicitly discards that app-owned archive and unfinished app transfer copies after pausing; verified Drive media remains. Local/USB ZIPs, browser Downloads files, extracted folders, Google Photos and Drive originals are never deleted. Late local/USB parts can still add metadata to an already completed job after its downloaded ZIP is removed.

Phone acceptance: use a small direct ZIP link, confirm automatic destination with only one connected folder, interrupt/reopen and resume the download, verify import hashes/metadata and ZIP removal, then try an expired/sign-in link and a ZIP with unsupported/skipped files. Real Google-account/S23 Ultra acceptance remains outstanding.

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

Release checks passed: 125 unit/integration tests, zero lint errors, CPU model checks, APK assembly and 12 Android emulator tests. Device tests include a synthetic ZIP round trip and both import controls. Live acceptance uses a small real album, Photos consent/selection, interrupted transfer, repeat import, late JSON, changed default destination, revoked authorization and a Drive sidecar conversion/cleanup round trip. User exports/photos are never committed.

Official contracts rechecked during development:

- [Picker sessions and polling](https://developers.google.com/photos/picker/reference/rest/v1/sessions)
- [Picked media schema](https://developers.google.com/photos/picker/reference/rest/v1/mediaItems)
- [Picker media downloads](https://developers.google.com/photos/picker/guides/media-items)
- [Drive file updates](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/update)
