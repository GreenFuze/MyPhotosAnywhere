# Validation status and device checklist

## MPA 0.4 alpha evidence

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
