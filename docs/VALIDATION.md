# Validation status and device checklist

## Current evidence

- MPA 0.2: **21 tests passed, 0 failures/errors/skipped**, Android lint reported no errors, and APK assembly passed for source commit `cc8e56aee0a99b0b7311b85917c45e6e34a1d6f4`. [Successful build and APK/checks artifacts](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/37954645025). `MPA-checks` includes the JUnit XML, lint reports, Room v2 schema, and signing-certificate report. This is automated evidence; no live Google account or phone has been exercised.

- Source-level review completed for the first gallery/handoff implementation.
- Android manifest and resource XML parse successfully.
- GitHub Actions successfully ran `testDebugUnitTest`, `lintDebug`, and `assembleDebug` for source commit `7888e298dea92457968bda42d7b0f66c9d7297a8`. [Build and APK artifact](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/37950038970). The uploaded artifact is named **MPA-debug**.
- The cloud workspace itself has no installed Android SDK/Gradle distribution; compilation was performed by GitHub Actions with the committed wrapper.
- Real-device checks have **not yet run**. Automated build checks passed, but a phone/emulator test is still required before declaring the gallery and provider round trip fully verified.

The project uses pinned, established Android dependency versions for its initial scaffold. Before Play Store release, revisit the then-current target SDK requirement and upgrade/test the toolchain as needed.

## Meaningful automated checks

Run `./gradlew testDebugUnitTest lintDebug assembleDebug` with SDK 35 (on Windows, use `gradlew.bat`).

- `CachePolicyTest`: unbacked-up, pending, pinned, and actively-read files cannot be eviction candidates; least-recently-used downloads are selected until budget is met; unsafe input is rejected.
- `EditPromptTest`: instructions are preserved, providers without generation get a fallback, and empty/oversized prompts are rejected.
- Lint and APK assembly must check Android API compatibility, Room/KSP generation, Compose compilation, and resource/manifest consistency.

The 0.2 checks passed: `DriveGatewayTest` covers pagination/query escaping, reserved-ID uploads, acknowledged offsets, session-host validation, rate-limit handling, and download limits; `DriveBindingTest` covers account/folder separation and range checks; and `DrivePersistenceTest` covers the v1-to-v2 upgrade, original/handoff retention, upload checkpoints, destination changes, and atomic rollback of imported photos/links/files. Live Drive/OAuth checks are in [GOOGLE_DRIVE_SETUP.md](GOOGLE_DRIVE_SETUP.md).

The cache policy's unit tests passed. It is a standalone component awaiting integration with verified Drive downloads. There is no original-file eviction in milestone 1.

## Real-device acceptance checklist

1. Import JPEG, PNG, and supported HEIC images, including EXIF-rotated portraits, transparent images, and a GPS-tagged image. Corrupt and oversized files must produce a useful error without losing existing imports.
2. Close/restart with airplane mode on. The grid, filename search, and details remain available. Hash the imported original and confirm that its bytes match the source.
3. Hand off a photo. Confirm the notice, clipboard prompt, correct orientation, maximum export dimension of 2048 pixels, and absent GPS EXIF in the temporary JPEG. A receiving app that ignores text should still receive the image.
4. Complete one round trip with each installed provider that supports it. Provider-specific limitations must remain visible; do not claim Claude generates edited images without verifying that capability.
5. Save an output, reopen MPA, and import it from AI activity. The original remains intact and the edited photo retains the source link, provider, and instructions.
6. Kill/recreate MPA while a result picker is open. Its saved handoff ID must resolve through Room, rather than depending on a UI list that may not have loaded yet. Cancel the picker and verify the handoff remains pending.
7. Dismiss a pending handoff. Only that activity entry is removed; its photo remains. If storage is nearly full, import should fail clearly and leave the existing library intact.
8. Review private storage and FileProvider configuration. Only explicit handoff exports are shareable; original photo directories must not be exposed.
