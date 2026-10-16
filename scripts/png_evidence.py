"""Validate complete screenshot bytes before accepting a native-test artifact."""
from pathlib import Path
import struct
import sys
import zlib


class PngEvidenceValidator:
    LIMIT = 64 * 1024 * 1024

    def validate(self, path):
        if not 32 <= Path(path).stat().st_size <= self.LIMIT:
            raise ValueError(f"Invalid PNG file size: {path}")
        data = Path(path).read_bytes()
        if not 32 <= len(data) <= self.LIMIT or data[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError(f"Invalid PNG signature/size: {path}")
        position = 8
        header = None
        compressed = bytearray()
        ended = False
        while position < len(data):
            if position + 12 > len(data):
                raise ValueError(f"Truncated PNG chunk: {path}")
            size, kind = struct.unpack_from(">I4s", data, position)
            end = position + 12 + size
            if end > len(data):
                raise ValueError(f"Truncated PNG payload: {path}")
            payload = data[position + 8:end - 4]
            crc = struct.unpack_from(">I", data, end - 4)[0]
            if zlib.crc32(kind + payload) & 0xffffffff != crc:
                raise ValueError(f"PNG CRC mismatch: {path}")
            if header is None and kind != b"IHDR":
                raise ValueError(f"PNG header missing: {path}")
            if kind == b"IHDR":
                if header is not None or size != 13:
                    raise ValueError(f"Invalid PNG header: {path}")
                header = struct.unpack(">IIBBBBB", payload)
            elif kind == b"IDAT":
                compressed.extend(payload)
            elif kind == b"IEND":
                if size != 0 or end != len(data):
                    raise ValueError(f"Invalid PNG ending: {path}")
                ended = True
            position = end
        if not ended or not compressed or header is None:
            raise ValueError(f"Incomplete PNG: {path}")
        width, height, bits, color, compression, filtering, interlace = header
        channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(color)
        if (not 0 < width <= 8192 or not 0 < height <= 8192 or channels is None
                or bits not in (1, 2, 4, 8, 16) or compression != 0 or filtering != 0
                or interlace not in (0, 1)):
            raise ValueError(f"Unsupported PNG header: {path}")
        def rows(w, h):
            return h * (1 + (w * channels * bits + 7) // 8) if w and h else 0
        if interlace == 0:
            expected = rows(width, height)
        else:
            expected = sum(rows(max(0, (width-x+dx-1)//dx), max(0, (height-y+dy-1)//dy))
                           for x, y, dx, dy in ((0,0,8,8),(4,0,8,8),(0,4,4,8),
                                               (2,0,4,4),(0,2,2,4),(1,0,2,2),(0,1,1,2)))
        if expected > self.LIMIT:
            raise ValueError(f"PNG pixel evidence exceeds bounded decoding budget: {path}")
        decoder = zlib.decompressobj()
        pixels = decoder.decompress(compressed, expected + 1)
        if not decoder.eof or decoder.unused_data or decoder.unconsumed_tail or len(pixels) != expected:
            raise ValueError(f"Incomplete PNG pixel stream: {path}")
        return width, height


if __name__ == "__main__":
    files = sorted(Path(sys.argv[1]).glob("mpa-*.png"))
    if not files:
        raise ValueError("No screenshot evidence was collected")
    validator = PngEvidenceValidator()
    for file in files:
        validator.validate(file)
    print(f"Verified {len(files)} complete PNG screenshot artifacts")
