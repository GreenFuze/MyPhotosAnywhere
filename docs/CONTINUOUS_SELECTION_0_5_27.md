# MPA 0.5.27 — Continuous drag selection

Long-press a photo, then drag to extend the selection in Photos and Search. Reverse the drag to shorten this gesture's range without removing photos selected earlier. Actual measured tile coordinates handle Square, Justified rows, portrait Mosaic spans and RTL. Edge scrolling has a bounded frame-based speed; the selection is limited to 500 photos and 1,000 visited identities, with a visible limit notice. Query, layout, refresh or content changes cancel the gesture safely. Tap and accessible selection alternatives, Back-to-exit, bulk partial results and explicit Drive Trash confirmation remain available.

## Validation

Immutable application `6be8d783c8014e2aa58bb115b5be4a8b75b55ff7` passed **392 unit/integration tests**, **zero lint findings**, and **125 native tests on each API29/35/37 (375 total)** with no failures, errors or skips. [Host checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38805285533) · [Native checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38805285554). Tests exercise forward/reverse ranges with pre-existing selection, all three gallery layouts, edge scrolling, Back, RTL/large fonts and cancellation during an import. Previous assertions remain unchanged except version text. All 56 previous captures remain, plus four selection captures per API; the twelve new captures were directly reviewed. Existing 10,000/100,000-item query, snapshot, layout and memory benchmarks remain available; emulator measurements do not establish physical-phone frame performance.

Room **schema 10 and all 22 tables are unchanged**, including the historical schema 9 file. Original bytes, metadata, identities, relationships, pins, drafts, consent, source authorization, durable imports/checkpoints, action repositories, Trash, timeline SQL/paging, viewer sessions, preview cache and rendering/lifecycle contracts are unchanged. The diagnosed Mosaic hit-coordinate defect was repaired before publication and temporary geometry traces were removed. Complete rejected-attempt evidence is retained separately.

Kotlin, Java, lint and Gradle warning gates remain enforced, with the previously approved exact AGP deprecation. Complete final logs, source-bound runtime diagnostic review, actual executed job IDs and timestamp-matched artifact digests are retained in BUILD_EVIDENCE.json. The new native Back test uses public Instrumentation.sendKeyDownUpSync; the test-only Espresso hidden-input warnings were eliminated on all three APIs without changing any selection assertion or the application APK bytes. Exact ART verifier/finite CheckJNI observations and one recoverable platform monitor-installation race are traced to official emitters with source hashes, raw context and actual DEX definitions. No new broad warning waiver is claimed. The APK is the exact signed host CI artifact with the established upgrade signer; publication changes documentation only and does not rebuild it.

Physical S23 Ultra, real Google operations, human TalkBack and translated Hebrew acceptance remain unverified. The earlier JPEG crash remains unreproduced and its root cause unconfirmed. The existing 250MiB import/large-video limitation remains separate backend work.

Direct date scrubbing and management refinement/full UX audit continue unattended. After that UX work, the newly reported oversized-XMP and item-specific import retry behavior will be addressed, as requested. [Delivery ledger](https://github.com/GreenFuze/MyPhotosAnywhere/blob/main/docs/PHOTO_FIRST_REFINEMENT_ROADMAP.md).

Version **0.5.27**, code **38**, minimum SDK **26**, target SDK **37**, Room schema **10**.

APK SHA-256: `75d75cb673638770e18e26bd067a6c78e44883096bbbcbd9a60fc9fbdcf88480`.

Upgrade certificate SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4`.
