# People, pets, and portable metadata: next milestone

Status: researched design, not functionality shipped in the 0.3 APK. The owner has made recognition quality a priority and requested metadata sidecars. This document records the storage direction and measurable implementation gates. Exact models and release thresholds remain subject to evaluation. Cats/dogs are the provisional first pet scope, pending the owner's reply.

## What the current implementation cannot establish

The 0.3 Android inference smoke test proves the model runs, not that identities are accurate. `PeopleRepository` indexes only available local originals and downsamples images to a 1600-pixel preview. `FaceEmbedding` excludes faces below 64 pixels, missing landmarks, and poses beyond 25 degrees. Faces passing the detector can therefore remain without a recognition embedding. Human matching uses fixed experimental thresholds and the highest score against any confirmed reference. There is no individual pet model: image labels such as Cat/Dog are category labels.

Face IDs currently depend on detector list order (`photoId:index`); records have no durable region geometry, quality score, model fingerprint, or structured rejected-identity constraints. Existing scans are skipped based on the photo checksum, so a new model alone will not schedule re-embedding. These are concrete blockers to reliable upgrades and portable corrections, even before choosing a better network.

Google Photos quality is the product goal. Its proprietary models/training data are unavailable to this project; matching its behavior is a result to measure on representative photos, not a claim obtainable by installing a particular library.

## Recognition pipeline

Index the **entire authorized Drive collection independently of offline pins**, with progress that distinguishes discovered, indexed, deferred, failed, and unsupported assets. Introduce a stable asset catalog shared by cloud and local records so a named person/pet can lead to uncached photos as well. First use a bounded recognition rendition only where its resolution and coordinate transform are adequate; otherwise fetch the supported original into protected staging, derive the required crops, and release the disposable bytes after committing annotations. A cropped or tiny Drive thumbnail is insufficient for a trustworthy region-to-original mapping. Respect the existing per-file limit and report unindexed files instead of implying complete coverage. Schedule with network/charging/thermal controls and resume by asset/model checkpoint, rather than loading every original into a permanently pinned library.

1. **Detect at useful resolution.** Preserve orientation and source coordinate transforms. Use a fast whole-image pass plus a bounded higher-resolution/tiled pass for small faces and groups. Deduplicate overlapping detections. Evaluate profile/occlusion handling and pet head/body detection independently. Never send the 480-pixel grid thumbnail straight into recognition as the final input.
2. **Measure quality and align correctly.** Record face/head size, blur, exposure, pose, occlusion, alignment residual, and detector/quality version. Follow each model's exact landmark template, channel order, range, and resize convention. Replace the existing three-point affine alignment with a validated similarity-transform pipeline when required by the selected model. Preserve difficult detections for manual naming; low quality is a reason to defer a match, not silently lose a face.
3. **Use separate human and animal identity encoders.** Model outputs are comparable only within the same model/preprocessing fingerprint. People and species-specific pet vectors are never mixed. For pets, evaluate animal-face matching and head/body appearance (coat markings) as separate signals, then calibrate fusion when both are present. Breed, color, collars, nearby owners, and location must not independently establish identity. If a face-only or body-only signal is insufficient, leave the pet unknown.
4. **Maintain diverse confirmed references.** Keep several quality-controlled exemplars per person/pet across poses, lighting, age, and events; use multiple prototypes where one average would hide that variation. Evaluate robust top-k/prototype scoring against the current single-best-reference baseline. Predicted matches cannot become training references until confirmed. Name changes do not alter identity UUIDs.
5. **Group with constraints and uncertainty.** Retrieve nearby candidates using a versioned approximate-nearest-neighbor index at large library sizes; exact-rerank candidates and measure retrieval recall. Group conservatively with user-confirmed links, rejected links, and quality-aware evidence. Use co-occurrence as a consistency check with exceptions for mirrors/collages/repeated depictions. Avoid identity merging through chains of weak matches. Separate a high-confidence group, a review suggestion, and an unassigned detection in the UI. Cosine similarity is not a probability.
6. **Make corrections permanent.** Persist explicit confirmation, rejection of a particular identity, same/different-identity decisions, merges, and split/reassignment history. A rejection of Alice should still allow a later suggestion of Bob. A detector/model upgrade must preserve reviewed annotations and record any unresolved remapping, instead of attaching an old correction to a new list index.

Keep inference outside the global cache mutex: register a protected reader lease and capture the needed file/asset state under lock, release the lock while decoding/detecting/embedding, then commit only if the source and indexing generation are still current. The current `withOriginal` holds the mutex throughout processing; making heavier recognition run there would delay viewer/download/storage operations. Bound concurrency and make cancellation release leases and staging data safely.

