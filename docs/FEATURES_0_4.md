# MPA 0.4.4: behavior and phone verification

This is an installable development build for testing, using the same debug signer as 0.3 and the 0.4 alphas. It is not a Google Photos quality recognition claim. Android 8+ is supported; automated device checks use Android 10. Keep independent backups of your photos.

## Face grouping and review in 0.4.3

Two usable appearances can form an unnamed group without a named reference or a large enrollment collection. Groups are deterministic across input order, require every pair to pass the existing similarity gate, and keep separate detections in one photo apart. Incompatible or invalid vectors remain available for manual naming. Naming a group applies to the selected appearances once; review includes each crop and a checkbox to leave unrelated faces out.

After naming an appearance, compatible matches are shown under that person's card as **Review N possible matches**. Confirm the selected crops to include them in the person's photos. The model and experimental similarity thresholds are unchanged; automatic suggestions still require review.

**Why separate?** explains missing/invalid recognition data, incompatible model versions, earlier manual corrections, insufficient similarity and ambiguous named matches. It shows actual local cosine scores and the closest compatible face in another photo. A detected face can lack an embedding: the current extractor rejects some small, angled or poorly aligned faces. Old scans do not record the specific extraction rejection, so the diagnostic cannot identify which gate failed.

**Rescan all faces** invalidates successful as well as failed checkpoints, preserves names/corrections/regions, and queues phone inference again. Evicted connected originals may be downloaded using the existing network preferences. **Scan now** continues to process new and failed photos without repeating successful unchanged scans. Stable geometry matches retain reviewed assignments; ambiguous and legacy regions stay available for manual review.

For the three-photo phone test: upgrade without uninstalling, open People, choose Rescan all faces, wait for completion, then check unnamed groups and possible matches under each named person. For any separate appearance, open Why separate? and report its status, closest named score and closest-face score. Similarity scores are not identity probabilities. More examples can help pose/lighting coverage; they are not needed to train the bundled pretrained model.

## Google authorization diagnostics in 0.4.2

Authorization return intents are parsed by Google's SDK even when the activity returns a canceled code, so a configuration failure is not automatically labeled as user cancellation. Missing result data and consent-launch failures have separate messages. **Storage → Google setup details** shows and copies the actual installed version, package, signing SHA-1 and requested Drive scopes; it is also available beside a connection error. Google Cloud must still grant access for the configured project/account. See [Google setup](GOOGLE_DRIVE_SETUP.md#if-authorization-closes-or-fails-after-reinstalling).

## Phone testing fixes in 0.4.1

**Scan now** is at the top of People and Storage, with opt-in choices when detection is disabled. Photos open full screen on tap; cloud-only items show original-download state. The viewer decodes visible original-file regions for zoom instead of enlarging a fixed 2,048-pixel image, with EXIF orientation and bounded decoded memory. Formats without region support show a preview-only label.

After a successful refresh, externally deleted Drive photos and cached copies disappear from the main gallery, while downloaded originals and annotations remain available through Offline, favorites and albums. Active overlapping sources keep shared photos visible. Independent imports are retained. Returning to the app queues refreshes subject to each source's network preference. See the [0.4.1 phone retest](releases/v0.4.1.md).

## Multiple sources and one identity library

Connect several Drive accounts and folders in Storage. The gallery combines them and offers source filters. Choose one default destination for new imports and edited copies. Changing that destination does not copy historical photos or retarget partially completed uploads. Other sources remain available for browsing, downloads and recognition.

People, their reviewed assignments and the phone model are shared across all connected sources. Removing a source preserves local photos and reviewed identities. Overlapping folders in one account can share a verified local copy. Separately uploaded copies across accounts are not automatically deduplicated.

## Portable XMP annotations

In Storage, **Back up annotations to connected folders** is off until enabled. Enabling it backs up each source's annotations to `.mpa-catalog.xmp` in that selected folder. This is a source-level XMP sidecar catalogue using MPA's RDF namespace, not a change to image EXIF. Original media bytes are unchanged. Adjacent per-image/MWG sidecars and catalogue partitioning are future interoperability improvements.

The catalogue stores portable asset/person/album IDs, favorites, archive status, place labels and GPS, capture time, labels, edit recipes and reviewed face regions. Regions use normalized coordinates in the oriented preview; model and preview dimensions are recorded. Source catalogues include only their related assets and referenced identities. Face crops, embeddings, OAuth tokens, local paths, offline pins and AI handoff state are not exported.

**Anyone who can read a Drive folder can read its names, locations and annotations.** Catalogues are not encrypted. Read-only sources keep annotations locally; **Export XMP** saves that source's projection through Android's file picker. Each catalogue is currently limited to 2 MiB / 10,000 assets, whichever is reached first; oversized catalogues fail without replacing existing metadata. Large-library partitioning is not included in this version.

An offline outbox is invalidated in the same database transaction as annotation changes. Uploads follow each source's network setting. Existing catalogues require recovery review on a new phone. Writes use `If-Match` revision checks: another device's change, duplicate catalogue names, missing files or unavailable revisions stop backup with a visible error. External XML fields outside MPA’s managed catalogue are retained. Fields inside MPA’s catalogue are managed by this schema and replaced by the source snapshot. It is a conservative review-and-recover workflow, without automatic conflict merging or automatic source retargeting.

