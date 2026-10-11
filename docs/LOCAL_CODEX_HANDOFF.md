## In progress: 0.5.9 terminal import dismissal and viewer safety

The owner wants cancelled/completed tasks dismissible and reports an image-viewing crash during import. Add reversible local history dismissal for COMPLETE/CANCELLED/FAILED with terminal-state recheck, durable rows/checkpoints preserved and late summary suppression. Keep active/paused work visible. No phone crash log is available, so do not claim a confirmed cause. Repair identified bitmap lifetime, decode-memory and cache-read risks; freeze viewer paging during import insertions. Version 0.5.9 / code 20 / Room v9. Local checks and immutable-source CI are pending; no 0.5.9 release is claimed yet.

## Follow-up: 0.5.8 in-app import progress

Add live progress in Storage using the shared Room-backed notification summary: verified-file count/bar, current filename/upload percentage and retry/pause status. Active imports and controls precede setup/history, and active jobs cannot be hidden by the ten-entry history limit. Version 0.5.8 / code 19 / Room v9 preserves checkpoints, scheduling and signing. Local checks and required immutable-source CI passed: 190 unit/integration tests, zero lint errors and 27 native tests each on APIs 29/35. The dedicated 0.5.8 publisher reuses the exact passed APK with the established signer. Real phone acceptance remains pending.

## Follow-up: 0.5.7 background transfers and Drive ZIP source

The owner requests continued background importing, stable notification progress and Takeout exports delivered to Drive. Version 0.5.7 / code 18 / Room v9 uses Android 14+ user-initiated transfer jobs, retains WorkManager FGS on older APIs, keeps bounded connection retries inside active work and provides stable IDs/Pause. Schedule UIDT only from visible Import/Resume/reopen, use its assigned network, and preserve the shared checkpoint engine. Android 14 ordinary ongoing notifications remain swipe-dismissible; never fake media/call styles or claim immunity to user/system stops.

A scoped authenticated Drive ZIP browser/checked range adapter avoids saving the complete archive on the phone. It retains the agreed individual-file destination layout, so entries still upload (or reuse exact verified duplicates). Source ZIPs remain untouched. Permanent ZIP-backed originals, no-reupload archive storage and perceptual duplicate review are future work. Local builds and required immutable-source CI passed: 189 unit/integration tests, zero lint errors and 26 native tests each on API 29 and 35. The dedicated 0.5.7 publisher reuses the exact passed APK with the established signer. Real Samsung background and Google-account Drive ZIP acceptance remain pending.

## Follow-up: 0.5.6 background recovery and exact-byte index

The owner's screenshot shows a running 17/1647 Takeout job with the old foreground-start waiting message. Opening MPA and stopping/resuming helped. That message was emitted for every IllegalStateException, so the screenshot does not identify a precise Android quota or device cause. The candidate removes eight-minute foreground-service handoffs, handles recognized Android foreground-start refusals as durable WAITING_FOR_APP work, and resumes blocked jobs on Activity ON_RESUME without changing transfer checkpoints or resurrecting paused/cancelled work. Legacy waiting retries are replaced on reopen. Unexpected service/programming errors still fail fast. Android may still defer or stop long-running work.

The owner approved staged smarter importing. First implement Room v9 scoped SHA-256/size indexing from Drive metadata and live remote checksum/ancestry verification, plus combined hashes during staging. Preserve all existing identities and checkpoints. Shared Drive hash manifests, perceptual candidates and quality decisions remain future increments; do not claim visual deduplication or delete lower-quality originals. Local and required immutable-source CI passed: 175 unit/integration tests, zero lint errors and 24 native tests each on API 29 and 35. The dedicated 0.5.6 publisher reuses the exact passed APK with the established signer. Real Samsung background/import acceptance remains pending.

# Handoff to local Codex on Windows

Paste the instructions below into a Codex session running on your computer. Select `C:\src\github.com\GreenFuze` as the local workspace when possible. The first task is cloning, then continuing implementation; cloud workspace files and account connections do not automatically move to Windows.

## Instructions for the local agent

