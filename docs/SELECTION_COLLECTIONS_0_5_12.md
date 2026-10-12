# MPA 0.5.12 — selection and Collections

This increment corrects Photo Details to open with **swipe up**. Swipe down does not open Details. The visible Details button remains available; zoomed pan, horizontal paging, decoding limits and lifecycle protections are preserved.

## Scope

- Long-press or accessible Select action begins selection in Photos, Search, album and person timeline grids. Tap toggles stable identities. Back exits selection. Rotation and imports preserve targets. Batches are explicitly bounded to 500 items, with an announced limit; there is no global select-all over paged results or drag-range selection in this increment.
- Contextual Favorite/Unfavorite, Add to album, Archive/Restore, Offline and Share actions replace normal navigation. Album grids also offer Remove from album. No deletion action is introduced.
- Metadata actions require an existing local record, not an original file. A remote-only representation can follow its verified link after download. Unlinked remote items report a download requirement and remain selected. Changed or missing content and missing albums report per-item failure. Successful targets leave selection; failures remain available for retry.
- Offline operations pin available originals or enqueue the established source-bound verified download. Queued work is never reported as an available original. Existing jobs, captured destinations and checkpoint policies are unchanged.
- Share prepares original bytes under existing cache leases, verifies SHA-256, checks storage with the existing safety budget and grants content-URI access through Android sharing. Failure/cancellation removes only its private staging directory and shares no subset. A successful Android chooser handoff means preparation, not proof that an external app received the files. Original metadata, including GPS, is retained when sharing originals.
- Collections uses adaptive thumbnail cards for Favorites, Archive, People, Places, Offline and Edited photos; albums retain create, rename, remove and pin behavior. Cards retain at most four covers. Offline cards inspect at most 24 candidate paths, so stale originals cannot trigger a full-library walk. Existing face crops/location thumbnails require no new recognition/map consent and never initiate those capabilities. Album counts describe membership; offline counts are omitted rather than inferred from cached thumbnails. Collection scroll state survives tab changes.

## Preservation and validation

Room schema remains 9. No import, download, recognition, map, editor, original-decoder or source-removal engine is rewritten. Recoverable IO/database failures have explicit results; invalid internal identities and impossible action combinations fail fast.

Regression coverage includes mixed remote/local results, source-scoped identity, representation transitions, rotations/import insertions, album removal without deletion, archive/restore, offline queue provenance, share bytes/missing files/cancellation, bounded covers, accessible selection, contextual clearance and large-font light/dark/RTL controls. Existing full CI, API 29/35/37 native checks and 10k/100k timeline benchmarks gate publication of one immutable application revision. S23 Ultra acceptance and the previously reported JPEG crash remain unverified.
