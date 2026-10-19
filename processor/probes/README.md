# Native feasibility probes

These are Stage 0 experiments, not the Processor application, a recognition-quality result, or a completed release. Source originals are never inputs to the probe. The JPEG fixture is generated in memory.

The reviewed candidates are OpenCV Zoo YuNet and SFace, ONNX Runtime CPU 1.31.0 and SkiaSharp 4.153.1. SDK 10.0.303 and NuGet content hashes are locked. `models.json` fixes the upstream revision, original LFS hashes, sizes and reviewed normalized hashes. Model binaries and private OAuth configuration stay outside Git.

From the repository root:

```text
python -m venv <private-build-environment>
<environment-python> -m pip install --only-binary=:all: -r processor/probes/requirements.txt
<environment-python> processor/probes/fetch_models.py
<environment-python> processor/probes/normalize_models.py
python processor/probes/run_native.py
```

The build-only Python dependencies have exact versions. Final tool/download hash freezing remains a Stage 0 exit requirement. The installed application will not require this Python environment.

The native runner restores in locked mode, compiles with warnings as errors, runs both verified models with two intra-op threads and one inter-op thread, checks finite/nonempty outputs, verifies model files are unchanged, and performs a native JPEG encode/decode/pixel check. ONNX telemetry is disabled before sessions are created. Runtime warnings/errors fail the runner. Each invocation retains separate logs, a report and hashes under `processor/build/feasibility/`.

The original SFace export lists constant initializers as graph inputs, producing ONNX Runtime warnings. `normalize_models.py` removes only those redundant input declarations, checks ONNX validity, proves restoring the input declarations reconstructs the entire original protobuf, and enforces the reviewed derivative hash. It does not rewrite operations or weights. The derivative carries a modification notice and its upstream license.

For a controlled numerical comparison:

```text
dotnet processor/probes/NativeFeasibility/bin/Release/net10.0/NativeFeasibility.dll --compare processor/.models processor/.models/normalized <new-report-path>
```

This comparison intentionally loads the original warning-producing reference model; retain that complete reference log separately. It checks all outputs on zero input and two seeded pixel-range inputs with absolute-plus-relative tolerance `1e-5 + 1e-5 * abs(reference)`. This is not proof of face detection/identity quality. The corrected normal runtime probe must still have zero diagnostics; reference warnings are not suppressed or accepted for shipping.

The official 1.31.0 NuGet omits Intel macOS. The feasibility workflow therefore builds its native CPU library from exact upstream revision `402979ac1bef89dcd55add41eaa7277c407599eb`, with telemetry disabled, on an Intel Mac. Native execution on Windows, Linux and both Mac architectures is required before selecting the final backend. Source compilation is not a native pass.
