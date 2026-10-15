# Complete photo-first UX delivery plan

Baseline: published MPA 0.5.22/code 33, Room schema 9. Updated October 15, 2026. The owner authorizes unattended implementation, validation and publication of all remaining UX work, including the additions below. Continue from one stage to the next without routine review pauses. Stop only for a necessary product decision, blocking regression, unresolved diagnostic or inability to verify a safe upgrade. Never treat elapsed time as approval.

## Delivery order and completion ledger

| Release | Scope | Status |
| --- | --- | --- |
| 0.5.23 / 34 | Google Drive Trash and Restore | [Published](releases/v0.5.23.md), tested `00a7efbc2503eb5d15414a2e9741a70b84b47d99` |
| 0.5.24 / 35 | Scrolling app bar and shared motion | [Published](releases/v0.5.24.md), tested `2655497b6f9de6f7a4e7e13beb3e2cb758402c3f` |
| 0.5.25 / 36 | Visual Collections, People, albums and Details | In implementation |
| 0.5.26 / 37 | Sharper adaptive thumbnails and richer mosaic | Planned |
| 0.5.27 / 38 | Continuous drag selection | Planned |
| 0.5.28 / 39 | Direct date scrubbing | Planned |
| 0.5.29 / 40 | Management refinement and complete UX audit | Planned |

Version allocations express delivery order, not a reason to skip a blocking fix. Update this ledger with tested commits, release links and pending physical-device evidence after each stage. Prefer focused changes and preserve each published upgrade path.

## 0.5.23 — Drive Trash and Restore

Add an icon-led Move to Drive Trash action to local/remote viewers and multi-selection. Confirm affected photos, account/folder and that Drive automatically deletes trashed files after 30 days. Collections gains Trash with preview grid, selection, Restore and truthful loading/offline/partial/failure states. Operate only on indexed media from connected MPA sources, never all unrelated Drive files. Do not add an account-wide Empty trash action.

Use Drive files.update(trashed=true/false), explicit account identity and canTrash/canUntrash capabilities. Revalidate exact file identity, content version and source ancestry before mutation; use conditional revision requests. Never turn a 403, stale identity or ambiguous source into success. A photo represented by multiple sources must show which actual Drive file(s) will change; do not silently trash other copies. Unsupported local-only media stays preserved and explains why Drive Trash is unavailable. Removing a downloaded phone copy remains a distinct action with existing verified-remote/cache-lease protections.

The current removed flag conflates trash, moved-out-of-source and deleted items, and existing Favorites/album exceptions deliberately retain removed local records. Therefore this requires explicit persisted trash/operation state; do not pretend removed means trashed or reuse Archive. Design an additive Room migration only for demonstrated fields, preserving all existing rows and relationships. Persist bounded operation intents before network mutation so process death or lost responses can reconcile actual Drive state; never blindly replay a restore/trash after content changes. New sources, imports, downloads and sync must not resurrect trashed representations or replace local originals. Retain metadata, album membership, edited-copy/people relationships and local-only originals for restore. Hide verified trashed entries consistently from normal Photos/Search/Collections. Reconcile externally trashed/restored files; retain actionable partial results and cancellation. No permanent photo deletion in this increment.

Tests: exact-account and source binding; linked/unlinked/duplicate-source identities; canTrash/canUntrash and 403/401/404/412; idempotent reconciliation after lost network reply/process death; local-only protection; local cache lease; restore metadata; visibility across all filters; bulk partial failure; import/download races; real migration 9-to-next preservation; native viewer/selection/Trash flows. No tests mutate the owner's live Drive.

## 0.5.24 — Scrolling top bar and motion

In Photos and photo browsing grids, scroll toward older content moves the top app bar continuously out of view. Reverse scrolling brings it back without requiring a return to the top. Use supported Material 3 enter-always nested scrolling. Content gains the released space; no blank bar, overlap with status bars, floating date or bottom navigation. Reset/restore bar state deliberately on query/destination changes, selection, date jump, rotation and viewer return. Keep it available for TalkBack/keyboard and provide visible alternatives to hidden actions. Avoid collapsing forms, Search input or management pages unexpectedly.

