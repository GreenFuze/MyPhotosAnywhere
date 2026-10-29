# Object and OCR feasibility — incomplete

October 20, 2026. These are bounded prototype checks, not completed processing plugins, recognition quality or installer acceptance. No private photo was used.

## Object model

OpenCV Zoo YOLOX-s `object_detection_yolox_2022nov.onnx`, source revision `47534e27c9851bb1128ccc0102f1145e27f23f98`, 35,858,002 bytes, SHA-256 `c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063`. Source directory license is Apache-2.0; model and license hashes are pinned in `processor/probes/models.json`.

The model has one static `[1,3,640,640]` input and produced 714,000 finite output values on Windows CPU. The provisional three-model run used about 148 MB peak process working set, with two intra-op threads and one inter-op thread; this is not a process-tree budget or recognition benchmark. All three models and the JPEG decoder completed without runtime warnings. Strict C# build had zero warnings/errors.

YOLOX bytes remain unchanged. The SFace derivative now embeds its dated modification notice in the ONNX document itself. Restoring original input declarations and the original document-field presence reproduces the exact complete original protobuf, proving operations and weights unchanged. Nine deterministic all-output comparisons passed; largest SFace absolute difference remains `5.662441253662109e-6`, within the recorded tolerance. The normalized SFace hash is now `3a4d57f0f067c3fc9d247522103d4f3a2315216d82692ddc38637b60cb81a6d1`. Original SFace warnings remain only in the intentional reference-comparison log; the corrected execution is clean.

Pending: representative images, correct preprocessing/postprocessing, category vocabulary/search behavior, detection-quality measurements, all required native targets, repeated resource/cancellation tests and final recipe/model-space contracts. Finite output does not establish useful detection.

## OCR

Official Windows Tesseract asset `5.5.3.20260724`, 26,573,224 bytes, SHA-256 `bee9e3434bd94fd65387d9be28cd467a41f61b1275383b55b0f59a1331270ae4`, downloaded from the project's release and independently checked against its reported digest. Extracted into private test storage without executing the installer or changing system installation.

The binary loaded as Tesseract 5.5.3 with Leptonica 1.87.0. The version output lists its bundled image/archive/network dependencies; their redistribution/source closure is **not accepted yet** merely because the engine runs.

English and Hebrew fast language data are pinned to official revision `87416418657359cb625c412a48b6e1d6d41c29bd`, with exact size/SHA-256 and the Apache-2.0 license in `processor/probes/ocr-assets.json`. `fetch_ocr_assets.py` verifies every byte bound/hash and executes nothing.

A Skia-generated PNG containing **MPA PROCESSOR 6045** was read exactly with `--oem 1 --psm 6 -l eng` and `OMP_THREAD_LIMIT=2`. Runtime stderr was empty. Fixture generation owns/disposes canvas, bitmap, typeface, font, paint, image, encoded data and output stream. No user image was opened. Hebrew data download/verification is not a Hebrew recognition-quality result.

Pending: Linux/Mac execution, package/dependency/license closure, representative English/Hebrew/photo/rotated-text quality, bounded worker integration, child-process cancellation and repeated lifetime/resource tests.

## Downloader ownership

Nine offline tests passed on Windows: valid promotion/reuse, truncation, oversize, digest mismatch, preservation of existing corrupt evidence, preservation of an unowned colliding pending file, unsafe manifest names, cancellation cleanup and model-fetch pending-file ownership. Downloads clean up only files they created. CI adds the same fail-fast checks on Windows/Linux. No real network is used by those tests.

Build-only ONNX tooling now pins exact wheel SHA-256 values for Python 3.12 on Windows/Linux x64. Windows wheel downloads passed `--require-hashes`; Linux installation remains a CI check. Python is not an application runtime dependency.

## Primary sources

- [Pinned YOLOX source and license](https://github.com/opencv/opencv_zoo/tree/47534e27c9851bb1128ccc0102f1145e27f23f98/models/object_detection_yolox)
- [Official OCR release](https://github.com/tesseract-ocr/tesseract/releases/tag/5.5.3)
- [Pinned language data](https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd)
- [Tesseract manual](https://tesseract-ocr.github.io/tessdoc/)
