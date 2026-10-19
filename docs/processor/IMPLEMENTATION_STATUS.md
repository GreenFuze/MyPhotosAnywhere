# MPA 0.6.0 implementation ledger

Updated: 2026-10-19. **Stage 0 in progress. No 0.6 application or installer is released.**

## Authority and baseline

The owner approved implementing the complete joint release unattended, reported switching to High effort, and subsequently directed emulator-first development with the S23 reserved for final tests. The historical plan-only stop in the plan documents has been satisfied. Preserve fast failure, object ownership and original-media protections.

Baseline: `6c9ead7b1d4446dc819dee71974aa7b68614f232` (0.5.33 publication revision), application source `7714d96b6f8b654c8f16e6358dd9d993356380fe`. Work branch: `feat/processor-060`. Native evidence records the exact clean committed SHA; early local experiments are explicitly provisional. No Android runtime files or schema have been changed.

## Stage ledger

| Stage | State | Evidence / remaining work |
| --- | --- | --- |
| 0: feasibility/baseline | In progress | Baseline audit; desktop OAuth identity created with actual owner approval; disposable Drive workspace; Windows model/decoder probes. OAuth production/functional/cross-client/refresh/duration proof and other native targets remain open. |
| 1: contracts | Pending | Golden codecs, causality, publication and approval contracts. |
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
- Existing OAuth project has External/Testing status, two Android clients and configured `drive.file`, `drive` and Photos Picker scopes. The owner explicitly approved creating a separate Desktop client named MPA Processor. Config is stored outside tracked files. No account grant or scope expansion has been performed by creating it.
- Production publishing is blocked by incomplete branding URLs. The owner supplied their existing website; MPA-specific homepage/privacy/terms are being prepared there for concrete review. Existing website and domain ownership verification files are preserved.
- Installed .NET SDK 10.0.303/runtime 10.0.13 works outside sandbox; the sandbox SCM error was an execution restriction, not an SDK defect.
- Separate headless API 35 development emulator booted; checksum-verified 0.5.33 baseline installed and launched there. No physical device changed. The old global emulator is development-only; final tests still require the pinned matrix.
- ONNX Runtime 1.31.0 + SkiaSharp 4.153.1 build with zero compiler/analyzer warnings/errors on Windows. Native execution runs YuNet/SFace and an in-memory JPEG round trip.
- Original SFace export emitted initializer-input warnings. A checked graph-declaration correction removes them without changing operations/weights. Both models passed all-output comparisons for three deterministic inputs; largest measured SFace absolute difference was `5.662441253662109e-6`. Corrected Windows runtime log has zero warnings/errors. This is feasibility, not recognition-quality acceptance.
- Official ONNX Runtime NuGet lacks osx-x64. The proposed native matrix builds that exact runtime source on Intel rather than loading a mismatched binary or omitting the target. That path is not yet verified.

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

1. Finish website review, authorized publishing/domain setup and OAuth Production configuration. Obtain action-time confirmation for security-sensitive access/publication changes; do not treat personal testing as a fix for seven-day Testing grants.
2. Implement bounded Drive feasibility/refresh probes, authorize exact grants explicitly, prove cross-client behavior, and start real elapsed-time observation. Full product implementation remains gated on immediate Gate A.
3. Execute native feasibility on Linux, macOS ARM64 and Intel; resolve all warnings and freeze dependency/model/license/tool evidence. Check object/OCR/video backend redistribution and runtime support too.
4. Finish signing/native-test-access inventory. Missing paid publisher signing uses the already approved personal-test path; missing native execution does not.

Do not stop at routine stages or ask for another Continue. Stop only for required human access/consent, unresolved diagnostics, safety or a material scope decision, while continuing useful independent work.
