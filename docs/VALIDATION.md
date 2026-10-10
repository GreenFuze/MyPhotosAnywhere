# Validation status and device checklist

## MPA 0.4.4 resumable Drive discovery

Source `11f36d41012951858e37e00e43b671f9e07577bf` passed **110 unit/integration tests**, Android lint with zero errors,
CPU model checks, APK assembly and **10 Android 10 (API 29) emulator tests**, with zero
failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38089106568) · [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38089106532).

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
Takeout, Picker and the shared migration engine are subsequent work.


## Unreleased local Drive discovery increment — 2026-10-11

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

Source `21f826723bb45a93d7a5aa80dee0d84b0ac39621` passed **95 unit/integration tests**, Android lint with zero errors, APK assembly and **nine Android 10 (API 29) emulator tests**, with zero failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38073741495) · [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38073741460).

New regressions verify two appearances group without enrollment, grouping remains consistent across input order, separate faces in one photo do not group or suggest the person already present, naming one appearance suggests another, incompatible/missing/invalid vectors stay reviewable, weak and ambiguous matches expose their scores, and manual corrections remain respected. A Room regression invalidates successful scan checkpoints across two sources while retaining original photo rows, names, corrected face regions and embeddings, and preserves indexing opt-in. The native UI test scrolls to a separate face, opens its missing-recognition-data diagnostic and opens/cancels full rescan without deleting the face or resuming paused scans.

The established signer is unchanged (`A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`); generated Room v5 schema matches the committed schema. The bundled model, alignment and experimental similarity gates are unchanged. Synthetic regressions and inference smoke checks verify behavior, not real-photo recognition accuracy. The reported three-photo case still needs the [phone diagnostic retest](releases/v0.4.3.md#three-photo-phone-retest). The owner confirmed existing-folder Google authorization works after the 0.4.2 setup correction.

## MPA 0.4.2 Google authorization diagnostics

Source `c878da08d73dc600c07d53cf77816abffa7c8e30` passed **88 unit/integration tests**, Android lint with zero errors, APK assembly and **eight Android 10 (API 29) emulator tests**, with zero failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38069967248) · [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38069967298).

Four new regressions cover canceled activity results containing a Google SDK error, preservation of configuration/network status codes, cancellation without data versus missing successful-result data, and intent-sender launch failure. The Android UI test opens Google setup details, verifies the installed version/package and certificate format, and confirms the copy action contains those public identifiers. Existing editing/original-detail, Drive-deletion, source/indexing, XMP and model checks remain passing. The established release signer is unchanged (`A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`); generated Room v5 schema matches the committed schema.

These checks verify error handling and setup diagnostics. They do not authenticate the user's real Google account or modify its Cloud project. The reported post-reinstall authorization failure still requires a live retry with correct package/certificate registration, requested Drive scopes and test-user access. See the [Google setup and computer-agent prompt](GOOGLE_DRIVE_SETUP.md#if-authorization-closes-or-fails-after-reinstalling).

## MPA 0.4.1 phone testing fixes

Source `26dd25e0bdd1d29ffe53de3d749b990990240990` passed **84 unit/integration tests**, Android lint with zero errors, APK assembly and **seven Android 10 (API 29) emulator tests**, with no failures, errors or skipped cases. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38067111244) · [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38067111071).

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

Alpha 2 source `34e470e477bb4a3b23dc874a12a28936bddaf55d` adds the original MPA vector launcher icon, API 26+ adaptive resources and API 33+ monochrome resources, plus application version code 5. It passed **55 unit/integration tests, lint with zero errors, APK assembly and three Android 10 emulator tests**. [Build](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38061086555) · [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38061086556). The signer matches alpha 1 and 0.3. The SVG presentation and matching color/monochrome path geometry were inspected; Android 13 launcher theme appearance still depends on the phone's launcher and theme settings. The alpha 2 release reuses that tested APK, with no changes to database schema or multi-source behavior.

Source `f1520f84828777306724ed96805edc9f9ba945bf` passed:

- **55 unit/integration tests, zero failures/errors/skipped**, Android lint with zero errors, model-contract checks and debug APK assembly. [Build checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38060276539). The generated Room v4 schema matches the committed schema, with existing v3 tables preserved.
- **Three Android 10 (API 29) emulator tests, zero failures/errors/skipped**. [Device checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38060276437). The new test combines two sources and switches source filters independently of Storage's selected source, in addition to edit-copy preservation and offline model execution.
- Released-schema v3→v4 migration preserves media, confirmed identities, the default destination, upload sessions/offsets and change tokens. Additional regressions cover one destination, existing task bindings, source removal, global cross-account suggestions, rejected matches, overlapping folders, separate accounts and changed remote bytes.
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
- Drive identity/persistence: account/folder separation, the v1→v2→v3 database upgrade preserving original photos/handoffs/upload checkpoints across restart, unchanged old-destination tasks, and atomic rollback of imported photos/links/files.
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
