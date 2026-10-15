## MPA 0.5.22: Drive access recovery

Application `eb041dbb4985a1d27a8eb7b68af2467be3d77070` passed 354 unit/integration and 106 native tests per API29/35/37 (318 total), no failures/errors/skips, zero lint findings, unchanged schema9 and verified upgrade signer. [Evidence and limits](releases/v0.5.22.md). Real S23 Ultra authorization acceptance remains pending.

## MPA 0.5.21: continuous photo grid

Application `7c6d2cf1ef220581843c381a64588269afadf033` passed 342 unit/integration tests, zero lint findings and 106 native tests on each of API 29/35/37 (318 total), with no failures/errors/skips. Exact CI APK, signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.21.md). S23 Ultra acceptance remains pending.

## MPA 0.5.20: immersive viewer

Application `37d8aebc19abb6fdfd157467971e8cb95ffae46f` passed 338 unit/integration tests, zero lint findings and 105 native tests on each of API 29/35/37 (315 total), with no failures/errors/skips. Exact CI APK, signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.20.md). S23 Ultra acceptance remains pending.

## MPA 0.5.19: Takeout metadata preview

Application `6ef6bed197cd0efdb5a1c5a4f13aae60101f4b3a` passed 338 unit/integration tests, zero lint findings and 101 native tests on each of API 29/35/37 (303 total), with no failures/errors/skips. Exact CI APK, signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.19.md). S23 Ultra acceptance remains pending.

## MPA 0.5.18: icon-led actions and organized Settings

Application `4fef4bdc1dba263dd7b7c03692233d17e219923a` passed 330 unit/integration tests, zero lint findings and 98 native tests on each of API 29/35/37 (294 total), with no failures/errors/skips. Exact CI APK, signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.18.md).

Date-range queries over 100,000 mixed items measured 481.1–899.3ms across API 29/35/37 CI emulators. Complete timeline, Search and imported-label 10k/100k measurements are attached. Metadata/Offline scans and full viewer-session preparation retain previously documented large-library latency; these are emulator results, not Samsung performance guarantees. Bounded 300-item mosaic planning measured 0.91–3.81ms in those fixtures (planning only, excluding drawing and thumbnail header IO).

Previously approved tooling diagnostics remain documented. S23 Ultra acceptance remains pending; the owner could not reproduce the JPEG crash again, and its cause remains unconfirmed.

## MPA 0.5.17: justified photo mosaic

Application `b966cfdd3c1a56f2676f1d23938d765d054728f4` passed 320 unit/integration tests, zero lint findings and 96 native tests on each of API 29/35/37 (288 total), with no failures/errors/skips. Exact CI APK, signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.17.md).

Date-range queries over 100,000 mixed items measured 457.3–1001.0ms across API 29/35/37 CI emulators. Complete timeline, Search and imported-label 10k/100k measurements are attached. Metadata/Offline scans and full viewer-session preparation retain previously documented large-library latency; these are emulator results, not Samsung performance guarantees. Bounded 300-item mosaic planning measured 0.49–4.99ms in those fixtures (planning only, excluding drawing and thumbnail header IO).

Previously approved tooling diagnostics remain documented. S23 Ultra acceptance remains pending; the owner could not reproduce the JPEG crash again, and its cause remains unconfirmed.

## MPA 0.5.16: floating gallery date

Application `a99a5e96ac7c901f28e530c790c8d5e769a33266` passed 308 unit/integration tests, zero lint findings and 94 native tests on each of API 29/35/37 (282 total), with no failures/errors/skips. Exact CI APK, signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.16.md).

Date-range queries over 100,000 mixed items measured 312.3–506.6ms across API 29/35/37 CI emulators. Complete timeline, Search and imported-label 10k/100k measurements are attached. Metadata/Offline scans and full viewer-session preparation retain previously documented large-library latency; these are emulator results, not Samsung performance guarantees.

Previously approved tooling diagnostics remain documented. S23 Ultra/JPEG acceptance remains pending.

## MPA 0.5.15: gallery gestures and date navigation

Application `5aae9cbf7195703ad6b3fcb54ac5a806f27353bc` passed 305 unit/integration tests, zero lint findings and 92 native tests on each of API 29/35/37 (276 total), with no failures/errors/skips. Exact CI APK, established signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.15.md).

Date-range queries over the mixed 100,000-item fixture measured 481.8–527.7ms across API 29/35/37 CI emulators. Timeline, Search and imported-label fixtures remain measured at 10k/100k. Metadata and Offline date ranges use bounded cursor scans, so their latency depends on matching data and filesystem checks. These are emulator measurements, not Samsung phone guarantees.

Previously approved tooling diagnostics remain documented. S23 Ultra/JPEG acceptance remains unverified.

## MPA 0.5.14: focused management and imported people

Application `e2d34e8240a328ed181220cf7f5f8aadce3b9785` passed 295 unit/integration tests, zero lint findings and 88 native tests on each of API 29/35/37 (264 total), with no failures/errors/skips. [Host](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38364372594) · [Native](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38364372555). Exact CI APK, established signer and schema 9 independently verified. [Scope and remaining acceptance](releases/v0.5.14.md).

At 100,000 indexed items, imported-label discovery measured 3.86–10.12s, a sparse first label page 0.49–1.28s, and its viewer snapshot 3.98–10.42s across CI emulators. These are fixture measurements, not phone guarantees. Discovery scans retained metadata on an IO dispatcher; media pages and cursor windows remain bounded. No new persistent full-text or people-membership table was added.

