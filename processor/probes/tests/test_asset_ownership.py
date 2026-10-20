"""Failure/ownership tests for real probe downloaders; never contact the network."""
from pathlib import Path
import hashlib
import importlib.util
import io
import tempfile
import unittest
from unittest.mock import patch


def load(name):
    path = Path(__file__).resolve().parents[1] / (name + ".py")
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ocr = load("fetch_ocr_assets")
models = load("fetch_models")


class AssetOwnershipTests(unittest.TestCase):
    def setUp(self):
        processor = Path(__file__).resolve().parents[2]
        owned = processor / "build/asset-checks"
        if not owned.resolve().is_relative_to(processor):
            raise RuntimeError("Test scratch directory escaped the Processor workspace")
        owned.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="mpa-asset-check-", dir=owned)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.payload = b"known synthetic test bytes"
        self.asset = {"file": "fixture.bin", "url": "https://github.com/synthetic/fixture.bin",
                      "bytes": len(self.payload), "sha256": hashlib.sha256(self.payload).hexdigest()}
        self.fetcher = ocr.OcrAssetFetcher()
        self.fetcher.root = self.root / "ocr"
        self.fetcher.manifest = {"assets": [self.asset]}

    def test_valid_bytes_promoted_and_reused_without_network(self):
        with patch.object(ocr.urllib.request, "urlopen", return_value=io.BytesIO(self.payload)):
            self.fetcher.run()
        self.assertEqual((self.fetcher.root / "fixture.bin").read_bytes(), self.payload)
        with patch.object(ocr.urllib.request, "urlopen", side_effect=AssertionError("Unneeded download")):
            self.fetcher.run()
        self.assertEqual(list(self.fetcher.root.glob("*.pending")), [])

    def test_truncated_download_rejected_without_promotion(self):
        with patch.object(ocr.urllib.request, "urlopen", return_value=io.BytesIO(b"short")):
            with self.assertRaisesRegex(ValueError, "Truncated"):
                self.fetcher.run()
        self.assertEqual(list(self.fetcher.root.iterdir()), [])

    def test_oversize_download_rejected_without_promotion(self):
        with patch.object(ocr.urllib.request, "urlopen", return_value=io.BytesIO(self.payload + b"x")):
            with self.assertRaisesRegex(ValueError, "exceeds"):
                self.fetcher.run()
        self.assertEqual(list(self.fetcher.root.iterdir()), [])

    def test_digest_failure_rejected_and_owned_partial_removed(self):
        with patch.object(ocr.urllib.request, "urlopen", return_value=io.BytesIO(b"x" * len(self.payload))):
            with self.assertRaisesRegex(ValueError, "digest mismatch"):
                self.fetcher.run()
        self.assertEqual(list(self.fetcher.root.iterdir()), [])

    def test_existing_corruption_preserved_as_failure_without_network(self):
        self.fetcher.root.mkdir()
        target = self.fetcher.root / "fixture.bin"
        target.write_bytes(b"evidence")
        with patch.object(ocr.urllib.request, "urlopen", side_effect=AssertionError("Must inspect existing bytes")):
            with self.assertRaisesRegex(ValueError, "length mismatch"):
                self.fetcher.run()
        self.assertEqual(target.read_bytes(), b"evidence")

    def test_collision_never_deletes_an_unowned_pending_file(self):
        self.fetcher.root.mkdir()
        pending = self.fetcher.root / "fixture.bin.fixed.pending"
        pending.write_bytes(b"not owned")
        with patch.object(ocr.uuid, "uuid4", return_value=type("Identity", (), {"hex": "fixed"})()), \
                patch.object(ocr.urllib.request, "urlopen", return_value=io.BytesIO(self.payload)):
            with self.assertRaises(FileExistsError):
                self.fetcher.run()
        self.assertEqual(pending.read_bytes(), b"not owned")

    def test_invalid_manifest_fails_before_network_or_external_path_write(self):
        for name in ["../escape", "absolute/name", "C:drive", "..", "bad\\name"]:
            self.asset["file"] = name
            with patch.object(ocr.urllib.request, "urlopen", side_effect=AssertionError("Unsafe name reached network")):
                with self.assertRaisesRegex(ValueError, "unsafe"):
                    self.fetcher.run()

    def test_cancellation_removes_only_the_owned_partial(self):
        self.fetcher.root.mkdir()
        unrelated = self.fetcher.root / "other.pending"
        unrelated.write_bytes(b"retain")

        class Interrupted(io.BytesIO):
            def read(self, size=-1):
                raise InterruptedError("Injected interruption")

        with patch.object(ocr.urllib.request, "urlopen", return_value=Interrupted()):
            with self.assertRaises(InterruptedError):
                self.fetcher.run()
        self.assertEqual(list(self.fetcher.root.iterdir()), [unrelated])

    def test_model_fetch_collision_preserves_unowned_partial(self):
        fetcher = models.ProbeModelFetcher()
        fetcher.destination = self.root / "models"
        fetcher.destination.mkdir()
        pending = fetcher.destination / "fixture.bin.fixed.pending"
        pending.write_bytes(b"not owned")
        fetcher.manifest = {"revision": "synthetic", "models": [dict(self.asset, directory="synthetic")]}
        with patch.object(models.uuid, "uuid4", return_value=type("Identity", (), {"hex": "fixed"})()), \
                patch.object(models.urllib.request, "urlopen", return_value=io.BytesIO(self.payload)):
            with self.assertRaises(FileExistsError):
                fetcher.fetch()
        self.assertEqual(pending.read_bytes(), b"not owned")

    def test_explicit_codec_sources_do_not_expand_ocr_source_allowlist(self):
        codec = ocr.OcrAssetFetcher("codec-assets.json", "codec-assets", {"ffmpeg.org", "www.nasm.us"})
        codec.root = self.root / "codec"
        codec.manifest = {"assets": [dict(self.asset, url="https://ffmpeg.org/releases/fixture.bin")]}
        with patch.object(ocr.urllib.request, "urlopen", return_value=io.BytesIO(self.payload)):
            codec.run()
        self.asset["url"] = "https://ffmpeg.org/releases/fixture.bin"
        with patch.object(ocr.urllib.request, "urlopen", side_effect=AssertionError("Wrong source reached network")):
            with self.assertRaisesRegex(ValueError, "official source"):
                self.fetcher.run()

    def test_manifest_and_output_names_cannot_escape_probe_root(self):
        for manifest, output in [("../codec-assets.json", "codec-assets"), ("codec-assets.json", "../outside")]:
            with self.assertRaisesRegex(ValueError, "Unsafe"):
                ocr.OcrAssetFetcher(manifest, output)


if __name__ == "__main__":
    unittest.main(failfast=True)