Introduce a small shared motion vocabulary for app-bar movement, navigation, selection chrome, activity strips, viewer controls and thumbnail upgrades. Use short slide/fade/size transitions with continuity; avoid instant conditional removal. Retain outgoing content until transitions finish without leaving hidden controls clickable or creating two active viewers. Use platform motion scale/reduced-motion settings. Do not fade the entire loaded photo grid on every import/progress update. Preserve predictive/system Back and keyboard behavior.

Tests: downward/upward scrolling actually changes geometry and content space; controls return/accessibility; no selection/zoom/drag conflicts; safe state restoration; reduced motion; light/dark/large-font/RTL screenshots; bounded recomposition and scroll performance.

## 0.5.25 — Visual Collections and Details

Collections uses a compact shortcut area (Favorites, Trash, Archive, Offline), visual album/person/place cards and real covers. Move album rename/remove/cover/pin management into labeled menus while retaining every existing operation. People separates reviewed face identities from imported Takeout name hints; imported names use photo covers, never fabricated face crops. Recognition and maps remain optional with existing consent. No empty decorative capabilities.

Redesign the swipe-up Details sheet for local and remote media with consistent typography, padding and grouped surfaces: date/time; caption/imported description where supported; people/location; file facts; source and original availability; edit relationship; secondary metadata/actions. Use icon-led rows and concise status. Show known dimensions and provenance honestly; do not invent EXIF. Keep the photo context visible and accessible Edit/Close actions. Existing favorite/offline/archive/album/location/people-correction/export/AI-handoff actions remain reachable. Editing metadata and editing image pixels remain clearly labeled. Show long filenames/email safely at large fonts. Keep slow original loading nonblocking.

Tests: all existing detail and correction actions; local/remote/unknown-date/disconnected states; Takeout names; Back/rotation; existing consent and membership assertions; first-viewport and typography review on native synthetic captures. Preserve swipe-up, zoom, paging and viewer memory/lifecycle safeguards.

## 0.5.26 — Thumbnail quality and richer mosaic

Audit found Drive previews are reduced to a 480px longest edge at JPEG85; this can be soft in large portrait tiles. Implement an object-oriented bounded thumbnail policy using actual cell size, aspect and display pixels. Reuse small cached preview immediately and upgrade to an appropriate larger variant with subtle fade. Cache variants by canonical content revision, orientation and size bucket, with bounded disk/memory budgets, cancellation and atomic writes. Never upscale a small provider preview and claim extra detail. Prefer existing verified local originals for regeneration; remote requests use supported authenticated provider thumbnails, not eager original downloads or undocumented URL rewriting. Existing thumbnails remain usable offline and during upgrade. Handle image/video rotation, transparency and corrupted caches without oversized decoding. Regeneration is demand-driven and does not download a whole library. Do not promise Google's private thumbnail implementation.

Add a deterministic bounded block mosaic with tall portrait tiles spanning multiple rows, inspired by the owner's screenshots. Retain Square and the current justified Mosaic as explicit fallbacks. Each canonical ID appears once in chronological traversal order, with no implicit deduplication, gaps or page-boundary instability. Layout must remain continuous across dates with floating date context. Preserve exact/nearest-visible media+pixel anchors on import/representation change/density/layout/rotation. No SQL/import/viewer rewrite bundled into visual layout.

Tests: pixel-size buckets/rotation/cache revision/atomic failure/low storage; no full original decode in grid; portrait/square/panorama/video mixtures; 10k/100k deterministic bounded packing; page boundaries, repeated invalidation and canonical-ID restoration; screenshot quality at device pixel scale; retained-memory and scrolling benchmarks.

## 0.5.27 — Continuous selection

Long-press and drag selects a continuous range with bounded edge autoscroll in every supported layout. Use canonical identities and actual visible hit rectangles, never mutable indices alone. Reversing the drag reconciles the selected range; dataset replacement cancels or safely reconciles the gesture. Keep tap/TalkBack alternatives, the 500-item bound, accurate partial failures, truthful progress and Back-to-exit. Imports cannot redirect any action. Trash remains explicitly confirmed. No unbounded select-all.