Previously approved tooling diagnostics remain documented. S23 Ultra/JPEG acceptance and the owner’s ZIP contents remain unverified.

## MPA 0.5.13: dedicated paged Search

Application `be9df5edc9b4ceb4e8199442bf26c2587bb3e2ee` passed 278 unit/integration tests, zero lint findings and 80 native tests each on API 29/35/37 (240 total), with no failures/errors/skips. [Host](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38349866325) · [Native](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38349866291). The exact CI APK preserves the established signer and schema 9. [Scope, benchmarks and acceptance](releases/v0.5.13.md).

At 100,000 indexed items, a sparse first Search page measured 0.59–1.03 seconds and a full no-match scan measured 5.13–7.27 seconds across CI emulators. These are measured fixtures, not phone guarantees. Search remains a linear indexed-metadata scan without a new full-text table; its cursor windows and retained pages are bounded, and matched text payloads are discarded before retention.

Previously approved tooling diagnostics remain documented; other warnings block. S23 Ultra acceptance and the reported JPEG crash remain unverified.

## MPA 0.5.12: upward Details, selection and Collections

Application `976986a18bd7c8fb19ed06cdd8d0055a87b8a062` passed 247 unit/integration tests, zero lint findings and 68 native tests each on API 29/35/37 (204 total), with no failures/errors/skips. [Host](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38307455339) · [Native](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38307455194). The exact CI APK preserves the established upgrade certificate and schema 9. [Release scope/evidence](releases/v0.5.12.md).

Stable selection, representation changes, partial failures, safe metadata/album actions, exact original sharing, cancellation cleanup, bounded covers, accessible controls and contextual clearance are covered. At 100,000 indexed items, viewer sequence preparation measured 8.0–12.8 seconds across CI emulators. The selected preview opens before preparation completes. This remains a large-library performance limitation; detailed query/seek/memory/import measurements are attached.

Prior approved tooling diagnostics remain documented; other warnings block. S23 Ultra acceptance and the reported JPEG crash remain unverified.

## MPA 0.5.11: viewer and Photo Details

Application `1f38424a7f3903655b3f00e55d32a518b36412aa` passed 236 unit/integration tests, zero lint findings and 53 native tests each on API 29/35/37 (159 total), with no failures/errors/skips. [Host](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38296583020) · [Native](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38296583052). The exact CI APK has the established upgrade certificate and unchanged schema 9. [Release scope and evidence](releases/v0.5.11.md).

Downward Details, Back/rotation, gesture ownership, current-photo actions, metadata/original preservation, remote authorization, missing files, Search continuity, large fonts and RTL are covered. At 100,000 indexed items, viewer sequence preparation measured 5.9–29.3 seconds across CI emulators. The selected preview opens before sequence preparation completes; this remains a large-library performance limitation. Exact query, seek, memory and concurrent-import measurements are in BUILD_EVIDENCE.json.

Prior approved tooling diagnostics remain documented; all other warnings block. S23 Ultra acceptance and the reported JPEG crash remain unverified. Broader selection and destination redesigns remain later work.

## MPA 0.5.10: photo-first shell and unified timeline

The application at `4a899d4c655ebc729353fac83d9e1194950a273b` passed 226 host unit/integration tests, zero lint findings, APK assembly and 43 native tests each on API 29/35/37, with no failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38252814319) · [Native checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38252814270). The exact CI APK was independently verified for package/version/code, checksum and the established upgrade certificate. Schema 9 matches the released Git blob byte-for-byte; no migration is added.

The owner authorized public-repository conversion and protected signing migration. The key now resides in an encrypted repository secret, not an Actions cache; every external contributor's fork PR requires approval. Public standard runners completed all final checks. This supersedes earlier billing-blocked status notes.

The owner approved publication with the specific emulator diagnostics documented, in addition to the earlier narrow AGP/Gradle deprecation exception. Every application/compiler/lint/Gradle warning remains blocking outside that exception; raw test/runtime diagnostics are retained and described in the [release notes](releases/v0.5.10.md). This is not a claim of warning-free emulator logs. S23 Ultra acceptance and the reported JPEG crash remain unverified.

At 100,000 indexed items, CI first-page queries measured 566–1010 ms, date seeks 403–657 ms and viewer sequence preparation 7.8–26.5 seconds. Preview rendering precedes preparation. Concurrent import writes completed in 2.9–4.3 ms during indexing. These are emulator measurements, not S23 Ultra results.

## Released 0.5.9: terminal history dismissal and viewer safety

The reported downloaded-image crash has no device trace yet; identified risks are tested separately rather than asserted as its exact cause. Regression coverage targets persistent reversible terminal dismissal, late notification suppression, preservation of rows/source bytes, current cache-read leases, rounded pixel bounds, native bitmap-frame lifetime, injected preview/tile memory failures and a downloaded image viewed while newer import rows arrive. Source `587527a32247bb0a76dd1a388291eb7117a0a8cc` passed **195 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **31 native tests each on Android 10/API 29 and Android 15/API 35 (62 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38164766321) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38164766305). Local Windows checks passed all 195 tests, lint and both APK builds. Room v9 matches CI; native checks ran in CI. Publication reuses the exact passed CI APK with the established signer.

Actual CI APK SHA-256: `e70e63d0da6a3f526423ea8b896e3f25c441d53fc78ca8b6c64ed6f6689d7f94`. Established signer: `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.9.json`; full evidence accompanies the release.