First clone my private repository `https://github.com/GreenFuze/MyPhotosAnywhere` into **`C:\src\github.com\GreenFuze\MyPhotosAnywhere`**. Use PowerShell and my existing Git credential manager or GitHub CLI authentication. Do not put credentials in commands. Run this bootstrap, checking command exit codes:

```powershell
$ErrorActionPreference = 'Stop'
$mpaParent = 'C:\src\github.com\GreenFuze'
$mpaRepo = Join-Path $mpaParent 'MyPhotosAnywhere'
New-Item -ItemType Directory -Force -Path $mpaParent | Out-Null
if (-not (Test-Path -LiteralPath $mpaRepo)) {
    git clone https://github.com/GreenFuze/MyPhotosAnywhere.git $mpaRepo
    if ($LASTEXITCODE -ne 0) { throw 'Clone failed. Check local GitHub access before continuing.' }
} elseif (-not (Test-Path -LiteralPath (Join-Path $mpaRepo '.git'))) {
    if ((Get-ChildItem -LiteralPath $mpaRepo -Force | Measure-Object).Count -eq 0) {
        git clone https://github.com/GreenFuze/MyPhotosAnywhere.git $mpaRepo
        if ($LASTEXITCODE -ne 0) { throw 'Clone into the empty destination failed.' }
    } else {
        throw 'The destination contains non-repository files. Preserve it and inspect before continuing.'
    }
}
Set-Location -LiteralPath $mpaRepo
git status --short --branch
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect this checkout.' }
git remote -v
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect repository remotes.' }
```

If the directory already contains a checkout, confirm its origin is GreenFuze/MyPhotosAnywhere, inspect uncommitted work/current branch and preserve all existing changes. Fetch normally; fast-forward a clean main checkout only when appropriate. Do not reset, overwrite, force-push or delete an existing directory to make cloning work. A fresh clone has ordinary GitHub ancestry; use normal Git locally. The cloud session used a separate local history plus GitHub Git Data commits, which is not a workflow to reproduce on this fresh clone.

If GitHub authentication is unavailable, explain that this private repository needs my local sign-in and help me use Git Credential Manager/GitHub Desktop/`gh auth login`. Never ask me to paste a token into chat. If Codex lacks write access to the requested path, use its supported workspace/permission flow.

After cloning, read any applicable AGENTS.md, then:

1. `docs/IMPORT_AND_DRIVE_SYNC_PLAN.md` — agreed next implementation plan and acceptance criteria.
2. `README.md`, `docs/FEATURES_0_4.md`, `docs/VALIDATION.md` — actual current features and evidence.
3. `docs/GOOGLE_DRIVE_SETUP.md`, `docs/LOCAL_DEVELOPMENT.md` — toolchain, OAuth and signing.
4. Recognition design only when relevant; its older sections are historical and are not the current implementation status.

Continue implementing the agreed plan on a suitable feature branch, starting with an audit of the existing Drive bulk-ingestion path. Do useful implementation and verification, not another open-ended planning session. Ask only for missing information that materially blocks the next concrete step. Routine repository work, local builds and fixes are authorized. Keep milestones reviewable, preserve existing work and explain real limitations. The user wants development/builds to move locally to reduce cloud usage: run focused local checks while developing, batch read-only inspection, avoid repeated full scans/builds without a reason, and retain required CI before publishing. Do not spawn other agents by default.

## Product requirements to preserve

- MyPhotosAnywhere / MPA is native Kotlin/Compose Android, package `com.myphotosanywhere.mpa`, minimum API 26. Keep the owner's LGPL 2.1 license and original MPA adaptive/monochrome icon.
- **Drive is durable storage.** Ordinary media added externally to any connected folder/subfolder must be discovered and reflected in the app. Browse from the local index/cache without downloading all originals. Handle rename, move, replacement and deletion with recoverable annotations/snapshots. Android background synchronization is eventual, not an instant-delivery guarantee.
- Multiple Drive accounts/folders share one gallery and one global recognition/identity library. One chosen default destination receives new imports/edits. Never retarget in-flight jobs or spread every import across accounts.
- Add both Takeout ZIP import with supplemental metadata and official Google Photos Picker import. Share resumable jobs, deduplication, verified upload and bounded staging. A verified migration upload becomes ordinary source-backed cached media; do not leave every imported original permanently protected on the phone.
- Portable annotations use XMP. Preserve original bytes, source-scoped catalogue privacy, metadata-backup opt-in, revision conflicts and reviewed recovery. Embeddings/crops remain local. Import people names only as hints until actual face assignments are reviewed.
- Phone inference is preferred, on demand or scheduled while charging at night; respect pause, opt-in, heat, battery and network preferences. Individual pet recognition is still future work.
- AI uses official ChatGPT/Gemini/Claude subscription app handoffs, without AI API keys, provider cookies/passwords or a mandatory server. Edits are linked copies.

