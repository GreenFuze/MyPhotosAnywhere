# MPA 0.5.30 — Resilient image imports

An individual image failure no longer stops the remaining selected images. Missing or changed source files, corrupt/unsupported previews, invalid per-image metadata, item-specific read/permission errors and nonretryable per-file transfer failures retain their own durable failure receipt while other selected images continue. Import history distinguishes these failures from verified successes. **Import selected / Resume** retries failed or unfinished selected items; completed photos are not uploaded again. No reimport is required for successful images.

The **XMP exceeds 2 MiB** error no longer blocks importing another original. Source-wide optional annotation backup runs separately from importing originals. Complete Takeout metadata, including exported people names, remains in Room and its durable dirty backup outbox. The existing 2MiB portable XMP format/restore safety bound is preserved: a catalogue over that limit still needs separate backup attention and is not falsely reported as uploaded. This release does not expand that backup format or discard annotations to fit it. Existing source asset and 250MiB media limits remain.

Resume preserves original source bindings, captured destinations, reserved remote IDs, upload offsets, hashes and resumable sessions. Unselected failed records remain untouched. Expired authorization, a disconnected/unwritable destination, an expired Photos selection and service-wide failures stop the job honestly instead of blaming every image. Retryable Drive network/HTTP failures retain bounded backoff. Cancellation propagates; unexpected internal invariant failures are durably recorded and rethrown under the fast-fail policy. An error after a photo is already atomically verified/committed never rewinds that success.

## Validation

Immutable `a9c567182679c07f229b7c8c1c92193da539c1a9` passed **428 unit/integration tests**, **zero lint findings**, and **139 native tests on each API 29/35/37 (417 total)**, with zero test failures/errors/skips. [Host checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38911040460) · [Native checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38911040459). All 73 previous screenshots remain, plus three import recovery captures per API. Complete PNG validation, raw reports, source/job/artifact digests, direct visual inspection, full log review and upgrade-signing proof are recorded in BUILD_EVIDENCE.json.

Focused tests exercise actual archive staging, corrupt-image decoding, verified upload/checksum commit, missing-file continuation, Activity recreation, retry without re-uploading a successful photo, changed defaults without destination retargeting, explicit exclusion of a still-corrupt input, Room reopen/checkpoint retention, pause/cancellation races, per-file versus global authorization failures, rate-limit/server backoff, internal fast fail and post-commit failure. A genuine oversized XMP fixture retains all prior metadata while another image imports successfully. Synthetic tests use synthetic accounts/transport; no live Drive photos are mutated.

Existing assertions remain except one deliberate strengthening: the internal-error branch of ImportExecutionTest now expects propagation after durable FAILED recording; previous retry/wait counts, state and secret-redaction assertions remain. Native version expectations update only displayed version text. Room **schema 10, all 22 tables and historical schema 9 remain unchanged**. Timeline, viewer decoding/lifecycle, editor, consent, archive CRC validation and remote original identity/checksum safeguards remain preserved.

Existing 10,000/100,000-item timeline, snapshot, Search, imported-people, Trash/date navigation benchmarks and bounded FrameMetrics diagnostics remain. Software emulator timings do not establish smooth 60Hz scrolling or physical S23 Ultra performance. Strict compiler/lint/Gradle gates remain; only previously authorized, specifically scoped upstream/emulator exceptions may be documented in the bound evidence. No warning baseline, blanket suppression or reduced severity is introduced. Publication changes only documentation and uses the exact signed CI APK.

All planned UX stages 0.5.23–0.5.29 are delivered. Physical S23 Ultra, live Google authorization/permissions/Trash, human TalkBack, translated Hebrew and physical frame-rate acceptance remain unverified. The reported JPEG crash remains unreproduced, with its cause unconfirmed. Optional oversized XMP backup capacity and the existing 250MiB video limit remain explicit backend limitations.

Version **0.5.30**, code **41**, minimum SDK **26**, target SDK **37**, Room schema **10**.

APK SHA-256: `e72a5858b80818e6668e975b25897d46c2650976e84d97536c57b4220c73cb9b`.

Upgrade certificate SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4`.