Bitmap ownership follows [Android's Bitmap lifecycle guidance](https://developer.android.com/reference/android/graphics/Bitmap#recycle()). Synthetic decoder failures are injected without exhausting the test device. The owner reported an intermittently crashing 2.5 MB JPEG on 0.5.8. No real phone crash trace or confirmation of its root cause is claimed. The first candidate's Android 15 run passed the four new dismissal/viewer tests but failed an existing clipboard assertion (null immediately after copying); Android 10 was cancelled by the fail-fast matrix. The final candidate waits for asynchronous clipboard publication and passed the full required matrix.

## Released 0.5.8: in-app import progress

Storage observes durable Room checkpoints and uses the same ImportProgress model as notifications. A native fixture exercises zero-total preparation, upload offset updates, selected-only verified counts, overall accessibility progress, retry detail and pause, including an active job older than eleven stopped jobs. Source `a75c45a52866d01b39768bc388ac7241c4ccdfb1` passed **190 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **27 native tests each on Android 10/API 29 and Android 15/API 35 (54 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38161296293) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38161296382). Local Windows checks passed all 190 tests, lint and both APK builds. Room v9 matches CI; native checks ran in CI. Publication reuses the exact passed CI APK with the established signer.

Actual CI APK SHA-256: `adcd798a6f4099044cc722048b2d3c357c0ac09717cb745fbc24a193ea0c7a66`. Established signer: `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable run references are in `docs/releases/v0.5.8.json`; full evidence accompanies the release.

## Released 0.5.7: continuous import transfers and direct Drive ZIP reading

Version 0.5.7 / code 18 / Room v9. Source `a375f5af5c880cc3f0d57d40ee1e9c4182e830a4` passed **189 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **26 native tests each on Android 10/API 29 and Android 15/API 35 (52 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38159501331) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38159501332). Local Windows checks passed all 189 tests, lint and both APK builds; local device tests were not run. Room v9 matches CI.

The exact passed CI APK has SHA-256 `47fba2f7807dee8d5ec2ef5f33baf622e1e4b4f3620afe4b49e3b98346fb3d2c` and established signer `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.7.json`; full results are attached as BUILD_EVIDENCE.json.

Android 14+ imports use user-initiated JobScheduler transfers with an immediate system-associated notification, assigned-network sockets/DNS, stable collision-free IDs, source network preferences, estimated upload bytes and graceful Pause. Earlier APIs retain WorkManager foreground execution. Shared execution retries transport failures in place with bounded backoff, preserving active verified progress and durable checkpoints. Visible reopen migrates existing running work; background initialization never creates UIDT jobs. Pause/cancel remains authoritative across completion/network races. System/user stops and Android 14 notification dismissal remain platform-controlled.

Drive Takeout browsing is source-scoped and paginated. Its seekable ZIP adapter requests bounded exact ranges with pinned ETags and live version/size/ancestry checks, reads JSON and stages selected media through the existing verified import pipeline. Source archives remain untouched and are never copied wholesale to phone storage. Individual media is still uploaded; permanent archive-backed originals are not implemented. Room v9 and existing jobs remain compatible.

Regression fixtures cover scheduler ID collisions/recreation, required network/bytes/permission, progress/Pause, retry/cancellation/fail-fast behavior and checkpoint retention, remote ZIP metadata/media streaming, distant seeks/cache budgets, ignored/shifted/truncated/changed HTTP ranges and replaced/moved archives. Native coverage checks the transfer contract and Room retry checkpoint preservation on both API 29 and 35. It does not reproduce live Samsung power management, exercise a real Google-account archive, prove swipe prevention or long-running JobService execution. Those remain phone acceptance checks.

## Released 0.5.6: background recovery and exact duplicate index

Version 0.5.6 / code 17 / Room v9. Source `6a1d23f17e8fb6eb404bc9fca314200e3e54ec0f` passed **175 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **24 native tests on each of Android 10/API 29 and Android 15/API 35 (48 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38156043758) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38156043815). Local Windows checks passed all 175 tests, lint and both APK builds; local device tests were not run. CI Room v9 matches the committed schema.

The exact passed CI APK has SHA-256 `f925519bbac1ddfe57b1252ebfa528ddbff174a4859f861eeecb54f2df829cf2` and established signer `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.6.json`; complete results are attached as BUILD_EVIDENCE.json.

The owner reports 17/1647 imported with the old foreground-start waiting message; opening MPA then stopping/resuming helped. This message previously covered all IllegalStateException instances and does not establish an exact Android/device cause. Recognized Android start refusals now persist WAITING_FOR_APP, with immediate recovery requested from Activity ON_RESUME. Paused, cancelled, failed and disconnected jobs are not auto-resurrected. Legacy waiting retry work is replaced so old backoff cannot block reopen recovery. Eight-minute foreground-service restarts are removed; real Android limits remain.

Room v9 adds a destination/SHA-256/size index. The migration preserves transfer checkpoints, identities, names and active scan cursors, backfills verified source-linked Photo identities and requests ordinary bounded metadata rescans for idle sources. SHA-256 is obtained from Drive listing/change metadata without original downloads. Active old scans continue; earlier pages without hashes may remain safe misses until a later full scan. Local hash candidates require live remote SHA-256, MD5, size, version and in-folder ancestry verification. Candidate pages contain at most 32 rows. Incoming media SHA-256, MD5 and CRC accumulate in one staging pass; a combined integrity pass remains before upload or duplicate commit. Cancellation is checked every buffer; expensive source/job/free-space checks run at most every 250ms plus start/end checks.

Regression coverage includes checkpoint-preserving waiting recovery, legacy retry replacement, pause races, known service refusal classification, fresh and resumed integrity checks, nested undownloaded media, stale/missing checksums, removal/moves, cross-source isolation, 404 versus permission failures, candidate pagination, v8 upgrades and native persisted recovery/index selection. Existing media/catalogue limits remain. No real Google-account completion, Samsung background-limit reproduction or performance benchmark is claimed. Shared Drive manifests, visual deduplication and quality decisions remain future work.

# Validation status and device checklist

## MPA 0.5.5 dismiss previous import errors â€” 2026-10-11

Source `d5a61380d8638d570f1b4d96ccad711882d65db6` passed **162 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **22 native tests on each of Android 10/API 29 and Android 15/API 35 (44 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38152207165) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38152207167). Local Windows checks passed all 162 tests, lint and both APK builds; local device tests were not run. CI Room v8 matches the committed schema.

The exact passed CI APK has SHA-256 `dcc4ab440a7c81b63c8b14e4e772925c9bb9ce3cf9ece2c003ed7e2cabaead3e` and established signer `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.5.json`; complete results are attached as BUILD_EVIDENCE.json.

 Version 0.5.5 / code 16 preserves Room v8. Stopped imports can dismiss persisted job/item/download error messages and their summary notification without altering status or any upload/source checkpoint. Database regressions cover actual database reopen, exact checkpoint retention, other-job isolation, active-job protection and fresh errors. Native UI checks exercise the Storage button, notification dismissal, activity recreation, preserved source/staging files and fresh errors returning on both API 29 and 35. Real-phone acceptance remains pending.

## MPA 0.5.4 Drive connection recovery â€” 2026-10-11

Source `280783727321a207323f39c60876891fe8f50d7c` passed **160 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **21 native tests on each of Android 10/API 29 and Android 15/API 35 (42 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38149409109) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38149409106). Local Windows checks passed all 160 tests, lint and both APK builds; local device tests were not run. CI Room v8 matches the committed schema.

The exact passed CI APK has SHA-256 `b6529b18b622ef8b4b32632966701c85c5fbdfd52d93aeecaaaeea90ac6ec3b6` and established signer `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.4.json`; complete results are attached as BUILD_EVIDENCE.json.

 Version 0.5.4 / code 15 preserves Room v8 and adds safe Drive DNS/transport errors, three bounded automatic retries with exponential backoff, checkpoint preservation and waiting notifications. Tests cover sanitized DNS errors, timeout classification, local/permission errors staying fail-fast, actual Room recovery transitions, paused/cancelled jobs, and resumable upload reprobe without a new reservation/session. Native checks cover preserved in-flight/verified rows and waiting notifications on Android 10 and 15. The reported hostname-resolution failure explains a Drive connectivity blocker; its phone/network cause is not diagnosed from the screenshot, and live recovery remains pending.

## MPA 0.5.3 background import progress â€” 2026-10-11

Source `498659de11a503809a836280bc27977bfa175737` passed **152 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **20 native tests on each of Android 10/API 29 and Android 15/API 35 (40 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38146814939) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38146815018). Local Windows checks passed all 152 tests, lint and both app/instrumentation APK assembly; local device tests were not run. CI Room v8 matches the committed schema.

The exact passed CI APK has SHA-256 `7c1191caec9d9f28076195277101968ffb49e57f04da3e49f878fd351bcf8a62` and established signer `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.3.json`; complete results are attached as BUILD_EVIDENCE.json.

 Version 0.5.3 / code 14 preserves Room v8 and upload checkpoints, adds foreground import progress, notification permission controls, MPA monochrome status icon, Storage navigation and terminal summaries. Regressions cover verified counts versus uploaded bytes, batch completion/yield, denied notifications, actual Android notification updates/navigation and a real foreground WorkManager completion. The owner reports a real ZIP import running in 0.5.2; its completion and other Google account round trips remain phone acceptance checks.

## MPA 0.5.2 ZIP file access fix â€” 2026-10-11

Source `96ce1b6972f8d7bf404cb3b29df130bb57d9bfea` passed **144 unit/integration tests**, lint with **zero errors**, CPU model checks, app assembly and **18 native tests on each of Android 10/API 29 and Android 15/API 35 (36 total)**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38136233885) Â· [Android 10 and 15 checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38136233947). Local Windows checks passed all 144 tests, lint and both app/instrumentation APK assembly. CI Room v8 schema matches the committed schema. Local device tests were not run.

The release reuses the exact passed CI APK, SHA-256 `d5aaf0fc43ed55bab036cb20e87e7a9bd0d582195126702afca07a5dafdebc44`, with established signer `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.2.json`; complete results are attached as BUILD_EVIDENCE.json. Real Google-account and S23 Ultra acceptance remains outstanding.

The initial local full batch hit a loopback connection timeout in the existing resumable-transfer test; the complete IPv4-loopback retry passed all 144 tests. Native CI uses offline cached-library fixtures with synthetic accounts, preserving every assertion and avoiding real Google authentication/network requests. Initial Android 15 runs passed the ZIP/permission cases but exposed a gallery fixture with timestamp `1`, placing one subject below earlier tests' photos and outside the smaller lazy-grid viewport. The fixture now places its two subjects first, retaining all source-filter assertions. Final native fixture changes were compiled locally; exact final-source CI covers all checks. The isolated provider uses only SDK/Java classes because target-app Kotlin libraries are unavailable in its separate APK process.

 This fix removes URL entry, restores direct file selection, retains the returned read grant and opens ZIPs through their granted descriptors. New tests cover multi-part/cancelled picker results, insufficient grants, private provider documents, durable read access and reconstructed readers, non-seekable providers, provider denial and refused persistence. Required device CI now runs on both Android 10/API 29 and Android 15/API 35. Room remains v8; established upgrades, single-folder defaults and user-selected source preservation remain required.

## MPA 0.5.1 automatic destination and Takeout URL imports â€” 2026-10-11

The feature uses Room v8, preserving v7 import jobs, upload checkpoints and existing media. New tests cover sole-folder destination without unpausing ordinary backup or retargeting existing jobs; HTTPS and redirect validation; durable HTTP range resume, changed revisions/ignored ranges, interrupted downloads, low space and size gates; HTML/expired links; and cleanup only after media commits. Android checks cover the ZIP URL chooser, Keystore link persistence and downloaded ZIP metadata/staging. Source `fb3a8352ed7c218bb25c3dc7336146752922dcaa` passed **139 unit/integration tests**, lint with **zero errors**, CPU model checks, APK assembly and **14 Android 10 (API 29) emulator tests**, with zero failures/errors/skips. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38127362589) Â· [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38127362600). CI's generated Room v8 schema matches the committed schema. Local Windows development checks passed all 139 tests, zero lint errors (36 warnings), app assembly and instrumentation APK assembly. The final two read-only selection guards were also compiled locally; the full CI suite covers the exact final source. Local device tests were not run.

The publisher reuses the exact passed CI APK. SHA-256: `6945a4662d0291fb7848fad3fbc9d695d13b8daf5b356de8769bbf2a3faea6a4`; signer: `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.1.json`; BUILD_EVIDENCE.json is attached to the release. The initial local combined check hit a Gradle worker loopback timeout after both APKs compiled; a single-worker test/lint retry passed. Real direct-link Takeout, Google consent/import and S23 Ultra cleanup acceptance remain outstanding. The Google Takeout browser sign-in page is not a direct ZIP link.

## MPA 0.5.0 Google imports â€” 2026-10-11

Source `d03eee08e6cdcb65e2be38900e84e0b80546f109` passed **125 unit/integration tests**, lint with **zero errors**, CPU model checks, APK assembly and **12 Android 10 (API 29) emulator tests**, with zero failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38116098551) Â· [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38116098649). The generated CI Room v7 schema matches the committed schema. Local Windows checks independently passed the same 125 tests, lint (zero errors, 34 warnings), app APK and instrumentation APK assembly using JDK 17 / SDK 35. Local device tests were not run.

The publisher reuses this exact passed CI APK rather than rebuilding it. Its SHA-256 is `37ed8cb5666ac2009c403728f1d283606d4303c7790209835a838766a19e3a2d`; the established upgrade/OAuth signer is `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Immutable references are in `docs/releases/v0.5.0.json`; complete totals and checksums are attached as BUILD_EVIDENCE.json. These checks do not prove real Google account behavior.

