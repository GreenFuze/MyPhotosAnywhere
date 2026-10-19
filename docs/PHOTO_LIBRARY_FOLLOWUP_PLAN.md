# MPA photo library follow-up — implementation plan

Baseline: published 0.5.30 (`afc838c37adcb2a1d2397b64af5a82de38a551e2`). Target: 0.5.31 / versionCode 42. Four focused workstreams, one final integrated release; do not hand off after an intermediate increment. Owner authorizes implementation and publication without visual-review pauses. Stop only for a necessary decision, regression, unresolved warning or unsafe upgrade.

## Evidence and constraints

The supplied Google Photos screenshots establish the desired hierarchy: account panel, grouped rounded menu rows, Preferences cards, then focused toggle pages. They also show a compact Drive/cloud status mark on backed-up media, with no such mark on device-only media. Copy the structure and interaction, preserve MPA identity, and expose only implemented features.

Repository audit: management routes already exist but some pages expose many controls together. No automatic MediaStore library/permissions exist. ImportedPeopleRepository builds source-scoped label groups separately from Person/FaceRecord. PeopleRepository already detects faces locally and generates reviewed-identity match suggestions, with rename/merge support for existing people. Mosaic plans currently start from the retained page window; page boundaries, prepends and late thumbnail geometry can change packing despite existing anchor restoration. These are implementation observations, not claims about the owner's precise runtime cause.

Preserve originals, verified transfer checkpoints/destinations, canonical IDs, metadata, albums, drafts, pins, people corrections, source bindings, consent and bounded viewer decoding/leases. Do not fake original availability, a hash, an uploaded backup, or a face match. Expected operational failures remain recoverable; cancellation and unexpected internal invariant violations propagate. Prefer object-owned responsibilities. No dependency/toolchain update is required for this work.

## 1. Gallery continuity foundation

Make packing stable across retained-page shifts and asynchronous preview loading. Geometry must be keyed by canonical identity and frozen for the browsing context; a preview upgrade must not move its tile. Introduce a bounded continuity/packing owner that preserves existing block membership/geometry for overlapping visible content, handles prepends/appends without repacking all retained photos, and retains deterministic traversal/source order. New non-overlapping content may receive a new layout; explicit density/layout changes may re-layout with an anchored transition.

Use canonical media + measured pixel-offset anchors through refresh, import insertions, page drops, viewer return, rotation and timezone changes. A viewport must not adopt a transient replacement layout as its new anchor. Preserve usable content while refresh runs; show a small New photos action when newer content is available above an older viewport. Never reset to the top or animate the entire grid after progress ticks. Use bounded fade/transition for content replacement, respecting reduced motion. Keep date seeking, continuous selection hit testing, accessibility traversal and the 300-entry paging budget correct.

Acceptance: exact visible keys and offsets remain stable through append/prepend/page retention, late previews, metadata-only invalidations and import/device inserts. Explicit removals choose a deterministic surviving neighbor. Native pixel/position assertions cover Mosaic, Rows and Square, rotation, viewer return and both directions of paging. Benchmarks remain at 10k/100k; physical 60Hz is not inferred from emulator timings.

## 2. Menu and settings hierarchy

Introduce shared grouped menu, preference row and toggle-row components: rounded containers, concise title/subtitle, consistent leading icons where useful, trailing chevron or switch, generous spacing, whole-row interaction and minimum 48dp targets. Use a quiet contrasting settings background with high-contrast grouped surfaces in both themes. Leaf controls belong on focused subpages. Switches align at the trailing edge, including RTL; long subtitles wrap.

Account/source entry becomes a clear account/source management panel with connected sources, captured default backup/import destination, compact storage/activity information when genuinely known, and Settings/help entries. Settings offers Preferences, Backup and metadata, Storage, Privacy/permissions and About. Preferences includes Photos view and Recognition; Photos view contains only implemented layout/density/date presentation options. Keep every existing destination/action reachable, preserving navigation/scroll/saved state. Apply the same menu vocabulary to management entry points and contextual menus, retaining appropriate modal confirmations for protected/destructive actions.

