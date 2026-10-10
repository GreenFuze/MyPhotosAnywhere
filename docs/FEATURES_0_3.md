# MPA 0.3 implementation and device test checklist

This milestone adds conventional image editing, local library organization, optional on-device discovery, geographic browsing, and a Drive cloud index. Build/test verification is recorded separately in VALIDATION.md.

## Editing

Photo details → Edit. Crop sides, rotate, straighten, adjust exposure/contrast/saturation/warmth, choose a filter, or draw on the preview. Undo/redo supports the current session. Draft recipes persist on this phone. Save copy produces a JPEG up to 4096 pixels with a parent link and recipe; the original bytes remain unchanged. GPS is excluded unless Keep GPS is selected. Share removes GPS from image previews; Export original preserves the source bytes and metadata. Videos have preview/playback/export, not a video editor.

## Library and places

Open full-screen viewing with swipe browsing, pinch/double-tap zoom, and video controls. Favorite and archive items, create/rename/remove albums, and pin albums for offline use. Search file names, dates (YYYY-MM-DD), place names, confirmed people, and optional object labels. Album removal preserves files. Location labels/coordinates are local metadata and do not rewrite an original. The Places map clusters nearby coordinates and uses OpenStreetMap with visible attribution. Online tiles are off by default; enabling them sends viewed tile requests to OpenStreetMap. There is no background GPS tracking or online geocoding.

## People and objects

Enable face indexing explicitly in People. Bundled ML Kit detects faces offline. A checksum-pinned Apache-2.0 MobileFaceNet model computes local face embeddings for sufficiently large, frontal faces with usable landmarks. Matching uses confirmed faces as references, with a conservative similarity/margin gate. Possible unknown groups require review; they do not establish identity. Name, correct, merge, split by reassigning individual faces, reject suggestions, or delete the entire index. Corrections survive restarts and ordinary rescans. Names, crops, and embeddings are never placed in Drive metadata or AI handoffs. Accuracy has not been calibrated on the user's photos.

Optional bundled image labeling indexes common objects/scenes on available local images. Pet class labels are supported by that general model; individual pet recognition is not implemented.

## Drive and cache

Selecting a folder schedules recursive, paginated discovery of images/videos. The metadata index and cached thumbnails support offline browsing without downloading every original. Discovery does not wait for thumbnail transfers; previews are fetched for visible entries as needed. A pre-scan changes token catches updates during enumeration; incremental reconciliation commits each page's rows and token in one transaction. Folder changes and expired tokens trigger a fresh scan. External deletions/moves hide cloud entries and never delete local originals. Changed remote bytes detach the old local snapshot from the cloud entry, making the new version downloadable separately; metadata-only changes retain the same verified local file. Refresh is roughly every 15 minutes when Android and network constraints allow it, plus manual refresh.

Tap a cloud item to enqueue a verified download. Downloads retry from staging on network failures and survive process restarts through WorkManager/Room. The first implementation restarts an interrupted download; uploads retain their existing resumable protocol. File size is limited to 250 MiB per import/download. Downloads are checked against size, MD5, version, and current folder ancestry before local commit. Missing disposable files can be restored under the same local ID, preserving albums and people corrections.

Storage defaults to 2 GiB of disposable downloaded originals and 256 MiB of thumbnails. Imports and edited outputs stay protected; so do pins, pinned albums, drafts, pending AI handoffs, active views, and unverified/changed remote copies. Eviction rechecks the current remote checksum/revision/ancestry. Offline verification failures preserve files even if the budget cannot be met. Thumbnail files are rebuildable and have a separate budget. Cache limits are soft limits when protection prevents safe deletion.

## Test on the phone

1. Upgrade with a matching signer where possible; preserve any app-only media before an uninstall. Register this build's SHA-1 with Google. CI caches the private debug keystore for subsequent builds, but cache eviction can still change it; production signing needs an owner-managed key.
2. Import image/video fixtures. Restart offline; test video playback, date/object/name search, favorites, archive, albums, and offline album pins.
3. Edit a photo through crop, rotate, straighten, adjustments, filters, and markup. Undo/redo, reopen its draft, save a copy, export it, and verify the original checksum remains unchanged and GPS follows the toggle.
4. Enable People, name a face, confirm a suggestion, correct an incorrect match, merge two names, and reassign one face. Restart and verify corrections. Delete the index and verify all face data is removed while photo files remain.
5. Import GPS-tagged photos, set a local place label/coordinates, browse clustered map pins with online tiles disabled/enabled, and confirm unknown GPS does not create a zero-coordinate pin.
6. Choose a disposable Drive subtree with multiple nested folders and more than 100 files. Refresh, browse offline thumbnails, download an image, interrupt/retry its download, modify a file remotely, move/delete a folder, and reconnect after revoked authorization. Local imports must remain intact and tasks must never switch accounts/folders.
7. Set a small cache budget, pin a download, leave another photo open, and create a pending edit/AI handoff. Clear downloads online/offline. Only safely verified, unprotected downloaded originals may disappear; metadata and protected files remain.

## Further work

Full Google Photos parity remains a product roadmap: video editing, pet identity models, production signing/store publication, calibrated face accuracy, multilingual semantic search, encrypted optional metadata recovery across devices, explicit remote trash/recovery, and a configurable map tile provider for larger deployment. Albums, edit recipes, names, and user location labels are local; only file bytes and existing asset/parent identifiers are backed up by this version.