New disk-backed tests cover released v6 migration preserving photos, reviewed faces, upload reservations/offsets and destination/checkpoints; process restart during and immediately after resumable transfer; mismatched remote identity rejection; Picker response/polling contracts; full unknown-field XMP round trips; and conditional JSON cleanup only after complete backup verification. Native tests add a synthetic seekable Takeout ZIP byte round trip and Storage import controls, alongside the ten prior device checks.

Browser control initially failed to initialize, then recovered after restarting Codex on 2026-10-11. In the existing `myphotosanywhere` Cloud project, Google Photos Picker API was enabled and its `https://www.googleapis.com/auth/photospicker.mediaitems.readonly` scope saved; the console confirmed both states. The existing Android client matches `com.myphotosanywhere.mpa` and the released signing certificate. External / Testing status, the existing test user, Drive scopes and Android clients were preserved. Real Takeout/Picker imports, interrupted/repeated imports and Drive JSON conversion on the S23 Ultra remain live acceptance checks. [Setup, supported limits and phone checklist](GOOGLE_IMPORTS.md).

## MPA 0.4.4 resumable Drive discovery

Source `11f36d41012951858e37e00e43b671f9e07577bf` passed **110 unit/integration tests**, Android lint with zero errors,
CPU model checks, APK assembly and **10 Android 10 (API 29) emulator tests**, with zero
failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38089106568) Â· [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38089106532).