An enrollment helper can request approximately 10–20 varied, clear examples per pet/person as a practical starting point, without treating that number as a guarantee. Ask for the examples that reduce ambiguity most, including similar-looking relatives or pets. Batch review by identity/event should reduce correction effort. Manual drawing of a missed face/pet region is a useful fallback.

## Model selection and licensing

Benchmark a reproducible shortlist. Every candidate needs a pinned source revision, checkpoint checksum, weights license, preprocessing specification, training-data provenance where documented, Android execution contract, and measurements before inclusion in an APK.

| Candidate | Proposed use | Evidence and remaining gate |
| --- | --- | --- |
| Existing Qualcomm MobileFaceNet | Human baseline and possible fast tier | Already hash-pinned; publisher/original Apache-2.0 notices are in the app. No measured MPA accuracy yet. |
| OpenCV Zoo SFace | Alternative human baseline | Its directory README states Apache-2.0 for all files. It is also a MobileFaceNet architecture, so a different loss/checkpoint does not automatically make it a stronger high-quality tier. Verify the exact file and measure Android integration and hard-photo accuracy. |
| AdaFace / an appropriately licensed stronger human checkpoint | Quality-oriented human experiment | Official code is MIT and includes a model-license discussion. Check the exact checkpoint and data/use terms rather than assuming the code license answers every distribution question. Evaluate difficult faces and Android conversion before choosing it. |
| PetFace-style species-specific identification | Animal research reference | The official PetFace repository explicitly restricts its dataset, code, and pretrained models to non-commercial research. This is not an unrestricted app dependency. A compatible license/permission or independently trained, properly sourced model is needed for deployment outside the permitted scope. |
| DINOv2 natural-image backbone | Pet head/body appearance experiment | The official README licenses its natural-image code/weights under Apache-2.0; specialized models in the repository have different terms. Generic visual embeddings are a baseline, not evidence of individual pet accuracy. Any pet adaptation needs a representative, licensed dataset and held-out evaluation. |

Public InsightFace pretrained weights are also research-restricted despite the library code's permissive license. Do not silently bundle them as an unrestricted improvement. If a licensed checkpoint cannot meet the quality goal, investigate author licensing or training a model rather than loosening identity thresholds.

The first implementation should remain usable on the phone. An **optional local computer/NAS batch indexer** could run a larger quality model while charging/overnight and write the same sidecars. The phone would browse those results and index new captures. This can expand compute choices without uploading the library to an AI service, but it does not itself prove accuracy. Do not make a server mandatory. Official ChatGPT/Gemini/Claude subscription handoff remains for editing; it is not an unattended bulk recognition API.

## Evaluation before a quality claim

Start with a manually checked representative set, typically a few hundred to a thousand photos across available identities, pets, dates, and devices. This is a pilot, not enough evidence by itself for an extremely low error rate. Expand after discovering failure categories. Keep raw photos and personal labels out of GitHub and CI.

- Label visible faces/pet regions, identity, species, event/date, and difficult conditions. Include unknown individuals absent from enrollment, similar relatives, similar-breed pets, children across ages, glasses/hats, profiles, small faces, blur, backlight, duplicates, and edited photos. Label only identities that can actually be established; mark ambiguous examples rather than inventing ground truth.
- Split enrollment, tuning, and held-out queries by event/time and keep near-duplicate bursts in one partition. For generalization experiments also hold out identities from model/threshold development; for personal-library testing each enrolled identity must have genuinely different query photos.
- Measure detection precision/recall separately from recognition. Report recognition on supplied crops and on the complete pipeline so missed detections and quality-gated faces remain visible.
- Report correct known-identity retrieval, unknown-to-known false identification rate, cluster pairwise precision/recall and B-cubed metrics, abstention/coverage, worst-slice results, and corrections per 1,000 photos. Pets get separate species-specific results. High pairwise verification accuracy is not sufficient evidence for large-gallery identification.
- Compare models at matched false-identification budgets. A **provisional design target** is no more than 0.1% false automatic identity assignments on the held-out query set, with confidence intervals and coverage disclosed. This is a proposed gate, not an achieved result or a universal threshold. Do not claim it from a small set with zero observed mistakes; estimate uncertainty at identity/event level and collect enough independent evidence. Keep suggestions reviewable until an automatic-assignment gate is supported.
- Measure actual target-phone peak memory, latency per image, indexing time for 10k/100k-image libraries, thermal/battery behavior, and retrieval recall. Compare floating-point and quantized/delegated execution; a conversion or quantization can change identity decisions.

Use the same evaluation set to compare the owner's Google Photos experience when feasible, with ground truth checked independently. That comparison can assess usefulness on this collection; it cannot establish universal parity. Model choice and threshold calibration follow the measurements, separately for humans and each pet pipeline.

