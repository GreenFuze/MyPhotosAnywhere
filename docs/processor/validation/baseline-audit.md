# Baseline reuse audit — 2026-10-19

Audited revision: `6c9ead7b1d4446dc819dee71974aa7b68614f232`. Last released application: `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Initial isolated checkout was clean. No repository AGENTS.md file was found; the owner's fast-fail/OOD instructions apply.

## Release and persistence

- Package: `com.myphotosanywhere.mpa`; version 0.5.33/code 44; min 26, compile/target 37.
- Room: schema 12, exported `app/schemas/com.myphotosanywhere.mpa.data.PhotoDatabase/12.json`; SHA-256 `c477c29974484c04a2940ad6ed99ac6f7fd37318f24480d519c814da574c6995`.
- Published APK SHA-256: `e56e543fba9eeccb46efb7128fdc9496ed11c291896dbefcb5de93f2a9e3e5ad`.
- Established upgrade signer SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4` (from prior verified release evidence; reverify final 0.6 APK).
- Existing release evidence reports 505 unit and 477 native tests, lint zero. Those are historical results, not new 0.6 test results. S23 acceptance was unverified.

## Ownership and reuse boundaries

| Existing code area | Preserve / adapt |
| --- | --- |
| `timeline/TimelineRepository.kt`, `TimelineSql.kt`, `TimelinePagingSource.kt` | Bounded keyset queries, stable identities and visibility semantics; no new parallel gallery stack. |
| `TimelineGridViewport.kt`, `TimelineAnchorRestorer.kt`, `MosaicContinuity.kt`, `SqliteViewerSession.kt` | Pixel anchors, frozen mosaic geometry and opened viewer sequence. |
| `data/MediaCache.kt`, `library/OriginalPhotoView.kt` | Original reader leases, remote verification, bounds/cancellation/lifecycle; do not release input while rendering uses it. |
| `data/OfflineStorage.kt` | Explicit downloaded-copy cleanup with protection/verification; not generic original deletion. |
| `device/DeviceMediaLibrary.kt`, `drive/DriveBackupWorker.kt` | Device discovery/permission and explicit backup/checkpoint/metadata behavior. Camera visibility is not automatic upload. |
| `drive/DriveAuthorization.kt`, `DriveGateway.kt` | Per-account access/refresh, typed operational recovery and scoped original operations. Desktop obtains its own authorization. |
| `importing/ImportExecution.kt`, `ImportItemRecovery.kt` | Per-item/per-stage recovery and captured destinations; transfer semantics move without whole Android class ports. |
| `people/CanonicalPeopleRepository.kt`, `CanonicalPeopleIndex.kt`, metadata owners | Imported and manually named canonical identities and corrections, not a second People silo. |
| `people/PeopleRepository.kt`, PeopleWorker, `FaceEmbedding.kt` | Android batch ML Kit/LiteRT generation is replaced only after desktop replacement passes. Remove dependencies/assets/scheduling at Stage 7. |
| `trash/DriveTrashRepository.kt`, `DriveTrashIndex.kt` | Explicit actual Drive Trash/restore separate from offline cleanup. |

## Current build/test inventory

Android build pins AGP 9.4.1, Kotlin 2.4.21, KSP 2.3.12, Gradle 9.8.1, Compose BOM 2026.09.00, Room 2.8.5 and Paging 3.5.3. JDK 17 is the established baseline. Lint/Kotlin/Java warnings are errors. Full Gradle diagnostics have an explicit gate script; do not disable it.

CI has Android host/unit/lint/build, native API29/35/37, model verification and release workflows. Existing jobs protect upgrade signing secrets from untrusted PRs. Processor jobs must not acquire those secrets. Do not reuse old release warning exceptions for 0.6.

The global local emulator is older than final CI's pinned 37.2.12. A fresh API35 AVD is development-only; install/launch of the hash-verified baseline is recorded locally. It is not physical S23 validation or a final native-matrix pass.