The CI-generated Room v6 schema matches the committed schema. The released-schema
migration preserves photos, portable identities, reviewed faces, upload sessions,
destination selection and Drive change checkpoints. The new native Storage test
confirms saved discovery progress and Refresh are visible above the lengthy settings;
all nine existing native checks also pass. The bundled model and similarity gates are
unchanged. The established signer SHA-1 is `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`.

Publication reuses this exact passed CI APK, with its SHA-256, build/device run IDs and
source recorded in the attached BUILD_EVIDENCE.json. Immutable run references are also
recorded in `docs/releases/v0.4.4.json`.
Live Google Drive, shared-drive permissions, concurrent external mutations and S23 Ultra
background behavior remain phone acceptance checks. See [the 0.4.4 retest](releases/v0.4.4.md).
Takeout, Picker and the shared migration engine were subsequent work at this release; see the 0.5.0 evidence above.


## Historical local Drive discovery checks before 0.4.4 â€” 2026-10-11

Based on main `15589ca`, branch `feature/durable-drive-discovery`, this increment passed
**110 unit/integration tests**, Android lint with **zero errors** (29 warnings,
3 informational findings) and debug APK assembly. Unit tests had zero
failures, errors or skipped cases. **Device tests remain unverified locally**: the isolated
Android 15 (API 35) emulator dropped its ADB transport during installation, before app
tests ran. These are local results;
there is no new published release or CI result for this increment. Room v6's generated
schema is included; published 0.4.3 remains Room v5 / version code 9.

