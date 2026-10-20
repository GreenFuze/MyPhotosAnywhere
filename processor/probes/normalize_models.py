"""Remove redundant initializer inputs, retaining every graph operation and weight.

Modified derivative of the separately licensed models. Originals remain untouched.
Run with the pinned build-only ONNX environment; never on user's media.
"""
from pathlib import Path
import hashlib
import json
import shutil

import onnx


class ProbeModelNormalizer:
    def __init__(self):
        self.root = Path(__file__).resolve().parent.parent / ".models"
        self.destination = self.root / "normalized"

    def normalize(self):
        self.destination.mkdir(exist_ok=True)
        manifest = json.loads((self.root / "models.json").read_text(encoding="utf-8"))
        changes = []
        for entry in manifest["models"]:
            source = self.root / entry["file"]
            with source.open("rb") as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != entry["sha256"]:
                    raise ValueError("Unverified source model")
            model = onnx.load(source)
            onnx.checker.check_model(model)
            if model.ir_version < 4:
                raise ValueError("Cannot remove initializer inputs from this ONNX IR")
            source_proto = model.SerializeToString(deterministic=True)
            original_inputs = list(model.graph.input)
            original_notice = model.doc_string
            had_notice = model.HasField("doc_string")
            initializers = {item.name for item in model.graph.initializer}
            removed = [item.name for item in original_inputs if item.name in initializers]
            kept = [item for item in original_inputs if item.name not in initializers]
            del model.graph.input[:]
            model.graph.input.extend(kept)
            notice = ("Modified by MPA on 2026-10-20: removed redundant initializer graph-input declarations; "
                      "graph operations and weights unchanged. Original: OpenCV Zoo SFace 2021dec, Apache-2.0.")
            if removed:
                if entry["file"] != "face_recognition_sface_2021dec.onnx":
                    raise ValueError("Unreviewed model modification")
                model.doc_string = (original_notice + "\n" if original_notice else "") + notice
            onnx.checker.check_model(model)
            normalized = model.SerializeToString(deterministic=True)
            if hashlib.sha256(normalized).hexdigest() != entry["normalizedSha256"]:
                raise ValueError("Normalized model differs from reviewed derivative")
            # Structural equivalence proof: restoring only the original input
            # declarations and documentary notice must reconstruct the entire
            # original protobuf. No operation or weight may change.
            del model.graph.input[:]
            model.graph.input.extend(original_inputs)
            if had_notice:
                model.doc_string = original_notice
            else:
                model.ClearField("doc_string")
            if model.SerializeToString(deterministic=True) != source_proto:
                raise ValueError("Normalization unexpectedly changed computation or weights")
            if len(kept) != 1:
                raise ValueError("Unexpected model input contract")
            target = self.destination / entry["file"]
            target.write_bytes(normalized)
            changes.append({"file": entry["file"], "sourceSha256": entry["sha256"],
                            "removedInitializerInputs": removed,
                            "operationAndWeightBytesUnchanged": True})
            entry["sourceSha256"] = entry["sha256"]
            entry["sha256"] = hashlib.sha256(normalized).hexdigest()
            entry["bytes"] = len(normalized)
            entry["modification"] = notice if removed else "Original model bytes retained; no modification"
            license_path = self.root / (entry["file"] + ".LICENSE")
            if hashlib.sha256(license_path.read_bytes()).hexdigest() != entry["licenseSha256"]:
                raise ValueError("Unverified model license")
            shutil.copyfile(self.root / (entry["file"] + ".LICENSE"),
                            self.destination / (entry["file"] + ".LICENSE"))
        (self.destination / "models.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        (self.destination / "normalization.json").write_text(json.dumps(changes, indent=2) + "\n", encoding="utf-8")
        print("Validated graph-input normalization and embedded modification notice; original files unchanged.")


if __name__ == "__main__":
    ProbeModelNormalizer().normalize()