Tests: forward/reverse drags, edge speed/bounds, mosaic spanning hit tests, range crossing retained pages, cancellation/rotation/import changes, selection limit, accessibility and correct bulk targets.

## 0.5.28 — Direct date navigation

Date rail offers drag preview and controlled date seek through the repository. Keep accessible Jump to date and existing month controls. Debounce/cancel superseded seeks, retain bounded pages and preserve canonical/pixel anchors. Unknown-date items and device timezone changes remain correct. Do not load an entire library or rewrite timestamps. Coordinate with the sliding app bar and floating capsule; avoid right-edge system gesture/RTL conflicts.

Tests: 10k/100k archives; earliest/latest/unknown dates; date boundaries/timezones; active import; repeated fast seeks/cancellation; viewer return and large-font/RTL/system-inset clearance.

## 0.5.29 — Management and complete acceptance audit

Finish Sources/account panels, activity summaries, import setup/history and Settings submenus with icon-led sections and progressive disclosure. All destinations/actions remain reachable, with specific recovery text and truthful progress. Preserve job checkpoints, captured destinations, default-only-for-new-job behavior and source-specific reauthorization. Keep browsing responsive while work runs. No generic cleanup can delete local-only originals. If imported names already missing from phone records require repair, provide a proven-identity metadata-only repair path or report the exact unresolved evidence; never claim preview or reimport repaired existing data.

Audit complete app navigation, action counts, state restoration, motion, cached thumbnail quality, search/category presentation, details, selection, Trash, consent, original availability and error handling. Exercise light/dark, font/display scaling, RTL, accessibility semantics/48dp targets/reduced motion, system Back and runtime lifecycle. Remove obsolete UI copy and unreachable duplicates. Record any known defect as unresolved rather than declaring UX complete. The JPEG crash remains unconfirmed unless reproduced and diagnosed.

## Shared release and unattended-work rules

Prefer object-oriented responsibilities and fail fast on internal invariants. Expected network/auth/storage errors are explicit recoverable states. Preserve original bytes, media/source IDs, metadata, albums, reviewed identities, face annotations, edit links, drafts, pins, consents, source bindings, durable jobs and upload/import checkpoints. Never collapse distinct sources based on guessed duplicates. Keep min SDK26 and stable pinned dependencies; schema upgrades require preservation/migration tests. Keep the established upgrade signer.

Each published increment requires full unit/integration/lint/APK checks and native API29/35/37 on one immutable source, complete logs, benchmark/screenshot evidence, inspection of new warnings, and zero unresolved application/build findings. Existing narrowly approved tooling/platform exceptions do not become a blanket waiver for future warnings. Preserve all existing assertions unless an intended UX expectation changes, and document each such change. No baseline/severity reduction/suppression to manufacture success. Publish the exact tested signed CI APK; independently verify package/version/checksum/certificate, documentation-only publication objects, tag/latest status and public assets. Do not rebuild after acceptance.

No routine confirmation or visual-review pause. Continue through all seven stages. Physical S23 Ultra, real Google account expiry/permissions, human TalkBack review and translated Hebrew acceptance cannot be fabricated; record them as pending owner validation while automated work proceeds. A real correctness/security/upgrade blocker or necessary new product decision stops dependent publication with a precise explanation. Keep private Takeout/photos out of public fixtures and artifacts.

## Scope boundary

Complete the photo-first UX and the newly requested Drive Trash capability. Do not add Google Ask/paid AI, Memories, decorative Create, mandatory servers, automatic visual deduplication, permanent ZIP-backed storage or a Google Photos backend dependency. The existing 250MiB import limit/large-video capability is a separate backend project, not an undisclosed UX completion claim.

## References

Owner screenshots are the visual reference. Supported Android app-bar behavior: https://developer.android.com/develop/ui/compose/components/app-bars. Drive Trash/Restore/capabilities/retention: https://developers.google.com/workspace/drive/api/guides/delete. Provider thumbnail and capabilities contract: https://developers.google.com/workspace/drive/api/reference/rest/v3/files. No claim is made about Google's private layout or thumbnail algorithms.
