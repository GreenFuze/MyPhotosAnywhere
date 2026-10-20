"""Bounded local Stage 0 CPU codec build; uses already verified/extracted public source.

No install step, no input media, no network access, no shell-built commands. This is not
the installer/runtime closure or a production video importer.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import signal
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import uuid


class CodecSmoke:
    def __init__(self, source_archive=None, assembler=None):
        if sys.platform != "linux":
            raise RuntimeError("This local probe requires the selected Linux environment")
        self.root = Path(__file__).resolve().parents[1] / "build/codec-feasibility"
        self.nasm = Path(assembler).resolve(strict=True) if assembler else self.root / "nasm-rpm/executables/usr/bin/nasm"
        self.archive = Path(source_archive).resolve(strict=True) if source_archive else Path(__file__).resolve().parents[2].parent / "preflight-060/codec-sources/ffmpeg-9.0.2.tar.xz"
        self.output = self.root / ("linux-build-" + uuid.uuid4().hex)
        self.environment = {key: os.environ[key] for key in ("PATH", "HOME", "TMPDIR") if key in os.environ}
        self.environment.update({"LC_ALL": "C", "LANG": "C", "OMP_THREAD_LIMIT": "2"})

    def command(self, name, arguments, timeout):
        log = self.output / (name + ".log")
        with log.open("xb") as destination:
            child = subprocess.Popen(arguments, cwd=self.work, env=self.environment,
                                     stdout=destination, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                status = child.wait(timeout=timeout)
            finally:
                if child.poll() is None:
                    os.killpg(child.pid, signal.SIGKILL)
                child.wait()
        text = log.read_text(encoding="utf-8", errors="strict")
        if status or re.search(r"\bwarning\s*:|\bWARNING\b|\berror\s*:", text):
            raise RuntimeError("Codec command failed diagnostic gate: " + name + "; inspect " + str(log))
        return text

    def run(self):
        if not self.archive.is_file() or not self.nasm.is_file():
            raise RuntimeError("Verify/extract the pinned source and official NASM first")
        with self.archive.open("rb") as archive_file:
            source_hash = hashlib.file_digest(archive_file, "sha256").hexdigest()
        if self.archive.stat().st_size != 12040788 or source_hash != "8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e":
            raise RuntimeError("FFmpeg source no longer matches the detached-signature-verified archive")
        with self.nasm.open("rb") as assembler:
            if hashlib.file_digest(assembler, "sha256").hexdigest() != "dc7ccb5acebb072f1cc4fe8fd4ba47997de595434aa01a540eabc5e4543372fb":
                raise RuntimeError("Official extracted assembler changed")
        self.output.mkdir(exist_ok=False)
        with tempfile.TemporaryDirectory(prefix="mpa-codec-") as staging:
            self.work = Path(staging)
            with tarfile.open(self.archive, "r:xz") as archive:
                members = archive.getmembers()
                if len(members) > 30000 or sum(member.size for member in members) > 512 * 1024 * 1024:
                    raise RuntimeError("Pinned source extraction exceeds its bound")
                for member in members:
                    if not (member.isfile() or member.isdir()) or not (member.name.startswith("ffmpeg-9.0.2/") or member.name == "ffmpeg-9.0.2" and member.isdir()) or not (self.work / member.name).resolve().is_relative_to(self.work):
                        raise RuntimeError("Unexpected source archive member")
                archive.extractall(self.work, members=members, filter="data")
            self.source = self.work / "ffmpeg-9.0.2"
            self._build_and_check()

    def _build_and_check(self):
        changes = []
        def patch(relative, old, new, reason):
            path = self.source / relative
            before = path.read_bytes()
            if before.count(old.encode()) != 1:
                raise RuntimeError("Pinned FFmpeg patch anchor changed: " + relative)
            after = before.replace(old.encode(), new.encode())
            path.write_bytes(after)
            changes.append(dict(file=relative, beforeSha256=hashlib.sha256(before).hexdigest(), afterSha256=hashlib.sha256(after).hexdigest(), reason=reason))
        patch("libavcodec/vlc.c", "            if (is16bit) info.val16[curlevel] = sym;\n            else info.val8[curlevel] = sym&0xFF;",
            "            if (is16bit) {\n                av_assert0((unsigned)curlevel < FF_ARRAY_ELEMS(info.val16));\n                info.val16[curlevel] = sym;\n            } else {\n                av_assert0((unsigned)curlevel < FF_ARRAY_ELEMS(info.val8));\n                info.val8[curlevel] = sym&0xFF;\n            }",
            "Assert actual union-member bounds before the recursive VLC write; invalid internal indices fail fast, valid table construction unchanged.")
        patch("libavcodec/vlc.c", "symbols_size <= 2U", "(unsigned)symbols_size <= 2U",
            "Make the pre-existing C unsigned comparison conversion explicit, preserving rejection of negative sizes.")
        patch("libavcodec/vlc.c", "COPY(len > nb_bits);", "COPY(len > (unsigned)nb_bits);",
            "Make the pre-existing unsigned length comparison conversion explicit for the strict native regression build.")
        patch("libavcodec/vlc.c", "if (len > 3*nb_bits || len > 32)", "if (len > (unsigned)(3*nb_bits) || len > 32)",
            "Preserve the existing arithmetic/comparison semantics with an explicit unsigned comparison conversion.")
        patch("libavcodec/vlc.c", "COPY(len && len <= nb_bits);", "COPY(len && len <= (unsigned)nb_bits);",
            "Make the pre-existing unsigned length comparison conversion explicit; no diagnostic option removed.")
        patch("libavcodec/h263dec.c", "\nframe_end:\n", "\n#if CONFIG_WMV2_DECODER\nframe_end:\n#endif\n",
            "Compile the WMV2-only label under the same feature condition as its sole goto; retain it whenever WMV2 is enabled.")
        for relative in ("libavcodec/vlc.c", "libavcodec/h263dec.c"):
            path = self.source / relative
            before = path.read_text(encoding="utf-8")
            first_include = before.index("#include")
            after = before[:first_include] + "/* MPA modification 2026-10-20: explicit bounds/comparison or feature guards; see the distributed source-change inventory. */\n" + before[first_include:]
            patch(relative, before, after, "Prominent dated modification notice; original upstream license retained.")
        (self.output / "source-changes.json").write_text(json.dumps(changes, indent=2) + "\n", encoding="utf-8")
        self.command("assembler", [str(self.nasm), "-v"], 15)
        self.command("configure", [str(self.source / "configure"), "--disable-autodetect", "--disable-network",
            "--enable-zlib",
            "--disable-doc", "--disable-debug", "--disable-shared", "--enable-static", "--disable-everything",
            "--enable-ffmpeg", "--enable-ffprobe", "--enable-protocol=file,pipe",
            "--enable-demuxer=mov,matroska,avi,rawvideo",
            "--enable-decoder=h264,hevc,vp8,vp9,mpeg4,mjpeg,png,ffv1,rawvideo",
            "--enable-parser=h264,hevc,vp8,vp9,mpeg4video,mjpeg",
            "--enable-encoder=rawvideo", "--enable-muxer=rawvideo",
            "--enable-filter=scale,format", "--x86asmexe=" + str(self.nasm)], 300)
        self.command("build", ["make", "-j2", "ffmpeg", "ffprobe"], 1800)
        check = Path(__file__).resolve().parent / "codec_vlc_check.c"
        self.command("vlc-check-build", ["cc", "-std=c17", "-Wall", "-Wextra", "-Werror", "-I", str(self.work),
            "-I", str(self.source), str(check), str(self.work / "libavutil/libavutil.a"), "-lm", "-pthread", "-o", "vlc-check"], 60)
        self.command("vlc-check", [str(self.work / "vlc-check")], 15)
        frame = bytes(component for y in range(48) for x in range(64) for component in (x * 4, y * 5, (x + y) % 256))
        # The reference fixture uses standard uncompressed AVI, independently generated by
        # Python. Production workers decode video; image-sequence demux and video encoders
        # are unnecessary. Still-image decoding/preview encoding belongs to Skia.
        def chunk(tag, data):
            return tag + struct.pack("<I", len(data)) + data + b"\0" * (len(data) % 2)
        def listing(tag, data):
            return chunk(b"LIST", tag + data)
        avi_header = struct.pack("<14I", 1000000, len(frame), 0, 0x10, 6, 0, 1, len(frame), 64, 48, 0, 0, 0, 0)
        stream_header = struct.pack("<4s4sIHH8I4h", b"vids", b"DIB ", 0, 0, 0, 0, 1, 1, 0, 6, len(frame), 0xffffffff, 0, 0, 0, 64, 48)
        bitmap = struct.pack("<IiiHHIIiiII", 40, 64, 48, 1, 24, 0, len(frame), 0, 0, 0, 0)
        bgr = bytes(frame[(47 - y) * 64 * 3 + x * 3 + component] for y in range(48) for x in range(64) for component in (2, 1, 0))
        movie = b"".join(chunk(b"00db", bgr) for _ in range(6))
        index = b"".join(struct.pack("<4sIII", b"00db", 0x10, 4 + i * (8 + len(bgr)), len(bgr)) for i in range(6))
        header = listing(b"hdrl", chunk(b"avih", avi_header) + listing(b"strl", chunk(b"strh", stream_header) + chunk(b"strf", bitmap)))
        avi = b"AVI " + header + listing(b"movi", movie) + chunk(b"idx1", index)
        (self.work / "synthetic.avi").write_bytes(chunk(b"RIFF", avi))
        facts = json.loads(self.command("facts", [str(self.work / "ffprobe"), "-v", "warning",
            "-show_streams", "-show_format", "-of", "json", "synthetic.avi"], 30))
        stream = facts["streams"][0]
        if stream["width"] != 64 or stream["height"] != 48 or stream["codec_name"] != "rawvideo":
            raise RuntimeError("Unexpected synthetic video facts")
        self.command("decode", [str(self.work / "ffmpeg"), "-v", "warning", "-nostdin", "-n", "-i", "synthetic.avi",
            "-frames:v", "1", "-threads", "2", "-pix_fmt", "rgb24", "-f", "rawvideo", "decoded.rgb"], 30)
        if (self.work / "decoded.rgb").read_bytes() != frame:
            raise RuntimeError("Synthetic lossless frame changed")
        report = {"status": "passed", "scope": "local Linux restricted CPU synthetic video only",
                  "frameSha256": hashlib.sha256(frame).hexdigest(), "stream": stream,
                  "source": "FFmpeg 9.0.2 verified detached publisher signature; NASM 3.02 official Fedora binary",
                  "redistributionClosureAccepted": False}
        (self.output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        for name in ("ffmpeg", "ffprobe", "synthetic.avi"):
            shutil.copyfile(self.work / name, self.output / name)
        print("Linux synthetic codec feasibility passed; no system installation or user media access.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-archive")
    parser.add_argument("--assembler")
    options = parser.parse_args()
    CodecSmoke(options.source_archive, options.assembler).run()
