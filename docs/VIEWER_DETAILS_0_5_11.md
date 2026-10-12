# 0.5.11 viewer and Photo Details increment

## Behavior

- On a photo at fit zoom, drag down to open Photo Details, or use the visible Details button. A horizontal swipe pages through photos. Pinch/zoomed panning retains control of its gesture.
- Details is a scrollable bottom sheet with a visible part of the retained viewer above it. Back or Close dismisses the sheet and returns to the same viewer page. Rotation preserves the viewer session and Details context.
- The viewer has a compact date/filename header and Share, Edit and Favorite actions. Share/Edit require a real local original; Favorites use the existing local identity and metadata update path. An unresolved remote representation does not acquire a fabricated local identity.
- Details retains the existing Favorite, offline pin, album, archive, location, recognized-person correction, sharing, original export and external-AI workflows. It shows the source and original availability. Viewing cloud metadata does not require an original download; authorization recovery remains explicit.
- The existing editor and manual external-AI-result workflow are reused. Share retains the existing bounded GPS-stripped image behavior; Export original preserves original bytes and metadata.
- Normal Photos browsing remains paged. Export resolves its saved identity directly rather than relying on a full-library subscription. Search keeps its existing semantics and returns to its originating query.

## Preserved boundaries

Room schema remains 9. This increment does not change migrations, imports/checkpoints, captured destinations, source identity, original verification, decoder budgets, cache-reader leases or the video playback implementation. Rendering work is suspended and video paused while Details or another workflow obscures the viewer.

The owner explicitly described a downward details gesture. This increment follows that request; it does not claim a particular Google Photos gesture direction from still screenshots.

Selection, broader Collections/Search/source/activity redesigns, date/caption editing and new AI capabilities remain subsequent increments. No Takeout URL entry is introduced.

## Validation contract

New tests cover gesture ownership and thresholds, current-photo identity, metadata persistence, original-byte preservation/export, remote authorization/availability, Details dismissal, Search return, rotation, compact controls, enlarged fonts, light/dark themes and RTL. Existing import, timeline continuity/scale, original-renderer lifecycle and cache safety tests remain required.

CI requires the existing strict compiler/lint/build gates and native checks on API 29, 35 and 37 for one immutable application revision. Required screenshots include local and remote Details plus large-font and RTL layouts. Release evidence is recorded after those checks pass.

The already approved AGP/Gradle deprecation and documented emulator infrastructure diagnostics remain the only tooling exceptions. No lint baseline or warning suppression is added.

Samsung Galaxy S23 Ultra acceptance and the previously reported JPEG crash remain unverified. Emulator tests do not establish the cause or resolution of that real-device crash.

Release gate follow-up: Paging is pinned to stable 3.5.3 (October 12 patch, page-retention/separator fixes), after CI correctly rejected the newly superseded 3.5.2. Search clears input focus when submitting/opening a viewer; its Back regression waits for the asynchronous IME dismissal before asserting sheet dismissal. No warning severity changes.

CI retains each API evidence archive directly. The redundant artifact-merge action was removed after its Node Buffer deprecation appeared in complete logs; this resolves the warning without suppressing diagnostics or changing application behavior.

## Final publication evidence

The immutable application and exact signed CI APK passed the release gates. [0.5.11 evidence and remaining phone acceptance](releases/v0.5.11.md). Release metadata does not change application, toolchain or workflow files.
