# Native CPU compatibility increment — passed

October 20, 2026. The prior Linux/Intel runs are **not accepted**. This increment attempts source/build fixes against ONNX Runtime revision `402979ac1bef89dcd55add41eaa7277c407599eb`; it is not a diagnostic waiver.

**Verified result:** CI run 39373514740, application revision `67607b119d7e1c6b89dcbafec67c95da72964e6c`, passed Windows x64, Ubuntu x64, Intel Mac and Apple Silicon. Downloaded evidence file hashes/source binding and complete Linux/Intel build logs independently verified. All four corrected runtime logs and both complete source build logs contain zero unresolved warnings/errors. Five source-change inventories and native append boundary assertions passed on both source-build targets; Linux GNU_STACK is RW.

This accepts the two-model/decoder compatibility increment, not the newer object/OCR candidate or final product. Peak Process memory was approximately 132 MB on Windows and 180 MB on Linux. Mac's Process API returned zero, which is an **unavailable measurement**, not zero memory; resource-budget acceptance remains open and the newer reporter makes this field nullable. No exception granted or used.

`patch_native_source.py` requires that revision and exact edit anchors, keeps upstream license text, and records original/final SHA-256 and reasons for each changed file. Upstream dependency archives remain version/hash-pinned by the runtime. Existing upstream patches run before the additional dependency fixes.

| Previous finding | Change requiring native validation |
| --- | --- |
| CPU builds defaulted to unsupported SVE/KleidiAI | Explicit supported `--no_sve --no_kleidiai` options on the x64 CPU paths. |
| Deprecated CPU-irrelevant CUDA/MSVC CMake policies | NEW policy behavior in ORT, Eigen and protobuf. This build never enables CUDA/MSVC. |
| Date's pre-3.10 compatibility | Retain its minimum but adopt policy behavior through CMake 3.28. |
| Deprecated download-only FetchContent | MakeAvailable with a non-project SOURCE_SUBDIR for mp11, SafeInt and the Mac protoc binary. No unintended dependency test project is configured. |
| Unused Dawn option without WebGPU | Generate it only when WebGPU is enabled. |
| ONNX documentation arguments/constants stripped from use | Retain operator documentation while preserving the existing lite protobuf flavor, operators and static-registration behavior. Measure resulting memory; no warning flag disabled. |
| Unused FlatBuffers Nim generator constant | Remove the unused declaration. |
| GCC range-insert overflow diagnostic | Validate input/root/capacity before arithmetic; reserve checked capacity and append bytes individually. New native regression checks cover 65 prefix sizes, exact bytes, null/truncated/root/size failures. This changes invalid-input behavior to explicit failure and needs native execution. |
| AMX object missing GNU-stack note | ELF-only non-executable-stack declaration before assembler termination. CI independently requires the linked runtime GNU_STACK flags to be RW. |
| Duplicate Apple link libraries | Current linker-capability and library-deduplication policies. |

Only the shared-runtime target is built. Earlier invocations compiled upstream unit-test executables but explicitly skipped running them. This change does not remove an executed MPA test or assertion; required model/decoder checks remain. The new append test also executes against the actual built FlatBuffers archive. Full product quality/regression testing remains later work.

The diagnostic scanner now also detects `CMake Deprecation Warning`; the former expression missed that spelling. It still rejects compiler/linker/CMake warnings and errors. No severity reduction, warning filter, baseline or exception added.

Local preparation passed Python syntax, exact-anchor application against downloaded pinned source inspection copies and diagnostic-classification checks. Subsequent native execution passed as recorded above. Model-quality, complete resource-budget and installer acceptance remain separate gates.

The native workflow paths now track native probe/tool/model inputs specifically, so unrelated authorization-source edits do not repeat a long native build. Final joint-release acceptance still requires the final immutable product revision across every target.

## Expanded three-model matrix - accepted feasibility increment

Run39380066718, source9bae0a31e70a054fb62560aa2828b077e8bc0526, completed successfully on attempt2. Windows, Linux and Apple Silicon passed on the first attempt; Intel's hosted runner lost communication and supplied no completed build log/artifact. Its same-source rerun passed. All four downloaded evidence manifests/file hashes, model/source binding and complete source-built logs were independently verified. Each target now loads YuNet, the notice-bearing normalized SFace and unchanged YOLOX, with the synthetic JPEG decoder check. Zero unresolved diagnostics.

Observed Process peaks: Windows147763200 bytes, Linux191766528 bytes. Both Mac peak values are explicitly unavailable/null, not zero. Working-set/process-tree monitoring and resource-budget acceptance remain open. This does not establish model quality, OCR/video/HEIF coverage, installer execution or the final immutable product revision.
