"""Fetch pinned public feasibility inputs; never execute an installer or access user media."""
from pathlib import Path
import hashlib
import json
import re
import urllib.parse
import urllib.request
import uuid


class OcrAssetFetcher:
    def __init__(self, manifest_name="ocr-assets.json", output_name="ocr-assets", allowed_hosts=None):
        self.probes = Path(__file__).resolve().parent
        if not re.fullmatch(r"[a-z][a-z0-9-]*\.json", manifest_name) or not re.fullmatch(r"[a-z][a-z0-9-]*", output_name):
            raise ValueError("Unsafe asset manifest/output identity")
        self.root = self.probes.parent / "build" / output_name
        self.manifest = json.loads((self.probes / manifest_name).read_text(encoding="utf-8"))
        self.allowed_hosts = frozenset(allowed_hosts if allowed_hosts is not None else {"github.com", "raw.githubusercontent.com"})

    def run(self):
        self.root.mkdir(parents=True, exist_ok=True)
        for fixture in self.manifest.get("fixtures", []):
            name = fixture["file"]
            if not re.fullmatch(r"fixtures/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", name) or not 0 < fixture["bytes"] <= 128 * 1024:
                raise ValueError("Unsafe synthetic fixture identity/bound")
            path = (self.probes / name).resolve(strict=True)
            if not path.is_relative_to((self.probes / "fixtures").resolve(strict=True)):
                raise ValueError("Synthetic fixture escaped its selected root")
            self.verify(path, fixture)
        names = set()
        for asset in self.manifest["assets"]:
            name = asset["file"]
            if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", name) or name in names:
                raise ValueError("Asset identity is unsafe or repeated")
            names.add(name)
            address = urllib.parse.urlsplit(asset["url"])
            if address.scheme != "https" or address.hostname not in self.allowed_hosts or address.username:
                raise ValueError("Asset escaped its official source")
            if not 0 < asset["bytes"] <= 32 * 1024 * 1024 or not re.fullmatch(r"[0-9a-f]{64}", asset["sha256"]):
                raise ValueError("Asset bound/digest is invalid")
            path = self.root / name
            if not path.exists():
                pending = path.with_name(name + "." + uuid.uuid4().hex + ".pending")
                owns_pending = False
                try:
                    with urllib.request.urlopen(asset["url"], timeout=90) as source, pending.open("xb") as output:
                        owns_pending = True
                        remaining = asset["bytes"]
                        while remaining:
                            block = source.read(min(remaining, 1024 * 1024))
                            if not block:
                                raise ValueError("Truncated asset download")
                            output.write(block)
                            remaining -= len(block)
                        if source.read(1):
                            raise ValueError("Asset download exceeds pinned length")
                    self.verify(pending, asset)
                    pending.replace(path)
                    owns_pending = False
                finally:
                    if owns_pending:
                        pending.unlink(missing_ok=True)
            self.verify(path, asset)
            print(name, "size/SHA-256 verified")
        (self.root / "verified-assets.json").write_text(json.dumps(self.manifest, indent=2) + "\n", encoding="utf-8", newline="\n")

    @staticmethod
    def verify(path, asset):
        if path.stat().st_size != asset["bytes"]:
            raise ValueError("Asset length mismatch")
        with path.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != asset["sha256"]:
                raise ValueError("Asset digest mismatch")


if __name__ == "__main__":
    OcrAssetFetcher().run()