New regressions use actual disk-backed Room and mocked Drive HTTP responses to verify
page/cursor/child-folder transaction atomicity, restart with saved continuation tokens,
2,000 nested media items across bounded passes, incomplete results, access errors,
cursor rejection, folder-change rescans, duplicate/cyclic folders, change-feed replay,
non-media exclusion and elapsed-time budgets. Released-schema v5 migration preserves
photos, portable identity, reviewed faces, upload sessions/offsets, destination and
change checkpoints. Changed-byte coverage preserves old snapshots/corrections while
invalidating stale thumbnails. The new native UI test asserts saved discovery state
and Refresh near the top of Storage. It joins the existing nine native checks, including
offline model execution, original-byte preservation and XMP recovery. All ten remain
required in CI; this local attempt does not establish their result for the new source.

Local tooling: portable JDK 17, Gradle 8.11.1 wrapper, existing SDK Platform 35 / Build
Tools 35.0.0 and Python 3.12. Builds ran from an isolated staging copy in the Codex
task workspace; the verified source files were then copied to the source checkout.
The unit suite ran first. Final lint and APK assembly were confirmed separately from
the unavailable device checks, from that staging directory:

```powershell
.\gradlew.bat lintDebug assembleDebug `
  -I '..\local-build.init.gradle' '-Pkotlin.compiler.execution.strategy=in-process' `
  --max-workers=4 --no-daemon --console=plain
```

The local-only init script selects an isolated test key; the compiler setting avoids a
Windows Kotlin daemon connection failure. Neither changes the repository build or CI
configuration. Test/model caches and signing material are ignored and excluded from
the source changes. Reports and APKs are retained under ignored `app/build/` in the
source checkout, including `reports/local-drive-ingestion-evidence.json`.
Use the ordinary wrapper commands in `LOCAL_DEVELOPMENT.md` from the source checkout;
the init-script path above belongs to the task workspace's isolated signing setup.

An interrupted command left an earlier Gradle invocation running; it exited before
the focused final run. The device task failed before executing app tests when the
emulator's automatic ADB connection went offline. Reconnecting its direct local port
temporarily restored the guest, but installation failed again. A direct instrumentation
attempt with a separate task-specific ADB server also failed during installation. The
task's emulator/server were stopped without changing host virtualization settings,
the owner's app, or other devices. Failed controller reports are retained; these
environment failures are not counted as passing app checks.

Local APK SHA-256: `64d00aed1d43d93da7dd569c23e576653f3ccf78a83b137a5a5d8ea81f355c1e`. Its debug certificate SHA-1 is
`DC:F6:3B:F5:BA:7E:68:6D:C7:5A:53:55:78:53:27:42:D6:18:A9:21`, which differs from
the established CI signer. It is for the isolated emulator and cannot upgrade the
owner's published app; no phone app was uninstalled or modified. Before publication,
run the retained CI build/CPU-model and device workflows on the final immutable source
and use the established signer. The existing 0.4.3 publisher must not be used unchanged
for this increment.

Live Google authorization, shared-drive access, concurrent real Drive mutations and
S23 Ultra background/thermal behavior still require the [bulk-ingestion phone
checklist](FEATURES_0_4.md#unreleased-bulk-drive-ingestion-checklist). The separate
Python CPU model smoke and native Android inference remain CI gates for this increment.
Read the [concrete audit](DRIVE_INGESTION_AUDIT.md) before the next shared migration
engine, Takeout and Picker increments. Full-library migration is not implemented here.


## MPA 0.4.3 face grouping and review

Source `21f826723bb45a93d7a5aa80dee0d84b0ac39621` passed **95 unit/integration tests**, Android lint with zero errors, APK assembly and **nine Android 10 (API 29) emulator tests**, with zero failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38073741495) Â· [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38073741460).

New regressions verify two appearances group without enrollment, grouping remains consistent across input order, separate faces in one photo do not group or suggest the person already present, naming one appearance suggests another, incompatible/missing/invalid vectors stay reviewable, weak and ambiguous matches expose their scores, and manual corrections remain respected. A Room regression invalidates successful scan checkpoints across two sources while retaining original photo rows, names, corrected face regions and embeddings, and preserves indexing opt-in. The native UI test scrolls to a separate face, opens its missing-recognition-data diagnostic and opens/cancels full rescan without deleting the face or resuming paused scans.

The established signer is unchanged (`A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`); generated Room v5 schema matches the committed schema. The bundled model, alignment and experimental similarity gates are unchanged. Synthetic regressions and inference smoke checks verify behavior, not real-photo recognition accuracy. The reported three-photo case still needs the [phone diagnostic retest](releases/v0.4.3.md#three-photo-phone-retest). The owner confirmed existing-folder Google authorization works after the 0.4.2 setup correction.

## MPA 0.4.2 Google authorization diagnostics

Source `c878da08d73dc600c07d53cf77816abffa7c8e30` passed **88 unit/integration tests**, Android lint with zero errors, APK assembly and **eight Android 10 (API 29) emulator tests**, with zero failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38069967248) Â· [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38069967298).