## Follow-up: 0.5.5 dismiss previous import errors

The owner requests dismissal of persistent previous-import errors. Version 0.5.5 / code 16 / Room v8 adds **Dismiss errors** for stopped imports. A transaction clears only job/item/download error text; it preserves history, failed status, resume controls, source grants/files, destination and all upload checkpoints. The summary notification is cleared. Active work is rechecked under the transaction and cannot be dismissed until paused. New failures remain visible. Local checks and required CI passed: 162 unit/integration tests, zero lint errors and 22 native tests on each of Android API 29 and 35. The dedicated 0.5.5 publisher reuses the exact passed APK; preserve older releases/signers and publish only the immutable CI-passed APK. Google API versus local ZIP reading was explained to the owner; this increment does not change the Drive-backed import storage contract.

## Follow-up: 0.5.4 Drive connection recovery

The owner reports 0.5.2 stuck at 14/1647, an MP4 marked staging, then an inability to resolve a Google API host. Takeout reading is local, but the migration uploads into the captured Drive destination and must call Drive. Prioritize the confirmed connection failure rather than claiming an MP4 format defect. Version 0.5.4 / code 15 / Room v8 adds sanitized transport messages, three automatic retries with exponential WorkManager backoff beginning at 30 seconds, waiting notifications and unchanged upload checkpoints. An atomic state update cannot resurrect paused/cancelled jobs. HTTP authorization/permission, local storage and invalid-data failures remain fail-fast. Staging byte progress/deadlines are separate follow-up work, not claimed as delivered here. Local checks and required CI passed: 160 unit/integration tests, zero lint errors and 21 native tests on each of Android API 29 and 35. The dedicated 0.5.4 publisher reuses the exact passed APK; preserve existing releases/signers and require immutable-source CI before publication.

## Follow-up: 0.5.3 background import notification

The owner confirms a ZIP import is running in 0.5.2 and requests a background status-bar icon and progress in notifications. Version 0.5.3 / code 14 keeps Room v8 and the shared durable migration engine. Takeout and Picker workers use a data-sync foreground notification with the MPA monochrome icon, verified counts and current transfer percentage. Tap opens Storage; Android 13 notification consent is requested at import start/resume, with controls for already running imports. Batch workers yield between files after eight minutes, preserving destination/network constraints and upload checkpoints. Local checks and required CI passed: 152 unit/integration tests, zero lint errors and 20 native tests on each of Android API 29 and 35. The dedicated 0.5.3 publisher reuses the exact passed APK. Preserve prior releases and the established signer.

## Follow-up fix: 0.5.2

The owner reports URL import failing and a downloaded ZIP returning access denied. Restore the direct Android Files picker, remove URL/fresh-link forms and read ZIPs through the granted descriptor rather than reopening `/proc/self/fd`. Retain actual read/persistable grants or fail with local-file recovery advice before creating durable work. Version 0.5.2 / code 13 keeps Room v8 and all historical jobs/checkpoints/cleanup; user-selected archives remain untouched. Preserve the automatic sole-folder destination. Local checks and required CI passed: 144 unit/integration tests, zero lint errors and 18 native tests on each of Android API 29 and 35. The dedicated 0.5.2 publisher reuses the exact passed APK with the established signer. See current validation for completed evidence; real S23 Ultra acceptance remains outstanding.

## Follow-up increment: 0.5.1