## XMP sidecars as durable metadata

Use **XMP/RDF sidecars** to represent EXIF fields and library annotations. A standalone EXIF blob is not the usual interoperable sidecar format. Keep source image/video bytes unchanged. For example:

```text
Chosen Drive folder/
  Trip/IMG_0123.jpg
  Trip/IMG_0123.jpg.xmp
  Trip/IMG_0123-edited.jpg
  Trip/IMG_0123-edited.jpg.xmp
  .mpa/catalog.xmp
```

The full original filename avoids collisions between `IMG_0123.jpg` and `IMG_0123.heic`/raw/video. Also read conventional basename `.xmp` files where association is unambiguous. Some desktop tools prefer basename sidecars, so document and test compatibility rather than claiming every application will discover the full-filename convention automatically. Drive IDs plus an asset UUID and original content checksum bind metadata; filenames alone are not identities. Preserve legacy local/Drive asset aliases during migration. Byte-identical copies can have different annotations, so do not merge assets solely on hash equality.

| Metadata | Representation and behavior |
| --- | --- |
| Capture time, GPS, orientation | Standard XMP EXIF/TIFF properties; preserve unknown timezone and absent GPS. Store a user override separately from embedded source values. |
| Titles, captions, keywords, rating | Standard Dublin Core/XMP properties; export only what exists. MPA favorite/archive flags stay explicitly defined custom fields, not an invented rating equivalence. |
| Human/pet regions and confirmed names | MWG region structures support `Face` and `Pet`, with normalized center-based rectangles and explicit applied-to dimensions. Export confirmed people in `Iptc4xmpExt:PersonInImage`; put pet species/identity and decision provenance in the MPA extension. |
| Confirmations/rejections/corrections | MPA extension with stable region/identity UUIDs, decision kind, target/rejected identity, revision, actor, and provenance. Persist unknown/rejected states so rescanning does not resurrect deleted annotations. |
| Albums and canonical identities | `.mpa/catalog.xmp` defines UUIDs, names, species, merge aliases, album definitions, and tombstones; per-asset sidecars carry memberships and materialized names for portability. A rename changes the catalog immediately and queues per-asset name refreshes. Readers use catalog identities when available. |
| Edits and relationships | MPA extension for source UUID/hash, saved recipe/schema version, provider and retained instructions, and rendered-output relationship. A sidecar does not claim other editors understand MPA recipes or automatically reproduce the output. |
| Inference state | Detector/encoder/preprocessing fingerprints and optional quality/provenance are durable; vector indexes, face-crop cache, and embeddings are rebuildable local data by default. Do not put high-dimensional vectors in standard EXIF fields. Optional encrypted acceleration bundles are separate later work. |

The extension namespace is proposed as `https://github.com/GreenFuze/MyPhotosAnywhere/ns/1.0/`, with explicit property types and versioned migration rules. The illustrative [XMP example](examples/IMG_0001.jpg.xmp) demonstrates standard and extension structure, not a finished parser or live photo.

Read/write region geometry needs tests across **all eight EXIF orientations**, including mirrored orientations, source dimensions, and edits. Define whether coordinates refer to stored pixels or the displayed upright image and encode the reference explicitly; transform at import/export as required by the originating schema. Do not silently assume all existing writers use the same frame. For a changed photo checksum, preserve the old annotation revision and flag its geometry for review. A detector rerun uses overlap/landmark matching to propose links to stable regions; ambiguous links require review. Embeddings from an old model are invalidated without discarding human decisions.

Readable XMP sidecars include names and locations, so folder collaborators can read them. That follows the requested portable metadata approach; show it clearly in Storage. Embeddings remain local by default. Sharing a preview continues to exclude library sidecars. Exporting metadata must be explicit, with people/location removal options; **Keep GPS off must also exclude GPS from an edited copy's sidecar**. Do not copy names/regions or location overrides into a generic AI handoff.

Room remains the fast local browse/index layer and operational outbox. Durable library annotations must be reconstructible from valid sidecars/catalog after clearing that index or reinstalling; device pins, cache access times, pending authorization/handoff operations, access tokens, and private filesystem paths are not portable metadata. Originals containing their own EXIF retain it; the sidecar records annotations/overrides and does not require duplicating every maker-note blob.

## Sidecar sync and recovery

