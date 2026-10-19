"""Strict native smoke runner. A successful process with warnings is not a pass."""
from pathlib import Path
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import uuid


class NativeProbeRunner:
    def __init__(self):
        self.root = Path(__file__).resolve().parent.parent
        self.evidence = self.root / "build" / "feasibility" / uuid.uuid4().hex
        self.evidence.mkdir(parents=True, exist_ok=False)
        self.dotnet = shutil.which("dotnet")
        if not self.dotnet:
            raise RuntimeError("Pinned .NET SDK is required")

    def command(self, name, arguments, runtime=False):
        log = self.evidence / (name + ".log")
        with log.open("wb") as output:
            result = subprocess.run(arguments, cwd=self.root, stdout=output,
                                    stderr=subprocess.STDOUT, timeout=300, check=False)
        text = log.read_text(encoding="utf-8", errors="strict")
        if result.returncode:
            raise RuntimeError(f"{name} failed ({result.returncode}); inspect {log}")
        if runtime and re.search(r"\[(?:W|E|F):|\b(?:warning|error)\b", text, re.IGNORECASE):
            raise RuntimeError(f"Unresolved runtime diagnostic; inspect {log}")
        print(name, "passed", flush=True)

    def run(self):
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=self.root,
                                           text=True, timeout=15).strip()
        dirty = subprocess.check_output(["git", "status", "--porcelain"], cwd=self.root,
                                        text=True, timeout=15).strip()
        if dirty:
            raise RuntimeError("Commit the reviewed increment before recording immutable native evidence")
        project = "probes/NativeFeasibility/NativeFeasibility.csproj"
        self.command("restore", [self.dotnet, "restore", project, "--locked-mode", "--verbosity", "minimal"])
        self.command("build", [self.dotnet, "build", project, "--no-restore", "-c", "Release", "--verbosity", "minimal"])
        native_log = self.root / "build/feasibility/native-dependency-build.log"
        if native_log.exists() and re.search(r"CMake Warning|\bwarning\s*(?::|[A-Z]+\d+:)",
                                            native_log.read_text(encoding="utf-8"), re.IGNORECASE):
            raise RuntimeError("Unresolved source-built native dependency warning; inspect full build log")
        output = self.root / "probes/NativeFeasibility/bin/Release/net10.0"
        if "MPA_ORT_NATIVE_DIRECTORY" in os.environ:
            native = Path(os.environ["MPA_ORT_NATIVE_DIRECTORY"]).resolve()
            extension = "*.dylib" if platform.system() == "Darwin" else "*.so*"
            libraries = list(native.glob("libonnxruntime" + extension))
            if not libraries:
                raise RuntimeError("Pinned source-built ONNX Runtime was not found")
            runtime_id = "osx-x64" if platform.system() == "Darwin" else "linux-x64"
            native_output = output / "runtimes" / runtime_id / "native"
            native_output.mkdir(parents=True, exist_ok=True)
            native_records = []
            for library in libraries:
                shutil.copyfile(library, output / library.name)
                shutil.copyfile(library, native_output / library.name)
                with library.open("rb") as stream:
                    native_records.append({"file": library.name, "sha256": hashlib.file_digest(stream, "sha256").hexdigest()})
            (self.evidence / "source-runtime.json").write_text(json.dumps({
                "revision": "402979ac1bef89dcd55add41eaa7277c407599eb",
                "deviceDiscovery": False, "telemetry": False, "files": native_records
            }, indent=2) + "\n", encoding="utf-8")
        elif platform.system() == "Darwin" and platform.machine() == "x86_64":
            raise RuntimeError("Intel macOS requires the pinned source-built ONNX Runtime")
        report = self.evidence / "native.json"
        self.command("runtime", [self.dotnet, str(output / "NativeFeasibility.dll"),
                                 str(self.root / ".models/normalized"), str(report)], runtime=True)
        evidence = []
        for path in sorted(self.evidence.iterdir()):
            if path.is_file():
                with path.open("rb") as stream:
                    evidence.append({"file": path.name, "sha256": hashlib.file_digest(stream, "sha256").hexdigest()})
        (self.evidence / "evidence.json").write_text(json.dumps({"sourceSha": revision, "files": evidence}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    NativeProbeRunner().run()