The owner's next requests are automatic default selection when only one Drive folder is connected, and Takeout ZIP download by URL with processing and phone cleanup. Implemented in version 0.5.1 / code 12 / Room v8; local checks and required CI passed. Publication reuses the tested immutable APK. See `docs/GOOGLE_IMPORTS.md` and the top of `docs/VALIDATION.md`. A sole source is an import default without unpausing ordinary backups. URL downloads require direct HTTPS ZIP bytes; Google's browser-authenticated Takeout page is not a direct ZIP URL. Never promise cookie/sign-in automation. Remove only MPA-owned downloaded archives after verified media commits; preserve local/USB source files and incomplete/unsupported items for review. Existing released APKs and signers must remain unchanged.

## Baseline and important context

Latest release: [v0.5.8](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.8), version 0.5.8 / code 19 / Room v9. Tested source `a75c45a52866d01b39768bc388ac7241c4ccdfb1` passed 190 unit/integration and 54 native checks (27 each API 29/35), lint/model/build checks. Storage now shows active verified progress and current uploads above setup/history, using notification checkpoints. Read `docs/releases/v0.5.8.md`. Preserve previous releases/signers and saved jobs; real phone acceptance remains pending.

Previous release: [v0.5.7](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.7), version 0.5.7 / code 18 / Room v9. Tested source `a375f5af5c880cc3f0d57d40ee1e9c4182e830a4` passed 189 unit/integration tests, lint/model/build checks and 52 native checks (26 each on Android 10 and 15). Read `docs/releases/v0.5.7.md`, `docs/GOOGLE_IMPORTS.md` and `docs/VALIDATION.md`. Android 14+ uses UIDT; connection backoff keeps active progress, notifications have Pause, and scoped Drive ZIP ranges avoid full-archive phone storage. Individual media still uploads. Preserve prior releases/signers and all checkpoints. Permanent ZIP-backed originals/visual de-dup and real phone acceptance remain pending.

Previous release: [v0.5.6](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.6), version 0.5.6 / code 17 / Room v9. Tested source `6a1d23f17e8fb6eb404bc9fca314200e3e54ec0f` passed 175 unit/integration tests, lint/model/build checks and 48 native checks (24 each on Android 10 and 15). Read `docs/releases/v0.5.6.md`, `docs/GOOGLE_IMPORTS.md` and `docs/VALIDATION.md`. Foreground waiting recovers on reopen, eight-minute restarts are removed, and exact-duplicate lookup uses a verified scoped hash index and combined staging fingerprints. Visual duplicate review/quality policy and shared manifests remain future work. Preserve prior releases/signers and all transfer checkpoints. Real phone acceptance remains pending.

Previous release: [v0.5.5](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.5), version 0.5.5 / code 16 / Room v8. Tested source `d5a61380d8638d570f1b4d96ccad711882d65db6` passed 162 unit/integration tests, lint/model/build checks and 44 native checks (22 each on Android 10 and 15). Read `docs/releases/v0.5.5.md`, `docs/GOOGLE_IMPORTS.md` and `docs/VALIDATION.md`. Stopped imports can dismiss old saved errors and summary notifications while preserving status, resume controls and all checkpoints. Fresh errors remain visible. Phone acceptance and real Google import completion remain pending. Preserve prior releases/signers.

Previous release: [v0.5.4](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.4), version 0.5.4 / code 15 / Room v8. Tested source `280783727321a207323f39c60876891fe8f50d7c` passed 160 unit/integration tests, lint/model/build checks and 42 native checks (21 each on Android 10 and 15). Read `docs/releases/v0.5.4.md`, `docs/GOOGLE_IMPORTS.md` and `docs/VALIDATION.md`. Drive transport failures get clear errors and bounded automatic retries without replacing import checkpoints. The owner still tests 0.5.2; actual DNS recovery, completed import and other account flows remain phone acceptance checks. Preserve prior releases/signers.

Previous release: [v0.5.3](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.3), version 0.5.3 / code 14 / Room v8. Tested source `498659de11a503809a836280bc27977bfa175737` passed 152 unit/integration tests, lint/model/build checks and 40 native checks (20 each on Android 10 and 15). Read `docs/releases/v0.5.3.md`, `docs/GOOGLE_IMPORTS.md` and `docs/VALIDATION.md`. Background imports have foreground notification progress and consent controls; checkpoints remain compatible. The owner reports a real ZIP import running in 0.5.2, with completion and other account flows still pending. Preserve older releases and signers.