- A local metadata change commits its decision and durable outbox event together. Serialize through a synced staging file and atomic rename. Retry serialization/upload after process death; show unsynced status and do not claim a metadata backup until the remote object is verified. Pending sidecars/outbox events are protected from cache cleanup.
- Extend Drive discovery and change handling to XMP/catalog objects; the current image/video-only query will not discover them. Detect changes to metadata independently of the photo revision. Existing-library mode may access ordinary third-party sidecars; `drive.file` mode sees only app-accessible objects. Never pretend selecting a folder grants its inaccessible sidecars.
- Preserve unknown RDF properties/namespaces and third-party regions, including values from desktop editors. Adopt foreign face labels only through a defined import/review path. Bound document size/depth/entity counts, disable external entity/DTD/network resolution, validate numbers and IDs, and treat unsupported schema versions as preserved/read-only rather than overwriting them.
- Use independent field/annotation revisions, actor IDs, causal ancestry, and tombstones. Concurrent edits to different fields can merge. Conflicting assignments of one region or concurrent name changes remain visible for resolution; wall-clock time alone cannot decide truth.
- Verify the available Drive conditional-write contract before implementing in-place canonical updates. A version check followed by a normal update is not atomic. Where atomic compare-and-swap is unavailable, retain immutable, idempotent per-device XMP revision objects and materialize the adjacent canonical sidecar. Revisions preserve concurrent decisions even if canonical materialization races. Compact only after retained-device acknowledgement and a recovery policy.
- Store library-level identities/albums in XMP so restores can resolve the same UUID everywhere. Maintain rename/move associations using IDs and hashes, flag orphans, and never overwrite an unrelated same-name sidecar. Handle photo-without-sidecar, sidecar-before-photo, deleted/moved photo, interrupted pair uploads, and unavailable catalogs without deleting valid user metadata.
- Distinguish **clear generated recognition cache** from **remove people/pet annotations from the library**. The latter writes tombstones and propagates removal to sidecars/revisions under a defined retention policy, so restore cannot silently resurrect them. Explain any retained Drive revision/history limits. Album removal and correction removal do not delete photographs.

## Implementation sequence and release gates

1. **Durable annotations first:** domain/schema migration for stable assets, region geometry, human/pet identities, decisions, and model versions; XMP/catalog codec and local atomic outbox; migrate existing names/corrections. Validate parsing, unknown-field preservation, orientation, checksum mismatch, crash recovery, and exact reconstruction before calling sidecars supported.
2. **Drive round trip:** discover/create/update sidecars without touching original bytes; verify folder/account isolation, duplicate filenames, moves, inaccessible files, revisions/conflicts, delayed pair uploads, offline changes, and reinstall recovery on a disposable live folder. Make pending metadata status visible.
3. **Recognition evaluation:** local annotation/evaluation runner with a consented photo set; baseline the existing model and detector, then benchmark licensed human and animal candidates. Publish only aggregate results/config/checkpoint fingerprints in the repository, never private photos/vectors/names.
4. **Human quality upgrade:** adaptive-resolution detection, validated alignment, quality measurements, versioned embeddings, retrieval/prototypes, rejected links, stable correction remapping, and regression tests for every reproduced failure. Keep unknowns where evidence is insufficient.
5. **Individual pets:** cats/dogs first unless the owner requests another scope; head/body detection, dedicated identity features, enrollment, open-set matching, and separate calibration. Do not ship a category tag as individual recognition.
6. **Product-quality gate:** target-phone measurements, held-out precision/coverage and correction-effort results, model-license manifests, backup/restore proof, and a reviewed-label regression set. Add optional desktop acceleration only if measurements show it is needed/useful.

Outstanding input: pet species, library size, target Android phone, and availability of representative locally evaluated photos. These affect model/compute selection and evaluation scope; they do not block defining portable metadata. No private photo access, model training job, or paid licensing has been initiated.

## Primary references (checked 2026-10-10)

- [Adobe XMP specifications](https://developer.adobe.com/xmp/docs/xmp-specifications/): RDF serialization, standard properties, EXIF reconciliation, and sidecar storage.
- [Exiv2 MWG regions schema](https://exiv2.org/tags-xmp-mwg-rs.html): dimensions, region types including Face/Pet, and extensible regions.
- [OpenCV SFace model README](https://github.com/opencv/opencv_zoo/blob/main/models/face_recognition_sface/README.md): architecture, alignment, directory license, and publisher benchmark. Its published accuracy is not an MPA result.
- [AdaFace official repository](https://github.com/mk-minchul/AdaFace) and [author's license response](https://github.com/mk-minchul/AdaFace/issues/4).
- [PetFace official dataset/model repository](https://github.com/mapooon/PetFace): animal identification, per-species experiments, alignment, and explicit research-only terms.
- [InsightFace official repository](https://github.com/deepinsight/insightface): code versus pretrained-model licensing.
- [DINOv2 official repository](https://github.com/facebookresearch/dinov2): natural-image backbone and model-specific licensing differences. General visual features still need pet-identity evaluation/adaptation.
