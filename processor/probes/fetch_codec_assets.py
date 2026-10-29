"""Fetch public pinned codec build inputs only; never run installers or access media."""
from fetch_ocr_assets import OcrAssetFetcher


if __name__ == "__main__":
    OcrAssetFetcher("codec-assets.json", "codec-assets", {"ffmpeg.org", "www.nasm.us"}).run()