Do not add decorative Memories, stacks, shimmer, subscriptions, Google account features or toggles without implementation. Existing consent and backup choices remain unchanged. Tests cover each previous entry point, Back, focus, TalkBack semantics, 2x fonts, RTL, themes and floating-nav clearance; update tests only where the owner's requested hierarchy changes their navigation expectation, retaining behavioral assertions.

## 3. Device library and explicit backup

Create an object-owned MediaStore discovery/access service, durable device-reference model/DAO and availability/backup presentation adapter. Index metadata and content URIs in bounded cancellable batches; never copy or hash the entire camera roll just to display it. Camera photos appear automatically after Android photo access is granted; other readable device media participates in the same chronological gallery. Observe MediaStore changes while active and reconcile on foreground return. Use volume/row identity plus revision/version information to handle ID reuse, edits, removed volumes and provider resets without overwriting an older verified original.

Support API26–32 READ_EXTERNAL_STORAGE with maxSdk32, API33 image/video permissions, and API34+ READ_MEDIA_VISUAL_USER_SELECTED/full/partial/denied access. Check actual permissions on each foreground access; do not persist assumed grants or repeatedly prompt. Explain partial access and provide an explicit Manage access/reselect control. Denied/revoked access must not erase metadata or Drive content, nor hide unrelated sources. Official reference: https://developer.android.com/about/versions/14/changes/partial-photo-video-access .

Device URI reads run off the main thread. Load bounded previews and known MediaStore dimensions; originals are opened/materialized only when required by viewing, sharing, editing, recognition or explicit backup. Integrate with existing cancellation, byte verification, cache budget and reader leases. No invented SHA-256 value or thumbnail-as-original state. Stable device identity survives materialization and verified Drive linkage. If new durable tables are necessary, make an additive schema11 migration with automated upgrade/data-preservation tests; historical schemas9/10 remain unchanged. Camera originals are never removed by cache cleanup or a Drive-only Trash action.

Show a compact In Drive/cloud-check indicator only for an existing, nontrashed Drive item or a verified current device-to-Drive relationship. Device-only media has no Drive badge. Provide Back up in viewer/details/context actions, with destination and progress/retry status. Capture the chosen destination when work is created; use the existing verified durable transfer engine and avoid re-uploading completed matching work. Do not silently enable camera uploads; automatic browsing is separate from automatic backup. A changed/unavailable device original or expired authorization retains recovery state and verified successes.

Acceptance: synthetic MediaStore camera/video files on API29/35/37 appear after access, without manual import; partial access/revocation/deletion/provider changes behave honestly. View/swipe/share/edit use real bytes with bounded resources. Manual backup verifies actual remote bytes before showing the badge; failure/rotation/default-destination changes do not mark success or retarget work. Original hashes, source files, drafts and cache protection are asserted.

## 4. One People identity model

Replace separate Imported people and Named faces sections with one People grid/list and one canonical Person identity. Introduce durable provider-label aliases and photo-person memberships as necessary, preserving source/name provenance internally. Imported names, manually named identities and confirmed face assignments open the same person detail and use the same rename/merge actions. Do not rewrite retained Takeout JSON or delete face geometry/corrections to unify presentation.

Backfill existing retained labels in bounded resumable batches; preserve all existing Person/FaceRecord IDs. Reuse an unambiguous existing identity mapping; ambiguous equal names require explicit review instead of merging different people silently. Rename changes the canonical display name while old exported labels remain aliases. Merge atomically redirects aliases, photo memberships, faces and suggestions to the chosen survivor and deduplicates memberships, preserving original photos/metadata and corrected face regions. Provide individual-face Name/Move to person/Not this person controls.

