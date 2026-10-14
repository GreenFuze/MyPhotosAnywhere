# MPA 0.5.19 — Takeout metadata preview

Takeout previews summarize valid media metadata, exported people labels, missing sidecars, unreadable records and items needing review. Filters show all items, media with people labels, media without metadata, or records needing review. Filtering never changes import selection. Selected-media and selected-label totals update independently of the filter. Names remain provider labels, separate from confirmed face identities; the preview does not start recognition.

Metadata parsing runs off the UI thread, observes cancellation and is keyed only by media identities and raw metadata. Transfer progress and checkbox updates reuse the parsed snapshot. Stale snapshots and duplicate identities fail fast. Malformed external metadata remains explicit rather than being silently presented as a record without people. Picker semantics stay unchanged.

Read-only local inspection of the owner's October 11 export found 1,661 media files in recognized formats and 1,644 media sidecars. The existing MPA decoder parsed and uniquely matched every sidecar, including 934 with exported names; 17 media files had no sidecar. No private records, names, paths or photos are included in repository fixtures or public artifacts. This does not prove that an earlier phone import retained those records, nor transfer Google's face models or coordinates.

Room schema 9, import workers, durable jobs/checkpoints, source destinations, recognition consent, originals and established upgrade signer remain unchanged. Native API 29/35/37 and strict host checks are required before publication. S23 Ultra acceptance and the unreproduced JPEG crash remain unverified.

The provided part has 1,647 media within the existing size limit and 14 MP4s above 250 MiB. Exported names occur on 923 size-eligible media and 11 oversized videos. The preview shows all discovered labels separately from selected-media totals; unsupported files remain preserved.

## Final publication evidence

[Immutable source, exact CI APK, validation and remaining acceptance](releases/v0.5.19.md). Publication metadata changes no application, toolchain or workflow objects.
