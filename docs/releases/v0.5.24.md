# MPA 0.5.24 — Sliding gallery bar and smoother controls

Scrolling toward older photos slides the top bar out and gives that space to the gallery. Reverse scrolling brings the controls back. The bar remains pinned for Search, management, selection and assisted navigation. Its state follows the current query and survives viewer return and activity recreation.

Navigation, selection tools, activity strips and viewer controls now use short slide/fade transitions. Outgoing controls lose input, focus and accessibility actions immediately while their exit animation finishes. The photo surface stays stable, preserving existing decoding, verified downloads, gestures, video lifecycle and cache leases. System animation-duration settings are respected, including zero-motion coverage.

## Validation

Immutable application `2655497b6f9de6f7a4e7e13beb3e2cb758402c3f` passed **373 unit/integration tests**, **zero lint findings**, and **113 native tests on each of API 29/35/37 (339 total)**, with no failures, errors or skips. [Host checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38738781861) · [Native checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38738781880).

Four added native scenarios exercise real bar geometry, released gallery space, reverse-scroll recovery, viewer-return pixels, recreation, pinned Search, outgoing input/semantics, large-font RTL and zero-motion transitions. All previous assertions remain; the existing continuity test now performs the reverse-scroll gesture before using the collapsed bar. All 45 previous required captures remain plus three motion captures per API. Full logs, direct synthetic screenshot review, 10,000/100,000-item timeline/Search/imported-people/Trash benchmarks and exact job/artifact/source proofs are retained in BUILD_EVIDENCE.json.

Room **schema 10 is unchanged**, including all 22 tables and the historical schema 9 file. No persistence, import, source authorization, Trash, timeline paging, search or media-rendering contracts change. Dependencies remain pinned. Kotlin, Java, lint and Gradle diagnostic gates stay enforced; previously approved tooling diagnostics and individually traced ART timing observations are recorded after full-log review. Final observations include one 3.120ms GC suspension wait, a 118.824ms unchanged AndroidX initializer verification, and three completed startup-only background-worker lazy-initialization waits (1.417s/1.265s/1.091s). Official emitters, exact unchanged dependency DEX definitions and full thread/startup context are retained; no app crash, ANR, OOM, test failure or repeating Kotlin-lazy contention appears. These emulator observations do not establish physical-device latency. No warning baseline, severity reduction or blanket suppression is introduced.

This is the exact signed CI APK, with the established upgrade certificate. Publication metadata changes documentation only. Physical Samsung S23 Ultra, real Google authorization/Trash/Restore, TalkBack and translated Hebrew acceptance remain unverified. The earlier JPEG crash remains unreproduced and its cause unconfirmed. Emulator results do not establish phone performance. The existing 250MiB import/large-video limitation remains a separate backend project.

The remaining stages continue unattended: visual Collections and Details; sharper thumbnails and richer mosaic; continuous drag selection; direct date scrubbing; management refinement and the complete UX audit. [Delivery ledger](https://github.com/GreenFuze/MyPhotosAnywhere/blob/main/docs/PHOTO_FIRST_REFINEMENT_ROADMAP.md).

Version **0.5.24**, code **35**, minimum SDK **26**, target SDK **37**, Room schema **10**.

APK SHA-256: `da8058133f22a9a8ffb99ed267c66a9f1aadc9ee29099e0ed79750b6475a40fb`.

Upgrade certificate SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4`.
