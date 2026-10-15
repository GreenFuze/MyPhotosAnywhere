# Google Photos-familiar refinement roadmap

Baseline: MPA 0.5.21/code 32, Room schema 9. Owner authorizes writing this roadmap, then implementing and publishing the next version after the existing checks. Five planned UX refinements remain after the focused Drive recovery release. These are refinement releases, not unfinished original architecture phases. Adjust later scopes when real-device evidence warrants it.

## Shipped foundation

0.5.10 unified chronology/paging/navigation; 0.5.11–12 viewer Details (current gesture **swipe up**), selection and Collections; 0.5.13 Search; 0.5.14 focused management/imported labels; 0.5.15 density/date controls; 0.5.16 floating date; 0.5.17 aspect-aware justified Mosaic; 0.5.18 icons/Settings; 0.5.19 Takeout preview diagnostics. Google Photos screenshots supplied by the owner are visual references. Static screenshots do not prove gestures, performance or every rollout's behavior.

## Shipped 0.5.21 / code 32 — continuous photo grid (owner refinement)

0.5.20 shipped the immersive viewer and passed its full release gates. The owner now requests removing visible day/date separators because the floating capsule already supplies date context. Render one continuous chronological grid in Mosaic and Square, including across month/year boundaries. Keep the floating month/year capsule, accessible full-date descriptions, Jump to date, unknown-date handling and stable media/pixel anchors. Preserve ordering, queries, metadata, original availability, selection and viewer sessions. Verify cross-date packing and existing date/continuity regressions before publishing the exact signed CI APK.

## 0.5.22 / code 33 — Drive access renewal and recovery wording

Prioritize the owner’s repeated reconnect and in-app Open MPA reports. Renew rejected access silently once per request, preserve real consent/permission recovery and durable checkpoints, and distinguish notification instructions from in-app status. See [scope and evidence](DRIVE_RECOVERY_0_5_22.md).

## 0.5.23 — visual Collections, People and albums

Put Favorites/Archive/Albums/Offline shortcuts near the top. Use real covers and compact menus for album management. Make People a visual browsing surface, with management/review actions progressively disclosed. Imported labels use photo covers and stay separate from reviewed face identities; never fabricate face crops. Keep every existing correction, merge, pin and album action reachable with clear destructive semantics. Pass existing membership/identity/consent checks plus first-viewport and Back/rotation tests.

## 0.5.24 — richer mosaic composition

Evaluate portrait tiles spanning multiple rows, closer to the supplied Google Photos reference. Design the bounded block layout before implementation: deterministic chronology, unique canonical IDs, continuous chronology across calendar boundaries, 48dp targets, stable page-boundary packing and exact/nearest-visible anchors. Retain Square and the current justified Mosaic as fallbacks until the new layout passes device/large-library tests. Do not combine this with SQL, viewer-session or import rewrites, or claim Google's private layout algorithm.

## 0.5.25 — continuous selection

Add long-press-and-drag range selection and bounded edge scrolling, including mosaic hit testing. Keep explicit tap/TalkBack alternatives, stable IDs, the 500-item limit, accurate partial failures and Back-to-exit. Imports must never redirect a bulk operation to new identities. Cancel or reconcile a changing drag dataset explicitly; never use mutable indices alone. No global select-all or ambiguous deletion action.

## 0.5.26 — direct date navigation

Refine the date rail into a drag preview and controlled date seek, retaining accessible Jump to date and existing month controls. Seek through the repository with cancellation/debouncing and bounded retained pages. Preserve the viewed identity, unknown dates, timezone rules and import continuity. Benchmark 10k/100k archives and test gesture-nav/RTL edge clearance. No full-library load or metadata timestamp rewrite.

## 0.5.27 — management and acceptance polish

Refine source/account panels, active-work summaries, import setup/history and Settings detail pages using compact icon-led menus and progressive disclosure. Keep job state truthful and source recovery explicit. If existing imported metadata needs repair, propose a proven-identity metadata-only recovery path before touching records; never imply that a preview performs repair. The existing 250 MiB import limit and large-video support are separate storage/capability work. Review motion, contrast, enlarged fonts, RTL, TalkBack and Samsung behavior across the complete app.

## Shared delivery gates

Keep MPA branding, Photos/Collections plus separate Search, top Add/Activity/Sources and Drive-backed ownership. No decorative Create, Memories, Ask/AI parity or Google backend dependency. Preserve originals, metadata, IDs, relationships, consents, drafts, pins, sources, captured destinations and durable checkpoints. Prefer focused object-oriented presentation/state responsibilities; recover expected operational failures and fail fast on internal invariants.

Each implemented release requires full unit/integration/lint/APK checks and native API 29/35/37 on one immutable revision, complete diagnostic/benchmark/screenshot evidence, zero unresolved application/build findings except the explicitly approved tooling diagnostic, exact CI APK/package/version/checksum/signer verification, and documentation-only publication proof. No blanket suppressions, severity reductions, baselines or weakened assertions. No additional visual-review pause is required; owner phone feedback still takes priority when supplied. S23 Ultra acceptance, real TalkBack and the unconfirmed JPEG crash remain pending. No speculative feature parity is bundled into these refinements.
