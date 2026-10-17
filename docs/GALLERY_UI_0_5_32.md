# MPA 0.5.32 — Gallery and Search follow-up

Candidate implementation. Publication requires final immutable host/device checks, complete diagnostic and screenshot review, unchanged-schema proof and verification of the established upgrade signer.

## Scope

Remove the gallery's edge scroll handle and track. Retain the animated floating date capsule, its semantic heading, 48dp activation target and Jump to month/year dialog. Unknown dates remain explicit. Date seeking, source filtering, canonical anchors, import continuity, viewer return and rotation remain covered through the retained Jump workflow.

Tie empty results to an actual completed query and restoration generation. A transient zero Paging slot count does not establish an empty library. Keep a completed empty Search result visible during same-query refresh; show compact updating/retry feedback where relevant. New queries load independently, usable photos remain visible on operational failure, and failed first loads do not claim a successful empty result. Search never substitutes the default gallery's empty-library message.

Use consistent mosaic columns. Compact four-column layouts have two-column portrait tiles and two regular adjacent columns; two/three-column layouts use one-column portrait tiles spanning rows. Canonical block identities determine stable left, middle or right placement. Limit portrait height, preserve source traversal order and expose actual column spans to accessibility. Existing bounded layout templates retain geometry through arrivals and sharper previews; explicit layout/density changes retain their canonical pixel anchor. RTL mirrors placement.

## Validation plan

Run all existing checks and additional presentation-state, geometry, span/distribution and viewport-boundary tests. Device checks cover repeated empty-search invalidation, rotation/filter clearing, compact RTL mosaics at 2x font scale, retained date jumping and absence of the scroll handle. Existing arrival, selection, source, viewer, import, People, camera/video and revoked-permission assertions remain. Additional capture names are `mpa-search-empty-stable` and `mpa-blocks-balanced-rtl-large`.

Version 0.5.32 / code43. Retain minimum SDK26, target37, dependency pins, Room11 and historical schema files. Do not change originals, metadata, identities, consents, captured destinations, jobs or checkpoints. Publish the exact verified CI APK only after the preceding 0.5.31 delivery has passed its release gates. Physical S23 Ultra, live Google operations, human accessibility and physical frame-rate acceptance remain separate and unverified.

## Explicitly deferred

Object-search improvements await the architecture discussion. The optional Windows/Linux/macOS MPA Processor remains a proposal: phone and Processor would communicate only through Drive, using stable media identities and incremental catalogs with a usable phone-only mode. No Processor code, network service, protocol migration or new recognition model is part of this work.
