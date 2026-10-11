# Photo-first UI increment

Owner authorized development on 2026-10-11 after supplying the provisional UI/UX redesign brief. Version 0.5.9 phone acceptance is still outstanding; authorization to continue does not establish that the reported JPEG crash is resolved on the phone.

## Current increment

The `ui/photo-first-shell` branch introduces Photos, Collections and Search navigation. Photos and Collections share a compact navigation surface; Search is adjacent. Real bundled vector icons replace navigation glyphs. The original MPA emblem remains in the app bar.

Photos drops the duplicate heading/count, tagline, permanent search box and permanent filter/source rows. Filters and source selection move into a bottom sheet. The existing grid gains three columns, two-dp spacing and quiet thumbnails; video markers and active download feedback remain. Sync problems lead to existing source recovery controls. A compact import summary observes job states and dismissal preferences without collecting per-item progress into the gallery; it opens existing detailed controls. Light/dark colors follow the system theme and preserve the MPA teal accent. Collections retains album management, Favorites, Archive and AI activity, with links to People, Places, Offline and Edits. Accounts and imports remain available through the top-bar controls and the existing Storage screen.

Search uses the existing search semantics in a dedicated destination. Its query survives recreation and does not restrict ordinary Photos. Presentation history preserves Back navigation from child destinations. Separate lazy-grid states retain Photos, Search and collection grid anchors.

This is an initial shell and presentation increment. The gallery still renders local/indexed media before remote-only media. There is no claim of a unified timeline yet. The database, workers, import checkpoints, source bindings, original-download flow and 0.5.9 viewer implementation are unchanged. Existing recognition/map consents and editing flows are retained. No release version, signer or schema change is included.

## Next: chronological projection audit

Source inspection confirms two existing representations:

- `Photo` in `photos` has a local ID, nullable capture time, import time, protected originals/edited-copy relationships, optional source binding/remote ID and metadata IDs.
- `CloudPhoto` in `cloud_photos` has a source-scoped key, remote ID, nullable `photoId` link, capture time, textual Drive modification time, thumbnails, version and removal state.
- The gallery already excludes remote rows whose `photoId` exists in the local list. Source identity and existing links must be retained; filename/hash similarity cannot manufacture a link.
- Local rows currently sort by `capturedAt ?: importedAt`, then ID. The cloud DAO has its own capture/modification/name ordering, and the UI concatenates those two lists. That explains the split chronology.
- Both DAOs currently observe complete lists. A scalable paged projection therefore requires a deliberate repository/query change, not only a sorted list in Compose.

Before that increment, define canonical projection keys, deterministic timestamp fallbacks, source visibility/tombstone rules, linked-edit behavior, date grouping and paging/anchor contracts. Evaluate a read-only UNION query over existing tables first. Do not persist new capture timestamps, merge distinct source items or assume a schema migration is needed. Keep the viewer's stable per-opening snapshot and cache leases. Date seeking and large-library paging need dedicated integration tests and query-plan evidence.

## Review and validation

Run existing unit/lint/build checks and the API 29/35 native matrix on one immutable source revision. New native coverage checks child navigation, saved Search state, first-viewport media, viewer return, tab scroll restoration and preservation of synthetic originals. Existing source-filter tests now enter the sheet; all underlying assertions remain. CI captures a synthetic shell screenshot for visual review. These emulator screenshots do not establish Samsung S23 Ultra acceptance or real Google-account behavior.

Before extending scope, review the first increment visually and test the existing JPEG while importing. Do not require cancelling an ongoing import, clearing app data or uninstalling to bypass signing. No new release is published as part of the shell review.

### First CI review

The first revision passed all 200 unit tests, lint with zero errors, and all 35 Android 10 native tests. Android 15 passed the four new shell tests but an existing People diagnostics check timed out before scrolling to the diagnostic message. The test now explicitly scrolls to and asserts that same message rather than assuming it is in the initial semantics viewport. No recognition code or data contract changes are involved. The 320dp screenshot also exposed an overfull app bar; at widths below 360dp the separate activity button moves into the Add menu while the compact import indicator and source controls remain accessible. Repeat the full matrix on this corrected revision.
