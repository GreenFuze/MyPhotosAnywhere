# MPA 0.5.28 — Direct date scrubbing

Drag the visible date handle to browse months across the library. The floating date capsule previews the destination; release commits the latest seek. Tap the handle or capsule for the accessible Jump to date controls. Unknown-date photos retain honest date context. Scrolling remains a continuous gallery without date separators.

Seeks are cancellable and belong to the current gallery query. A held drag survives its own page refresh, while superseded requests cannot replace a later destination. The chosen photo and pixel offset remain stable beside tall Mosaic tiles, during new imports, viewer return and recreation. Ordinary scrolling resumes normal visible-photo tracking. Date ranges use bounded repository queries and lifecycle-aware observation, rather than loading all photos.

## Validation

Immutable `7d71cfcc087d1c9cfbe9bf6243b937a79d8a7b3e` passed **402 unit/integration tests**, **zero lint findings**, and **129 native tests on each API29/35/37 (387 total)**. All tests passed without failures, errors or skips. [Host checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38861743136) · [Native checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38861743211). Coverage includes rapid and held seeks, insertions during a drag, exact Mosaic anchors, viewer return/recreation, RTL, enlarged fonts and unknown dates. All 60 previous captures remain; four date-navigation captures per API were directly inspected. API29 Jump and API35 unknown-date captures show honest loading states; those two images are not evidence of ready controls. Ready behavior is asserted separately. All 192 final PNGs passed complete byte/CRC/decompression validation; none required a transport reread. Rejected attempts and incomplete screenshot evidence are preserved separately; publication requires complete final captures.

The additional 10,000/100,000-item date benchmark performs 21 seeks per fixture, verifies returned identities and bounded 90-row windows, and records date-range/query latency, concurrent writes and memory. Existing timeline, snapshot, Search, imported-people and Trash benchmarks remain. These are emulator measurements, not a claim of physical S23 Ultra frame performance.

Room **schema 10 and all 22 tables remain unchanged**, including the historical schema 9 file. No original metadata is rewritten to sort photos. Original bytes, identities, albums/people/edit relationships, pins, drafts, consents, captured import destinations/checkpoints, action repositories, Trash, timeline SQL/paging, viewer sessions, cache, authentication and viewer rendering/lifecycle contracts remain preserved. Prior test assertions and public Back injection are retained; only version expectations change.

Kotlin, Java, lint and Gradle warning gates remain enforced with the previously approved exact AGP deprecation. Full logs, source-bound runtime review, executed job IDs, timestamp-matched artifact digests, benchmarks and signing proof are retained in BUILD_EVIDENCE.json. Any previously approved emulator observation must match its exact recorded context; there is no new broad warning waiver. Publication uses the exact signed host CI artifact, with the established upgrade signer and documentation-only publication changes.

The raw API37 log contains the exact previously authorized Google Play services persistent-process SIM_STATE_CHANGED ANR before the first MPA test. API29 records one completed 133ms main-thread MessageQueue contention wait during an unchanged test. Its official Android emitter and context were inspected and preserved; this finite emulator timing does not establish smooth phone performance. No MPA crash, ANR, failed test or unresolved application/build warning is accepted.

Physical S23 Ultra, live Google operations, human TalkBack and translated Hebrew acceptance remain unverified. The previously reported JPEG crash remains unreproduced, with its cause unconfirmed. The existing 250MiB large-video import limitation remains separate backend work.

Management refinement and final UX audit continue unattended. After the UX work, the oversized-XMP and item-specific import retry request will be completed. [Delivery ledger](https://github.com/GreenFuze/MyPhotosAnywhere/blob/main/docs/PHOTO_FIRST_REFINEMENT_ROADMAP.md).

Version **0.5.28**, code **39**, minimum SDK **26**, target SDK **37**, Room schema **10**.

APK SHA-256: `5e91aecbeb141bae5a602b410d50661b44dd7220dc3d976856c06f32d2f9bd60`.

Upgrade certificate SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4`.
