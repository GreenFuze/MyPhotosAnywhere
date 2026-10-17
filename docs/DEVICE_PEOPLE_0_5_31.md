# MPA 0.5.31 — Device photos, People and familiar menus

Implementation candidate. Publication still requires the final immutable host/device checks, diagnostics, screenshots, migration proof and established upgrade signer.

## Photos on this device

Open the account/source icon, then **Photos on this device**, and allow Android photo/video access. Readable camera photos and other device media join the existing chronological gallery automatically. MPA rechecks access and discovers changes on foreground return; it does not repeatedly display a permission request. Android 14+ can grant only selected media, with an explicit control to change access. [Android Selected Photos Access](https://developer.android.com/about/versions/14/changes/partial-photo-video-access).

Discovery reads metadata and content references in bounded batches. It does not copy or hash the whole camera roll. Viewing opens actual device originals with the existing bounded image/video lifecycle. Camera videos receive bounded posters. A private verified working copy is prepared only for an operation that needs it, such as editing, sharing, keeping offline or explicit backup. Preparation requires room for the original plus a safety reserve. Its camera file remains untouched.

Revocation, removed media or provider changes preserve indexed information and existing owned originals. Inaccessible device-only references leave the normal gallery until access returns; protected owned originals remain usable. Cache cleanup and Drive Trash never remove a camera URI. Previously verified bytes are not overwritten when Android reports a changed reference.

## Explicit Drive backup

Device-only media has no In Drive badge. Use **Back up** in its viewer/details, or select several photos and choose the icon action. New jobs use the chosen folder; an unfinished job keeps its captured account and folder. Automatic camera uploads are not enabled by these actions. Already verified matching work is retained, and other selected photos continue if one preparation fails.

Details shows the durable queued/uploading/retry state. Authorization problems direct the user to Sources for that captured account. The In Drive mark requires a verified current relationship or completed transfer receipt; progress or a cached thumbnail is insufficient. Known remote changes, deletion and Drive Trash override an older receipt. Backups lease their private original during transfer, preserving cache/lifecycle protection.

## One People experience

Imported names and manually named/reviewed faces use the same People cards, navigation, photo counts, Rename, Merge and correction controls. Collections and Search use the same canonical covers. Existing retained Takeout labels are indexed without reimporting originals or enabling recognition. Indexing is resumable, reports partial work, and isolates invalid metadata for retry.

Rename changes the common display name while retaining original exported names as aliases. Merge chooses one survivor and redirects future imports through those aliases. Names that were previously distinct may need a one-time merge when they refer to the same person. Equal names from unrelated sources are not sufficient proof to merge people automatically.

When recognition is enabled, new device/imported photos use the same identities for local match suggestions. Names-only Takeout exports do not supply proven face coordinates or embeddings. A single-label/single-face association can offer a reviewable hint; ambiguous or contradictory faces stay unassigned. Confirmation assigns a face. **Not this person** corrections survive later metadata updates. Choosers are searchable/scrollable and retain identities and drafts through rotation; stale choices cannot recreate a removed person.

Optional XMP annotations retain canonical aliases, renames/merges, photo-level memberships, exclusions and removed-name markers. Existing raw Takeout records and face geometry remain intact. Legacy catalogs remain readable and the 2 MiB safety bound remains. Recovery previews disclose shared alias/name changes and apply accepted data atomically, preserving unmatched photos and faces. An unrelated empty identity is not silently deleted.

## Menus and gallery continuity

Account, Settings and Preferences use shared rounded groups and focused rows/toggle pages. Person and face menus use the same vocabulary. Existing actions and consent choices remain available; unsupported Google creation/AI features are not added.

The gallery preserves bounded layout templates and canonical pixel anchors through newer/older/in-range arrivals, paging and late preview changes. New photos can be revealed explicitly from an older viewport. Import progress does not re-layout the grid on every tick. Photos, Rows and Square retain their existing density/date controls and floating navigation clearance.

## Upgrade and validation boundaries

Version 0.5.31, code 42, minimum SDK 26, target SDK 37. Room 11 adds device references, explicit-backup authorization and canonical-label bookkeeping. Existing tables, media/people IDs, originals, metadata, edits/drafts, pins, consents, sources and captured job/checkpoint state are preserved; historical schemas 9 and 10 remain unchanged.

Native permission tests run separately from ordinary gallery fixtures and revoke access between instrumentation processes. All stages retain their XML, logs and screenshot evidence, with no duplicate test counting and an unchanged application APK across invocations. Large-library benchmarks distinguish indexed device references from physical MediaStore discovery and frame-rate measurements. Synthetic media/accounts never establish live Google acceptance.

Physical S23 Ultra, live Google authorization/permissions/Trash, actual foreign-photo reselection in Android's picker, human TalkBack, translated Hebrew and physical frame-rate acceptance remain unverified. The previously reported JPEG crash remains unreproduced and its cause unconfirmed. Existing oversized optional XMP and import media-size limits remain explicit. No new dependency, warning baseline, blanket suppression or reduced severity is introduced.
