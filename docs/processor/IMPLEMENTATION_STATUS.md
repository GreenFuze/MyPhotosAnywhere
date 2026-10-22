# MPA 0.6.0 implementation ledger

Updated: 2026-10-22. **Stage 0 validation continues; contract, core and catalog integration are in progress. No 0.6 application or installer is released.**

## Authority and baseline

The owner approved implementing the complete joint release unattended, reported switching to High effort, and subsequently directed emulator-first development with the S23 reserved for final tests. The historical plan-only stop in the plan documents has been satisfied. Preserve fast failure, object ownership and original-media protections.

Baseline: `6c9ead7b1d4446dc819dee71974aa7b68614f232` (0.5.33 publication revision), application source `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Work branch: `feat/processor-060`. Native evidence records the exact clean committed SHA; local experiments are explicitly provisional. Android has portable protocol consumers and bounded legacy XMP output; UI composition, version and Room schema remain at baseline.

## Stage ledger

| Stage | State | Evidence / remaining work |
| --- | --- | --- |
| 0: feasibility/baseline | In progress | Production OAuth, full desktop grant, fresh-process refresh, bounded synthetic Drive round trip passed. Manual post-consent source discovery, actual-expiry refresh and injected lost-completion reconciliation passed. Four-target two-model/decoder native matrix passed on 67607b1; expanded object model matrix is running on 9bae0a3. Actual cross-client/crash/multiple-account/duration and remaining codec/OCR/package gates stay open. |
| 1: contracts | In progress | Matching signatures/identity/facts-tree/source-coverage fixtures pass: 47 C# Protocol tests and 28 focused Android tests; all 537 Android unit tests and zero-findings lint passed for the coverage slice. Stable-patch revalidation and complete integration/scale acceptance remain. |
| 2: core/plugins | In progress | Local SQLite ownership, durable stage/checkpoint/receipt outbox and failed-only retry foundation under test. Resource reservations, actual process ownership/recovery, complete plugins and tray/Exit remain. |
| 3: accounts/coordination | In progress | Production protected account/refresh and authenticated loopback web host implemented. Browser LAN onboarding and Drive-only mobile enrollment/acknowledged controls remain. No phone LAN connection is permitted. |
| 4: catalog | In progress | Bounded WAL facts snapshots, verified immutable objects/tree traversal and source coverage are implemented. Publishing, approved-configuration/ancestry agreement, staged phone activation and human synchronization remain. |
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

Native current account/setup run39452233450 ond73f686 passed all4 targets: Protocol29/Core43/Google9/Setup5. Desktop+acceptance-probe build39452226434 passed all4. Downloaded reports/TRX/full logs and independently checked source/counters/zero diagnostics/file hash inventories under account-native-ci-d73f686 and account-desktop-ci-d73f686. No absent assembly-byte hashing claim.

LAN identity candidate passes7local tests: separate protected P256 TLS/document keys, no silent rekey on corruption/identity mismatch, renewal with stable pins, joined startup cancellation, actual mutual TLS and actual wrong-pin/name rejection. Windows native Schannel rejected ephemeral keys; OS-managed temporary CurrentUser key container solves that without a PFX file, PersistKeySet, Exportable or trusted-root change. Actual test proves temporary CNG container removed on disposal. Native run39454956807 on1928b54 passed all4 targets; download verification for this increment remains separate.

## Owner-approved desktop web revision, 2026-10-20

`DESKTOP_WEB_REVISION.md` now governs the desktop UI and communication changes. Desktop is a full photo client with a locally bundled React/TypeScript web UI and an independent .NET background engine. Other computers access its web UI on the same LAN. **Android communicates only with Google Drive**; all earlier phone LAN/API/pairing/discovery plans are superseded. Mobile feature settings/requests require immutable Drive delivery and applied/conflict receipts. No phone LAN permission, direct connection or fallback is authorized. Use Chrome for web acceptance; native UI automation is reserved for genuinely native behavior.

Candidate code implements the engine-owned web host, bounded read-only Drive discovery/thumbnail adapter, private browser session authority, account setup/check/reconnect/disconnect/recovery in the web interface, native Open MPA/background lifecycle wrapper and exact-version locally bundled frontend. The rich Avalonia account panel is removed. Existing Processor schema2 and Android Room12 remain unchanged; one stable local library ID is retained in existing metadata before keys are created. Closing a browser does not own/cancel durable processing work.

Local Web10 tests pass, including actual HTTPS/Kestrel with the authenticated server pin, Secure/HttpOnly/SameSite cookies, remote browser forbidden from host Google authorization, single-use/endpoint-bound invitations, monotonic expiry, CSRF, Origin, rebinding rejection, operational errors distinct from empty data, session isolation and joined request shutdown. This is transport/native-loopback acceptance, not proof of browser certificate onboarding or separate-machine LAN operation. Desktop currently starts loopback only; LAN enablement/onboarding remains unfinished. Frontend strict TypeScript/esbuild build passes; locked npm audit reports0vulnerabilities. Native matrix now includes Web tests and desktop frontend compilation; current-source matrix must still run.

The first actual Chrome gallery correctly read60real Drive entries but failed thumbnails because its adapter incorrectly assumed a `/d/` URL path. Failure remained visible in host attention/diagnostics. Correction retains the exact HTTPS `lh3.googleusercontent.com` origin, standard port and no-userinfo/fragment constraints without imposing an undocumented path shape; foreign-origin rejection remains tested. A new read-only ProductMediaAcceptance probe reused the existing approved protected product account and verified a60-entry page plus a7120-byte JPEG thumbnail at2026-10-20T18:49:02Z. No new consent, original download or Drive write; private media names/IDs/URLs are not in its report. This is not full catalog/original-viewer acceptance. Earlier failed UI evidence is retained, not waived.

Actual corrected Chrome review is pending browser automation reconnection. The corrected background engine is under a retained process-handle acceptance driver; its stderr is currently empty. Do not infer complete visible gallery/viewer success from the adapter result. Full catalog/Drive-operation enrollment, source policies, imports, plugins, all desktop photo functions, Android adaptation, LAN browser onboarding, native installers, scale/physical/final checks and joint publication remain required. No0.6APK/installers/release exists.

## Source configuration and development listener follow-up

- Committed web foundation aaf59e0ff8c60fa2927f29e0c5b31a46de714f28 passed desktop run39479597044 and contract run39479597002 on Windows/Linux/macOS ARM/Intel. Downloaded full logs/TRX; independently checked source, Protocol29/Core45/Google13/Setup5/LAN7/Web10 per target, zero diagnostics, zero npm audit findings, lock hash and agreement of four reported frontend inventories. Artifacts contain reports/logs rather than application binaries; absent assembly bytes were not independently hashed.
- Processor local schema3 adds source bindings, revision fences and an atomic canonical configuration outbox. Disable/reconnect preserves identities, captured jobs and original media. Schema2 upgrade preserves installation/library/accounts/jobs. Android Room12/product behavior remain unchanged. Local Core52/Google16/Web11 tests pass, including bounded folder reads, wrong-parent rejection, CSRF denial, malformed IDs and stale policy conflicts that preserve the usable session.
- Sources UI adds bounded folder navigation, explicit selection and source-policy switches with account-change cancellation and retained errors. Configuration is durable locally; Drive publication, indexing and actual Chrome acceptance remain unfinished. Discovery Photos is not yet the selected-source catalog.
- Owner requested localhost-only development because of repeated firewall prompts. Every development HTTP/HTTPS listener, including TLS fixtures, now binds 127.0.0.1; startup fails if actual endpoints differ. Real listener tests preserve HTTPS/session checks. No firewall rules, trusted roots or OS security settings changed. Rebuilt Windows engine observed listening only on127.0.0.1:22416 (ephemeral port); strict build0warnings/0errors and empty stderr. This does not establish remote LAN acceptance or prove no future Windows prompt.
- Chrome automation disconnected during retry. A prior tab retained old Failed to fetch content from its stopped server and is not acceptance of the latest build. Browser reconnection requested while independent implementation/checks continue. No visible-success claim or new Google consent request.

## Local discovery projection candidate

Processor schema4 adds bounded account/file identity facts, source memberships and local discovery revisions. It preserves sources/accounts/jobs and keeps AndroidRoom12 unchanged. Keyset timeline/date-seek queries retain provenance and unknown dates; stale source/account revisions fence batches, older file revisions cannot overwrite facts and same-version conflicts roll back. Incomplete scans do not delete data. This repository is not yet connected to the source scanner, portable publication or UI. See LOCAL_DISCOVERY.md.

Local Core62 passed including10,000/100,000 fixtures; subsequent2timestamp tests passed after resolving the static-member analyzer finding without suppression.100,000 preparation214.6seconds/date-seek11.9ms/max60-item page75.4ms/fullwalk17.9seconds on concurrently loaded Windows. These are provisional single observations, not full import/memory/UI acceptance.

Android run39479596925 on aaf59e0 failed API35 draggingAndReversingAcrossAllLayoutsPreservesEarlierSelection: selected identity-set assertion passed, then selected tile008 was not composed in the reduced viewport. API29 passed; API37 was canceled by fail-fast. Test-only correction aae2757 scrolls every originally asserted tile into view and keeps every selection/non-selection assertion plus database-preservation checks. Native revalidation remains required. No product selection code changed or failure waived.

Read-only source-page adapter candidate passes20Google tests (all existing plus4source cases). It limits listings to60items/512KiB, validates canonical parent/shared-drive membership, retains page cursors/incompleteSearch, isolates malformed individual metadata, rejects repeated identities and never follows shortcuts into additional source scope. It makes only identity/metadata GET requests. Runtime source scheduling/checkpoint integration is still required; no background scan or original download is claimed. Initial fixture-allocation analyzer finding was fixed with a reusable immutable fixture array; no suppression.

Catalog revision e70dda564b79d422d96c04ffb89c2ca3a2671f83 passed native contracts39487318191 and desktop39487318199 on all4 targets. Downloaded and independently verified132tests/target (Protocol29/Core64/Google16/Setup5/LAN7/Web11), complete clean logs/source bindings and both scale measurements per target. Windows contract job took about10minutes; it passed within the existing timeout. Android native39487318259 remains separate and pending. No native exception was introduced.

Processor schema5 candidate adds immutable per-item captured context to durable jobs, atomic job+initial-item admission, exact replay checks over input/context/recipe graph, and kind-scoped claiming so a metadata scanner cannot claim uploads. Empty context is supplied during upgrade without changing existing jobs/checkpoints. Existing schema1/2/3 fixtures now recreate their actual historical item columns. Local66non-scale Core tests passed; after adding replay comparisons all5focused captured-item tests passed. Final native69-test Core coverage remains required. This is scanner admission groundwork, not a running source scanner or a completed import engine.

## 2026-10-21 source and read-owner follow-up

- Source/credential revision82bee80557c4596243c611e3a28dcadfa3f89e53 passed contracts39546071800 and desktop39546071747 on all four desktop targets. Downloaded and independently verified155tests/target (Protocol29/Core78/Google20/Setup10/LAN7/Web11), complete clean restore/test/build logs, both scale measurements, zero npm audit findings and agreeing reported frontend inventories. Application binaries were not uploaded, so their reported hashes were not independently compared.
- Earlier e70 Android native39487318259 passed159tests each on API29/35/37. Independent verification matched every unique runtime start/finish identity and full log hash, plus98screenshot file hashes per API. This verifies evidence completeness, not a new blanket Android diagnostic waiver.9d Android39543721409 also passed all three APIs; its downloaded evidence verification remains separate. Android still uses the0.5.33 product runtime;0.6 activation remains open.
- Actual Chrome review briefly verified all60gallery previews plus branding loaded, viewer open/next/details with honest thumbnail status, and grouped Settings. Sources then met an expired local browser session; a fresh CLI invitation was issued without Google reauthorization. The Chrome extension subsequently became unavailable, including after a fresh automation session. No whole-PC/browser fallback or firewall/security change was used. Development listeners remain127.0.0.1only.
- Candidate SourceDiscoveryRunner now executes one bounded metadata step through the shared read owner, captures verified source scheduling, preserves usable catalog facts, records per-item/page repairs, yields durable page checkpoints, and joins cancellation/pause/Exit before returning leases. Known failed files use an exact captured-file GET; moved-out-of-scope items remain explicit operational failures, while identity substitution and internal conflicts fail visibly. Incomplete search retains healthy facts plus a retryable observation. A terminal checkpoint can finish without another network read.
- Local current AppServices19/Google24/Web11 tests pass without diagnostics. These cover healthy-folder continuation, failed-only retry, interrupted page/authorization recovery, held cancellation/Exit, concurrent pause/source changes, incomplete search, inaccessible-child recovery and invariant failure. The runner is not yet activated by a periodic engine scheduler or Sources UI; progress/recovery controls, metadata storage budgets, publication, Android consumption and all remaining joint-release work remain open. No original bytes are read or mutated by this metadata increment.

## Automatic admission and dispatcher candidate

Automatic source requests now commit an idempotent verification job before any Google call. Stable request identities include the library, account and captured source/account revisions; restart or repeated browser scheduling preserves failed/completed work. Accepted root/shared-drive verification is checkpointed before root-folder admission, so replay cannot replace it with newer network facts. No schema6 or Android persistence change is introduced.

SourceDiscoveryScheduler owns the browser-independent dispatch loop and cancellation epochs. Pause acknowledges only after the active read and durable lease have joined; Resume starts a fresh epoch. Exit joins the loop, and internal failure remains visible with an unsuccessful exit. Exclusive-startup recovery is limited to the three known metadata stages, preserves pause/cancel/checkpoints, and leaves upload/native-worker fences untouched. It must run under the composition root's exclusive state lease before new reads are admitted.

Local full Core84, AppServices26 and strict Desktop compilation pass without diagnostics. Current Google24/Web11 regressions passed in the preceding runner increment. This dispatcher is not yet instantiated in the product composition root: unified web/tray/IPC pause controls, metadata storage-budget admission, Sources activity/retry/rescan and catalog UI integration must be connected before enabling it. The complete joint release remains in development.

## Shared work controls candidate

IProcessorWorkControl gives native worker supervision and metadata dispatch a common pause/resume surface. ProcessorWorkGroup serializes web/tray/IPC control intent, starts and joins both owners even when one throws synchronously, preserves component failures, and quiesces a successful half of a failed resume. Its control gate has explicit joined disposal: active commands finish, queued commands are rejected before gate release, and no borrowed work owner is disposed implicitly. The composition root still owns actual work shutdown.

Local AppServices31 and Web12 tests pass, including five shared-control cases and a real HTTP409/session-preservation check for operational work-state conflicts. The three existing native worker-supervisor lifecycle tests also pass after the interface addition. Initial ownership analyzer finding was resolved with joined async disposal, without suppression. Product composition-root, native tray/IPC and browser integration remain pending; the dispatcher is still not enabled in the running product.
# Metadata storage admission follow-up

The full affected AppServices suite now passes36tests with strict analyzers and no compiler warnings. Five new tests cover actual state-volume measurement, exclusion of unowned files, missing/replaced state files, low-space admission, concurrent disk use after a read, and background pause/recovery/Exit. Existing scheduler/read-owner/failed-only repair tests remain intact. This storage component and scheduler attention are not yet instantiated by the product composition root; activation still requires shared web/tray/IPC controls, joined lifecycle and visible progress. No original bytes or Drive content are modified by this increment.


## Activated discovery, catalog and frozen viewer follow-up, October 21

The historical pending/runtime statements above are superseded for the implemented slices in `LOCAL_DISCOVERY.md`: the product now activates joined metadata discovery, shared storage admission, Activity/retry/rescan and a bounded selected-source gallery. Runtime revision 82b480a passed all four native targets and all three Android API jobs. Independently downloaded protocol/desktop evidence verifies 203 tests per desktop target, complete clean logs, source bindings and scale reports; absent application binaries were not independently hashed. Catalog revision b1567c1 also passed all four workflows, with its independent artifact review still pending.

The next slice adds browser-owned disposable whole-query viewer identity indexes, tracked read-only source snapshots, storage reservations and joined cleanup. Local Core 96, AppServices 41 and Web 24 tests pass, with strict Desktop/TypeScript builds free of compiler/analyzer findings. The real-account native HTTP acceptance probe passes against the isolated synthetic-source state: bounded preview, exact selected identity, same-browser reopen, cross-browser denial and sign-out/Exit cleanup. No source original is downloaded or modified. The probe is compiled, never account-executed, in CI.

Chrome reappeared without user action during the latest retry, then its automation connection dropped again when discovering the newly opened page. Current frozen-viewer visual acceptance remains unverified. The prior native GUI engine did exit 0 with empty stderr; the latest run has a retained bounded-wait process owner. The unattributed browser receiving-end diagnostic is still open. No warning is waived by these passes. Continuous gallery virtualization/anchors, full media functions, portable Drive publication/operations, Android thin activation, plugins/imports, LAN/headless/installers and final joint acceptance/publication remain unfinished. Processor schema 5 and Android Room 12 remain unchanged.


## Viewer native evidence and browser recovery

Viewer revision 47bdb2a passed all four native desktop and contract targets. Downloaded complete TRX/log/report artifacts independently verify 221 tests per target (884 total), zero failures/skips/unresolved compiler diagnostics, both scale fixtures, source bindings, lock hashes, zero npm audit findings and agreeing reported web asset inventories. Actual application binaries were absent, so their reported hashes are not independently verified. Android build passed; its initial native attempt failed before tests because Google's API 29 image download closed. The same immutable device run was retried, with no assertion/gate change; final results remain to be checked.

Chrome reconnected from Codex without a user restart. Actual review then verified immediate preview during session preparation, ready viewer/Details, returning through Settings, reopening in a second tab without breaking earlier controls and sign-out propagation to both tabs. Sign-out left zero SQLite viewer indexes. Native engine 336980 exited 0 with empty stderr and removed its generated viewer directory. A static SVG with zero scripts independently reproduced the receiving-end console record; it is outside MPA application JavaScript, with its originating browser component still unidentified. No user browser/firewall/security configuration was changed.

The next local contract candidate adds matching C#/Kotlin catalog manifest/index readers and shared malformed cases (18 commit, 21 index). C# Protocol 32 and Android JVM Protocol 14 tests pass, including real approved-actor signatures before typed parsing. Complete Desktop strict build has zero warnings/errors. All 519 Android unit tests passed with no failures/skips; lint has zero findings. The existing exact AGP deprecation exception is the only accepted Gradle diagnostic, independently checked by the unchanged gate. This does not activate Drive publication, an enrolled authority or a complete graph/chunk consumer. See `protocol/catalog-v1.md` for exact bounds and the remaining activation checks. No persisted schema or Android product behavior changes.


Existing model/codec feasibility evidence was also independently downloaded and checked: all four desktop targets on ecd4262 executed YuNet, normalized SFace and YOLOX with exact expected hashes, nonempty outputs and clean complete logs; native JPEG feasibility passed. Linux f98d083 passed its restricted FFmpeg synthetic rawvideo probe. These are native feasibility observations, not face/object quality, full media-format acceptance, installer payload verification or codec redistribution closure. The codec report explicitly retains `redistributionClosureAccepted: false`.


## Joined local-control shutdown correction

Contract run 39601550164 on dc4e20d exposed a macOS ARM null reference inside the runtime's Unix `WaitForConnectionAsync` during shutdown. The MPA owner canceled and disposed the pipe before awaiting its actual server task. It now cancels the asynchronous accept/read/write token, joins `Completion`, then disposes the transport and cancellation owner. Microsoft documents cancellation support for pipes configured with `PipeOptions.Asynchronous`, which this endpoint already uses: [WaitForConnectionAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.waitforconnectionasync?view=net-10.0). No null-reference catch or warning exception was added.

All eight focused control tests pass locally. New coverage holds a real admitted handler after cancellation and verifies that the peer does not receive premature EOF, then releases it and verifies joined closure. Forty idle accept/shutdown cycles reuse the same pipe identity after actual completion. Existing malformed/disconnected-client, partial-frame, concurrent disposal and unexpected-handler-failure assertions remain. Native macOS and complete matrix revalidation are required for the correction; the failed earlier run is retained.

The same dc4e20d Android build and all API29/35/37 device jobs passed. Its enhanced restricted Linux codec probe also passed actual synthetic H.264 frame decoding. Independent compressed-codec report review remains separate; codec redistribution, other formats/platforms and the complete joint product remain open.


## Bounded compressed catalog readers

The next increment adds Android's background catalog decoder and a typed-reference overload for the desktop decoder. Both consume independent Python gzip/hash fixtures. Sixteen shared cases cover transport/expanded hash changes, lengths, CRC corruption, a truncated footer with a matching transport reference, noncanonical/duplicate/invalid-Unicode JSON, excess entries, smaller consumer budgets and expansion beyond the declared 16 MiB ceiling. Existing tests remain intact.

The footer fixture exposed a real desktop acceptance gap: the runtime returned decoded bytes for a truncated gzip footer. Explicit complete-body CRC32/ISIZE validation now rejects that input, alongside both SHA-256 checks. It does not replace cryptographic integrity with CRC32. Android runs decoding off the main thread and propagates cancellation; neither codec mutates files or activates a catalog.

Local acceptance passes all 35 C# Protocol tests, 17 Android Protocol tests, all 522 Android unit tests and lint with zero findings. Complete Desktop strict compilation passes with zero warnings/errors. The unchanged Gradle gate accepts only the exact already approved AGP deprecation. Current-source native matrix results and full staged-generation integration remain required. No persisted schema or Android product behavior was changed by enabling these isolated codecs.

Independent H.264 evidence review also verifies run 39601550192 on dc4e20d: pinned 2,272-byte source, H.264/320x240, exactly 230,400 decoded RGB bytes, expected green center, unchanged input and clean complete logs. Actual application/codec binaries were not present in these report artifacts. Other formats/platforms, recognition quality and redistribution closure remain open.

## Originating cancellation before identity promotion

The local-control correction at 4720b6a passed all four native contract and desktop jobs,
plus Android APIs 29/35/37. Downloaded native artifacts independently verify 226 tests per
desktop target (904 total), complete clean logs and both scale fixtures. The next decoder
revision f8595a5 passed its desktop and Android build jobs, but Linux contract validation
exposed an existing identity-startup cancellation race. Its failed run 39634600821 is kept.

`LanIdentityOwner` previously checked only a linked token after a delayed vault read.
`CancelAsync` marks the originating source synchronously and dispatches callbacks
asynchronously, as documented by [Microsoft](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelasync?view=net-10.0).
A read could finish before the callback marked its linked token. The owner now checks the
original shutdown/caller sources after borrowed operations and before a protected write,
and promotes the identity under its lifetime gate only after a final cancellation check.
Unresponsive writes remain owned and joined; a completed write cannot publish a canceled
startup identity. Candidate keys and serialized private bytes retain their existing cleanup.

All nine LAN tests pass locally. A deterministic test blocks caller-cancellation delivery
to the linked token, then completes the vault read and requires cancellation without a key
write. Another holds a write through shutdown and requires joined cancellation without an
exposed identity. Existing assertions remain. Native revalidation is required; no warning,
test failure or cancellation was converted to success.

## Shared observed Drive facts

Matching C#/Kotlin `DriveCatalogFacts` readers now validate the same bounded leaf schema,
including library/account/file-derived identities, source revisions, timestamp provenance,
unknown dates and literal metadata. An independent fixture contains 38 malformed cases.
The desktop encoder sorts without modifying input records, rejects invalid Unicode and
requests a partition split before exceeding count or expanded-byte budgets; it never drops
rows to fit. Metadata observations are explicitly separate from verified original receipts.

Local acceptance passes all 39 Protocol tests, 20 Android Protocol tests and the complete
525 Android unit tests (98 suites), with zero failures/skips and zero lint findings. The
unchanged gate accepts only the already approved exact AGP deprecation. Complete Desktop
strict builds have zero warnings/errors. `protocol/catalog-v1.md` records the exact profile.
These are schema/encoder checks; complete graph/source coverage, enrolled authority, real
Drive publication, transactional activation and Android product integration remain open.

## Parent handshake socket ownership

The snapshot scale-suite run exposed an unchanged parent-lifetime test failing at peer
closure: 101 tests passed and one partial-handshake socket remained readable until its
deadline. A controlled negative replay of the added race tests against the previous
09db04b listener reproduced the same peer-closure timeout. Its log/TRX and exact source
hashes are retained; the corrected source was byte-for-byte restored afterward.

The listener now pre-owns the destination socket before asynchronous accept, using the
supported [Socket.AcceptAsync destination overload](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.acceptasync?view=net-10.0).
This removes the unowned accept-completion handoff. Acquiring the borrowed NetworkStream
also shares the shutdown gate; a replay exposed `GetStream` racing `TcpClient.Dispose`
before that second correction. Reads and acknowledgments reuse the admitted stream.
Neither a null-reference/argument catch nor a longer deadline was added.

All seven focused parent-lifetime cases pass, including 72 rapid real loopback shutdowns
with zero, one and 31 handshake bytes. They require both an unsuccessful partial handshake
and actual peer EOF/reset, and do not accept a read deadline as closure. Existing four
cases retain their assertions. Complete Core/scale and native matrix validation remain
required before publication. This private worker IPC is unrelated to phone connectivity.

## Bounded facts snapshot projection

`CatalogFactsSnapshot` now projects the real SQLite discovery data into the portable
observation models. Its owned read-only WAL snapshot captures library, discovery and
configuration revisions together. The coordinator opens it briefly; its background reader
then uses indexed identity ranges and keyset pages of at most 250 assets, with bounded
source memberships. Imports remain writable. Enabled-source/depth visibility, overlapping
folders, distinct accounts and retained disconnected metadata follow the existing gallery
rules. Disposing the main state owner while a snapshot lives still fails fast.

All 105 Core cases pass after the parent-socket correction, including four focused snapshot
tests and the existing 10k/100k fixtures extended to read every projected identity once.
The latest shared Windows test process measured approximately 0.20s/12.13s for complete
10k/100k projection walks, with maximum 250-asset page observations of 8.1ms/74.8ms.
These are concurrent-host-load observations, not UI responsiveness, compression or Drive
publication acceptance. Complete Desktop strict compilation has zero warnings/errors.
Initial SQL-separator and test-cleanup mistakes were corrected with their failed evidence
retained. No database schema or source media was changed, and no generation was activated.

## Workspace artifact read adapter

The Google connection now has a bounded, account-owned metadata workspace read operation.
It checks actual folder membership/type/download permission before fetching bytes, verifies
exact size/SHA-256, and rechecks Drive revision and scope afterward. Original images/videos,
shortcuts and Google Workspace documents are rejected before blob download. The shared
reader interface exposes the operation without creating a second credential owner.

All 31 Google adapter tests pass, including explicit expected error classes, moved/replaced
and restricted objects, corruption/truncation, unknown HTTP lengths, cancellation during
blob reading and network errors. AppServices 41 and Web 24 regressions also pass; their
preview fixtures explicitly reject unexpected workspace reads. Complete Desktop strict
compilation has zero warnings/errors. Test allocation and ValueTask cleanup analyzer
findings were fixed; no check was suppressed. These HTTP fixtures contain only synthetic
metadata and do not establish real publication, enrolled trust or active catalog staging.

Shared-facts revision 09db04b has now passed every native contract/desktop target and Android
API 29/35/37. Independently downloaded complete native artifacts verify 235 tests per target,
940 total, clean logs, both scale fixtures, lock/source bindings, zero npm audit findings and
agreeing reported web inventories. Application binaries are absent from these artifacts and
their reported hashes are not independently verified. The subsequent parent/socket and
snapshot work still requires current-revision native validation.

## Joined parent acceptance follow-up

Native run 39642356278 on 2f80af3 found the pre-owned socket correction insufficient:
macOS ARM timed out at peer closure in all three new race cases while 102 other Core
cases passed. That failed log is retained. A pending accept destination was still being
disposed before the native operation settled. Underlying runtime causality beyond that
ownership gap has not been established.

The parent now owns a cancellation source and tracks the actual accept/handshake task.
Shutdown cancels its linked operation, closes the listener/admitted client, joins acceptance,
then releases cancellation/key ownership. Only the completed accept loop closes a pending
destination. Async worker startup and failed-stop paths await this join; synchronous disposal
uses the same completion. Unexpected internal exceptions remain failures. The race tests
exercise both synchronous and concurrent asynchronous disposal without changing deadlines
or treating a peer-read timeout as closure.

Local validation passes all 15 focused parent/worker cases and the complete 105 Core cases,
including both scale fixtures, followed by a zero-warning/error Desktop rebuild. These
results supersede the prior local result but do not prove the macOS correction; a new
immutable cross-platform run is required. No diagnostic exemption was introduced.

## Actual protected-account workspace read

The manual desktop acceptance probe adds a `--workspace-read` mode restricted to the
established isolated account state, its single explicitly identified disposable source
folder and the exact pre-existing 92-byte synthetic JSON. It uses the production native
credential vault/Google adapter, starts no listener and cannot substitute arbitrary input
content. CI compiles this mode without account access.

Actual Windows acceptance passed at 2026-10-21 17:20:52 UTC with process exit 0: exact
fixture bytes/SHA-256, wrong workspace, changed size, changed digest and original-image MIME
rejections. Connection/vault/state owners joined before the sanitized report was written.
No original was downloaded, no Drive file changed, no browser/consent flow ran and no account,
file identity, URL or credential was logged. This uses the synthetic source as a transport
fixture; it does not establish managed workspace enrollment, a signed generated catalog or
the actual beyond-seven-day refresh observation, which remains pending.

## Sequential verified facts-tree traversal

Matching C#/Kotlin traversal readers now stream bounded identity partitions from a
previously approved signed manifest. Every index/chunk hash and leaf identity is checked;
object reuse/cycles, foreign workspace/account references and consumer-budget exhaustion
fail explicitly. Reads are sequential without buffered leaf prefetch. Android parsing
and checksums run off the main thread. A later failure remains a failed traversal even
after earlier valid leaves were emitted into staging; it is never empty/ready success.

Independent Python fixtures exercise 21 malformed/late-failed/over-budget graphs. Real
signature tests tamper with valid settings JSON and require rejection before object reads.
Local acceptance passes all 43 Protocol tests, 24 focused Android Protocol tests and all
529 Android unit tests in 99 suites, with zero failures/skips and zero lint findings.
Complete Desktop strict compilation has zero warnings/errors. Repeated test-array analyzer
findings were fixed through shared fields, not suppression. The unchanged Gradle gate
accepts only the previously approved exact AGP deprecation.

This completes facts-tree traversal, not complete-generation activation: coverage, signed
ancestry/authority agreement, policy fences, durable staging and the active-pointer switch
remain required. No live catalog, persisted schema or Android product behavior changed.

## API 29 startup mismatch and bounded XMP output

The native/Desktop runs for `645441e7a59140678565f3da07388a8a93b56ca2`
passed on all four desktop targets. Independently downloaded reports contain 996
passing tests (249 per target), no failed/skipped tests and no warning findings;
reported web inventories agree and npm audits report zero vulnerabilities. These
artifacts do not contain application binaries, so their binary hashes are not
independently verified. Android build passed, but API 29 device acceptance failed:
two thumbnail quality assertions returned 724 instead of 768 pixels, and optional
XMP serialization exhausted a 16 MiB runtime heap. API 35/37 were canceled.

The preserved boot log shows the 64-bit zygote starting with `-Xmx16m` before
qemu-props delivered the AVD's existing `dalvik.vm.heapsize=576m`; the 32-bit
zygote subsequently received 576m. Compiler heap arguments in the same log are
distinct from the runtime heap. The CI preparation now checks the actual primary
64-bit startup arguments. Only that exact API 29/16m-versus-576m mismatch permits
a framework restart before tests, followed by a new PID and matching heap check.
It changes neither heap configuration nor assertions, never clears logs and
rejects physical devices, other images and unexplained mismatches. Six Python
tests pass, including failed repair and physical-device rejection. The next immutable
run on `bcae057bbb3ea45282d83b9a9e8af372e977ee00` passed all three API targets.
Independent verification confirms 477 passing tests and all matching runtime start/finish
events/report hashes. API 29's actual initial runtime heap was already 576m, so no
framework restart occurred; the repair branch remains isolated-test coverage only.

XMP encoding now checks a minimum UTF-8 catalogue budget before constructing its
DOM and uses a byte-bounded serialization stream for escaped and retained external
XML. Overflow remains an actionable `IllegalArgumentException`, not a swallowed
OOM, and the existing optional-XMP import recovery contract is preserved. Fourteen
focused tests pass, including early rejection, escaping, external annotations and
supplementary Unicode. All 533 Android unit tests and zero-findings lint passed locally;
Android CI build and API 29/35/37 device suites passed on the same pushed revision.
Thumbnail memory limits and all existing quality/recovery assertions are unchanged.

## Borrowed workspace transport integration

`CatalogWorkspaceObjectSource` implements the catalog traversal's transport using the
existing engine-wide `ProcessorAccountService` reader owner. It rejects foreign or
unbounded references before borrowing a reader, carries the selected account revision,
checks actual returned object identity and rechecks connected/revision state before
returning bytes. Connection replacement or disconnection cannot promote late results.
It neither owns credentials nor starts a second connection/worker/listener.

All six focused transport tests and all 47 AppServices tests pass, including credential
retirement waiting for a held read, joined Exit, late cancellation, wrong identity,
reconnection and scope rejection. Complete Desktop compilation passes with zero warnings
and errors. The native Google object reader previously passed actual synthetic Drive
acceptance; this new application-service bridge currently has synthetic integration
coverage. Complete-generation validation, durable staging and activation remain separate.

Independent four-target verification on `bcae057` confirms 259 tests per target,
1,036 total, complete clean test/build logs, captured 10k/100k measurements and matching
reported web inventories; npm audits remain zero. These report artifacts contain no
application binaries. Device runtime diagnostics still require final 0.6 review; passing
test reports alone do not establish a warning-free release or S23 acceptance.

## Bounded source coverage and membership validation

C#/Kotlin readers now validate immutable coverage roots/pages and assemble a bounded
mapping of at most 256 sources. Captured configuration/discovery/account/source revisions,
ordered ranges, exact page bytes/hashes, complete page inclusion and unique bindings are
checked explicitly. Stale/incomplete scans cannot claim complete coverage; asset source
memberships must match current captured account/policy/depth. Retained facts from honestly
partial/offline/disconnected sources remain valid; missing observations never imply deletion.

The independent shared fixture exercises all eight states, empty and 256-source libraries,
and 44 malformed graphs/membership cases. All 47 C# Protocol tests, 28 focused Android
Protocol tests and all 537 Android unit tests across 101 suites pass. Lint contains zero
findings and strict complete Desktop compilation has zero warnings/errors. The Gradle
gate accepts only the unchanged exact AGP exception. An analyzer finding prompted a real selected-workspace check in page reading;
the new foreign-reader regression passes. No severity/suppression change was made.

This establishes portable validation, not live publication/activation. Capturing actual
current scan coverage from the same durable snapshot as facts, complete graph-role and
predecessor/authority validation, journals, dirty-prefix publication and transactionally
staged activation remain. No persisted schema or product UI behavior changed in this slice.

## Atomic current-scan selection

Processor schema 6 adds an explicit current-scan reference per source. The reference,
captured source/account revisions, job and initial work item commit in one transaction.
No timestamp, UUID comparison or row order determines the selected scan. Automatic
scheduling retains the current admitted request, including failed/paused work. A new
explicit scan selects its own job; replaying an older HTTP request returns that job's
original identity without rewinding a newer selection. Missing/corrupt selection records
fail visibly instead of resetting work or returning an empty/completed source.

Schema 5 upgrades add the new empty selection table and preserve every preexisting row,
installation identity, captured context, checkpoints and receipts. A fresh metadata-only
automatic request establishes selection without guessing the order of old jobs. Historical
work is preserved. Older-schema test setup now removes the new table when constructing
an authentic earlier schema; all existing preservation assertions remain intact.

All 112 Core tests, including seven new selection/migration/fault cases and the 10k/100k
fixtures, pass. All 47 AppServices tests, 24 Web tests and strict complete Desktop build
pass with zero compiler/analyzer warnings/errors. Native CI for this change is pending.
Android Room remains 12. Current scan selection is a prerequisite for snapshot coverage
export, which is not yet wired to publication or live generation activation.

## October 22 dependency and registry diagnostics

CI for `8194f0c` discovered newly published stable OkHttp/MockWebServer 5.5.1 after local
lint had passed against earlier repository metadata. Both exact pins are updated together.
The upstream tag and changelog identify a certificate-blocklist fix. A full fresh-dependency
Android invocation passes all 537 unit tests and zero-findings lint; the unchanged AGP
exception is the only accepted diagnostic. Immutable revalidation of this pin is pending.

The same revision's Intel macOS frontend audit failed at npm's retiring Quick Audit
endpoint; no clean audit result was assumed. An independent fresh audit of the unchanged
lockfile returned zero vulnerabilities. Retrying the failed CI job then produced a clean
four-target Desktop run. The original failure remains recorded; its preceding bulk-request
failure cause is not independently established. Audit severity, dependencies and checks
were not relaxed. Independent reports verify 1,052 native tests (263 per target) and all
four Desktop builds on `8194f0c`. No application binaries were included in these reports.

API 29/35 passed on that revision, but API 37 never reached MPA tests. The SDK CLI printed
`No url ... Ignoring` for pinned system image revision 6 while returning success. The
emulator reported a broken system path; cleanup then blocked in device-waiting logcat
until the 35-minute job cancellation. The full logs and small artifact remain preserved.
This is neither an application-test pass nor an identified MPA crash/ANR.

Google's current primary repository lists stable API 37.0/google_apis/x86_64 revision 8
(channel 0); that exact image pin replaces 6. SDK setup now verifies every requested
installed package's exact identity/revision, stable status and required nonempty payload
files and archives a metadata-hash inventory. A skipped package fails before compilation
or emulator startup. Six SDK verification tests pass and run in CI. Emulator startup also
checks image files; boot probes and failure diagnostics have bounded ADB waits. Existing
test assertions, native counts and acceptance requirements are unchanged. Actual new-image
CI execution remains pending.

## Captured coverage and scan-selection validation (2026-10-22)

The trusted push checks on `63b19e3` passed Android build/unit/lint, the four-target
Protocol/Core/Google/AppServices/LAN/Web matrix, and the four-target Desktop matrix.
Independent downloaded reports verify 270 native tests per target (1,080 total),
complete clean logs, 10,000/100,000-item fixtures and agreeing reported web inventories.
Application binaries are absent from those report artifacts; their binary hashes are
not independently verified. Independently downloaded API 29/35/37 reports and complete
runtime invocation records verify 159 passing tests per API (477 total). The captured
API 37 inventory verifies the stable revision-8 system image. API 29's actual initial
heap is 576 MiB; the conditional repair branch was not exercised. This establishes a
passing device-test matrix, not a completed runtime-warning review or S23 acceptance.

`CatalogFactsSnapshot.ReadCoverageSources` captures source coverage using the same
owned read-only WAL transaction as portable photo facts. It reads at most 256 source
bindings, then streams selected scan stages without buffering library-sized journals.
No writable discovery repository or network operation is constructed inside the snapshot.
Source selection, policy, account availability, immutable scope, recipes, item identities,
bounded checkpoints and accepted completion receipts are validated. Missing/corrupt work
fails explicitly instead of claiming empty or complete coverage. Shared journal decoding
retains the existing discovery semantics with an explicit 32-KiB input byte bound.

Current failed stages determine outstanding failures; historical folder failure counters
cannot keep an accepted repair permanently failed. Counts describe processing observations
and steps, not unique photos. Incomplete Drive searches remain partial. Disabled,
disconnected, obsolete-scope, pending, scanning, access-lost, offline, partial and complete
states retain their distinct meanings. A framed journal SHA records observation provenance;
raw pagination tokens, error messages and local paths are not exported. Facts/coverage stay
stable while live imports, retries, account changes and new scan selections continue.

Twenty-one focused snapshot tests pass locally, including failed-only repair, terminal
receipts, cancellation/disposal, policy/account changes, missing journals and the source
bound. The complete local Core suite passes 133 tests, AppServices passes 47 and Web passes 24;
Desktop compilation reports zero warnings/errors. This read-only export is not
an approved configuration, signed generation, publication, staging activation or release.

The matching C#/Kotlin covered traversal now verifies complete coverage before admitting
photo facts. Coverage pages share the complete tree's role/budget ledger; a required
configuration-admission callback receives the immutable ordered source policies before
any photo-tree read. Every leaf checks its memberships against that captured map.
An empty catalog still requires configuration admission; rejection, cancellation and late
failures remain explicit. The older facts-only traversal retains its isolated validation
semantics. Source enrollment, actual approved policy comparison, ancestry and transactional
activation remain mandatory integration work. The shared fixture has 22 invalid graphs.
All 52 C# Protocol tests and all 542 Android unit tests across 102 suites pass locally;
lint contains zero findings and Desktop compilation reports zero warnings/errors.
The unchanged gate accepts only the exact existing AGP deprecation. The first local Android
invocation lacked ANDROID_HOME and stopped before compilation; the corrected explicit-SDK
invocation produced the reported clean result.

The `63b19e3` runtime review remains open. One MPA-process resource-release warning occurs
on each of API 35 and 37 without an allocation stack; framework ownership has not been
established for these exact records. API 29's close warning belongs to com.android.phone.
API 37 also reports a Google Play services ANR and resource warnings in other system apps.
The bundled face detector prints its tensor-137 diagnostic on each API. Old version-specific
exceptions are not treated as approval for 0.6, and passing tests do not erase these records.

The instrumentation-only `MpaDiagnosticTestRunner` enables the supported
[`detectLeakedClosableObjects`](https://developer.android.com/reference/android/os/StrictMode.VmPolicy.Builder#detectLeakedClosableObjects())
VM policy before constructing the application, preserving existing VM checks and logging
violations through teardown. No hidden-API exemption or production hook is added.
The runtime-capture gate requires an early matching allocation-tracing marker for every
actual test process, including later isolated stages; missing, late and wrong-process
markers fail. Nine verifier tests pass. The test APK builds, zero-findings lint passes,
and independent aapt2 inspection confirms the actual runner/target package. Native traced
execution and allocation ownership conclusions remain pending. Explicit legacy inspection
mode applies only to archived pre-runner evidence; the CI entry point always requires tracing.

## Enrolled source-policy comparison (2026-10-22)

Matching C#/Kotlin `CatalogSourceConfiguration` readers accept only the selected library,
workspace and verified enrolled-controller document kind/capability. Bounded immutable
account/source lists require explicit account association and unique canonical bindings.
The coverage-admission callback can now compare every captured source against those exact
approved identities, revisions and enabled/recursion flags. Self-consistent coverage cannot
add, retarget or enable a source. Connectivity/credential revisions remain observed state;
this source policy does not grant recognition consent or original-mutation authority.
Actual enrollment, durable authoritative configuration selection, operation admission and
activation remain separate integration work. The independent fixture contains 30 rejected
documents, nine coverage-policy conflicts and empty/maximum sets; tests also verify an
enrolled signature and the actual combined traversal callback. All 58 C# Protocol tests,
548 Android unit tests and zero-findings lint pass locally. Release-mode Desktop compilation
also reports zero warnings/errors.

The complete `a7862d0` trusted push matrix passed. Independent native/Desktop reports verify
1,184 tests (296 per target), clean complete logs and agreeing reported web inventories.
Application binary hashes remain outside those report artifacts. Its device job passed
API 29/35/37; independent artifact/runtime verification and its warning review are pending.

## Windows desktop source-build entry points

The owner's requested root `build_desktop.bat` runs the locked frontend install/build,
dependency audit, locked .NET restore and Release build beneath the pinned SDK directory.
`build_and_run_desktop.bat` launches only after build success and forwards native arguments.
Neither changes firewall/security settings or selects a private acceptance profile.
The real build passes with zero warnings/errors from outside the repository. The launch
helper forwards `--status` and a profile path containing spaces to the real application;
an intentionally unconfigured profile returns the expected unavailable status/exit 3
without starting a listener. Both helpers propagate an injected npm failure as exit 23
and skip launching, including when the scripts' own path contains spaces. This validates
development helpers, not GUI/install acceptance. Batch line endings are explicit in Git.

## Immutable workspace-creation transport

The existing Google account owner now implements a separate publication interface with
single-ID allocation and bounded create-only metadata upload. It does not extend the
read-only connection interface or construct a second credential owner. The caller must
authorize and journal the chosen workspace/ID/payload before creation; retries never
allocate a replacement or update a conflicting existing file. Exact downloaded hash,
length, actual parent/type, before/after object revision and final workspace checks are
required after both success and conflict. Caller memory is copied before asynchronous
work. Cancellation/disposal joins the actual response. There is no original-upload,
update or delete entry point here, and no live publishing scheduler was enabled.

All 40 Google Drive tests pass locally, retaining the preceding 31 tests plus nine
publication tests for allocation, captured bytes, conflict, uncertain replies, scope/type/
hash failures, permissions, canceled/bad inputs, malformed IDs and actual response joins.
All 47 AppServices and 24 Web regressions pass; the strict Desktop Release build has
zero warnings and errors. Non-secret role/digest discovery hints use Drive properties
visible across authorized applications, without granting file access. They are not
trusted enrollment, identity or authorization evidence.
These are synthetic HTTP tests; actual Drive publication, durable outbox/enrollment,
generation activation and release acceptance remain pending.

## Durable immutable-object publication and account integration

Processor schema 7 adds a bounded metadata outbox, preserving the schema-6 state,
installation/library identities, source bindings, journals and local originals. Android
Room remains schema 12. Older migration fixtures explicitly remove the new table when
constructing historical databases; their original data-preservation assertions remain.

The journal captures library/actor/generation, account and credential revision, workspace,
configuration revision, authority digest, role, MIME and owned payload bytes. A canonical
context hash binds immutable fields to the payload hash. ID assignment precedes create
and cannot replace another allocated ID, alias a different operation in the same account
or retarget old work. Verification receipts match exact account/folder/file/hash/length.
Pending enumeration returns bounded IDs; payloads are loaded one at a time. Storage
reservations, SQLite failures and malformed records remain explicit, preserving retries.
Completed rows remain retained; no garbage collection or original deletion is added.

The uploader borrows the existing engine account lease, requires an explicit authority
validator and rechecks current account/configuration before uploads and receipt admission.
Lost replies retain allocated IDs. Concurrent callers re-read beneath that account lease
and reuse completed work. Late cancellation, disconnect, revoked authority or unexpected
transport bytes cannot confirm completion. Shutdown and credential cleanup join the
actual response. A retained receipt records an earlier verified upload, not current
remote availability or authority to activate a generation.

These components do not yet enable a web route, publishing scheduler, enrollment or
catalog-head activation. Authority is independently supplied by the future enrollment
owner; neither the journal nor Drive discovery properties create trust. Local checks
pass 141 Core, 53 AppServices and 24 Web tests, with zero Desktop Release warnings/errors.
The final receipt-fence review additionally passes all eight focused journal tests and
all 53 AppServices tests. Immutable cross-platform validation remains required.