New/imported/device photos update the shared label membership index and, when face-indexing consent is enabled, enter existing local detection/matching. Match against the same canonical identities regardless of how they were named. Imported names without face coordinates/embeddings are photo-level hints, not proven biometric templates: present candidate confirmation where association is uncertain; confirmed faces then supply matching data for subsequent photos. Do not enable recognition or download all Drive originals merely because imported names exist.

Portable annotations must retain canonical renames, merges and photo-level memberships even when Takeout supplied no face coordinates. Extend the existing XMP namespace with optional, backward-readable membership/alias elements; preserve raw Takeout JSON, existing faces and the 2 MiB safety limit. Recovery previews expose conflicts and apply accepted changes atomically; legacy catalogs without the extension remain valid. Include round-trip, unknown-namespace, limits and restore tests.

Acceptance: historical imported labels and named people share navigation/counts/actions; rename/merge persists across restart and new imports. Person queries include label memberships and confirmed faces exactly once, with source/archive/tombstone rules preserved. Duplicate names, multiple faces, zero embeddings, contradictory corrections and disabled recognition remain honest. Tests assert migration/backfill idempotence, aliases, safe merge, suggestions, unassign/corrections, metadata hashes and 10k/100k bounded indexing/query behavior.

## Integrated delivery gate

Implement in the order above with focused commits and a reviewable PR. Add device and person tables together to the declared additive migration if both need persistence; never publish a partially implemented schema. Run appropriate focused tests at each step, then freeze one final application SHA for full unit/integration, lint, APK and API29/35/37 native suites. Preserve existing assertions; document intentional hierarchy/identity expectation changes explicitly. Validate complete real PNG captures, full raw logs, migration/data snapshots, benchmark and pixel-anchor evidence, package/version/checksum and established signer. Keep strict warning gates and only the already explicitly authorized narrowly documented upstream/emulator exceptions.

After passing checks, fast-forward main to the tested source, prove publication-only documentation leaves application/build/workflow objects unchanged, publish the exact CI APK, and independently verify public download/assets/tag/latest/checksum/signing certificate. Report the install link and physical S23/live Google/human TalkBack/translated Hebrew/performance acceptance that cannot be established by emulators. Do not claim those passed.

## Integrated delivery — 0.5.31 / 42

All four implementation workstreams are delivered and validated at `0d9d21252e749cabd83147e9a51ad192dbbdcd9d`. [Release evidence](releases/v0.5.31.md) records the additive migration, full checks, source/artifact integrity and remaining physical/live-service/human acceptance.

## Owner follow-up — queued for 0.5.32

Remove the gallery scroll handle while retaining the floating date and accessible Jump to date. Keep settled empty Search results visible through same-query refresh, with query-bound loading and error presentation. Balance the mosaic using consistent column spans and deterministic varied portrait positions, preserving identities and pixel anchors through arrivals, rotation and viewer return. Deliver all three together with the existing full warning, regression and exact-APK publication gates; retain schema11.

Object-search changes are deferred. The optional Windows/Linux/macOS MPA Processor is a discussion proposal only: phone and Processor communicate through Drive, with a usable phone-only mode. No Processor implementation is part of these releases.

## UI follow-up delivered — 0.5.32 / 43

All three requested UI changes are delivered at `c6fe4ad4fcd1c6b0b0059c4baf0bebb051df7774`: no gallery edge scrollbar, stable completed empty Search through refresh, and varied portrait mosaic placement with consistent columns. [Validation](releases/v0.5.32.md). Physical/live-service/human acceptance remains unverified. Object-search changes and the optional Drive-mediated Processor remain discussion-only and deferred.

## Device storage delivered — 0.5.33 / 44

Device folders, discoverable Android access, explicit original/metadata backup and verified-download cleanup are delivered at `7714d96b6f8b654c8f16e6358dd9d993356380fe`, with scoped recognition ownership and safer place-editor input dismissal. [Validation](releases/v0.5.33.md). Physical/live-service/human acceptance remains unverified. The optional Drive-mediated Processor remains discussion-only.
