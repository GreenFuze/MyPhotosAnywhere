# MPA 0.6.0 implementation ledger

## Retained change-history replay through the protected account owner

Complete metadata scans now replay the retained unresolved Drive interval before document
downloads, using the same joined account owner and state-only callbacks. Every relevant
page, supported candidate and exact continuation commits atomically before the next read
or a recovery error. New metadata requires a fresh inventory; historical versions cannot
rewind current candidates. Unknown removals, access changes and unsupported metadata are
retained as sticky recovery evidence, rather than inferred safe from missing parents.

Replay batches are bounded to 128 pages and 100 events per page. Longer feeds resume at
their last atomically committed cursor; a new batch starts closed. The earliest unresolved
cursor never advances merely because transport, listing or replay succeeds. All retained
identity evidence is bounded to 4,096 entries and hash protected. Missing, corrupted,
foreign or overflowing records fail without truncation. Cancellation joins actual account
work; stale access cannot admit its late end cursor. No original is written.

Scan completion and current admission require completed replay, unchanged history and no
sticky recovery flag. Old optional-field encoding remains readable and private schema 10
is unchanged. Raw replay is not signature, predecessor, covered graph or head authority;
the general generation owner and renewed-access admission remain unfinished. Initial
policy-only bootstrap retains its narrower source mode.

All 298 Core and all 213 AppServices cases pass, including page rollback/low-storage,
restart, superseded callbacks, large-feed continuation, 4,096 identities, maximum opaque
cursors, protected-account integration and disconnect during an uncooperative read.
The strict Desktop Release build has zero warnings/errors. Complete final logs are clean;
both test suites and Desktop use the same Core binary SHA-256. Fresh exact-revision CI
remains required. No live Drive write, new device acceptance or release is claimed.

## People readiness regression found by exact-revision native CI

e8f36ae passed the complete Protocol, Desktop and Android build workflows, but its API37
native invocation failed one existing People diagnostic assertion (150/151 ordinary cases
passed). The retained semantics explicitly reported `Loading people` when the test tried
to scroll to asynchronously computed diagnostics. API35 passed; matrix fail-fast canceled
API29. That run is not accepted as passing, and no warning exception is added.

People review results are now bound to their exact observed input, so a previous/default
result cannot declare replacement data ready. Review loading has real progress and
accessibility state. The existing native test awaits the seeded Room row and the matching
rendered face count/current review before scrolling; all original diagnostic, rescan,
pause and data-preservation assertions remain intact. There are no sleeps, whole-test
retries or relaxed assertions.

Local full unit tests, lint and both debug APK builds pass the strict gate, with only the
already approved exact AGP deprecation. Fresh native checks remain required. This is a
readiness correction in the current Android baseline; the 0.6 thin-client activation and
the complete joint release remain unfinished.

## Complete signing inventory and scan admission

ProtocolSigningJournal now captures all local actors and every reserved/sealed intent
in one transaction, including idle actors and unsealed work. It validates real payloads
and signatures one at a time and retains only bounded immutable descriptions. Framed
hashing supports more than the protocol's 1,000-item array limit without relaxing that
limit. Explicit 64-actor/4,096-intent bounds reject incomplete inventories; orphaned,
corrupt or missing counter history cannot be filtered out. Original IDs, bodies, exact
envelopes, actors, counters and native keys are never replaced or advanced by inspection.
An empty journal remains insufficient proof of native ownership or remote history.

Every new actual workspace scan captures the complete signing digest before network
inspection. Completion checks it after the checkpoint and attempt writes, within the
same transaction, so concurrent reservation/sealing cannot leave a successful checkpoint.
Current completed-scan admission also checks the digest. Historical checkpoint reading
still works after later signing, while a fresh scan must reconcile the changed work.
The optional omitted field preserves older canonical attempt bytes for historical reading;
those attempts cannot complete or grant current admission without a fresh scan. Schema 10
is unchanged.

The initial setup runtime reports an actual changed signing inventory as needs-attention
and preserves the pending work. Missing initial completion over later unsealed work keeps
its explicit recovery error before any retryable scan error, rather than restarting
bootstrap. No general publication/head authority is created. Initial setup still checks
ready native ownership, exact original plans, current policies and actual cloud reads.
General failed-history replay, complete predecessor/covered graph verification, renewed
access and generation publication/activation remain unfinished.

All 291 Core cases passed before the final legacy-proof guard. Final affected-Core128
and all-AppServices204 cases pass with that guard; the Core binaries used by those suites
and the strict Desktop Release build have the same SHA-256. The build has zero warnings
or errors, and complete final logs have no unresolved diagnostics. The initial development
run caught an exception-priority regression; explicit missing-stage recovery was restored
without changing its assertion. A new synthetic orphan-row fixture was corrected to
disable foreign keys only on its corruption-injection connection. Production constraints
are unchanged. No live Drive operation, new UI/device acceptance, installer, APK or joint
release is claimed. Warning gates and existing assertions remain unchanged.

The preceding 731fb9d passed Protocol39983066115 and Desktop39983066176 on all four
native platforms, Android39983066016 and device39983065976 on APIs29/35/37. Fresh
exact-revision CI is required for this increment and the eventual joint release.

Updated: 2026-10-22. **Stage 0 validation continues; contract, core and catalog integration are in progress. No 0.6 application or installer is released.**

## Authority and baseline

The owner approved implementing the complete joint release unattended, reported switching to High effort, and subsequently directed emulator-first development with the S23 reserved for final tests. The historical plan-only stop in the plan documents has been satisfied. Preserve fast failure, object ownership and original-media protections.

