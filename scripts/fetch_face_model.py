"""Fetch the Apache-2.0 MobileFaceNet asset once; verify archive and model hashes."""
import hashlib
import io
from pathlib import Path
import urllib.request
import zipfile

URL = "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/mobile_facenet/releases/v0.64.0/mobile_facenet-tflite-float.zip"
ARCHIVE_HASH = "92dc09420007cd24b45d64cc0e0bf9a7aabe3b7833579ec2e417fa0ec05253d3"
MODEL_HASH = "2046ab776fba2ec7e420767ffe7eb74a7402a8412d3d7ea8b9f4ca8420e5f3d7"
target = Path(__file__).resolve().parents[1] / "app/src/main/assets/mobile_facenet.tflite"
if target.is_file() and hashlib.sha256(target.read_bytes()).hexdigest() == MODEL_HASH:
    print("Verified cached MobileFaceNet model.")
else:
    with urllib.request.urlopen(URL, timeout=60) as response:
        archive = response.read(20 * 1024 * 1024)
    assert hashlib.sha256(archive).hexdigest() == ARCHIVE_HASH, "Model archive checksum mismatch"
    with zipfile.ZipFile(io.BytesIO(archive)) as content:
        model = content.read("mobile_facenet-tflite-float/mobile_facenet.tflite")
    assert hashlib.sha256(model).hexdigest() == MODEL_HASH, "Face model checksum mismatch"
    target.parent.mkdir(parents=True, exist_ok=True)
    staging = target.with_suffix(".part")
    staging.write_bytes(model)
    staging.replace(target)
    print("Downloaded and verified MobileFaceNet model.")