Previous release: [v0.5.2](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.2), version 0.5.2 / code 13 / Room v8. Tested source `96ce1b6972f8d7bf404cb3b29df130bb57d9bfea` passed 144 unit/integration tests, lint/model/build checks and 36 native checks (18 each on Android 10 and 15). Read `docs/releases/v0.5.2.md`, `docs/GOOGLE_IMPORTS.md` and `docs/VALIDATION.md`. ZIP URL entry is removed; the sole-folder default and old download records/cleanup remain. Preserve all older publishers, release APKs and signers.

Previous release: [v0.5.1](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.1), version name 0.5.1 / code 12, Room v8. Tested source `fb3a8352ed7c218bb25c3dc7336146752922dcaa` passed 139 unit/integration tests, lint/model/build checks and 14 Android emulator tests. Read `docs/releases/v0.5.1.md`, `docs/GOOGLE_IMPORTS.md` and `docs/VALIDATION.md`. Direct HTTPS ZIP URLs are supported; real S23 Ultra acceptance remains outstanding. Use the dedicated `.github/workflows/release051.yml`; preserve all older publishers and release APKs.

Previous release: [v0.5.0](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.5.0), version name 0.5.0 / code 11, Room v7. Tested source `d03eee08e6cdcb65e2be38900e84e0b80546f109` passed 125 unit/integration tests, lint/model/build checks and 12 Android emulator tests. Read `docs/GOOGLE_IMPORTS.md`, `docs/releases/v0.5.0.md` and `docs/VALIDATION.md`; Picker Cloud setup was completed and verified on 2026-10-11; real-account phone acceptance remains outstanding.

Previous release: [v0.4.4](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.4.4), version name 0.4.4 / code 10, Room v6. Tested source: `11f36d41012951858e37e00e43b671f9e07577bf`. It passed 110 unit/integration
tests, lint/model/build checks and ten Android emulator tests. See `docs/releases/v0.4.4.md`
and `docs/VALIDATION.md`. The historical baseline below explains the previous cloud handoff.

