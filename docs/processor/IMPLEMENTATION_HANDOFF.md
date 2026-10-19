# MPA Processor implementation handoff

Date: 2026-10-19. Governing specification: [MPA_PROCESSOR_REDESIGN_PLAN.md](MPA_PROCESSOR_REDESIGN_PLAN.md).

## Start condition

The original effort-change/start checkpoint has been satisfied: the owner reported switching to High and instructed emulator-first implementation. Stage 0 has started. Do not request another start confirmation, change model settings, spawn agents or create chats. Track evidence and remaining gates in `IMPLEMENTATION_STATUS.md`.

Actual owner answers, 2026-10-19: personal testing first; Android plus Windows/Linux/macOS all remain required; no purchases; documented unsigned/ad-hoc test installers are accepted if publisher credentials are unavailable. This is a scoped distribution exception, not permission to weaken runtime/integrity checks or omit a platform.

**Updated owner instruction: begin development with emulators and reserve the S23 for final tests.** The reported physical connection is not an implementation startup gate. Verify live Google Cloud/Drive access through the owner's Chrome session before the Stage 0 probes. Ask for reconnection if those required sessions are unavailable; verify/reconnect the S23 at final acceptance. The owner has reported switching to High, so do not request the effort-change confirmation again. Current observations are in `MPA_PROCESSOR_PREFLIGHT_2026_10_19.md`.

After that instruction, implement the whole joint release through the eleven stages below. Use focused commits and the stage ledger, continuing between routine packets without asking for another permission. External credentials/consents, purchases, unresolved warnings, safety failures and material departures from the approved scope require a concrete decision. Do not publish a partial APK as completion.

**Unattended execution is explicitly requested.** Once started, proceed through every included feature and all eleven stages, including fixes, tests, native packaging, publication and independent artifact verification. The owner does not need to attend stage transitions, approve routine choices or provide another Continue message at each increment. Keep progress and the ledger current without turning them into permission gates. Do not end with an intermediate feature batch or quietly omit release scope. Genuine human-dependent blockers and explicit owner stops remain exceptions. Preserve a resumable checkpoint if context/execution limits interrupt the run; do not imply autonomous work happened while execution was unavailable.

## Non-negotiable implementation checks

- One designated Processor and controlling phone per library initially. Phone inference/fallback and Drive-delivered remote control commands are deferred.
- Android remains Kotlin/Compose. Processor is C#/.NET 10 LTS with Avalonia; heavy plugins are separate first-party workers with a language-independent protocol.
- Direct Drive APIs; no installed Drive client, mandatory MPA server or paid AI dependency.
- Originals change only through explicit approved actions. Background analysis has no original-write interface. Save edited copies initially; no in-place image replacement.
- Human corrections beat generated suggestions. Causal conflicts preserve both alternatives; wall clocks cannot choose a winner.
- Complete results/artifacts are immutable and verified. Publication manifests come last. Local SQLite/Room files never travel through Drive.
- Durability, original identity, availability and UI progress are separate. Uploaded archive, verified photo original, generated preview and applied catalog are different facts.
- Per-item errors do not stop unrelated items. Retry only unfinished/failed stages. Internal invariant failures stop the affected publisher and preserve the last verified view.
- Bound memory, process trees, caches, parsers, archive expansion and network calls. Close/dispose owned resources after native work has finished.
- Breaking old app/library formats is allowed; automatic deletion of old originals/workspaces is not. A clean installation is an explicit user procedure.
- Every release includes exact tested APK and native installers for Windows/Linux/macOS, with compatible versions, licenses, signatures/checksums and evidence.

## Repository and evidence conventions

At the start, locate the actual managed repository/current branch rather than assume the chat cwd is a Git checkout. The audited worktree was `stage-0533-worktree`, branch `feat/device-storage-folders`, clean after its merged release. The published baseline tag/docs revision is `6c9ead7b1d4446dc819dee71974aa7b68614f232`; tested app source is `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Verify whether newer work exists and preserve it.

Keep `app/`. Proposed additions after implementation starts:

```text
processor/
  src/          Core, domain, persistence, Drive, LAN, desktop shell and worker host
  plugins/      First-party plugin workers and manifests
  tests/        Unit, native, recovery and packaging tests
protocol/
  schemas/      Versioned wire contracts
  fixtures/     Cross-language valid/invalid golden records
packaging/
  windows/      NSIS installer/uninstaller
  macos/        Application/package/sign/notarize/uninstall
  linux/        deb/rpm and login integration