Baseline: `6c9ead7b1d4446dc819dee71974aa7b68614f232` (0.5.33 publication revision), application source `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Work branch: `feat/processor-060`. Native evidence records the exact clean committed SHA; local experiments are explicitly provisional. Android has portable protocol consumers and bounded legacy XMP output; UI composition, version and Room schema remain at baseline.

## Stage ledger

| Stage | State | Evidence / remaining work |
| --- | --- | --- |
| 0: feasibility/baseline | In progress | Production OAuth, full desktop grant, fresh-process refresh, bounded synthetic Drive round trip passed. Manual post-consent source discovery, actual-expiry refresh and injected lost-completion reconciliation passed. Four-target two-model/decoder native matrix passed on 67607b1; expanded object model matrix is running on 9bae0a3. Actual cross-client/crash/multiple-account/duration and remaining codec/OCR/package gates stay open. |
| 1: contracts | In progress | Matching identity/facts-tree/coverage/source-policy/publisher/signature fixtures pass: 67 C# Protocol and 557 Android unit tests with zero-findings lint. Complete live publication/activation and operation integration remain. |
| 2: core/plugins | In progress | Local SQLite ownership, durable stage/checkpoint/receipt outbox and failed-only retry foundation under test. Resource reservations, actual process ownership/recovery, complete plugins and tray/Exit remain. |
| 3: accounts/coordination | In progress | Production protected account/refresh, native signing ownership, authenticated loopback web host and reviewed local workspace setup implemented. Cloud library discovery, browser LAN onboarding and Drive-only mobile enrollment/acknowledged controls remain. No phone LAN connection is permitted. |
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

The journal captures library/actor/actor epoch/generation, account and credential revision, workspace,
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

## Shared signed-file envelope

C# and Kotlin now package the same canonical document and detached ES256 signature in
one bounded immutable Drive file. The total 256 KiB limit includes signature/envelope
overhead. Readers require a separately enrolled actor and verify its library, actor,
epoch and allowed document kind. Self-asserted keys, unsigned files, duplicate or extra
fields, noncanonical encodings and changed payloads cannot authorize a document.

The shared fixture uses an independent Python cryptography P-256 signature and retains
only synthetic public material. It includes 15 invalid/unsupported envelopes; matching
tests also check wrong enrolled scopes and the total-file boundary. The first C# boundary
test setup exceeded the existing per-string limit before exercising the envelope. The
corrected fixture distributes its padding across eight bounded strings without relaxing
any limit. All 62 C# Protocol and 552 Android unit tests pass; Android lint has zero
findings. Both initial fixture-setup failures remain in the local diagnostic history.
No enrollment, live publisher, background route or catalog activation is enabled.

## Controller-approved publisher policies

Matching C#/Kotlin readers verify a signed publisher policy against a separately enrolled
controller. The selected library/workspace cannot be changed by the received file. A
publisher has a separate actor, key and epoch and can sign catalog commits only; it cannot
use that role to authorize human operations or changes to originals. Explicit revocation
returns no publisher. The owner still must persist current policy selection, reject forks
and prevent replay of an earlier active policy; pure decoding cannot choose current state.

Independent synthetic controller/publisher signatures cover active approval, revocation,
a catalog genesis commit, and a denied user operation. Shared vectors include 26 signed
invalid/overbroad policies. All 66 C# Protocol and 556 Android unit tests pass; lint has
zero findings and Desktop Release compilation has zero warnings/errors. No live enrollment
or publishing is enabled.

## Durable local controller and publisher selection

The local authority repository records one independently approved controller/workspace
public-key pin, then verifies and atomically retains signed publisher-policy history.
Bootstrap is reserved for trusted setup after native private-key persistence; received
Drive files cannot bootstrap or replace that root. A missing/corrupt root, policy hash,
history record or selected sequence fails explicitly. Selection must equal the highest
accepted publisher sequence, so rewinding a pointer cannot reactivate a revoked key.
Delayed/repeated earlier policies return the current selection; conflicting bytes at the
same sequence preserve agreed state and report a conflict. Duplicate accepted replays do
not require a new storage reservation.

All 149 Core, 53 AppServices and 24 Web tests pass locally, including eight new tests for
restart, revocation, delayed/repeated policies, root replacement, corrupt/missing state,
low storage and failed SQLite writes. Desktop Release compilation has zero warnings/errors.
Processor schema remains 7; the public authority records use existing local metadata.
Originals, jobs and human captions remain preserved. Native-key ownership, bootstrap UI,
source-policy selection, cross-kind controller counters and generation activation remain
pending; this repository does not enable publishing.

If a newly observed revocation cannot be persisted, the repository throws and retains
last agreed state. The generation/authority owner must halt publishing until that pending
policy is reconciled and saved; it must not continue using an older policy after such an
error. This owner integration remains required before live publishing is enabled.

The signed-file revision's first macOS ARM build failed because npm's fallback audit
endpoint returned HTTP 400. The unchanged lockfile independently audited with zero
vulnerabilities, and the failed/canceled jobs passed on one retry at the same application
revision. Original failure evidence is retained; no audit check or dependency was changed.

## Native library signing-key ownership

A dedicated owner now keeps separate controller and publisher P-256 keys and actor IDs
in the existing native credential vault. Public IDs/SPKI/key fingerprints are exported;
private material uses a bounded binary record, with transient PKCS8/vault buffers zeroed
after use. New keys become available only after the real vault write and exact read-back
complete. Existing records retain actors and epochs across restart. Missing pinned keys,
wrong pins, malformed records or another library/installation fail without replacing or
deleting saved keys. Role checks bind signatures to the owner's library/actor/epoch;
publisher keys cannot sign controller or user-operation documents.

Initialization, cancellation and Exit join the actual vault operation, including a store
that ignores cancellation. A failure after persistence is treated as uncertain; restart
reads the saved identity rather than minting a replacement. Nine new tests include
blocked cancellation delivery, unavailable read-back, missing pinned keys, uncertain
writes, role isolation and secret-buffer cleanup. All 62 AppServices and 24 Web tests
pass; strict Desktop Release compilation has zero warnings/errors. The vault exchanges
in these tests are synthetic; actual setup/native-store and cross-platform acceptance
remain required. No production bootstrap route or publisher scheduler is enabled.

The setup owner must independently approve/pin the public controller and bind the chosen
publisher policy. Signing callers still must reserve durable sequences, reconcile cloud
history after restore and journal exact signed envelopes before publication; this private
key owner does not authorize a counter reset or activation of a generation.

## Screenshot completion repair after API 37 evidence rejection

Authority revision `41e2fef` passed all 159 API 37 test assertions and complete runtime
event/tracing verification, but correctly failed screenshot acceptance. One Takeout RTL
PNG had four complete IDAT chunks and no IEND. Its 32,877-byte device file and both host
copies had identical hashes, establishing that transport did not introduce truncation.
The original encoder/provider cause is not proven; all original failure evidence remains.

The test helper now encodes the single captured bitmap fully in bounded memory before
creating its MediaStore artifact, checks PNG completion, writes a pending row through one
owned descriptor, flushes/syncs/closes it, reads back exact bytes and then publishes it.
It neither captures a replacement UI state nor retries assertions. Existing UI assertions
and the full independent PNG/CRC/pixel-stream verifier remain unchanged. Local test-APK
compilation and lint pass with zero findings; all eight existing Python PNG/transport tests
pass. Their first local invocation was blocked by sandbox temporary-directory permissions;
using a task-owned workspace temp directory passes without changing test code. Full native
matrix validation of the repaired capture path remains required before acceptance.

## Canonical Unicode envelopes and durable signing intents

Revision `d5114c3` subsequently passed all four push workflows, including the complete
device matrix. Independent artifact verification is in progress; a green workflow is
not a claim that every runtime warning has been resolved.

Both envelope implementations now preserve canonical body bytes during assembly and
extraction. A shared independently signed Unicode fixture reproduces intermediate
serializer expansion that rejected valid bounded wire data. Local validation passes
67 Protocol tests, 557 Android unit tests and zero-findings lint without increasing any
wire/string/nesting limit.

Processor schema 8 adds signing actors and captured document intents; Android Room
remains 12. Fresh independently approved controller bootstrap registers its separate
publisher in the same transaction. The journal reserves canonical body/identity/sequence
before native signing and seals exact signature bytes before Drive allocation. Replays
retain their sequence and envelope; changed bytes, missing counters, corrupt history or
unknown keys fail instead of resetting or replacing identities. Controller document
kinds share one counter while the publisher has a separate stream. Low-space and SQL
failures roll back both document and counter. All 160 local Core tests pass, including
11 new restart, role, corruption and rollback cases. The first focused run exposed an
absent-versus-empty nullable buffer defect; the fix retains the original assertions.

This journal does not activate publication. The generation owner still must reconcile
cloud high-water marks and restored state, ensure current approved configuration and
publisher policy (including pending revocations), verify dependencies/predecessors,
serialize catalog heads and join real Drive work. Existing schema-7 trust records with
no signing history cannot silently acquire reset counters. No live setup or publisher
route is enabled by this increment.

The native signing boundary now combines the borrowed key owner and serial journal.
It requires an already pinned matching controller, reserves before signing and verifies
retained sealed replays against the actual native public identity. Concurrent requests
for one intent retain its first envelope; cancellation after reservation leaves the
same unsealed intent available for retry. Six integration tests exercise real P-256
signatures with a synthetic vault, including uncertain seals and restart. All 68
AppServices and 24 Web tests pass; strict Desktop Release compilation has zero warnings
and errors. Scheduling, cloud reconciliation and independent setup approval remain
separate responsibilities.

Downloaded `d5114c3` artifacts independently verify 1,400 native tests (350 per desktop
target), four clean Desktop build/audit reports and all 477 device tests with complete
traced runtime capture and valid PNG evidence. Artifacts contain reports rather than
release binaries; no independent binary-signature claim is made. Revision `0bcb814`
also passed all four push workflows; independent artifact inspection remains pending.

## Host-owned workspace setup

The desktop runtime now owns the controller/publisher key owner alongside its existing
account/vault owners. Startup verifies protected persistence; shutdown joins browser
requests before retiring keys and vault. Host-only authenticated/CSRF-protected endpoints
provide read-only folder review and explicit local workspace approval. The React Settings
screen reuses the bounded Drive picker, explains source association and shared-folder
privacy, and keeps approval distinct from the pending first shared catalog.

Approval rechecks actual folder identity, read/write capability, Drive revision, native
controller fingerprint, connected account revision and selected-source revision before
atomic pin/counter bootstrap. Source folders cannot become their own metadata workspace.
Canonical My Drive/shared-drive root IDs are rejected as well as the root alias. The
picker also disables root selection. Shared-drive items correctly require shared-folder
approval when Google's documented `shared` field is omitted; missing My Drive sharing
state is not guessed private. See the [Drive files reference](https://developers.google.com/workspace/drive/api/reference/rest/v3/files).

Local checks pass all 45 Google, 74 AppServices and 25 Web tests, TypeScript compilation,
zero-vulnerability web audit and strict Desktop Release compilation with zero warnings
or errors. Tests retain assertions for canonical roots, permission denial, shared-folder
acknowledgment, source/account races, cancellation, host-only access and operational-error
recovery. An initial test fixture used the wrong protected credential format; it now uses
the established credential-store fixture. A root-selection exception was initially caught
as malformed data; the corrected operational type preserves the explicit recoverable
error. No assertion or warning check was weakened.

Chrome review against the authorized isolated Windows profile confirms native startup,
real Drive browsing, rejection of a selected photo source, and disabled root selection.
The final valid-folder review also passes against real Drive, with approval disabled
until explicit source association and no workspace approved during the review.
No Drive file was changed or new workspace approved during these checks. Complete cloud
library discovery/enrollment, managed-workspace traversal exclusion, signed publication,
restore/high-water reconciliation and phone activation remain mandatory before enabling
the first shared catalog. This local setup screen is not the completed joint release.

Fresh local approval additionally requires a positively verified empty workspace. A
one-item bounded child query checks actual membership; nonempty, incomplete or continued
listings are never guessed empty. Malformed results fail explicitly. Existing approved
library replays remain valid after publication makes their workspace nonempty. Unit
coverage now passes all 46 Google, 75 AppServices and 25 Web tests; frontend and strict
Desktop compilation pass. A no-write native acceptance run against the exact authorized
synthetic source also verifies actual permission/revision/contents inspection and the
existing hash/size/parent/original-MIME rejection checks. All owners joined, no listener
started, no original was downloaded, and no Drive file changed. This local diagnostic is
provisional until tied to the final immutable release revision. Existing-library discovery
and source-consent persistence remain prerequisites for complete setup/publication.

## Durable initial source association

Explicit workspace setup now captures the original reviewed source IDs, account/folder
bindings, flags, revisions and shared-metadata acknowledgment in the same transaction as
controller/actor bootstrap. Its canonical snapshot and digest are immutable and survive
restart. Display labels and credential-connected state are excluded: reconnecting an
account neither grants new source permission nor invalidates that existing association.
Missing approval is not inferred from a pinned key or connected account. A source change
requires its separately reviewed later policy; setup replay cannot replace initial consent.

Readers validate canonical bytes, digest, controller/workspace scope, identities, ordering
and bounds. Missing/corrupt proof or a lost controller fails rather than recapturing current
settings. A storage reservation remains owned through actual commit. A failed source-proof
checkpoint rolls back root, actors and approval together. Processor schema remains 8 and
Android Room 12; no source or original is modified.

All 166 Core, 75 AppServices and 25 Web tests pass; strict Desktop Release compilation
has zero warnings/errors. All 14 authority cases also pass after the reservation-lifetime
review. These checks include restart, corruption, atomic rollback, low-storage replay,
source/privacy changes and preservation across disconnect. The private initial snapshot
is not a remote signature, recognition consent or permission to mutate originals. Signed
source-policy publication, subsequent reviewed operations, cloud reconciliation and
managed-workspace traversal exclusion still must be connected before catalog activation.

## Managed workspace discovery boundary

Source admission now verifies actual parent identities through its captured account and
rejects the pinned metadata workspace or its descendants. IDs, not names, define the
boundary across accounts. Ancestor cycles, changed identities and exceeded bounds fail
explicitly. Network reads remain outside state transactions, are joined on cancellation,
and a changed workspace requires fresh source admission before committing.

Discovery filters that exact workspace from ordinary pages and failed-item repairs.
Already queued work is checked before reading and again after the real read returns,
so concurrent setup cannot insert generated previews into the photo catalog. Exclusion
has its own durable receipt. If the reserved folder already has photo observations,
processing fails with those facts preserved for reconciliation rather than calling the
folder empty or deleting earlier originals. Unrelated failed originals remain retryable.

Local validation passes 169 Core, 84 AppServices and 25 Web tests, plus all three ancestry
cases after a test-fixture-only bound adjustment. Strict Desktop Release compilation has
zero warnings/errors. Processor schema 8 and Android Room 12 are unchanged. Push checks
for the preceding initial-source-association revision `453f4ec` all passed; intentionally
skipped same-repository pull-request duplicates are not counted as validation. Cloud
enrollment, reviewed later source policies, publication snapshots and high-water/restore
reconciliation remain prerequisites for the first shared catalog and completed release.

## Signed source policy acceptance and initial signing

Source configuration now has an exact signed-envelope journal and selected pointer under
the independently pinned controller. Restart verifies retained bytes, signature, scope
and sequence. Delayed records retain the selected newer configuration; same-sequence
equivocation, revision rollback and conflicting same-revision updates are rejected.
Publisher and source documents cannot reuse a controller sequence. A lost controller or
damaged source pointer/hash is an explicit invariant failure, not fresh enrollment.

Initial outgoing source signing reads the original approval and checks current settings
under the serial state owner before reserving or replaying its signing intent. Changed
sources cannot silently become the first approved policy. Account disconnect preserves
the proof and exact sealed envelope; it does not grant Drive write access. Source-policy
acceptance itself neither edits local source bindings nor uploads/activates a catalog.
Later human-reviewed policy changes and cloud reconciliation remain separate work.

All 177 Core, 86 AppServices and 25 Web tests pass, including eight new authority cases
and two initial-signing cases. Strict Desktop Release compilation has zero warnings and
errors. Low-storage admission, failed envelope/hash and failed selection-pointer writes
preserve the prior selection and roll back partial history. The first focused test had
an incorrect expectation for an unsupported capability; the corrected exact exception
assertion preserves rejection. Two strict compiler/analyzer findings in new test code
were fixed without suppressions. Processor schema remains 8 and Android Room 12.

Before catalog publication, the coverage reader still needs explicit handling and
regression coverage for intentional metadata-workspace exclusion receipts from the same
WAL snapshot. No catalog publication or activation route is enabled by this increment.

## Workspace exclusions in captured coverage

The coverage reader now recognizes intentional exclusion receipts against the pinned
controller captured in its own WAL transaction. It cannot consult newer writable state
to reinterpret an earlier observation. Common root decoding verifies the same canonical
pin, digest, library and public-key identity. Lost/corrupt authority fails explicitly.

Excluded repairs do not add a photo observation. Receipt source/folder/reason must match
their captured context and the actual reserved workspace. A folder repair additionally
requires its zero-observation parent checkpoint. Previously completed workspace reads
that observed originals block publication with those facts preserved for reconciliation.

All 186 Core, 86 AppServices and 25 Web tests pass, including nine new coverage cases
and end-to-end coverage assertions in four existing discovery/repair tests. Earlier WAL
observations remain stable while setup and work finish. Altered receipts, missing pins,
corrupt hashes and overlap with original observations remain blocking. Strict Desktop
Release compilation has zero warnings/errors. An initial new fixture used a nonexistent
claim method; it was corrected to the established `ClaimNext` API without changing the
assertions. Processor schema 8 and Android Room 12 remain unchanged.

## Frozen catalog preparation and joined lifecycle

Catalog preparation now copies the selected facts and coverage from one live WAL
observation into a private bounded-cache SQLite file, then releases that observation
before its awaited consumer begins compression or network work. Canonical per-photo
facts retain identity and a checked digest. Paging stays bounded at 250 records; all
identities, membership, dates and coverage remain fixed during later source/import changes.
No original content or protected credentials enter the generated copy.

A dedicated marked/leased directory permits two private captures, verifies its complete
bounded recovery inventory before removing stale files, and refuses unmarked directories,
links or unknown files. Storage is reserved conservatively before writing and retained
until the generated file actually closes and is removed. Cancellation, low storage,
corruption and cleanup uncertainty have explicit failure paths and retained owners.

The engine-lifetime preparation service serializes captures and awaited consumers.
Desktop shutdown cancels and joins it before account/vault/state release. Its tests keep
an actual consumer running after cancellation and verify that files remain owned until
the consumer joins. Other cases prove real WAL release, subsequent captures after source
changes, safe recovery, corruption rejection and idempotent cleanup. Publication scheduling,
authority/high-water reconciliation and activation remain separate prerequisites; no
new publication route is exposed by this increment.

Local validation passes 197 Core, 90 AppServices and 25 Web tests and strict Desktop
Release compilation with zero warnings/errors. Compiler/analyzer findings in new test
code and explicit throws inside cleanup clauses were resolved without suppressions;
cleanup preserves primary failures and independently retains unclosed resources. The
initial corruption test exposed inconsistent exception classification; the implementation
now reports invalid stored facts explicitly while preserving rejection assertions.

The 10,000/100,000-photo fixtures measured approximately 665/14,298 ms to capture,
12/11 ms to read the first page, and 8/26 ms for an import write interleaved with capture.
Their generated metadata files were 6,385,664/63,819,776 bytes. Cumulative managed allocation
was approximately 187 MB/1.87 GB; after-GC retained managed memory increased by 10,552 bytes
in each shared-test-process observation. These loaded-host measurements establish private
metadata preparation behavior. Native UI, compression/network responsiveness and dedicated
process measurements remain part of final acceptance. Processor schema 8 and Room 12 are
unchanged. All three preceding `5c7c13e` device jobs report success; GitHub's run-level
status was still stale/in-progress at the last observation and is not counted as a
completed passing workflow.

## Bounded dependency preparation

The frozen catalog now supplies indexed scalar partition bounds before materializing a
candidate chunk. The partitioner emits at most 1,000 records per chunk, applies the
consumer's 16 MiB expanded/4 MiB compressed limits and splits dense partitions through
the existing four byte-prefix levels. A capped pooled compression buffer includes gzip
framing in the bound. Exhausted depth or object budget fails without truncating photos.

Canonical index and coverage writers feed a generation dependency builder. It compares
every captured source binding and revision with the accepted signed configuration before
any sink call, validates coverage pages against their captured root, independently
decodes generated compressed facts and checks each source membership. Nested indexes
reference exact captured metadata objects; scope, digest/size, payload mutation, duplicate
role IDs and predecessor aliasing are rejected. Cancellation joins actual sink work.

The builder returns dependency references only. It creates no signed commit, library
discovery pointer or active generation. Its production sink still must persist exact
outbox scope/bytes and verify actual Drive ownership under current authority. High-water,
restore, current revocation/source policy and predecessor admission remain mandatory
before enabling live publication or phone activation.

All 104 AppServices and 25 Web tests pass, plus strict Desktop Release compilation with
zero warnings/errors. Fourteen new cases cover count/compression splits, exact decoder
round trips, empty roots, exhausted capacity/cancellation, reference rejection and full
nested graphs. The complete traversal case uses 1,001 photos forced into one initial
partition and 18 sources across two coverage pages. An isolated signed traversal fixture
does not assert production publisher authorization. Initial fixture capability ordering
and strict analyzer findings were fixed without weakening checks. Core's preceding full
197-test run passed; the new scalar partition query is exercised by these service cases.
Immutable validation for the complete increment is pending; schemas remain unchanged.

## API 29 pre-test identity diagnostic

The `e46e4b0` Desktop, Protocol and Android build workflows passed. Its device matrix
stopped before MPA installation/instrumentation: the API 29 heap checker rejected the
unrecorded `pidof zygote64` response. Fail-fast canceled the other two device jobs. The
downloaded diagnostic log shows zygote PID 1687 starting with `-Xzygote` and `-Xmx576m`.
It does not establish the raw identity response or the exact reason for its rejection.
The expected AGP deprecation was separately accepted by its unchanged exact warning gate.

The checker now saves raw named-process IDs, the PID/PPID/name inventory and complete
runtime log before identity admission. It requires exactly one init-owned (`PPID=1`)
64-bit zygote also present in the current named inventory, then checks that actual PID's
complete startup arguments. Non-primary processes sharing a transient name do not define
the runtime heap. Missing/ambiguous/malformed evidence still fails. The existing physical
device guard, configured heap, known-mismatch-only restart and app assertions are intact.

All nine Python checker tests pass, including the six existing cases. Actual emulator
validation of this change is pending. This is no exception or passing native-test claim;
the final immutable revision still requires the complete API 29/35/37 acceptance matrix.

## Current local publication-policy fence

A read-only transaction now captures the pinned controller, current signed publisher and
source policies, exact local source settings and connected workspace account revision.
Its canonical authority digest includes both selected signed-policy hashes/sequences and
actor/key/workspace/configuration identities. Assertions reject a captured scope after
revocation, source changes, account disconnect/reconnect, altered identities or corruption.
Reconnection changes the required access revision while preserving human source authority.

All 202 Core, 104 AppServices and 25 Web tests pass; strict Desktop Release compilation
has zero warnings/errors. Five new authority cases preserve rejected/corrupt state and
prove that the read-only fence works without a writable storage budget. Processor schema
8 and Room 12 remain unchanged.

This is only the durable local policy check. It does not establish native publisher-key
possession, a completed cloud reconciliation/high-water proof, an observation barrier for
policies that could not be persisted, a verified predecessor or exclusive head admission.
It cannot be used as a stand-alone production publication authority. Those prerequisites
and the immutable pending-upload access-reconciliation path must be connected before
enabling live generation publication, scheduling or phone activation.

## Bounded read-only workspace document discovery

The existing protected Google connection now lists ordinary JSON candidates in a captured
workspace through bounded 100-item/512 KiB pages, retaining continuations and incomplete
state. It does not use names or `mpaRole` properties as authority. Candidates require
actual parent membership, downloadable JSON type, canonical version/size and the separate
256 KiB signed-document bound. Reading one candidate rechecks its captured version/type,
exact byte length and parent/revision before and after the actual joined download.
Malformed, duplicate, moved, trashed or denied candidates remain explicit errors.

Candidate bytes remain untrusted: signature/library verification, complete change-cursor
reconciliation and high-water admission are still required. These read operations mutate
no source and confer no enrollment/publication authority. The shared workspace property
parser retains existing original/shortcut/Google-Doc rejection for immutable reads and
create-only publication verification.

All 53 Google, 104 AppServices and 25 Web tests pass; strict Desktop Release and native
acceptance-probe compilation have zero warnings/errors. Seven new Google cases cover
bounded inventory, real scope/revision checks, stale/malformed/denied metadata, input
admission and shutdown joining an actual held inventory read. An initial target-typed
return could not infer its async generic; its explicit page type fixes compilation.

A native no-write probe against the already authorized exact 92-byte synthetic fixture
also verifies actual inventory, candidate transport, stale-version rejection and existing
hash/size/parent/original-MIME rejection. All protected owners joined. No listener, new
consent, original download or Drive write occurred. This dirty-tree local diagnostic is
not final immutable release acceptance or signature/enrollment proof. Schema 8 and Room
12 remain unchanged.

The `b728101` native device workflow passed all three APIs. Downloaded API 29 evidence
records one init-owned zygote PID 1687 matching its named inventory and actual startup
`-Xmx576m`, with no framework restart. It does not recover the earlier `e46e4b0` raw
identity response or prove that earlier failure's exact cause. Independent full-artifact
inspection and final-revision acceptance remain separate requirements.

## Account shutdown result-admission race

The f2e4444 protocol matrix exposed a real ownership race on macOS Intel: the held
workspace inventory request could finish after DisposeAsync began but before asynchronous
linked cancellation reached that request. The unchanged shutdown regression expected
cancellation and failed (52/53 Google cases passed); this is not an accepted diagnostic.
GoogleAccountConnection now admits completed results under the same gate that starts
shutdown, checks cancellation there, and still joins the actual operation before disposing
its native owners. Once stopping wins that gate, a late transport success is canceled.
The existing inventory and change-feed shutdown assertions remain intact.

Local strict regression after this fix passes 58 Google, 104 AppServices and 25 Web
tests; Desktop Release has zero warnings/errors. Cross-platform CI must verify the fix.

## Scoped Drive change-feed transport

The existing account-owned connection now supports bounded account/shared-drive change
cursors and explicit partial/end markers. Tokens remain opaque and scoped; invalid or
foreign cursors fail before network work. Changes preserve removals, corpus/access loss,
current parent membership, MIME, version and optional size/downloadability. This is an
account feed, not a folder-filtered or atomic snapshot. An observation never instructs
MPA to delete an original. No cursor is persisted by this transport.

All 58 Google, 104 AppServices and 25 Web tests pass locally; Desktop Release and the
native acceptance probe compile with zero warnings/errors. Five new Google methods test
scoping/encoding, removals, malformed/missing/ambiguous markers, canceled actual request
joining and response disposal. No checks or warning severities were reduced.

The protected-account native no-write probe also obtains an actual start cursor for its
captured account/drive, then repeats the pinned synthetic metadata read and integrity
rejections. It does not enumerate the account's change history or log its opaque token.
All owners join; no listener, new consent, original download or Drive write occurs. This
local dirty-tree diagnostic is not final release, signature or publication acceptance.

Transport semantics follow Google's primary changes/getStartPageToken, changes/list and
changes reference documentation. Complete workspace reconciliation, signed authority,
high-water and durable cursor/observation admission remain separate required steps.

## Change-bracketed workspace inventory and account integration

WorkspaceDocumentInventoryReader captures its account/shared-drive cursor before listing
bounded ordinary JSON candidates, then drains the intervening feed. A relevant changed
ID, new JSON candidate, missing corpus/workspace access, incomplete search, repeated
continuation or unsupported bound rejects this observation explicitly. Both previous
IDs and current folder membership matter, including moved, trashed, removed and
MIME-changed documents. Missing previous IDs are retained separately and never reset
accepted policies or counters. A later change check brackets candidate verification.
Limits are 4,096 candidates/previous IDs and 128 pages per traversal; exceeding them
requires reconciliation rather than truncation. Empty/partial pages never prove authority.

CatalogWorkspaceDocumentSource runs those operations and bounded versioned document
reads through the existing account owner. Captured access revisions are checked before
reads and current connected state again afterward. It rejects late disconnected,
reconnected, canceled or substituted results. ProcessorDriveReaders now serializes
result admission against its own shutdown gate as well, retaining actual joined cleanup.
These classes expose no route, scheduling, signed-head or publication authority. Their
inventories are neither an atomic Drive snapshot nor signed evidence; durable policy,
observation/high-water, native-key and predecessor admission remain required.

All 116 AppServices and 25 Web cases pass, including twelve new inventory/account tests.
Desktop Release and native probe builds have zero warnings/errors. Tests cover known-ID
loss/moves, new policies, scope replacement, inventory/feed loops and bounds, original
non-download, held operations, cancellation, account replacement and credential cleanup.
The first focused compilation found CA1861 allocations in new tests; static fixtures
resolve them without suppression. Existing assertions and strict diagnostics remain.

The protected native no-write probe now brackets the same pinned synthetic workspace
inventory with actual change-feed reads, verifies candidate bytes/version and prior
scope/integrity rejections, then performs a final change check. It logs no account-feed
contents or opaque cursor. All owners join; no listener, new consent, original download
or Drive write occurs. Evidence: workspace-inventory-*.log/TRX, local-results.json and
actual-workspace-inventory-native.json under the private preflight evidence directory.
This dirty-tree diagnostic is not final immutable release acceptance. Schemas 8/12 remain.

## Native possession and retained cloud signing-history fences

The ready native signing owner now compares both controller and publisher public roles,
keys/epochs, library and pinned workspace against records captured from local authority.
Uninitialized/stopped keys, substituted publisher/controller roles or workspace identities
fail explicitly. This check does not mint signatures, write keys or infer cloud authority.

ProtocolSigningJournal can now verify one cloud envelope against an already registered
local actor and require the exact corresponding sealed intent. A valid remote sequence
beyond the complete local journal, or an observed locally unsealed intent, requires
explicit recovery; counters are never advanced, recreated or sealed from Drive alone.
Conflicting bodies or distinct valid signatures for the same body cannot replace the
retained immutable envelope. Invalid signatures, foreign roles and incomplete/corrupt
local histories fail before accepting an observation. This read-only check also works
without free write space and does not change the database schema.

Five new Core cases and two native-owner cases pass. Full strict regression passes
207 Core, 118 AppServices and 25 Web tests; Desktop Release has zero warnings/errors.
The signature variation case deterministically uses the distinct valid ECDSA (r,n-s)
form, verifies it, then requires the outbox's exact original envelope. No random retry,
conditional test skip, warning suppression or automatic history repair is used.

These are components of publication admission, not a complete permission grant. The
owner must reconcile ALL cloud history under the independently pinned current policies,
retain failed-observation barriers, verify predecessor/dependencies and exclusively admit
one head before live scheduling/publication. Empty/subset inventories cannot prove that
work safe. No live publication route is enabled. Processor schema 8 and Room 12 remain.

## Durable dependency outbox adapter

CatalogGenerationWorkspaceSink now connects the bounded dependency builder to the
existing immutable publication journal/uploader and joined account reader. It owns exact
payload bytes before awaiting, reserves a deterministic generation/scope/role/content
identity before Drive allocation, and reuses the retained payload/file identity after a
lost completion reply or a new adapter instance. Dependency roles/MIME, canonical JSON,
wire byte limits and complete captured scope are validated explicitly. This adapter
cannot publish catalog commits/controller policies, mutate an original or activate a head.

A stored upload receipt proves only an earlier write. Every reference returned by this
adapter requires a fresh actual workspace read and byte/hash check, plus current authority
and local access fences afterward. Missing/moved/changed remote objects remain errors;
the adapter never silently recreates a receipt's missing object or returns an old receipt
as current availability. Revocation during readback, late cancellation and changed caller
buffers cannot produce an accepted dependency reference. Original bytes are untouched.

All 125 AppServices tests pass, with seven new adapter cases covering durable pre-create
ordering, exact lost-reply retry, readback on every reuse, deleted/altered remote state,
revocation, role/generation isolation, payload ownership, held cancellation and wire bounds.
The preceding full Core207/Web25 and strict Desktop Release checks pass; Desktop has zero
warnings/errors. Test-only signed/catalog authority remains explicitly synthetic.

No production authority is fabricated and no live route/scheduler is enabled. Full cloud
reconciliation, failed-observation/history barriers, durable exclusive generation/head
ownership, immutable pending-work reauthorization, predecessor validation and signed
commit admission still precede live use. Reconnection must not remint or retarget captured
work. The adapter does not claim those unfinished generation-owner responsibilities.
Processor schema8 and Room12 remain unchanged. Local evidence: catalog-workspace-sink-*
logs/TRX under preflight-060; these are development checks rather than final-release proof.

## Canonical outgoing catalog commit bodies

CatalogPublicationWriter now serializes a bounded canonical commit payload and validates
it against the same existing typed field/role/history rules, without constructing an
unverified object disguised as VerifiedProtocolDocument. Signed incoming admission still
requires its original kind/capability checks. Existing malformed golden assertions remain
unchanged. The outgoing writer additionally rejects the Drive root alias as an object.

CatalogCommitDocumentWriter captures an immutable plan for the journaled signer's bounded
body factory. It checks publisher/library/epoch, exact approved source policy digest and
revision, workspace, prepared dependencies, genesis/parent shape and minimum history.
Canonical body creation precedes reservation; the existing journal reserves the exact
sequence/body before native signing and replays the sealed envelope exactly. A changed
plan cannot reuse the same intent, and invalid context/parent shape cannot burn a counter.
This is serialization, not current authority, remote ancestry or head activation proof.

Full local strict regression passes 69 Protocol, 207 Core, 127 AppServices and 25 Web tests.
Four new methods cover golden byte equivalence, malformed/reference/history cases, actual
journal/native signing and exact replay, scope/configuration mismatch and unburned failed
reservations. The first CA1822 finding was resolved by capturing the plan in an OOD owner;
no suppression/check removal was used. Desktop Release has zero warnings/errors. Evidence
is catalog-commit-writer-*.log/TRX in preflight-060. Processor schema8/Room12 are unchanged.

Pushed 3d72d3a completed Desktop39836408286, Android39836408404, Protocol39836408231 and
Device39836408179 workflows successfully; all three device APIs passed. The Intel native
job row remained stale/in_progress after every step including Complete job succeeded;
its downloaded six reports independently verify all465 tests and both shutdown regressions,
with no log diagnostics. This resolves the actual earlier f2e4444 cancellation-admission
regression without waiving it. The reports do not constitute final0.6 binary acceptance.

Live publication, controller policy delivery, exclusive generation/head ownership,
cloud reconciliation/history/observation persistence and pending-work reauthorization are
still required before wiring these components into production scheduling or activation.
No production permission is inferred from a constructed payload or a stored upload receipt.

## Windows rejected-TLS fixture teardown ordering

The 952c6b1 Protocol workflow failed only in its Windows LAN suite: the rejected-peer
fixture disposed NetworkStream before the canceled server authentication task joined.
Schannel's pending read threw ObjectDisposedException from that local teardown race.
The fixture now cancels and joins authentication before scoped socket disposal; its
wrong-pin/wrong-name rejection and unauthenticated-client assertions remain unchanged.
No exception type was added to the accepted outcomes. All nine local strict LAN tests
pass. Desktop, Android build and all API29/35/37 workflows for952c6b1 passed; cross-platform
Protocol verification of this fixture correction is pending. No warning waiver is used.

## Durable catalog capture and retained snapshot recovery

Processor schema9 adds one exclusive durable catalog capture per library, fixing the
original publication scope, snapshot ID and signing-intent ID before capture. Atomic
migration preserves prior jobs, authority, accounts and signing history; Android Room12
is unchanged. Captures cannot adopt prior uploads or release their claims after signing
or outbox work begins. Frozen files survive Exit and verify their context, coverage,
row count, byte length and whole-file digest before reuse. Interrupted copies remain
claimed; recovery deletes only files proved to belong to abandoned unpublished work,
after validating the entire bounded marked inventory. Unknown files remain untouched.

The retained directory is included in actual storage accounting. Native readers retain
the directory lease until joined; accounting tolerates only a concurrently completed
recognized SQLite temporary journal. No user media path is accepted by this storage.

Local strict evidence: 228 Core tests passed before the last cleanup/accounting cases;
the final affected authority, retention and migration selection passed59 tests. All128
AppServices and25 Web tests passed; Desktop Release has zero warnings/errors. Logs/TRX
are generation-retention-* in preflight-060. Cross-platform verification of this source
is pending. This is capture infrastructure, not complete generation/head scheduling,
cloud reconciliation, renewed-access admission or production publication permission.

The preceding fc39232 Windows TLS teardown correction also passed Protocol39919491932
on all four native targets, Android39919491983 and all three API jobs39919491966. No new
warning exception was required. Final0.6 installers, APK and release remain pending.

## Joined journaled-capture application owner and macOS lease path

CatalogGenerationCaptureService now serializes capture/resumption, fixes identities
through the durable journal, recovers a complete copy after a lost seal checkpoint,
and refuses to overwrite partial files. Only its reopened frozen reader reaches the
awaited consumer. It checks the supplied external authority and current local source/
publisher scope before admission, sealing and use, and after consumption. Exit cancels
and joins consumers and native file work before releasing retained directory owners;
cleanup failures latch the owner closed. This service is not wired to a production
scheduler or route and does not substitute for cloud reconciliation/head admission.

Local strict regression:59 affected Core/migration tests,135 complete AppServices tests,
then8 final lifecycle tests (including a local-policy-change rejection) pass. Desktop
Release has zero warnings/errors. Evidence: generation-owner-*.log/TRX in preflight-060.
Initial analyzer findings were fixed by forwarding cancellation and reusing a constant
account array; no suppression or check reduction was used.

Cross-platform CI for5fde308 found a genuine macOS path-alias defect: SQLite resolved
/var through /private/var, so comparing its returned path rejected the correctly leased
state directory. ProcessorStateDatabase now retains its actual constructor lease path;
the retained store compares against that trusted path, preserving exact-directory and
installation-marker checks. All nine macOS failures shared this cause. The correction
requires a fresh full native matrix; the failed run is not counted as passing.

## Durable workspace observations and native controller policy recovery

Processor schema 10 adds bounded document observations and inventory checkpoints. The
journal retains file identities, versions, byte counts and digests; an immutable file
rewrite, older version, missing known document, stale access or foreign workspace
cannot silently replace that history. Cursor advancement requires the complete exact
observed inventory. The checkpoint is historical evidence, never publication admission.
Migration failure preserves schema 9 and capture/history rows; prior upgrade fixtures now
also remove the schema 10 additions when constructing their genuine older schemas.
Android Room 12 is unchanged.

WorkspaceObservationService performs a full joined scan on every invocation, persists
each actual read before invoking its signature/history/policy verifier, and checks Drive
changes again before committing the cursor. A failed state/policy write or final check
keeps the prior cursor. New policies are still observed before reporting missing older
files. Exit cancels and joins even an uncooperative reader before account/state retirement.
No stored cursor bypasses startup reconciliation and no production scheduler is enabled.

LocalControllerPolicyObserver connects actual native identity and exact sealed signing
history to monotonic source/publisher selection. Valid cloud work beyond the local counter
or with an unsealed local intent requires explicit recovery; it cannot adopt/reset the
counter. Revocation remains selected when an older authorization is replayed. Retired
access and failed policy writes preserve the earlier accepted state. This observer handles
this installation's controller policies only; catalog ancestry and general enrollment are
separate unfinished responsibilities.

The 4096-document inventory digest streams length-framed canonical records, retaining the
shared protocol's 1000-item array rejection. A 32768-byte checkpoint cap supports valid
4096-character opaque cursors including non-ASCII text. Local final checks pass 68 affected
Core/upgrade/migration tests and all 146 AppServices tests, including real native-key and
journaled policy cases. Earlier full Core 236 and Web 25 regressions passed; the final Core
selection adds the two new bound cases and rechecks changed code. Desktop Release reports
zero warnings/errors. Final logs contain no diagnostics. Evidence is workspace-observation-*
in preflight-060. Initial analyzer findings were corrected without suppressions.

Preceding 2fe7a02 completed Protocol 39926369077 on Windows/Linux/ARM+Intel macOS,
Desktop 39926369021, Android 39926369123 and all API 29/35/37 jobs 39926369099 successfully.
Fresh cross-platform validation of schema 10/current observations is required after push.
Full cloud/head reconciliation, exclusive complete generation phases, pending-work access
attempts, production publication/activation, plugins/client workflows/installers and final
joint release acceptance remain pending. No final 0.6 APK, installer or release exists.

## Synchronous catalog retirement and result admission

The 816f36f native matrix found a real result-admission race on Intel macOS:
CatalogPreparationService could return success after Exit began because CancelAsync's
linked-token callback had not propagated before the consumer resumed. The failure was
not waived. Preparation, retained capture and workspace observation now check their
synchronous owner retirement flags before accepting results or dispatching later policy
work, while still joining actual consumers/readers before releasing borrowed resources.
The observation owner also rejects late reads before persisting or invoking a verifier.

Existing cancellation assertions remain unchanged. Both cooperative and deliberately
uncooperative catalog consumers are covered. All 20 local affected lifecycle cases pass;
Web passes 25 cases. The final integrated working-tree suite also passed all 151
AppServices cases and Desktop Release has zero warnings/errors. Evidence is
catalog-retirement-final and initial-policy-services-full logs/TRX in preflight-060.
New immutable-revision cross-platform verification is required. No diagnostic exception,
exception-type relaxation or test removal was introduced.

## Fixed initial controller-policy plan and revocation-safe signing

Initial setup now persists both publisher/source signing-intent identities before any
signature. The plan captures the pinned controller/workspace, registered native publisher
and original human source-approval digest. A lost reply/restart returns that exact plan.
Missing plan state with retained signing work or nonzero counters cannot create a fresh
setup. Partial/corrupt plan writes fail explicitly and source changes preserve the old
plan while blocking signing. The bounded singleton uses the existing authority metadata
store; Processor schema 10 and Android Room 12 are unchanged.

JournaledProtocolSigner can prepare this plan and sign its fixed initial native publisher
authorization. Initial source signing also enforces its fixed intent when a plan exists.
Both paths check current human approval, exact registered native identities and accepted
policy history before counter reservation or sealed replay. A newer publisher revocation
or source policy retires both old setup paths, including when the source payload remains
unchanged. Historical plan inspection never reauthorizes a publisher. Exact sealed-history
restoration/upload remains a separate recovery permission; these signing methods grant no
Drive upload, original-mutation or complete cloud/head admission.

All 69 local authority tests pass, including eight initial-plan cases. All 17 journaled
signer tests pass; the integrated AppServices suite passes 151 tests. The final 20 lifecycle
cases and Web 25 tests pass, and Desktop Release has zero warnings/errors. Logs/TRX are
initial-policy-* and catalog-retirement-final in preflight-060. No warning suppression,
new diagnostic waiver or weakened assertion was introduced. These changes and df7cde0's
Intel macOS shutdown-race fix require fresh native/platform CI before acceptance.

## Immutable initial policy upload context

Initial policy publication now captures the original scope/generation, both object IDs,
fixed signing intents and exact sealed envelope digests before outbox preparation. It
requires the original human source approval, locally selected initial policies and real
sealed signing history in one serial state transaction. A restart returns the same
context; reconnecting retains its old account revision without granting renewed access.
Missing context over existing outbox work cannot allocate a replacement plan. Failed or
corrupt context writes preserve both policies and existing work for explicit recovery.

The signing journal's transaction-aware integrity reader is now internal so this owner
reuses actual envelope/body/signature validation within its transaction rather than
copying a partial SQL check. Historical context inspection remains separate from current
native/cloud admission. Processor schema 10 and Android Room 12 are unchanged.

Local strict coverage passes 98 authority/outbox/signing cases, including five new upload
context cases; all 154 AppServices and 25 Web tests pass. Desktop Release is clean. Fresh
reviewed artifact names avoid the local test runner's prior overwrite-report diagnostic;
no warning was suppressed or accepted. Evidence is initial-outbox-*-reviewed in
preflight-060. One new development test initially compared memory owners; it now verifies
retained IDs/scope/hash and actual payload bytes. Cross-platform checks remain required.

## Native initial policy to outbox preparation

InitialLibraryPublicationPreparation now connects the actual fixed native signing plan to
local signature-checked policy selection, the captured transport plan and both exact
outbox records. It performs no network writes and cannot activate a catalog. Concurrent
or lost-reply retries reuse the same two signing intents/object IDs. A failed second
record resumes only that missing object. Changed account access remains blocked by the
current local scope fence; it does not silently adopt a new revision. Callers still join
preparation before retiring the borrowed native key/state owners.

The create-only outbox now supports the explicit publisher-authorization metadata role.
The facts dependency sink continues to reject publisher/source policies, commits and
originals; its existing role-separation test passes. Actual upload admission remains
closed until the production bootstrap/history owner is implemented. No HTTP route or
scheduler was enabled and no Drive files were written by this increment.

All 21 focused signing/initial-preparation/role-separation tests pass; the reviewed full
AppServices suite passes 154 cases. Reviewed Core authority/outbox/signing coverage passes
98 cases, Web passes 25 and Desktop Release has zero warnings/errors. The preceding
09a6e5a revision passed all four native runners in Protocol39932564342 and all device APIs
in39932564427; Desktop39932564472 also passed. Fresh current-source CI is still required.

The upload authority contract now explicitly states that validation runs inside the
joined account lease. Production admission must reconcile before entering that lease
and validate its run-owned proof/current local state inside it. Re-entering account
network reconciliation from ValidateAsync would deadlock and is not a valid design.

## Joined initial policy publication and current cloud admission

InitialLibraryPublicationService now drives the actual native signer, retained outbox,
account-owned folder inspection, full initial-workspace scan and create-only uploader.
Its private admission capability permits one joined upload and performs only local/native
validation inside the account lease. Actual cloud reconciliation happens outside that
lease before each write and after every receipt. Concurrent retries, lost replies and
interrupted post-write observations retain the original policy/object identities.

Initial scans enumerate every child without a MIME/name/property authority filter.
Unsupported originals, nested folders or other metadata roles stop bootstrap before their
content is downloaded. This strict scan is separate from general JSON document discovery.
Its change-feed fence also includes non-JSON children; ordinary discovery retains its
existing scope. Folder permissions, sharing approval and shared-drive context are checked
again before the final change fence/checkpoint. A verified object that was never observed
before a crash still cannot disappear and be recreated as a fresh workspace.

Actual local-controller observations are persisted and selected before initial-only
classification, so an observed newer revocation closes this owner without restoring old
permission. An unknown copy cannot be adopted even if its sealed bytes match. Complete
outbox enumeration includes verified work and fails rather than returning a truncated
subset. Reconnected access remains blocked without a separate renewed-access proof.

Exit/caller cancellation joins uncooperative uploads and final folder inspections before
borrowed keys/accounts/state may retire. Late results cannot admit receipts/checkpoints.
All 173 AppServices and 60 GoogleDrive transport tests pass locally. New tests exercise
actual account serialization, native keys/signatures, SQLite journals and simulated Drive
storage; they do not claim a live Drive publication or a physical-device result.
Affected Core authority/outbox/signing coverage passes 99 cases, all 25 Web cases pass,
and the Desktop Release build has zero warnings/errors. Full final logs and actual TRX
counters are recorded in preflight-060/initial-owner-validation.json. The initial local
development run exposed a synthetic transport that incorrectly rejected an exact retry;
the fixture now preserves create-only replay semantics and every original assertion.

This owner deliberately handles only the two initial policies. It is not yet connected
to a production route/scheduler and does not publish a generation or activate a catalog.
General history/head reconciliation, reconnect recovery, durable setup scheduling and
the rest of the joint release remain unfinished. Processor schema 10 and Android Room 12
are unchanged. The preceding 285811f revision passed native Protocol39936601289, Android
39936601321, Desktop39936601222 and device39936601180; fresh current-source CI is required.

## Durable initial publication completion

The joined native owner now records an immutable historical completion receipt after
both exact policies were uploaded, read back and included in the final current scan.
One SQLite transaction validates the fixed plan, sealed signing history, original human
source approval, verified outbox records and retained observations/checkpoint before
writing the receipt and its digest. It reuses the actual transaction-aware integrity
readers rather than copying partial SQL checks. Processor schema 10 is unchanged.

Subsequent setup stages can read this receipt after later source policies, revocations,
disconnects or additional catalog work. That is historical evidence, not current cloud
availability, head activation or renewed-access authority. Later observations do not
replace the original receipt. Missing/corrupt completion over later outbox or even
unsealed signing work stops bootstrap reconstruction. Lost approval, signing/outbox or
observation records and checkpoint revision reuse cannot be treated as a new setup.

All 108 affected Core authority/signing/outbox/observation cases pass, including nine
completion cases. Native application coverage also verifies that a failed completion
write retains both uploaded objects and resumes without reuploading them. Low storage
and interrupted receipt/hash writes leave no half-completed stage.

## Engine-owned setup scheduling and web progress

DesktopWebRuntime now owns InitialLibraryPublicationRuntime, resumes durable approved
setup at startup and joins it before retiring accounts, keys, vault or state. A committed
workspace approval queues the job independently of its browser request. Closing the
browser or canceling a progress read cannot cancel or duplicate the admitted work.
Retry joins the previous attempt, including the short interval after an error becomes
visible but before that task completes. Startup uses historical completion only to
advance this initial stage, not to grant current catalog publication authority.

Operational failures retain exact work and show safe actionable progress. Expired Google
authorization, locked credential storage, changed source authority, interrupted scans and
storage failures remain separate from unexpected invariants. An invariant reports to the
engine failure owner and cannot be automatically restarted. Full internal diagnostics
remain outside browser status responses. No original file is modified by setup.

The host-only authenticated API exposes bounded status and a CSRF-protected retry.
Remote browser sessions cannot inspect or mutate this setup. Settings shows actual
running/completed/attention/stopped progress, permits reviewing a pinned workspace that
still lacks explicit source association, and offers read-refresh after a status failure.
It explicitly distinguishes saved Drive setup from the still-pending shared photo catalog.
Progress reads stop on navigation; the engine job continues independently.

Frozen local coverage passes all 184 AppServices and 26 Web cases, 108 affected Core
cases, and four frontend API tests. TypeScript/bundling and Desktop/probe Release builds
pass with zero warnings/errors. The frontend build now runs the API tests in CI. The
read-only HTTP acceptance probe refuses a pinned-controller state before starting an
engine that could publish approved setup automatically. It was built, not executed against
Google Drive in this increment. New UI visual review and live setup publication remain
unverified; no account credentials, real Drive files or physical devices were used here.

## Partial-page history and typed metadata discovery

The actual joined account inventory now reports its start before listing and persists
each valid bounded page before requesting another page or draining changes. A later page
failure, incomplete-search flag or change-fence failure retains earlier candidates. Both
initial and final change fences retain newly seen supported metadata before closing the
scan. No signature callback or download precedes the required durable page writes.

StartInventory retains the earliest unresolved cursor across retries and even completed
listings. Completing a listing cannot erase failed history. Optional null cursor fields
preserve the previous canonical attempt encoding; the private attempt bound accounts for
three separately bounded opaque cursors without changing any signed envelope or array cap.
General history admission still must independently reconcile that retained history.

The Google account transport now provides unfiltered typed metadata discovery. JSON keeps
its 256 KiB limit; gzip and octet-stream candidates use the separate 4 MiB compressed-object
limit. Membership, exact size, MIME, Drive revision and download permission remain checked
before and after reads. Originals, folders, shortcuts and Google documents are rejected
without content download or mutation. Names and properties never select trusted roles.

CatalogWorkspaceDocumentSource supports an explicit allMetadata mode, mutually exclusive
with initial bootstrap. The shared reader and durable observation service retain encoding
as part of immutable evidence. Encoding changes conflict; binary observations cannot become
sealed JSON policy receipts. Null stored MIME preserves legacy canonical JSON bytes. Initial
publication continues using its strict initial-only scope and original account revision.

These reads do not establish valid gzip, signatures, complete graphs, current authorization
or an active catalog. Full failed-history, pending-object and predecessor reconciliation,
renewed access and complete generation publication remain required. The current 4,096-object
inventory bound is unchanged; general multi-generation retention still needs reconciliation
with the protocol graph's separate object budget. Processor schema 10 and Android Room 12
remain unchanged. No live Drive or new UI/device acceptance is claimed by this increment.

## Worker failure ownership after resource termination

Protocol CI for 37a572f exposed an existing shutdown race on Linux: a resource-limit
failure terminated its child, then a truncated pipe frame replaced that original failure
when the supervisor awaited its task group. The original exact exception assertion stayed
blocking; no rerun, exception waiver or relaxed assertion was used to accept that result.

FramedWorkerProcess now retains every observed fault, joins all owned tasks and propagates
the first observed failure through Completion and the frame consumer. Secondary diagnostics
remain available through its failure record after retirement. A pipe fault caused by owned
termination cannot replace the resource failure; an original malformed-frame failure still
remains a protocol failure. Existing release/join/input-lease behavior is preserved.

A real child fixture leaves a flushed partial frame before its lifetime stop. Regression
coverage requires the exact resource exception, retained truncated-frame diagnostic, joined
process, consistent frame-consumer error and intact/released input lease. The initial nine
worker cases pass locally; strengthened final full suites and fresh platform CI remain
required. No release acceptance is claimed from the earlier failed matrix.

## Complete pending-publication observations

Each scan now captures a complete bounded local outbox description, including unallocated,
uncertain allocated and previously verified objects. Original scopes, IDs and payload
digests remain immutable. Descriptions are framed and hashed individually; payloads are
validated one at a time without retaining the entire outbox in memory. Truncated, foreign,
aliased or unsupported work cannot silently disappear from reconciliation.

Allocated IDs participate in the change fence even before a successful read. An uncertain
allocation may be absent until retry; a verified receipt remains required even if no earlier
scan observed it. Actual bytes, encoding, size and minimum verified version must match the
captured work. Conflicting reads are retained before rejection. The scan completion and
unchanged-outbox assertion share one transaction, including rollback if a write trigger
changes a receipt. Newly admitted work survives an invalidated scan.

A changed outbox is a typed operational retry condition. Corrupted immutable descriptions
remain invariant failures. The actual setup runtime reports the operational distinction and
retries using the same captured allocation and signing plan. Inspecting older scopes through
renewed access never changes those scopes or grants renewed upload authority.

The integration passed the full 281 Core cases before the final exception classification,
then all 102 affected Core cases and all 203 AppServices cases with the final classification.
The strict final Desktop Release build has zero warnings/errors and complete logs are clean.
The preceding b915a82 worker correction passed Protocol39978807541, Android39978807619,
Desktop39978807933 and device39978807825; skipped duplicate runs are not passes. Fresh
exact-revision checks remain required for this increment. Processor schema 10 and Android
Room 12 are unchanged. General signed history/predecessor verification, generation publication,
renewed-access admission and the remaining joint release work are still unfinished.

The preceding 5d5d2ef passed Protocol39946912329 on all native platforms, Android39946912239,
Desktop39946912280 and device39946912278 on all three API levels. Fresh final-source CI is
required. Processor schema 10 and Android Room 12 are unchanged. General current-history,
head and pending-work reconciliation, renewed-access recovery, complete generation
publication/activation, processing/plugins, desktop/mobile workflows, LAN onboarding,
installers and exact tested joint release are still unfinished.

## Durable observation failure fencing and unread candidates

WorkspaceObservationService now commits an incomplete attempt before network work.
Starting another scan invalidates earlier completed evidence even if no new bytes are
successfully read. After a complete bounded inventory returns, it retains every listed
candidate identity/version/size atomically before downloading the first document. A failed
download cannot make an unread policy disappear from the next scan's known identities.
Candidate records and their framed inventory digest commit together, without expanding
the protocol's array limit or changing Processor schema 10.

Only the exact current attempt may complete. Its completion fence and actual final
checkpoint are one transaction. Failed writes, cancellation and restart retain any incomplete
work, known documents and the earlier historical checkpoint. Superseded callers cannot
replace a newer owner's completion. A digest mismatch, missing candidate, regressed
version, rewritten size, retired access or changed workspace closes reconciliation.

The actual initial-policy publication owner checks this fence before allocation/create
admission and again before accepting a receipt. Initial completion also requires the
current completed fence; it cannot reuse an older checkpoint after a newer failed scan.
Existing historical completion receipts remain readable without becoming current authority.
No network reconciliation runs inside the upload authority callback or account lease.

All 270 Core cases and all 190 AppServices cases pass against the frozen final source.
The final Desktop Release build has zero warnings/errors; complete final logs have no
unresolved diagnostics. Coverage includes unread candidate recovery, 4,096 identities,
atomic candidate/hash/checkpoint writes, stale concurrent scans and interrupted uploads
before create or after remote create. Both upload cases recover the originally allocated
identity without accepting a late receipt. No assertions or warning gates were weakened.

The preceding b47b7a8 passed Protocol39962637760 on all four native platforms,
Android39962639392, Desktop39962637709 and device39962637736 on APIs29/35/37.
Fresh exact-revision CI remains required for this increment and the final joint release.

This covers the existing bounded JSON document scan and initial setup. Full mixed-role
JSON/gzip inventory, partial-page history, general failed-observation reconciliation,
predecessor verification, renewed-access admission and complete catalog publication remain
unfinished. A stored completed attempt cannot authorize a new process or skip startup
reconciliation. No live Drive write or new device/UI acceptance is claimed here.
