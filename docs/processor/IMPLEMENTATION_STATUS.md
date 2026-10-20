# MPA 0.6.0 implementation ledger

Updated: 2026-10-20. **Stage 0 validation continues; Stage 1 contracts are in progress. No 0.6 application or installer is released.**

## Authority and baseline

The owner approved implementing the complete joint release unattended, reported switching to High effort, and subsequently directed emulator-first development with the S23 reserved for final tests. The historical plan-only stop in the plan documents has been satisfied. Preserve fast failure, object ownership and original-media protections.

Baseline: `6c9ead7b1d4446dc819dee71974aa7b68614f232` (0.5.33 publication revision), application source `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Work branch: `feat/processor-060`. Native evidence records the exact clean committed SHA; early local experiments are explicitly provisional. Only isolated protocol classes/tests have been added to Android; no UI behavior, version or Room schema has changed.

## Stage ledger

| Stage | State | Evidence / remaining work |
| --- | --- | --- |
| 0: feasibility/baseline | In progress | Production OAuth, full desktop grant, fresh-process refresh, bounded synthetic Drive round trip passed. Manual post-consent source discovery, actual-expiry refresh and injected lost-completion reconciliation passed. Four-target two-model/decoder native matrix passed on 67607b1; expanded object model matrix is running on 9bae0a3. Actual cross-client/crash/multiple-account/duration and remaining codec/OCR/package gates stay open. |
| 1: contracts | In progress | Shared Kotlin/C# canonical JSON and Drive identity goldens passed; public-key signature interoperability and bounded causal field contracts under test. Publication, alias and approval contracts remain. |
| 2: core/plugins | Pending | Durable jobs, resources, worker ownership, tray/Exit. |
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