Four new regressions cover canceled activity results containing a Google SDK error, preservation of configuration/network status codes, cancellation without data versus missing successful-result data, and intent-sender launch failure. The Android UI test opens Google setup details, verifies the installed version/package and certificate format, and confirms the copy action contains those public identifiers. Existing editing/original-detail, Drive-deletion, source/indexing, XMP and model checks remain passing. The established release signer is unchanged (`A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`); generated Room v5 schema matches the committed schema.

These checks verify error handling and setup diagnostics. They do not authenticate the user's real Google account or modify its Cloud project. The reported post-reinstall authorization failure still requires a live retry with correct package/certificate registration, requested Drive scopes and test-user access. See the [Google setup and computer-agent prompt](GOOGLE_DRIVE_SETUP.md#if-authorization-closes-or-fails-after-reinstalling).

## MPA 0.4.1 phone testing fixes

Source `26dd25e0bdd1d29ffe53de3d749b990990240990` passed **84 unit/integration tests**, Android lint with zero errors, APK assembly and **seven Android 10 (API 29) emulator tests**, with no failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38067111244) Â· [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38067111071).

New checks replay actual mocked Drive trash/removal/move change events through Room and the gallery visibility policy, verify checkpoint advancement and unchanged local bytes, preserve overlapping-source and independent-import behavior, test all eight EXIF transforms and bounded region sampling, and decode a one-pixel line at original resolution in a 4,096-pixel-wide PNG on Android. Device UI checks open the viewer directly, retain Details/Edit access, and verify Scan now is visible without scrolling in both People and Storage and requires opt-in when indexing is disabled. The existing import/edit checksum, XMP recovery, scheduling and model smoke checks also pass.

The established signer SHA-1 is unchanged (`A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`); generated Room v5 schema matches the committed schema. Publication reuses this tested APK. Live Google Drive and Samsung One UI behavior, HEIC/large-camera-photo detail, HDR/color appearance, memory and thermal behavior still need the [S23 Ultra retest](releases/v0.4.1.md). The launcher-icon report remains unresolved; this patch does not claim to fix it.

## MPA 0.4.0 evidence

Source `5211cb7bdbd35c120d4362500f4f920e302576c0` is the 0.4 APK source:

- **77 unit/integration tests, zero failures/errors/skipped**, lint with zero errors and debug APK assembly. [Build checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38064322367). The generated Room v5 schema matches the committed schema. Upgrade tests preserve existing photos, reviewed names, upload sessions and Drive change checkpoints.
- **Five Android 10 (API 29) emulator tests, zero failures/errors/skipped**. [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38064322371). These cover combined source filtering, import/view/edit with unchanged original checksum, offline Android inference, an actual Android XMP encode/decode/recovery round trip, and Storage controls saving a window across midnight and pausing scans.
- New regression coverage checks source-only XMP projections, unknown XML namespace preservation, bounded/validated input, rejected document/entity declarations, conditional Drive writes and duplicate catalogue rejection, markup recipe provenance, checksum-matched recovery, transaction rollback on face-ID collisions, durable outbox generations, annotation/outbox atomicity, pending-scan cache protection, mutual geometry matches instead of detector order, and scheduling across midnight and daylight-saving changes.
- CPU model contract checks and the established debug signer **A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD** are retained. Model smoke tests are compatibility checks, not measured identity accuracy.

The release publisher reuses the passed source build's APK and verifies both workflow runs belong to that source. Its tag may point to a later publication commit, after comparison verifies that only release documentation/publication metadata differ. Attached BUILD_EVIDENCE.json records the tested source, release commit, test totals, certificate, APK byte length and SHA-256. Existing release APKs are never silently replaced.

Live multi-account OAuth/Drive writes, ETag behavior, recovery from another phone, recognition accuracy, thermal/battery behavior and vendor background restrictions require the [0.4 phone checklist](FEATURES_0_4.md). Individual pet recognition, calibrated human quality, adjacent/MWG sidecars, large catalogue partitioning and automatic conflict merging are not claimed by this release.


## MPA 0.4 alpha evidence

Alpha 2 source `34e470e477bb4a3b23dc874a12a28936bddaf55d` adds the original MPA vector launcher icon, API 26+ adaptive resources and API 33+ monochrome resources, plus application version code 5. It passed **55 unit/integration tests, lint with zero errors, APK assembly and three Android 10 emulator tests**. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38061086555) Â· [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38061086556). The signer matches alpha 1 and 0.3. The SVG presentation and matching color/monochrome path geometry were inspected; Android 13 launcher theme appearance still depends on the phone's launcher and theme settings. The alpha 2 release reuses that tested APK, with no changes to database schema or multi-source behavior.

Source `f1520f84828777306724ed96805edc9f9ba945bf` passed:

- **55 unit/integration tests, zero failures/errors/skipped**, Android lint with zero errors, model-contract checks and debug APK assembly. [Build checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38060276539). The generated Room v4 schema matches the committed schema, with existing v3 tables preserved.
- **Three Android 10 (API 29) emulator tests, zero failures/errors/skipped**. [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38060276437). The new test combines two sources and switches source filters independently of Storage's selected source, in addition to edit-copy preservation and offline model execution.
- Released-schema v3â†’v4 migration preserves media, confirmed identities, the default destination, upload sessions/offsets and change tokens. Additional regressions cover one destination, existing task bindings, source removal, global cross-account suggestions, rejected matches, overlapping folders, separate accounts and changed remote bytes.
- The APK signer SHA-1 matches v0.3: `A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`. Use package `com.myphotosanywhere.mpa` for Google OAuth registration.