Baseline before the local 0.4.4 increment: [v0.4.3](https://github.com/GreenFuze/MyPhotosAnywhere/releases/tag/v0.4.3), version name 0.4.3 / code 9, Room v5. Tested source: `21f826723bb45a93d7a5aa80dee0d84b0ac39621`; release publication commit: `299e1f67ae5f2b1a58fe31c9ab5853efdd84f263`. This handoff commit is later documentation; recheck the actual remote HEAD rather than assuming those are still main.

Evidence: 95 unit/integration tests, zero lint errors, nine Android 10 emulator tests, no failures/errors/skips. Build run `38073741495`; device run `38073741460`. The owner tests a **Samsung S23 Ultra using Smart Launcher**. Existing-folder Google authorization now works. An earlier icon report accompanied a failed update and reinstall; do not assume an ongoing launcher defect. Old gallery/full-quality-viewer/external-deletion bugs had fixes in 0.4.1; confirm current behavior before rewriting them.

0.4.3 adds unnamed grouping review, pending matches under named people, Why separate? diagnostics and a true Rescan all faces that preserves names/corrections. The owner found the rescan button; it is below lengthy settings, so future primary-action layout should improve discoverability. A real diagnostic showed closest named similarity **0.503**, runner-up **0.100**, below experimental threshold **0.720** / margin **0.080**. An occluded-eye example was involved. This demonstrates a weak score with usable vectors, not ambiguity or insufficient photo count; it does not establish why preprocessing/model similarity fell. Do not lower thresholds globally from one pair or claim Google Photos accuracy. Keep model/alignment changes out of the import milestone unless separately justified.

Relevant entrypoints:

- Drive: `drive/CloudSync.kt`, `DriveGateway.kt`, `CloudDatabase.kt`, `CloudDownloads.kt`, `DriveBackupWorker.kt`, `DriveCoordinator.kt`.
- Storage/index: `data/PhotoDatabase.kt`, `PhotoRepository.kt`, `MediaCache.kt`, `core/LibrarySettings.kt`, `library/GalleryVisibility.kt`, `LibraryFeaturesModel.kt`.
- Portable annotations: `metadata/XmpCatalog.kt`, `MetadataDatabase.kt`, `MetadataRepository.kt`.
- Recognition: `people/PeopleRepository.kt`, `FaceEmbedding.kt`, `FaceMatching.kt`, `FaceReview.kt`, `FaceRegions.kt`; UI `library/PeopleScreen.kt`.

Paths above are under `app/src/main/java/com/myphotosanywhere/mpa/`. Audit actual code before changing contracts. The 0.4.4 Drive-ingestion increment adds `drive/DriveIngestion.kt`, durable Room v6 bulk-scan progress and primary per-source refresh/progress controls. Read `docs/DRIVE_INGESTION_AUDIT.md` and `docs/VALIDATION.md` before continuing; the new bulk-ingestion phone checklist remains a live acceptance gate. The 0.5.0 increment implements the shared migration engine, Takeout ZIP/folder and Photos Picker adapters, plus opt-in verified Drive JSON conversion. Read `docs/GOOGLE_IMPORTS.md` for controls, Cloud setup and limits; check `docs/VALIDATION.md` and release evidence before treating an APK as published or real-account acceptance as complete. Published 0.4.4 uses Room v6; 0.4.3 used Room v5.

## Local toolchain, phone signing and first work increment

Use JDK 17, Android SDK Platform 35 / Build Tools 35.0.0, Python 3 and the included Gradle 8.11.1 wrapper. Check installed tooling before downloading replacements. Keep SDK path in ignored `local.properties`; set `MPA_PYTHON` to the discovered Python executable if Windows lacks `python3`. Gradle downloads a checksum-pinned Apache-2.0 MobileFaceNet asset; keep it ignored. The Python TFLite smoke script's Linux CI runtime may require WSL/a supported interpreter on Windows; do not make a duplicate platform-specific runtime install a blocker for ordinary Android builds or native inference tests.

```powershell
.\gradlew.bat testDebugUnitTest lintDebug assembleDebug --no-daemon
.\gradlew.bat signingReport --console=plain
# Run when a local emulator/test device is available:
.\gradlew.bat connectedDebugAndroidTest
```

Run relevant tests for the first change, then the required checks once the increment is ready. Establish baseline behavior from the recorded CI plus local tooling; do not repeatedly run full suites without a new change/failure. Add meaningful regressions for scan checkpoints, partial traversal, import restart/verification/deduplication and schema migrations. User Takeout data is not a test fixture to commit: request a small album export only when needed for live validation and keep it outside the checkout.

Published CI APKs use SHA-1 **`A9:09:D0:BC:00:22:09:7F:9C:9F:B8:6F:E9:BC:E6:EF:60:44:C0:DD`**. A newly generated local debug key will differ and cannot update the installed published APK. First inspect existing local signing configuration without exposing key material. Preserve any matching key already available; never regenerate it for convenience or uninstall the owner's phone app to bypass the mismatch. If the matching key is unavailable, test on a separate emulator/device or continue publishing through the established CI signer. Register the actual local certificate as an additional Android OAuth client when testing local builds. Never commit keys, passwords, tokens or exports.

The first deliverable shipped in 0.4.4: a concrete Drive-ingestion audit and tested resumable bounded discovery/visible sync state, preserving existing reconciliation safety. The shared engine, Takeout and Picker are implemented in the 0.5.0 increment; complete the small real-album, consent and Drive conversion round trips on the owner's phone. After restarting Codex on 2026-10-11, browser setup completed in the existing `myphotosanywhere` project: Picker API enabled, Picker scope saved, released package/certificate association verified, and existing External / Testing audience and test user preserved. See `docs/GOOGLE_IMPORTS.md`. The tested 0.5.0 build uses version code 11; keep future version changes tied to an actual testable build. For 0.5.0 release work, read `.github/workflows/release050.yml` and its immutable references. Preserve `.github/workflows/release044.yml`: it targets the immutable 0.4.4 source and passed CI artifacts. The older `release04.yml` remains dedicated to 0.4.3. Future releases need their own deliberate version/artifact references; never replace an existing APK.
