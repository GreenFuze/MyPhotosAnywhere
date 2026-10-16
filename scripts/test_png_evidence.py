import struct
import tempfile
import unittest
import zlib
from pathlib import Path
from png_evidence import PngEvidenceValidator


class PngEvidenceTest(unittest.TestCase):
    @staticmethod
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind+payload) & 0xffffffff)

    def image(self, compressed=None):
        header = self.chunk(b"IHDR", struct.pack(">IIBBBBB", 2, 1, 8, 6, 0, 0, 0))
        pixels = zlib.compress(b"\0" + b"\xff\x00\x80\xff" * 2) if compressed is None else compressed
        return b"\x89PNG\r\n\x1a\n" + header + self.chunk(b"IDAT", pixels) + self.chunk(b"IEND", b"")

    def validate(self, data):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/"fixture.png"
            path.write_bytes(data)
            return PngEvidenceValidator().validate(path)

    def test_complete_rgba_evidence(self):
        self.assertEqual((2, 1), self.validate(self.image()))

    def test_header_only_and_truncated_pixel_data_are_rejected(self):
        for data in (self.image()[:33], self.image()[:-6], self.image(zlib.compress(b"\0\xff"))):
            with self.subTest(size=len(data)), self.assertRaises(ValueError):
                self.validate(data)

    def test_crc_and_trailing_bytes_are_rejected(self):
        damaged = bytearray(self.image())
        damaged[44] ^= 1
        for data in (damaged, self.image()+b"extra"):
            with self.assertRaises(ValueError):
                self.validate(data)

    def test_compressed_output_cannot_exceed_declared_pixel_budget(self):
        with self.assertRaises(ValueError):
            self.validate(self.image(zlib.compress(b"x"*1024)))


if __name__ == "__main__":
    unittest.main()