The prerelease reuses this successful source build's APK. Live multi-account OAuth, sync, downloads and cache behavior need the [0.4 phone checklist](releases/v0.4.0-alpha.1.md). Synthetic matching tests establish shared identity behavior, not recognition accuracy. Individual pet recognition, XMP synchronization and overnight indexing remain planned in [the implementation stages](IMPLEMENTATION_0_4.md).

## MPA 0.3 evidence

Source `a5977ddb74766ff12bed87939cdcf588174b429b` passed:

- **49 unit/integration tests, zero failures/errors/skipped**, Android lint with zero errors, and debug APK assembly. [Build checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38037767687). The generated Room v3 schema matches the committed schema.
- **Two Android 10 (API 29) emulator tests, zero failures/errors/skipped**. [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38037767678). One imports a fixture, opens/closes the full-screen viewer, opens editing, saves a linked copy, and verifies the original SHA-256 is unchanged. The other executes the bundled embedding model and ML Kit face detector on Android without a cloud inference service. A real PNG screenshot is retained in the device artifact.
- Checksum-pinned model acquisition, CPU TensorFlow Lite inference, tensor shapes, finite/nonzero output, and duplicate-input consistency. These are compatibility checks, not measured recognition accuracy.

The user confirmed Google setup works on their phone with 0.2. New 0.3 live-account transfers, reconciliation, maps, face accuracy, video codecs, and storage-pressure behavior have not yet been phone-tested. Emulator fixtures are synthetic; they do not establish face detection/recognition accuracy on actual photographs. See the [0.3 phone checklist](FEATURES_0_3.md#test-on-the-phone) and [Google live checks](GOOGLE_DRIVE_SETUP.md#required-live-checks).

The release uses an existing successful APK artifact, rather than rebuilding during publication. `docs/releases/v0.3.0.json` identifies the exact source and successful build/device runs selected for publication. Its attached `BUILD_EVIDENCE.json` records test totals, APK checksum, and size; the signing report identifies the Google OAuth certificate required by that APK.

## Meaningful automated coverage

- Drive gateway: pagination/query escaping, resumable uploads and reserved IDs, acknowledged offsets, host checks, rate-limit classification, size limits, recursive image/video/folder discovery, and change-page metadata/checkpoints.
- Drive identity/persistence: account/folder separation, the v1â†’v2â†’v3 database upgrade preserving original photos/handoffs/upload checkpoints across restart, unchanged old-destination tasks, and atomic rollback of imported photos/links/files.
- Cloud/library persistence: changed remote bytes preserve old local snapshots and people corrections; metadata-only changes retain verified files; failed checkpoint transactions roll back; name merges/deletions preserve individual corrections and photos.
- Cache: real filesystem deletion only for verified, unprotected downloads; imports, pins, drafts, and active readers survive; failed remote verification preserves a disposable download. Policy tests also cover pending handoffs, LRU order, and invalid input.
- Editing: recipe round trips, validation of finite adjustments/crop/markup, undo/redo branching, crop/rotation geometry, untouched original bytes, and conservative removal of straightening's transparent corners.
- Search/places: combined people/place/object terms, capture-date queries, type/favorite filters, unknown GPS, and coordinate validation including valid zero coordinates.
- Face matching: normalized embedding serialization, ambiguity between identities, multiple reference faces, distant unknowns, conservative grouping, and invalid vectors.
- AI prompts: bounded nonempty instructions and provider capabilities. End-to-end provider editing remains a manual handoff test.

## Running checks

Use JDK 17, SDK 35, and Python 3. Gradle fetches the licensed model with verified hashes. On Windows use `gradlew.bat`.

```sh
./gradlew testDebugUnitTest lintDebug assembleDebug
./gradlew connectedDebugAndroidTest
```

GitHub Actions performs build and device jobs separately. The cloud workspace has no local Android SDK/Gradle installation. CI runs a CPU model smoke check and an accelerated Android emulator. A production rollout also needs broader Android-version/device/codec coverage, real Google authorization/reconciliation tests, consented face-model calibration, and owner-managed signing.

## Historical evidence

MPA 0.2 passed **21 tests, zero failures/errors/skipped**, lint with no errors, and APK assembly for source `cc8e56aee0a99b0b7311b85917c45e6e34a1d6f4`. [Build/artifacts](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/37954645025). Its checks include Room v2 and its old signing report. The user subsequently confirmed Google setup works. The original gallery scaffold also [built successfully](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/37950038970).

## AI round-trip acceptance checklist

1. Import JPEG, PNG, supported HEIC, EXIF-rotated portraits, transparent images, and GPS-tagged media. Corrupt/oversized files must fail clearly without losing existing photos.
2. Restart in airplane mode. Cached previews/details and imported originals remain available. Compare the original checksum with its source.
3. Hand off a photo: confirm the disclosure, clipboard prompt, orientation, bounded 2048-pixel JPEG, and absent GPS EXIF. A provider ignoring text should still receive the image.
4. Test each installed official provider. Its subscription and app capabilities determine available edits; do not assume Claude generates an edited image.
5. Save an output, return to MPA, and import the AI result. Verify its parent/provider/prompt link and preserved original.
6. Recreate MPA while its result picker is open. The saved handoff ID must resolve from Room. Canceling leaves the pending handoff intact.
7. Dismiss a handoff: only that activity entry disappears. A low-storage failure must leave existing library files intact.
8. FileProvider exposes explicit handoff exports only. No provider receives the library or background face metadata.
