"""Fetch immutable, hash-pinned CPU feasibility models; never touch photo inputs."""
from pathlib import Path
import hashlib
import json
import shutil
import urllib.request
import uuid


class ProbeModelFetcher:
    def __init__(self):
        self.probes = Path(__file__).resolve().parent
        self.destination = self.probes.parent / ".models"
        self.manifest = json.loads((self.probes / "models.json").read_text(encoding="utf-8"))

    def fetch(self):
        self.destination.mkdir(exist_ok=True)
        revision = self.manifest["revision"]
        for model in self.manifest["models"]:
            path = self.destination / model["file"]
            if path.name != model["file"]:
                raise ValueError("Model path escaped the probe directory")
            if path.exists():
                self.verify(path, model)
            else:
                url = (f"https://media.githubusercontent.com/media/opencv/opencv_zoo/{revision}"
                       f"/models/{model['directory']}/{model['file']}")
                pending = path.with_name(path.name + "." + uuid.uuid4().hex + ".pending")
                owns_pending = False
                try:
                    with urllib.request.urlopen(url, timeout=90) as source, pending.open("xb") as output:
                        owns_pending = True
                        remaining = model["bytes"]
                        while remaining:
                            block = source.read(min(remaining, 1024 * 1024))
                            if not block:
                                raise ValueError("Truncated model download")
                            output.write(block)
                            remaining -= len(block)
                        if source.read(1):
                            raise ValueError("Model download exceeds pinned size")
                    self.verify(pending, model)
                    pending.replace(path)
                    owns_pending = False
                finally:
                    if owns_pending:
                        pending.unlink(missing_ok=True)
            license_url = (f"https://raw.githubusercontent.com/opencv/opencv_zoo/{revision}"
                           f"/models/{model['directory']}/LICENSE")
            with urllib.request.urlopen(license_url, timeout=30) as source:
                license_bytes = source.read(128 * 1024 + 1)
            if not license_bytes or len(license_bytes) > 128 * 1024:
                raise ValueError("Invalid model license document")
            if hashlib.sha256(license_bytes).hexdigest() != model["licenseSha256"]:
                raise ValueError("Model license digest mismatch")
            (self.destination / (model["file"] + ".LICENSE")).write_bytes(license_bytes)
            print(model["file"], model["sha256"], "verified")
        shutil.copyfile(self.probes / "models.json", self.destination / "models.json")

    @staticmethod
    def verify(path, model):
        if path.stat().st_size != model["bytes"]:
            raise ValueError("Model size mismatch: " + path.name)
        with path.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != model["sha256"]:
                raise ValueError("Model digest mismatch: " + path.name)


if __name__ == "__main__":
    ProbeModelFetcher().fetch()