To recover: connect the original source, download the photos whose annotations you need, then choose **Preview recovery**. Review the matching/skipped counts and global name changes. **Recover matching photos** replaces the displayed categories only for media with matching remote ID, byte length and SHA-256. The preview is rejected if either the remote catalogue or local annotations changed. Other photos are preserved. Download more media and repeat to recover the rest. Source projections retain remote annotations for media not yet downloaded locally.

New face regions have stable IDs. Redetection reuses a region only through an unambiguous mutual geometry match. Legacy assignments with unknown geometry remain for review; list order is never used to transfer a name. Restored crops/vectors are rebuilt on the phone. Legacy metadata without usable regions can need manual reassignment after a fresh installation.

## Phone indexing

Enable people and/or object indexing, then use **Scan now**. **Include all connected Drive sources** allows small batches of verified original downloads across sources; it does not require pinning your whole library. At most eight indexing downloads are in flight, rotating between sources. Each source's network preference still applies. Downloaded originals awaiting an enabled scan are protected from eviction; after scanning, the ordinary cache budget applies.

Phone scans checkpoint each photo's checksum and model version. Each batch processes at most 24 photos or roughly three minutes, checking pause, opt-in, schedule and thermal state between photos. Failures are recorded; Scan now retries failed photo scans. Failed cloud downloads can be retried separately in Storage. Progress describes the current local batch, rather than an estimate of the entire remote library.

**Automatically scan while charging at night** restricts background inference to the saved phone-local time window and charging. A window can cross midnight. Android may delay jobs; the app is not an exact alarm. **Scan now** runs on demand outside the night window. Without the night restriction, enabled indexing runs when the battery is not low. **Pause scans** preserves checkpoints and annotations. Photo transfers already queued are separate from inference and can finish after the inference window closes.

The bundled human model is unchanged from 0.3. Suggestions require confirmation. Cat/dog object labels are searchable, but **individual pet recognition, calibrated accuracy and Google Photos parity are not delivered by this release**. Representative consented photos and real phone measurements are still required before claiming that quality.

<a id="unreleased-bulk-drive-ingestion-checklist"></a>

## Bulk Drive ingestion in 0.4.4

The 0.4.4 increment is described in [the ingestion audit](DRIVE_INGESTION_AUDIT.md).
It adds Room v6 scan checkpoints and bounded worker passes. Published 0.4.3 used Room v5.
Use a test folder and keep independent copies before exercising remote changes.

1. Add thousands of images/videos across nested folders externally while MPA is closed.
   Reopen/Refresh. Watch folder-page progress in Storage and progressively browse new items.
   With indexing disabled, confirm there are no bulk original downloads.
2. Stop/restart MPA during discovery, then refresh. Check that saved page progress resumes,
   old unseen gallery items remain until traversal completes, and no duplicate entries appear.
3. Add files and nested folders during traversal. Rename, move in/out, replace and trash
   selected items. Refresh to convergence; byte changes must not inherit reviewed faces.
   Previously downloaded snapshots and annotations must remain recoverable.
4. Interrupt connectivity or revoke folder access mid-scan. Confirm saved progress and
   unrelated sources remain intact, errors/reconnect are visible, and Retry/Refresh resumes.
   Background workers must not launch consent.
5. Connect overlapping roots in one account and a second account. Confirm shared downloaded
   identities where appropriate, independent cross-account copies, per-source progress and
   network preferences. Test a shared-drive folder with the actual supported permissions.
6. Enable bounded cloud indexing and its charging/night schedule. Newly discovered images
   should eventually enter the existing verified-download queue; pause and heat/network
   restrictions must still apply. JSON/XMP/ZIP files and shortcuts must not appear as media.

## Phone checklist

1. Upgrade from 0.3/alpha without uninstalling; confirm local photos, names and existing Google sign-in remain. If Android reports a signer mismatch, stop and check the certificate rather than clearing data.
2. Connect two folders/accounts, filter the combined gallery, select a default destination, import/edit a photo and confirm only that destination receives the copy.
3. Enable face/object indexing and cloud indexing. Verify scan progress, pause/resume, cross-source naming suggestions and cache protection. Review every identity suggestion.
4. Save a night window spanning midnight. Confirm automatic scans wait for charging and the window, then stop after unplugging or leaving the window. Check heat and battery on your phone.
5. Enable XMP backup in a writable test folder. Favorite/archive a photo, add an album/name/location and edit draft; check `.mpa-catalog.xmp` in Drive. Disconnect network, change annotations and reconnect to verify the pending outbox.
6. Edit the catalogue from another device (preserving valid XMP), then change a local annotation. Verify backup reports a conflict; preview recovery shows the incoming changes before applying them.
7. Test recovery in a separate test installation/device with copied test media and a connected original source. Download originals, preview and recover. Confirm names/regions/albums/recipes and unchanged media checksums. An altered media file must be skipped.
8. Test a read-only source, duplicate catalogue filenames and an unavailable/moved catalogue; verify visible errors and preserved local annotations. Do not use your sole photo backup for destructive test cases.

Live Google OAuth/Drive behavior, real face/pet accuracy, Android vendor battery restrictions and heat are phone checks. Automated fixtures do not substitute for those measurements.
