"""Validate screenshot transport; never recapture another UI state to replace bad evidence."""
from pathlib import Path
import hashlib
import json
import re
import shutil
import subprocess
import sys
import zlib
from png_evidence import PngEvidenceValidator


class DeviceScreenshotReader:
    def metadata(self, name):
        path = "/sdcard/Download/MPA/" + name
        size = subprocess.check_output(["adb", "shell", "stat", "-c", "%s", path], text=True, timeout=30).strip()
        digest = subprocess.check_output(["adb", "shell", "sha256sum", path], text=True, timeout=30).split()[0]
        if not size.isdigit() or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ValueError("Invalid device screenshot metadata")
        return {"bytes": int(size), "sha256": digest}

    def read(self, name, target):
        with target.open("wb") as output:
            subprocess.run(["adb", "exec-out", "cat", "/sdcard/Download/MPA/" + name],
                           stdout=output, check=True, timeout=30)


class ScreenshotCollector:
    def __init__(self, reader=None):
        self.reader = reader or DeviceScreenshotReader()
        self.validator = PngEvidenceValidator()

    @staticmethod
    def digest(path):
        if path.stat().st_size > PngEvidenceValidator.LIMIT:
            raise ValueError("Screenshot artifact exceeds the bounded byte budget")
        return {"bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}

    def collect(self, folder, mapping):
        folder = Path(folder).resolve()
        evidence = {"policy": "Re-read only the same completed device file; no new capture, app action or test assertion retry.", "files": {}}
        try:
            for source, target in mapping:
                if not all(re.fullmatch(r"[A-Za-z0-9_-]+\.png", name) for name in (source, target)):
                    raise ValueError("Unexpected screenshot name")
                path = folder / target
                row = {"pulled": self.digest(path), "reRead": False}
                evidence["files"][target] = row
                try:
                    self.validator.validate(path)
                except (ValueError, zlib.error):
                    rejected = folder / "rejected-transport"
                    rejected.mkdir(exist_ok=True)
                    shutil.copyfile(path, rejected / target)
                    row["device"] = self.reader.metadata(source)
                    if not 32 <= row["device"]["bytes"] <= PngEvidenceValidator.LIMIT:
                        raise ValueError(f"Device capture itself has invalid size: {source}")
                    part = folder / (target + ".part")
                    self.reader.read(source, part)
                    row["reReadBytes"] = self.digest(part)
                    if row["reReadBytes"] != row["device"]:
                        raise ValueError(f"Screenshot changed or transfer checksum failed: {source}")
                    self.validator.validate(part)
                    part.replace(path)
                    shutil.copyfile(path, folder / "raw" / source)
                    row["reRead"] = True
                row["verified"] = self.digest(path)
        finally:
            (folder / "capture-transport-review.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
        return evidence


if __name__ == "__main__":
    directory = Path(sys.argv[1])
    pairs = [tuple(line.split()) for line in (directory / "capture-map.txt").read_text(encoding="utf-8").splitlines()]
    if not pairs or any(len(pair) != 2 for pair in pairs) or len({pair[1] for pair in pairs}) != len(pairs):
        raise ValueError("Invalid screenshot mapping")
    result = ScreenshotCollector().collect(directory, pairs)
    print(f"Verified {len(result['files'])} complete screenshots; same-file transport rereads: {sum(row['reRead'] for row in result['files'].values())}")
