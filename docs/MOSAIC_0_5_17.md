# MPA 0.5.17 — justified mosaic

The owner requested the dynamic photo layout visible in Google Photos screenshots. MPA uses an aspect-aware justified mosaic: different-width tiles in aligned rows, retaining chronological order and day headings. This is not a claim to reproduce Google's private layout algorithm or portrait tiles spanning multiple rows. Square remains an explicit Photos > Filters > Photo layout option; Mosaic is the default. Density still chooses a target of 2/3/4 photos per row. Incomplete rows keep their ordinary footprint.

Preview header dimensions are read on Dispatchers.IO, without allocating image pixel buffers, fetching originals or rewriting metadata. Existing MPA previews are orientation-normalized. Unknown/missing previews use square geometry. A per-ViewModel 600-identity LRU freezes geometry within the active 300-item paging window; newly available shapes can appear on later visits after eviction or process recreation. Rendering uses one immutable row/cell snapshot and canonical-key anchor lookup. Layout planning is bounded by retained pages. Minimum tile targets, logical collection semantics, selection, date navigation, viewer sessions and all storage/import contracts remain.

Version 0.5.17/code 28 retains schema 9 and the established upgrade signer. Publication requires full host/API 29/35/37 native checks, strict diagnostics and exact signed CI APK verification. The owner reports being unable to reproduce the JPEG crash; its cause is still unconfirmed, and it is not claimed fixed. S23 Ultra acceptance remains pending.

## Final publication evidence

[Immutable source, exact CI APK, validation and remaining acceptance](releases/v0.5.17.md). Publication metadata changes no application, toolchain or workflow objects.
