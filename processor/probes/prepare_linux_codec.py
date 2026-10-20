"""Verify the pinned publisher signature and extract only the Linux assembler tools."""
from pathlib import Path
import hashlib
import os
import re
import shutil
import signal
import subprocess
import sys
import uuid
from fetch_ocr_assets import OcrAssetFetcher


class CodecPreparation:
    def run(self):
        if sys.platform != "linux":
            raise RuntimeError("Linux preparation requires a Linux runner")
        probes = Path(__file__).resolve().parent
        self.root = probes.parent / "build" / ("codec-preparation-" + uuid.uuid4().hex)
        self.root.mkdir(parents=True, exist_ok=False)
        self.root.chmod(0o700)
        fetch = OcrAssetFetcher("codec-assets.json", "codec-assets", {"ffmpeg.org", "www.nasm.us"})
        fetch.run()
        key = self.root / "publisher.gpg"
        self.command("dearmor", ["gpg", "--batch", "--no-options", "--homedir", str(self.root),
                                "--output", str(key), "--dearmor", str(fetch.root / "ffmpeg-devel.asc")])
        signature = self.command("signature", ["gpgv", "--homedir", str(self.root), "--keyring", str(key), "--status-fd", "1",
            str(fetch.root / "ffmpeg-9.0.2.tar.xz.asc"), str(fetch.root / "ffmpeg-9.0.2.tar.xz")])
        valid = re.findall(r"^\[GNUPG:\] VALIDSIG ([0-9A-F]+) .* ([0-9A-F]+)$", signature, re.MULTILINE)
        expected = fetch.manifest["publisherSigningFingerprint"]
        if valid != [(expected, expected)]:
            raise RuntimeError("Publisher signature fingerprint does not match the frozen identity")
        extractor = shutil.which("7z") or shutil.which("7zz")
        if not extractor:
            raise RuntimeError("A supported 7-Zip extractor must already be installed on this runner")
        payload = self.root / "payload"
        tools = self.root / "tools"
        self.command("rpm", [extractor, "x", str(fetch.root / "nasm-3.02-0.fc43.x86_64.rpm"), "-o" + str(payload), "-y"])
        entries = list(payload.iterdir())
        if len(entries) != 1 or entries[0].is_symlink() or not entries[0].is_file():
            raise RuntimeError("Unexpected official RPM payload")
        archive = entries[0]
        with archive.open("rb") as source:
            magic = source.read(4)
        if magic == bytes.fromhex("28b52ffd"):
            # 7-Zip 23 exposes the RPM's compressed payload; newer versions unwrap it.
            # Both routes must produce the exact same pinned CPIO before selecting tools.
            zstd = shutil.which("zstd")
            if not zstd:
                raise RuntimeError("This extractor requires an already installed Zstandard decoder")
            expanded = self.root / "assembler.cpio"
            with expanded.open("xb") as output, (self.root / "zstd.log").open("xb") as errors:
                result = subprocess.run([zstd, "-d", "-c", str(archive)], stdout=output, stderr=errors, check=False, timeout=30)
            if result.returncode or (self.root / "zstd.log").stat().st_size:
                raise RuntimeError("Pinned RPM payload decompression failed")
            archive = expanded
        with archive.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        if archive.stat().st_size != 2835056 or digest != "c1c5f7ffcd2d302b387900d7bb78d8873166cb93453ae460ffb82e1f129656a6":
            raise RuntimeError("Unexpected official RPM CPIO bytes")
        # Never extract package build-id symlinks or install files into system directories.
        self.command("tools", [extractor, "x", str(archive), "-o" + str(tools), "-y", "./usr/bin/nasm", "./usr/bin/ndisasm"])
        nasm = tools / "usr/bin/nasm"
        if nasm.is_symlink() or not nasm.is_file():
            raise RuntimeError("Assembler extraction did not yield an ordinary file")
        with nasm.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != "dc7ccb5acebb072f1cc4fe8fd4ba47997de595434aa01a540eabc5e4543372fb":
                raise RuntimeError("Extracted assembler digest changed")
        nasm.chmod(0o700)
        self.command("synthetic-codec", [sys.executable, str(probes / "linux_codec_smoke.py"),
            "--source-archive", str(fetch.root / "ffmpeg-9.0.2.tar.xz"), "--assembler", str(nasm)], timeout=2400)

    def command(self, name, arguments, timeout=120):
        with (self.root / (name + ".log")).open("xb") as output:
            child = subprocess.Popen(arguments, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                status = child.wait(timeout=timeout)
            finally:
                if child.poll() is None:
                    os.killpg(child.pid, signal.SIGKILL)
                child.wait()
        text = (self.root / (name + ".log")).read_text(encoding="utf-8")
        if status or re.search(r"\bwarning\s*:|\bWARNING\b|\berror\s*:", text):
            raise RuntimeError("Codec preparation failed: " + name + "; inspect " + str(self.root))
        return text


if __name__ == "__main__":
    CodecPreparation().run()