tests/interop/  Two-client/Drive/LAN fault and round-trip rig
docs/processor/
  IMPLEMENTATION_STATUS.md
  decisions/
  validation/
```

These paths are a responsibility map, not a requirement to create an assembly for every folder. Prefer a small number of focused projects over framework scaffolding. Do not move the Android tree or rebuild stable UI components without a demonstrated need.

Every status-ledger entry records: stage, immutable source SHA, completed contracts/decisions, exact test commands and result locations, unresolved gates, and next packet. Private photos/names, embeddings, tokens, resumable session URIs and keys are not public evidence. Use permitted fixtures and sanitized diagnostics.

## Stage 0 — Establish feasibility and freeze choices

**Entry:** The owner has instructed emulator-first implementation after switching to High; Google Cloud/Drive preflight is verified. Prepare a suitable emulator as part of toolchain setup. The S23 is reserved for final tests, where enabled debugging/pairing alone does not prove an active connection.

1. Read applicable AGENTS/repository instructions and the two plan documents. Capture clean/dirty Git state without resetting existing work. Verify baseline version/package/signer and the current test/workflow inventory.
2. Inventory existing original/download leases, device references, timeline/viewer sessions, transfers, Takeout recovery, human annotations and Trash rules. Record what is reused and what will be replaced.
3. Inspect live OAuth setup with authorized access. Prepare any required desktop-client/production/verification changes for explicit consent. Never expose credentials or assume old setup notes are current.
4. Implement only the bounded feasibility probes needed for Gate A: existing/manual nested files, new-file discovery, cross-client workspace reads/writes, immutable publications, range reads, checksum fields, lost upload responses and refresh after expiry/restart/network loss. Use a clearly identified disposable test folder for writes; do not alter the owner's photo library.
5. Start the beyond-seven-day real authorization test with the intended production grant. Configuration/short-refresh/access proof is the immediate gate; elapsed-time evidence continues until Stage 9. A pending duration test is recorded, never reported as passed.
6. Prove a small native inference/decoder worker on Windows x64, Linux x64 and macOS x64/ARM64. Record candidate detector/embedding/object/OCR licenses and redistribution rights, runtime/operators, models, native dependencies and hashes. No model training or new paid service.
7. Inventory Windows/Apple signing, notarization and platform test access now. Do not buy certificates. Missing paid publisher credentials use the owner's already approved documented personal-test unsigned/ad-hoc path; missing native platform testing is not waived.
8. Pin exact stable .NET SDK/runtime, Avalonia, NuGet packages, workers/models and packaging tools in lockfiles/checksum manifests. Retain the verified Android toolchain unless compatibility/security evidence requires change.

**Artifacts:** `baseline-audit.md`, `drive-feasibility.md`, `dependency-model-matrix.json`, `distribution-prerequisites.md`, initial status ledger.

**Exit:** Immediate Gate A passed; a viable licensed CPU worker path exists for all required targets; signing/access blockers have concrete owner decisions or remain explicitly blocking. Do not build the full product around an unproven Drive permission assumption.

## Stage 1 — Freeze contracts and failure semantics

**Entry:** Stage 0 immediate gates passed; elapsed-time OAuth check may still be running.

1. Define wire schemas and Kotlin/C# codecs for library/actor/authority, sources/representations, content fingerprints, user operations, jobs, manifests, result recipes, preview indexes, errors and capabilities.
2. Specify encoding/canonical hashing, bounded lengths, timestamp provenance/orientation, optional/required fields and major/minor compatibility. No ambiguous enum/default behavior or fake zero timestamp/hash.
3. Define identity association and causal conflict algorithms with examples: independent Drive discovery, device backup/discovery race, durable aliases, duplicate result, human rename during processing, conflicting rename, merge/rejection, changed source, tombstone/restore and obsolete Processor epoch. A restored actor cannot reuse old sequence IDs.
4. Specify bootstrap, first controller pairing, replacement/lost-phone recovery and old authority rejection. A copied local job database or stale Drive head cannot silently create a second active publisher.
5. Define the approved-original-action envelope and which host operations can consume it. Explicit Create/Back up/Save a copy/Trash/Restore are scoped actions; analysis consent is not.
6. Freeze transactional boundaries and crash recovery: local domain/outbox commit, source reservation, worker result, verified upload receipt, immutable commit, generation-scoped Room staging/short activation, reader reclamation and cursor advancement. Avoid clear-and-refill or an unbounded new catalog copy for every progress tick.
7. Prototype bounded preview bundle lookup and manifest pagination using feasibility evidence. Keep individually fetched viewer previews separate from grid bundles. Lock parser/chunk limits.

**Artifacts:** protocol schemas, valid/invalid fixtures, conflict/state tables and recovery diagrams. Documentation/codecs only for the new contract; do not replace the gallery yet.

**Exit tests:** Kotlin and C# read the same fixtures; randomized arrival order/replay converges or preserves the specified conflict; incompatible data leaves last verified state intact. Hashing and signature coverage are unambiguous.

## Stage 2 — Core, resources and plugin workers

**Entry:** Contracts fixed enough for executable fixtures.

1. Build a small object-owned core, local SQLite job/outbox store, typed errors, StorageBudget, leases and process ownership. Avoid a service-locator interface exposing all core capabilities to plugins.
2. Build a worker host and a harmless metadata/test worker. Enforce protocol negotiation, output-path validation, bounded progress, recipe/model identity and staging validation.
3. Add cancellation, deadlines, termination escalation, checkpoint recovery and single-instance handling. Kill/join a hung worker before releasing its files/native-memory leases; bound shutdown time and mark interrupted work retryable.
4. Add native credential-vault adapters without using plaintext fallback. Keep worker environments free of Google tokens and release secrets.
5. Implement Avalonia status/tray controls, pause/resume, actual Exit, CLI status/stop and normal window-close behavior. Keep UI ownership separate from jobs.
6. Measure idle/core and worker process-tree memory, native handles and repeated start/stop. Apply one-heavy-worker/two-thread defaults with real backend controls.

**Exit tests:** All native target builds run the sample worker; malformed output cannot publish; native crash/hang is contained; original fixture hashes and locations are unchanged; resource and shutdown tests pass without unresolved warnings.

## Stage 3 — Accounts, pairing and LAN management

**Entry:** Core/worker lifecycle and credential ownership are tested.

1. Implement desktop system-browser OAuth with the tested scope strategy, refresh lifecycle and independent per-account sessions. Preserve Android's existing valid authorization behavior.
2. Implement mDNS/DNS-SD discovery, OS permissions and private-network listening with an automatically selected port. Verify interface changes and duplicate instance names. No router forwarding or trust derived from broadcast contents.
3. Implement short-lived pairing with computer confirmation, TLS/pinned device trust, actor/library association and revocation/replacement. Test wrong account/library, replay, spoofed discovery and abandoned setup.
4. Implement typed idempotent LAN commands and reconnectable progress/status. Desired settings become applied only after a persisted acknowledgement.
5. Add the minimal Android Connect a computer/status/settings screen. Phone-disconnected controls remain pending rather than being sent through Drive as remote commands.
6. Configure supported OS firewall/login integration transparently. Any OS consent is shown; inability to discover does not break the gallery or trigger permission loops.

**Exit tests:** At least one actual Android-to-desktop pairing round trip, token refresh/restart recovery and multiple accounts. A VM/network test rig is acceptable evidence for protocol mechanics, but is labeled separately from S23/router acceptance.

## Stage 4 — Durable results and human synchronization

**Entry:** Both peers can authenticate, negotiate and retain applied configuration.

1. Implement immutable artifacts/commits with per-producer advisory heads, account change cursors, verification and reconciliation. Never use live SQLite sync or a shared last-writer-wins JSON file.
2. Add Android's transactional human-operation outbox and Processor ingestion. Implement correction precedence, same-field conflict retention and explicit resolution.
3. Add authoritative generated catalog publication, epoch validation and obsolete/conflicting publisher protection. Two same-epoch canonical histories stop promotion; clocks do not elect a winner.
4. Implement bounded Room application while retaining prior usable content. Preserve gallery anchors, stable geometry, selection and viewer sessions through incremental metadata/preview arrivals.
5. Add generated-data removal/tombstones, recovery snapshots and history-floor semantics. Keep human operations and pending uploads protected from cleanup.
6. Complete the first vertical slice with a verified original and simple metadata/preview: upload, analyze, publish to Drive and browse on phone; preserve exact source bytes.

**Exit tests:** Arrival-order permutations, duplicates/gaps, lost heads, partial manifests, tampered hashes, source revision changes, restored backups, old epochs and crash injection at every durable boundary. UI never flickers to an empty library during a valid refresh.

## Stage 5 — Real imports, metadata and previews

**Entry:** Durable slice and failure recovery pass.

1. Port Takeout semantics/fixtures, not Android classes wholesale. Implement Processor inputs for staged local ZIP, staged extracted folder and existing Drive ZIP, including multipart jobs and captured destinations.
2. Reuse the proven original-transfer/checkpoint rules. Persist IDs/sessions/receipts; reconcile unknown completion before re-upload. Uploading archive input is not verified media backup.
3. Parse metadata with bounded streaming and raw provenance. Malformed/oversized XMP records a metadata-stage issue and cannot stop other originals or undo completed uploads.
4. Implement preview/orientation/ICC handling and sizes, indexed bundles, viewer previews, basic video facts/posters and unsupported-codec reporting. Never mark a preview as an original.
5. Apply storage and archive expansion safeguards, including duplicate/case-colliding target names, path traversal, symlinks and input-revision changes. No expansion of the whole library into RAM.
6. Present the stage-specific import state and failed-item/stage retry controls. Browsing continues while uploads/extraction happen.

**Exit tests:** Representative Takeout fixtures with duplicate names, missing/ambiguous metadata, three source paths, multiple parts, huge XMP, corrupt media/ZIP entries, network interruption, authorization loss, full disk and process death. Resume performs no successful-stage rework; no input or local-only original is altered/deleted.

## Stage 6 — People, objects and OCR

**Entry:** Native backend/license decisions and metadata/preview contracts are verified.

1. Implement face detector/alignment/embedding/grouping workers with complete recipe fingerprints and bounded native owners. Never mix embedding spaces or use imported names alone as biometric templates.
2. Synchronize canonical people, aliases, rename/merge, reviewed/rejected assignments and source memberships. New photos compare against the same confirmed identities irrespective of naming origin.
3. Implement object/category labels and OCR documents with provenance/confidence/coverage. Keep manual names/captions and human corrections separate.
4. Make the phone index/query the supplied documents locally without inference. Object-search coverage and completed-empty states are honest.
5. Evaluate representative held-out recognition/search cases and publish only aggregate permitted evidence. CPU behavior is required on each target; GPU is optional and cannot be needed for correctness.
6. Test consent off, revocation/cancellation and late results. Publish only allowed face geometry/identity/previews; keep biometric vectors local to the Processor initially.

**Exit tests:** Native execution on every shipped OS/architecture; measured quality scope, conservative unknowns/suggestions and corrections surviving a model rebuild. No silent accuracy claim, paid model/service, license ambiguity or unbounded face comparison workload.

## Stage 7 — Complete Android integration and remove batch processing

**Entry:** Replacement catalog/processing workflows exist end to end.

1. Adapt existing repositories rather than create new Photos/ViewModel stacks. Preserve timeline order, device folders, source visibility, availability, backup badges, selection and viewer memory/lifecycle protections.
2. Finish Processing, Sources, Activity and grouped Settings pages. Distinguish phone uploads, computer processing, metadata synchronization and failed items. Pending LAN commands show the actual pending state.
3. Preserve Details swipe up, quiet grouped facts, manual overrides, safe explicit source actions and progress/error recovery. Preserve floating dates, balanced mosaic, disappearing/reappearing app-bar motion and no scrollbar.
4. Remove PeopleWorker/batch inference scheduling, ML Kit/LiteRT runtime dependencies, bundled face model asset/fetch hooks and obsolete phone-analysis notifications once replacement tests pass. Check manifest/startup/background paths for indirect scheduling.
5. Retain only essential display/transfer/index/interactive-render computation on the phone. Archive extraction, bulk derived previews, OCR, detection and automatic recognition cannot quietly return as fallback.
6. Implement explicit fresh-install/incompatible-format handling. Preserve the established package/signer, but do not pretend an old database/catalog can be used safely without a supported conversion.

**Exit tests:** Full source/device/backup/Trash/offline/People/Search regression; dependency/APK inspection proves phone inference removed; app remains usable without Processor/LAN; pixel-anchor and lifecycle assertions pass. Record APK size/memory differences instead of promising them before measurement.

## Stage 8 — Native packages and lifecycle

**Entry:** Functional integrated product, with signing decisions already settled.

1. Build self-contained target-specific payloads with locked native dependencies. Use NSIS on Windows, application/pkg on macOS and deb/rpm on Linux. Use existing authorized publisher signing/notarization where available; otherwise use and document the approved unsigned/ad-hoc personal-test path.
2. Implement install registration, login startup, desktop links/status, user-scoped config/credential initialization and actual stop/exit behavior. Never run the user's OAuth setup as root merely because an installer was elevated.
3. Implement uninstall on every target, preserving Drive library/originals. Warn/export unsynced local work before local-data removal; remove only MPA's own credentials/login entries/rules and files.
4. Implement compatible upgrades and a verified native-installer update flow. No hot swap of running plugins or unsigned automatic updater.
5. Test required OS/architecture payloads, including real bundled inference/codec libraries. Validate signing, notarization and permission prompts on the final package, not just an unpackaged executable.

**Exit evidence:** Install → connect → process → tray/status → Exit → relaunch → upgrade → uninstall on Windows, both Mac architectures and the chosen deb/rpm distributions. Missing paid publisher keys alone are covered by the accepted test-installer exception. Platform omissions, invalid executable signing, failed integrity checks or inaccessible required native testing remain blockers.

## Stage 9 — Freeze and validate the joint candidate

**Entry:** No unfinished functional or packaging stage; candidate versions frozen.

1. Freeze one application/Processor/protocol/model/packaging SHA and run the complete applicable Android and desktop suites. Retain prior assertions; record deliberate contract removals/replacements.
2. Run Android native suites on API 29/35/37. Run all required desktop target tests on native architectures; archive complete build/runtime/installer logs, not only summaries.
3. Run 10k/100k fixtures, crash/replay/cancellation/per-item retry/low-storage/resource scenarios and catalog/preview network-call measurements. Record hardware, process-tree memory and cold/warm conditions.
4. Complete live disposable Drive round trips and the real beyond-seven-day authorization check. Configuration proof plus an immediate refresh is not a substitute for elapsed-time evidence.
5. Validate Android-to-desktop LAN on Windows and a non-Windows system. Separate protocol-rig evidence from physical S23/router tests and human accessibility review.
6. Inspect all warnings. Old release-specific approvals do not expand automatically. No compiler/lint suppression or weakened tests; ask about an unfixable upstream diagnostic only after cause and safe alternatives are investigated.
7. Produce a precise acceptance ledger of passed, failed, unverified and explicitly waived requirements. Every waiver includes exact cause/scope/artifact identity and actual owner response.

**Exit:** All required automated/native/integrity checks pass for one candidate, external/platform gates are satisfied, and any genuinely unavailable acceptance has an explicit owner decision where required. Changes after freeze rerun the affected checks and invalidate stale artifact claims.

## Stage 10 — Publish exact app and installers

**Entry:** Stage 9 evidence authorizes the exact candidate. Publication is within the implementation/release instruction, but must not bypass required gates.

1. Assemble the tested APK and native installers, joint compatibility manifest, model/license/SBOM inventory, checksums and sanitized evidence. Preserve the exact final payloads that passed package tests, reporting actual publisher-signing/notarization or the accepted personal-test unsigned/ad-hoc status accurately.
2. Independently inspect APK package/version/min/target and established signer. Inspect each installer version/architecture/signature, installed payload tree and plugin/model hashes.
3. Merge/fast-forward the tested source, attach any created PR, and prove any publication-only commit leaves application/protocol/build/model/packaging objects unchanged. Do not rebuild to publish.
4. Publish one GitHub release with all required platforms. Do not call a Windows-only test asset the completed three-platform release or silently omit a processor installer.
5. Independently redownload every public asset; verify checksums/signatures/notarization/version/tag/source/compatibility/latest status. Retain old releases as rollback/reference material.
6. Report APK/installer links, actual validations, clean-install implications and precisely pending physical/human/live acceptance. Stop with a complete release, not a promise that artifacts will appear later.

## Stop conditions and bounded decisions

Stop and ask when required Google Cloud/Drive access is unavailable, the S23 is unavailable when final physical tests are due, an action needs unavailable OAuth/account consent, no no-cost verification path is viable, a required OS cannot be tested/distributed, a new warning cannot be safely resolved, integrity/causality is inconsistent, or the behavior requires a material scope change. Do not ask again about missing paid publisher certificates: the documented personal-test path is already accepted. Do not block emulator-first implementation on the phone. Finish independent authorized work where useful and present concrete reconnection steps/options.

Do not ask again for already accepted architectural defaults or reversible routine implementation choices. Do not treat generic background warnings as approved. Do not turn the present planning request into implementation before the effort-change opportunity.
