import hashlib
import tempfile
import unittest
from pathlib import Path
from collect_png_evidence import ScreenshotCollector
import test_png_evidence


class FakeDevice:
    def __init__(self, data):
        self.data = data
        self.reads = 0

    def metadata(self, name):
        return {"bytes": len(self.data), "sha256": hashlib.sha256(self.data).hexdigest()}

    def read(self, name, target):
        self.reads += 1
        target.write_bytes(self.data)


class ScreenshotCollectorTest(unittest.TestCase):
    def fixture(self, pulled, device):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        folder = Path(temporary.name)
        (folder / "raw").mkdir()
        (folder / "mpa-fixture.png").write_bytes(pulled)
        return folder, ScreenshotCollector(device)

    def test_valid_evidence_does_not_request_a_new_frame_or_read(self):
        data = test_png_evidence.PngEvidenceTest().image()
        device = FakeDevice(data)
        folder, collector = self.fixture(data, device)
        result = collector.collect(folder, [("fixture.png", "mpa-fixture.png")])
        self.assertEqual(0, device.reads)
        self.assertFalse(result["files"]["mpa-fixture.png"]["reRead"])

    def test_truncated_pull_is_repaired_only_from_the_same_valid_device_file(self):
        data = test_png_evidence.PngEvidenceTest().image()
        device = FakeDevice(data)
        folder, collector = self.fixture(data[:33], device)
        result = collector.collect(folder, [("fixture.png", "mpa-fixture.png")])
        self.assertEqual(data, (folder / "mpa-fixture.png").read_bytes())
        self.assertEqual(data[:33], (folder / "rejected-transport/mpa-fixture.png").read_bytes())
        self.assertEqual(data, (folder / "raw/fixture.png").read_bytes())
        self.assertEqual(1, device.reads)
        self.assertTrue(result["files"]["mpa-fixture.png"]["reRead"])

    def test_bad_device_capture_remains_rejected_and_keeps_diagnostics(self):
        data = test_png_evidence.PngEvidenceTest().image()[:33]
        folder, collector = self.fixture(data, FakeDevice(data))
        with self.assertRaises(ValueError):
            collector.collect(folder, [("fixture.png", "mpa-fixture.png")])
        self.assertEqual(data, (folder / "mpa-fixture.png").read_bytes())
        self.assertTrue((folder / "capture-transport-review.json").is_file())

    def test_changed_device_bytes_cannot_be_accepted(self):
        class ChangedDevice(FakeDevice):
            def read(self, name, target):
                target.write_bytes(self.data + b"changed")
        folder, collector = self.fixture(test_png_evidence.PngEvidenceTest().image()[:33], ChangedDevice(test_png_evidence.PngEvidenceTest().image()))
        with self.assertRaisesRegex(ValueError, "checksum failed"):
            collector.collect(folder, [("fixture.png", "mpa-fixture.png")])


if __name__ == "__main__":
    unittest.main()
