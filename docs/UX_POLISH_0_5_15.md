# MPA 0.5.15 — gallery gestures and date navigation

Saved 2/3/4-column density has an explicit Photos filter control and a two-finger pinch alternative. Density changes retain the visible canonical identity and pixel offset; single-finger scrolling, selection and the full-screen viewer keep their existing gesture paths. Search results share the density preference.

A compact date rail opens month/year navigation. Its bounds come from matching timestamp endpoints in the existing read-only projection, with no full-library subscription or retained membership list. Month scrubbing previews a date; Jump seeks through the repository only on confirmation. Unknown dates remain in the gallery and never acquire invented timestamps. Exact-day Jump to date remains available. Operational errors keep the grid visible with retry; query changes cancel stale navigation. Navigation selection colors use Compose's system-scaled animation clock without animating media geometry or replacing rendering/download/lifecycle protections.

Room schema 9, existing workers, durable jobs and the upgrade signer remain unchanged. Publication requires the full host/native matrix and independent exact-APK verification. S23 Ultra acceptance and the reported JPEG crash remain unverified. This completes the planned UX implementation batches, subject to real-device acceptance and resulting fixes.
