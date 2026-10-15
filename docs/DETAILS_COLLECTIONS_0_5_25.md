# MPA 0.5.25 — Visual Collections and clearer Details

Collections begins with compact Favorites, Trash, Archive and Offline shortcuts, followed by real People/Places covers and albums. Album pin, rename and removal actions are grouped under a labeled management control. Its state follows live album updates, so pinning and renaming retain the current pin state. Reviewed people use real face crops; imported Takeout name hints use photo covers and remain separate from reviewed face identities. Recognition and map consent are preserved.

Swipe **up** for the redesigned Details sheet. Local and remote media share grouped date/file, source/original availability and action sections. Date provenance follows the existing canonical timeline projection; dimensions come from an available original using a bounds-only read under its cache lease, including EXIF rotation. Inspecting Details does not download an original merely to obtain dimensions or rewrite capture metadata. Explicit Close slides the sheet away. Favorite, offline, archive, album membership, location, face correction, share/export and existing edit/AI actions remain available.

## Validation

Immutable application `016222142c36886f05b7f8a6c8bfa1c775b55f0f` passed **374 unit/integration tests**, **zero lint findings**, and **116 native tests on each of API 29/35/37 (348 total)** with no failures, errors or skips. [Host checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38750785439) · [Native checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38750785291).

Added coverage verifies rotated original dimensions, exact-source linked-date precedence, timestamp/byte preservation, compact shortcut navigation and lazy People navigation. A real album-menu sequence pins, renames, unpins and removes an album, preserving its photo and original bytes. All prior assertions remain; the existing Details favorite action explicitly scrolls to its new section before tapping. All 48 previous captures remain, plus four visual Details/Collections/People/album-menu captures per API. Full logs, directly reviewed synthetic captures, 10,000/100,000-item benchmark fixtures, exact job/artifact IDs, archive digests and source proofs are retained in BUILD_EVIDENCE.json.

Room **schema 10 and all 22 tables are unchanged**, including the historical schema 9 file. Missing indexed original/thumbnail files now keep Archive and other galleries browsable instead of throwing during thumbnail loading. An available cloud preview is used only through its verified existing photo link and matching content/revision; a changed or unrelated representation cannot substitute another photo. The existing cache lock and bounded 480px base decoder remain, and no original bytes are created or rewritten by this fallback. Existing metadata, media identities, relationships, source authorization, imports/checkpoints, Trash, original bytes, paging/anchors and viewer decoding/lifecycle contracts remain intact. Dependencies and SDK requirements remain pinned. Kotlin, Java, lint and Gradle gates stay enforced; the approved AGP deprecation and individually traced platform timing observations are documented. Unknown application/build diagnostics and all unapproved crashes, ANRs, OOMs and test failures block publication. No warning baseline, severity reduction or blanket suppression is added.

The APK is the exact signed CI artifact, with the established upgrade signer and no rebuild. Publication metadata changes documentation only. Physical Samsung S23 Ultra, real Google operations, TalkBack and translated Hebrew acceptance remain unverified. The earlier JPEG crash remains unreproduced and its root cause unconfirmed. Emulator results do not establish phone performance. The existing 250MiB import/large-video limitation remains separate backend work.

Four stages continue unattended: adaptive sharper thumbnails and richer mosaic; continuous drag selection; direct date scrubbing; management refinement and the complete UX audit. [Delivery ledger](https://github.com/GreenFuze/MyPhotosAnywhere/blob/main/docs/PHOTO_FIRST_REFINEMENT_ROADMAP.md).

Version **0.5.25**, code **36**, minimum SDK **26**, target SDK **37**, Room schema **10**.

APK SHA-256: `25b20f4b435bb5d2016b1b4725de8b17d11e4a41a2d8dc3a63c60b54fb4c3a96`.

Upgrade certificate SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4`.
