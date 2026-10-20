# Native CPU compatibility increment — awaiting CI

October 20, 2026. The prior Linux/Intel runs are **not accepted**. This increment attempts source/build fixes against ONNX Runtime revision `402979ac1bef89dcd55add41eaa7277c407599eb`; it is not a diagnostic waiver.

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

Local preparation passed Python syntax, exact-anchor application against downloaded pinned source inspection copies and diagnostic-classification checks. **This is not Linux/Intel compilation, inference-equivalence, memory or installer acceptance.** Complete CI logs and source-change inventories must be reviewed before recording a pass.

The native workflow paths now track native probe/tool/model inputs specifically, so unrelated authorization-source edits do not repeat a long native build. Final joint-release acceptance still requires the final immutable product revision across every target.
