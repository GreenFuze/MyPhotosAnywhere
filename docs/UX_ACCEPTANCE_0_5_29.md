# MPA 0.5.29 — Management, Details and protected library actions

Sources and import history now use icon-led cards, expandable Transfer settings and Source help, clearer recovery actions, and layouts that wrap at large font sizes. People names and Trash action counts use appropriate wrapping/plurals. Existing destinations and actions remain reachable. The floating date capsule is an accessible heading; the photo grid stays continuous.

Details reads facts for the current photo identity and content revision, shows honest loading and original availability, and exposes an explicit **Remove device download** action for eligible Drive-backed photos. This is separate from Drive Trash. Confirmation closes the active viewer before removal, verifies the current local checksum and fresh exact Drive copy, and rechecks source, identity, readers and protected work. Local-only/shared originals, offline pins, drafts, AI handoffs, pending recognition and unfinished transfers prevent removal. A valid cached preview is retained or regenerated within bounded decoding/storage limits. Metadata, albums, people and edit relationships stay in the library. Failed verification, cancellation or preview generation keeps the original.

For photos already imported from Takeout, open the saved import's details and choose **Repair saved metadata**. It reapplies retained sidecars in bounded batches only when the saved checksum, byte count, photo ID, captured Drive source and remote ID all match. It preserves reviewed annotations, original files, destinations and item/job checkpoints, and never uploads originals again. Invalid or conflicting records produce a bounded review receipt while other metadata records continue. If no sidecar was retained, add the matching ZIP part first. Only people names actually exported by Google can be restored; imported name hints remain separate from reviewed face groups.

Album rename/offline-pin updates now change only their intended database columns, preventing stale screen values from overwriting unrelated changes. A regenerated base preview at the same filename triggers a fresh image request instead of leaving a failed painter visible. Source/account picker and browser failures use narrow recoverable handling; internal invariants and cancellation remain visible.

## Validation

Immutable `80612beaebee2362d84e3b6234512a781300c468` passed **417 unit/integration tests**, **zero lint findings**, and **137 native tests on each API 29/35/37 (411 total)**, with no failures, errors or skips. [Host checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38867723485) · [Native checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38867723466). All 64 previous captures remain, plus nine new captures per API, with complete PNG validation and direct visual inspection recorded in BUILD_EVIDENCE.json.

Focused coverage exercises real viewer/confirmation/Cancel continuity, landscape Details and portrait return, pinned/local-only protection, fresh verification and reader/source races, cancellation, saved metadata repair/idempotence/conflicting records/checkpoints, regenerated-preview pixels and original hashes, both themes, RTL, enlarged fonts, semantics and minimum touch targets. Existing native assertions are preserved; Google setup details now additionally expands Source help before the previous signing/package/clipboard checks. Negative Robolectric decoding uses Android 8.1's supported BitmapFactory path with invalid-data fabrication disabled; native device decoding tests remain on API29/35/37.

The scrolling audit measures actual Window FrameMetrics during 12 inserts into a 320-photo fixture, bounds its collector to 1,024 samples and records heap use. These are emulator diagnostics, **not a passed 60Hz or S23 Ultra performance claim**:

| API | Frames | Median ms | P95 ms | Maximum ms |
| --- | ---: | ---: | ---: | ---: |
| 29 | 329 | 63.29 | 107.50 | 301.89 |
| 35 | 149 | 50.15 | 133.49 | 300.37 |
| 37.0 | 178 | 50.20 | 142.34 | 357.71 |

Existing 10,000/100,000-item timeline, snapshot, Search, imported-people, Trash and date-seek benchmarks remain, with bounded pages and deterministic identities. Room **schema 10 and all 22 tables remain unchanged**, including historical schema 9. Authentication, original import/upload execution, durable checkpoints, captured destinations, consent, editor and viewer decoding/lifecycle contracts remain preserved. The focused metadata repair is explicitly user initiated.

The final five raw CI/device logs contain no crash, ANR, out-of-memory or test-failure markers, and no new application-process warning families relative to published 0.5.28. An earlier unchanged-source native attempt stopped at a Google SDK HTTP502 before compilation/tests; the complete same-source rerun passed. The failed bootstrap log and exact executed artifact/job provenance remain preserved.

Strict compiler/lint/Gradle gates remain enforced with only the previously authorized AGP deprecation. Full raw logs, individually investigated runtime observations, exact job/artifact/source digests and signing proof are retained. Publication uses the exact CI APK; publication metadata changes no tested application/build objects.

Physical S23 Ultra, live Google authorization/permissions/Trash/removal, human TalkBack and translated Hebrew acceptance remain unverified. Emulator timing does not establish smooth physical scrolling. The reported JPEG crash remains unreproduced, with its cause unconfirmed. The existing 250MiB video-import limit remains separate backend scope. These limits remain explicit in the UX delivery ledger.

The queued **oversized XMP and general import recovery work follows this release**. This version does not claim that fix. The required next behavior is to continue other images after an item failure and retry only failed/unfinished images, keeping verified successes and captured destinations intact.

Version **0.5.29**, code **40**, minimum SDK **26**, target SDK **37**, Room schema **10**.

APK SHA-256: `078e1019ab597dd39eebe208dfad66d227560b603e459b9a065e556a38d22955`.

Upgrade certificate SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4`.
