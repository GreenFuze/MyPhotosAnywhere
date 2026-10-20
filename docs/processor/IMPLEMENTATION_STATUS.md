# MPA 0.6.0 implementation ledger

Updated: 2026-10-20. **Stage 0 validation continues; Stage 1 contract integration and Stage 2 foundations are in progress. No 0.6 application or installer is released.**

## Authority and baseline

The owner approved implementing the complete joint release unattended, reported switching to High effort, and subsequently directed emulator-first development with the S23 reserved for final tests. The historical plan-only stop in the plan documents has been satisfied. Preserve fast failure, object ownership and original-media protections.

Baseline: `6c9ead7b1d4446dc819dee71974aa7b68614f232` (0.5.33 publication revision), application source `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Work branch: `feat/processor-060`. Native evidence records the exact clean committed SHA; early local experiments are explicitly provisional. Only isolated protocol classes/tests have been added to Android; no UI behavior, version or Room schema has changed.

## Stage ledger

| Stage | State | Evidence / remaining work |
| --- | --- | --- |
| 0: feasibility/baseline | In progress | Production OAuth, full desktop grant, fresh-process refresh, bounded synthetic Drive round trip passed. Manual post-consent source discovery, actual-expiry refresh and injected lost-completion reconciliation passed. Four-target two-model/decoder native matrix passed on 67607b1; expanded object model matrix is running on 9bae0a3. Actual cross-client/crash/multiple-account/duration and remaining codec/OCR/package gates stay open. |
| 1: contracts | In progress | Shared Kotlin/C# canonical JSON and Drive identity goldens passed; Kotlin 11 targeted tests plus zero-findings lint passed; C# 25 tests now cover signatures, causality, aliases, closed approval scopes, artifact integrity and worker framing. The earlier 17-test contract revision passed all four native OS/architecture targets. Catalog/worker wire and staged-index contracts are specified; integration/scale acceptance remains. |
| 2: core/plugins | In progress | Local SQLite ownership, durable stage/checkpoint/receipt outbox and failed-only retry foundation under test. Resource reservations, actual process ownership/recovery, complete plugins and tray/Exit remain. |
| 3: accounts/LAN | Pending | Production sessions, discovery, secure pairing, acknowledged controls. |
| 4: catalog | Pending | Immutable publishing, staged phone catalog, human synchronization. |
| 5: imports/previews | Pending | All three Takeout paths, per-stage retry, previews/video. |
| 6: recognition | Pending | People/object/OCR workers, consent, quality and native coverage. |
| 7: Android | Pending | Complete remote workflows and remove batch inference. |
| 8: installers | Pending | All required native install/upgrade/uninstall paths. |
| 9: acceptance | Pending | Frozen full matrix, scale, live Drive/LAN, duration and S23. |
| 10: release | Pending | Exact tested APK/installers, publication and public verification. |

## Completed observations and experiments

- Existing app: 0.5.33/code 44, min SDK 26, compile/target 37, Room 12. New release target remains 0.6.0/code 45, protocol major 1; no premature version bump.
- Chrome is signed into the owner's intended Cloud project; Drive API enabled. A new empty disposable test folder was created separately from the existing library. Private identifiers/screenshots remain in the local preflight evidence, not this repository.
- Initial OAuth inspection found External/Testing status and two Android clients and configured `drive.file`, `drive` and Photos Picker scopes. The owner explicitly approved creating a separate Desktop client named MPA Processor. Config is stored outside tracked files. Creating the client did not itself grant account access or expand configured scopes; subsequent explicit grants are recorded below.
- Bounded Windows authorization/refresh probe prepared with official Google library, PKCE/state and DPAPI. Synthetic storage round trip, expiry persistence, plaintext-marker exclusion, type isolation, corruption rejection and deletion passed with zero build warnings/errors. The actual loopback server also passed synthetic valid-state, mismatched-state and cancellation/temporary-request cleanup checks; concurrent probes are excluded by a private file lease. On October 20, narrow desktop consent and new-process immediate refresh passed. A read-only folder probe observed the existing manual folder as inaccessible under the returned drive.file scope. Full scope and immediate refresh subsequently passed, followed by bounded synthetic uploads, status/resume, hash/range reads, reserved-ID replay and nested paged discovery. Cross-client/manual-source and duration acceptance remain pending. See `validation/drive-feasibility.md`.
- The initial branding blocker was resolved. The owner supplied their existing website and reviewed and approved MPA-specific homepage/privacy/terms plus the domain/production configuration. Website main was fast-forwarded to the reviewed pages; Pages deployment completed successfully and all three public pages were verified. Google confirmed the branding save and **In production** status after a fresh action-time owner confirmation. Verification is still incomplete and the unapproved-scope 100-user cap is visible. Chrome control recovered after transient disconnects. The owner approved the narrow desktop grant, and immediate fresh-process refresh passed. The owner explicitly approved full Drive and completed the safety interstitial personally. The callback returned exactly drive at 2026-10-20T04:33:16Z. New-process refresh and folder reads passed; synthetic-only fixtures were created beneath the disposable root. Real beyond-seven-day observation is due after October 27 04:33:16 UTC. Existing website and domain ownership verification files are preserved.
- Installed .NET SDK 10.0.303/runtime 10.0.13 works outside sandbox; the sandbox SCM error was an execution restriction, not an SDK defect.
- Separate headless API 35 development emulator booted; checksum-verified 0.5.33 baseline installed and launched there. No physical device changed. The old global emulator is development-only; final tests still require the pinned matrix.
- ONNX Runtime 1.31.0 + SkiaSharp 4.153.1 build with zero compiler/analyzer warnings/errors on Windows. Native execution runs YuNet/SFace and an in-memory JPEG round trip.
- Original SFace export emitted initializer-input warnings. A checked graph-declaration correction removes them without changing operations/weights. Both models passed all-output comparisons for three deterministic inputs; largest measured SFace absolute difference was `5.662441253662109e-6`. Corrected Windows runtime log has zero warnings/errors. This is feasibility, not recognition-quality acceptance.
- Native probe steps on CI Windows and Apple Silicon passed on `b29fb84c6a43d8737a239861157d8779bd3ac8f7`, but their jobs were canceled by matrix fail-fast after Linux correctly blocked a hardware-discovery warning. Preserve that distinction; the run did not pass.
- Official ONNX Runtime NuGet lacks osx-x64. The revised matrix builds that exact runtime source on Intel and Linux with unused accelerator discovery and telemetry disabled. The Linux diagnostic was `device_discovery.cc/GetPciBusId` rejecting a synthetic VM path. No diagnostic suppression or exception is introduced. On revision `7bf2141ec5d24ef1c42914f27e04d8808124cdf6`, complete Windows and Apple Silicon jobs succeeded and their downloaded report hashes/source binding were verified. Linux compiled but failed strict upstream diagnostic review (76 warning lines); Intel was canceled by fail-fast with partial diagnostics retained (87 warning lines). Neither source-build target is accepted. No diagnostic exception is approved for these builds.

## Commands and local evidence

```text
dotnet restore probes/NativeFeasibility/NativeFeasibility.csproj
dotnet build probes/NativeFeasibility/NativeFeasibility.csproj --no-restore -c Release --verbosity minimal
python processor/probes/fetch_models.py
<isolated-onnx-python> processor/probes/normalize_models.py
dotnet <probe-dll> --compare <original-model-dir> <normalized-model-dir> <new-report>
dotnet <probe-dll> <normalized-model-dir> <new-report>
```

Local evidence outside Git: `preflight-060/native-build-normalized.log`, `native-equivalence.json` and its complete original-reference log, `native-windows-x64-normalized.json`, `native-normalized.log`, `baseline-emulator.json`, baseline install/launch logs and browser screenshots. NuGet lockfile and candidate model hashes are tracked. CI evidence must bind an immutable commit; it has not passed merely because its workflow is written.

## Open gates / next packet

Latest October 20 packet: `67607b119d7e1c6b89dcbafec67c95da72964e6c` is the current pushed native compatibility increment. CI run 39373514740 completed Windows/Apple Silicon successfully; downloaded four-file evidence inventories for each were independently hash/source verified. Linux/Intel subsequently passed too. Complete source-build logs, five source inventories per target, append checks and all four native report hashes were independently verified; no unresolved diagnostics. This is the two-model feasibility increment, not product acceptance. Authorization run 39373514936 passed strict build and synthetic checks without using any Google grant in CI.

Live full-Drive recovery also passed an injected I/O failure after the server completed a synthetic upload but before the uploader received its response: reconcile the persisted ID/hash, obtain 409 on replay and verify exact downloads. Separate Chrome-created nested metadata was discovered and byte-verified without another consent. Real socket/process interruption, Android cross-client and elapsed-time gates remain open.

The next local candidate adds pinned YOLOX, embedded SFace modification notice, exact build-tool wheel/license hashes, Windows synthetic OCR and nine offline downloader-ownership tests. Local strict builds and corrected native smoke are clean; all nine model-output comparisons passed. This is provisional Windows feasibility, not a completed recognition plugin or final native matrix. See `validation/object-ocr-feasibility.md`. No product Android files/schema/version changed.

1. Website and OAuth Production configuration are complete. Preserve the existing approvals and the personal-test/unverified-app limitations; do not ask again for the completed grant.
2. Complete independent Android/Desktop access, manual-source discovery, actual expiry and operational-recovery tests. Configuration/full access/short-refresh and the personal-use path are observed; real elapsed-time observation started October 20. Native feasibility remains open before Stage 0 completes.
3. Execute native feasibility on Linux, macOS ARM64 and Intel; resolve all warnings and freeze dependency/model/license/tool evidence. Check object/OCR/video backend redistribution and runtime support too.
4. Finish signing/native-test-access inventory. Missing paid publisher signing uses the already approved personal-test path; missing native execution does not.

Do not stop at routine stages or ask for another Continue. Stop only for required human access/consent, unresolved diagnostics, safety or a material scope decision, while continuing useful independent work.

## October 20 verified follow-up

- Native CI 39373514740 on 67607b119d7e1c6b89dcbafec67c95da72964e6c passed all four OS/architecture targets. Independent downloaded evidence/report hashes and source binding verified; complete Linux/Intel build logs have zero diagnostics. Earlier failed/canceled runs above remain historical failures, not current unresolved source defects. See validation/native-compatibility.md.
- A manually uploaded synthetic nested file added through Drive UI after consent was found and read byte-for-byte by the existing desktop credential. A real-server upload completion response was discarded by the test transport before consumption, and reserved-ID reconciliation/replay passed. This is injected I/O failure, not a real process-crash claim.
- At 2026-10-20T06:22:43.0693717Z a fresh process refreshed the stored access token after its recorded expiry 06:03:57.8061269Z, without interactive authorization. The actual beyond-seven-day observation remains due after October 27 04:33:16 UTC.
- Android CI on 9bae0a3 correctly failed its strict lint gate after Gradle 9.8.2 became stable. The exact official checksum is pinned in the candidate update; no check is disabled. The existing AGP setVisible diagnostic remains separately checked by owner, label, ID, origin and version.

## Protocol and conditional mutation follow-up

- Revision 8e50af7ab98d3ef74ba7c9a3efe6a3de8b82fc88 passed all four desktop contract jobs (CI 39385167894); these contain the first 17 C# tests. Subsequent local candidate has 25 passing C# tests; its final cross-platform run is still required. Targeted Kotlin 11 tests and strict lint passed; no product navigation/storage behavior was enabled.
- Gradle 9.8.2 pin passed full Android build/unit/lint CI 39385167843 on 8e50af7, in addition to local targeted checks. New source/fixture LF rules resolve Git conversion warnings.
- Expanded native run 39380066718 on 9bae0a3 passed Windows/Linux/Apple Silicon; their downloaded evidence hashes, source/model binding and logs independently verified. Intel failed because the hosted runner lost communication; no completed log/artifact was available. That failed job was rerun on the same source, not waived.
- Read-only v3 synthetic metadata returned no ETag. A separate bounded synthetic-only probe confirmed v3 accepted an invalid If-Match (HTTP200). v2 exposes the ETag and rejected formerly-valid stale ETags (HTTP412), including Trash/restore, while matching ETags permitted those changes. Original bytes stayed exact. No existing originals changed; fixture retained. A deliberately arbitrary opaque ETag caused HTTP500 in v2; this negative observation is retained and is not evidence of successful conditional enforcement. Only the valid/stale captured-tag cases establish that capability. Product mutation adapters must use the independently verified conditional path and preserve replay/source/destination checks. See validation/drive-feasibility.md.

## Durable core foundation

- Microsoft.Data.Sqlite 10.0.13 with exact compatible SQLitePCLRaw 2.1.14 pins; the Windows test runtime reports SQLite 3.54.0. The 2.x patch branch matches Microsoft.Data.Sqlite's native provider dependency family; no preview/dynamic selectors. Lockfiles capture all transitive bytes. Actual native core testing on the other platforms is still required.
- The new core owns a dedicated marked local state directory, single-instance file lease and SQLite connection. Unknown directories/databases/future versions are preserved and rejected; no silent reset. WAL/FULL durability, foreign keys, parameterized statements and bounded transactions are explicit. SQLite calls belong to the serial background coordinator, not the UI thread; async wrappers do not make SQLite disk I/O asynchronous.
- Job contexts are immutable captured JSON/hash, including destinations. Stage attempts have fresh lease IDs; checkpoints survive retries. Completion and the publication outbox are one transaction. A failed item does not block independent work, and failed-only resume never requeues verified ready stages. Pause/cancel fence late results, wait for the worker owner to confirm exit, and retain completed receipts. A reopened running attempt stays fenced until the actual worker recovery owner resolves it.
- Seven local core tests passed. A repeated TRX filename caused a test-harness overwrite warning; the verification rerun uses a fresh unique result file. Native child/process-tree cleanup and crash recovery are NOT established by these repository-level tests; they are the next owner/service work.

## Storage budget and Linux codec follow-up

- Core candidate now has 11 passing local tests with no diagnostics. Storage reservations are disposable, account for other writers/cache use, recheck before writes and retain reservations/checkpoints on low-space failure. Default reserve remains max(2 GiB, 5% volume); explicit acknowledged settings may change the percentage while retaining the 2 GiB floor. No settings were silently changed and no file-deletion path was added. Actual volume adapters/cache eviction and worker resource enforcement remain.
- Corrected local Linux FFmpeg 9.0.2 decode-only source build and synthetic AVI pixel comparison passed with zero diagnostics. NASM 3.02 was extracted from its official package and executed without system installation. The additional strict native regression passes both six-symbol 8-bit and three-symbol 16-bit VLC recursion against independent expected bit patterns. Checked patches add union bounds assertions, scope the WMV2-only label and make existing unsigned comparison conversions explicit. No diagnostic flags were removed. This is provisional local feasibility, not a frozen cross-platform codec package or redistribution closure.

## Managed worker ownership increment

- Expanded three-model native matrix39380066718 passed all four targets after the same-source Intel infrastructure retry. Independently downloaded evidence verified. Mac peak measurement remains unavailable; no memory waiver or zero-memory claim.
- New private controller-lifetime channel uses an authenticated loopback connection with a one-launch key, explicit five-second handshake bounds, rejection of invalid peers, protected diagnostic serialization and joined concurrent disposal. It is internal IPC, separate from LAN pairing/Google credentials. Partial ownership handshakes close accepted sockets.
- The first-party framed worker owner verifies/holds its executable, clears inherited environment, captures bounded stderr, limits pending frames, fences new writes during shutdown, and joins process/pumps/writes before releasing resources. A failed bounded stop retains ownership and exposes a recovery join, rather than declaring clean Exit. Unexpected exit/frame failures remain errors after cleanup.
- Twenty local core tests pass, including actual child echo, malformed framing, unexpected exit, stubborn child termination, held-file release and deliberate controller termination while its worker ignores stdin EOF. The worker observes its parent channel closing and exits; synthetic source bytes remain unchanged. This is a real managed-process fixture, not validation of native OCR/video tools or Windows job objects/macOS linked guards. Those and actual tray/Exit integration remain.

## Dependency scheduling and Linux crash-test identity

- Persist blocked dependency stages outside the ready-work queue. Completion promotes only the same item after every prerequisite is ready; failed-only retry, reopen, and cancellation preserve that boundary. This avoids repeated scans of all blocked stages during large imports. Candidate Processor database schema 1 is still unpublished; Android Room remains unchanged.
- Windows strict Core tests now pass 22/22, including actual worker/controller-loss tests and new multi-prerequisite/reopen/cancellation cases.
- Four-platform run 39397061782 correctly caught Linux process-start wall-clock estimates differing across observers. The test now compares kernel boot ID plus raw start counter on Linux, preserving exact process-instance ownership instead of adding a timing tolerance. Other platforms retain exact process start ticks. Cross-platform verification of this correction is pending.
- Expanded native model run 39380066718 on 9bae0a3 passed all four targets after the same-source Intel runner retry. Independently downloaded artifact inventories, source/model bindings and full native logs verified; no diagnostic exception was used.

## First executable product worker

- Current local Protocol tests pass 29/29 and Core tests 27/27 with no build diagnostics. The actual ByteFacts worker performs framed role/session/sequence negotiation, verifies an immutable assigned input, returns a separately hashed output, cancels prepared work without output, and isolates a changed snapshot before processing another item. It is a harmless host acceptance worker, not face/object/OCR feature completion. Cross-platform verification for this new host remains pending.
- Linux codec CI 39400442460 on f98d083 passed publisher signature/pinned compressed CPIO and executable validation, strict restricted FFmpeg build, recursive VLC regression and exact synthetic AVI frame decoding. Downloaded report confirms SHA aed3580452b8440323740cb07e8a4f9369c1d92a36dd3ab15b0221c0e7c033e2. Complete diagnostic verification and other native codecs/format/license closure remain open. Two local WSL configure runs hit their bounded 300-second deadline; their owners killed/joined compiler groups. These timeouts are not accepted product evidence or warning waivers.

## Desktop controls and verified ownership follow-up

- Protocol/Core CI 39431189286 on 8a0ecca087718107e958a5c19e0e04374fde8d66 passed all four supported desktop targets: Protocol 29 tests and Core 31 tests, including actual operations/readers/reservations and rapid process exits. Downloaded final evidence verification remains required.
- Candidate desktop shell uses exact stable Avalonia 12.1.4 and its locked compatible SkiaSharp 3.119.4 graph. This renderer is separate from the 4.153.1 worker process; it is not forcibly upgraded across an unsupported Avalonia ABI. Original Android vector branding supplies the icon; derivation hashes are tracked.
- Local candidate Core tests now pass 36 tests, adding actual current-user local control, malformed client isolation, explicit handler-failure propagation and partial-frame shutdown. Final local re-run after the process-ID reply addition passed all 36 tests with no diagnostics; complete the native matrix before accepting it.
- Local Windows native UI check against isolated workspace state passed close-to-background, same-instance reopen, actual UI Pause/Resume independently confirmed via IPC, and actual Exit: process 245380 exited with code 0 and 0 stderr bytes. These are intermediate shell tests, not installer or final release acceptance. Tray menu entries exist, but direct system-tray menu clicking was not exercised by the available target-window tool.
- Native review found and corrected repeated Window.Opened database initialization, and a concurrent-launch race. Program now acquires durable state ownership before constructing a window; a competing launcher waits for the current owner's endpoint. Either concurrent launcher may become owner; control replies expose the actual local PID. An isolated concurrent launch selected one owner and accepted repeated show/pause/resume/stop commands with the installation marker unchanged.
- Initial exploratory test harness had a non-terminating PowerShell date-conversion error; scoped process/path evidence identified only the isolated fixture. Subsequent guards use terminating errors and exact process start ticks. Earlier failed native probes remain retained evidence, not accepted results.
- No 0.6.0 Android UI/runtime activation, signed joint installers or publication yet. Accounts/pairing/catalog/plugins/phone thinning and full acceptance remain open.

Desktop CI39443445552 on75dd7d10c4e2b40ce8b4fbc5fadea1fe69b03faf passed strict builds on all4 targets. Core IPC CI39443445237 correctly failed on macOS: the standard per-user TMPDIR plus the long endpoint name exceeded Unix socket path limits. Candidate shortens only the label from MPA.Processor.Control.v1 to mpac1-, preserving the same128-bit state digest and CurrentUserOnly checks. Actual Mac IPC must pass before acceptance; no exception requested.

Native IPC run39444762318 onbbd346a got past the socket-path issue, then caught an immediate-disconnect race in .NET's explicit Unix socket buffer setup on Apple Silicon. Use default transport buffers while preserving the independent8KiB protocol frame cap. Add20 repeated disconnect/status exchanges; native revalidation pending. No native failure was waived.

## Product credential adapters

- New native credential owner passes8 local tests with zero diagnostics: real Windows DPAPI round trips/restart/overwrite/exact removal/128KiB binary records/ciphertext corruption, plus held-call cancellation/disposal and diagnostic serialization checks. No Google grant was moved or logged. Mac Keychain and direct Linux SecretService adapters are implemented but not accepted until native CI passes. Their four-target workflow uses synthetic secrets, a private Linux D-Bus/keyring and owned daemon shutdown. Locked-store/native installer acceptance and actual account UI integration remain open. See NATIVE_CREDENTIALS.md.
- Immediate-disconnect IPC regression also found the Windows Broken state needs Disconnect even though IsConnected is false. Explicit reset now passes37 local Core tests; native matrix is running on11598ec. No production failure was hidden or waived.

Native credential run39446619902 one94259d passed all4 platforms (8tests each). Independent artifact verification remains. IPC run39446618889 caught a Linux shutdown race: after cancellation the loop tried to accept again before checking the stop token, with a connected pipe intentionally retained for disposal. Check cancellation before entering the next accept; no exception is hidden. Targeted local control checks passed6/6; native revalidation remains.

Downloaded all4 native credential artifacts for run39446619902 and independently checked sourcee94259d, report/assembly-hash syntax,8passing/0failed/0skippedTRXresults each, full restore/test/keyring logs and artifact-file hashes. No diagnostics found. Private inventory is native-vault-ci-e94259d/independent-verification.json; assemblies themselves were not distributed, so report binding is not claimed to independently hash absent assembly bytes.

Product Google connection now builds without diagnostics and passes9local synthetic tests. It owns PKCE/state/system-browser loopback cancellation, scoped native-vault token records, preserved refresh credentials, read-only stable permission-ID checks, and joined Exit. HTTP/account-mismatch failures preserve credentials; reconnection must promote a new verified pending connection rather than overwrite an existing grant. No live grant moved yet; desktop account UI and native CI remain. See GOOGLE_ACCOUNT_CONNECTION.md.

Core run39447816313 caught a Windows synthetic-fixture race: the resource limiter could stop the test child between creating its input file and flushing byte42. The source-preservation assertion remains unchanged; instead the test owner now creates/flushes the input before launching, and the child opens it read-only. Separate readiness marker proves invalid launch policies/hashes did not start the worker. All37local Core tests pass with this correction. Linux listener tests passed in39447816313; full native revalidation remains required. This did not involve real source media or a product input-write path.

## Durable account setup and native follow-up

- Native run39448589289 on67e69272d1884670431295e9342a3da24101e712 passed all4 targets: Protocol29, Core37, Google9. Downloaded all targetTRX/full restore-test logs; independently verified exact counters, zero diagnostics and file hashes. Earlier socket/disconnect/shutdown/fixture failures remain retained, corrected rather than waived.
- Candidate Processor SQLite schema2 adds pending/verified Drive account references and a durable credential cleanup journal; Android Room12 remains unchanged. Stable Google permission IDs survive reconnection. Wrong-account, duplicate-account and stale-revision attempts cannot promote; disconnect preserves account identity and unrelated saved jobs. Installation ID survives reopen. Candidate schema1 migration preserves captured import destinations and existing jobs. Local Core suite now43passing; final focused account/migration checks6passing with zero diagnostics. Native schema2 revalidation remains pending.
- Shared ProcessorStateCoordinator serializes repository calls off the UI thread and joins active state work. Program/database ownership and desktop account UI integration are the next increment.

## Desktop account setup candidate

- Desktop exposes Google connect/check/reconnect/disconnect, resumable pending setup and retryable credential cleanup. Program retains the shared SQLite coordinator and database through work/account/vault/control shutdown. Account setup I/O stays off the UI thread. Background failures retain evidence and an unsuccessful exit rather than becoming empty/ready state.
- Private Windows product acceptance reused the already authorized full-Drive grant in a new isolated Processor build state. The access token was already expired; product refresh, protected storage and read-only about identity verification passed at2026-10-20T15:41:55Z. No new consent or Drive file write occurred. Original private probe grant remains intact. This is not the real beyond-seven-day test.
- Actual Windows account UI displayed the protected account and completed Check connection, Pause/Resume and Exit. Original process289008 exited0 with0stderr bytes. A follow-up corrected the misleading Ready to connect heading after account connection. Actual corrected UI227528 showed Ready to pair your phone, close-to-background/reopen preserved the exactPID/account, and native Exit again returned0 with0stderr bytes. All private evidence retained in preflight-060/desktop-account-ui*.json/log. Direct tray-menu clicking and native installer acceptance remain unverified.
- An initial account-UI status harness used a GUI executable without explicitly waiting, so its inherited LASTEXITCODE falsely reported failure despite a successful reply. Corrected harness waits the command process and checks its actual ExitCode plus retained UI PID. This was test-driver behavior, not an MPA failure or exception waiver.
- Final local affected regression passed Credentials8, Google9, Core43 and AppServices5 with no diagnostics. Desktop and private acceptance probe strict builds have0warnings/0errors. Native matrix is extended to AppServices and acceptance-probe compilation; final current-source evidence remains required. Android still baseline0.5.33/code44/Room12; no0.6.0 APK/installers/release. Pairing/catalog/source/import/plugin/Android/installer/final acceptance work remains open.
