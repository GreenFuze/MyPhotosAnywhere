# MPA 0.5.13 - Search and filtering

The next phase replaces Search's full-library subscriptions with a dedicated paged metadata workflow. Search offers visual shortcuts, eight clearable recent queries in saved screen state, independent media/source/organization/date filters and deterministic newest-first results.

Local matching preserves filename, place, labels, date, MIME, people, imported metadata and existing favorite/video/image/edit/GPS keywords, adding album names. Unicode and punctuation retain Kotlin literal matching. Unlinked remote-only matching remains filename/MIME matching; unavailable recognition is never implied.

Search scans one cancellable cursor per page through the existing read-only projection. Cursor windows and retained pages are bounded. Searching a large unindexed text field remains linear; benchmarks must report that cost rather than invent relevance or full-text indexing. Viewer snapshots apply the identical predicate, then retain only existing identity columns. Original retrieval, imports and Room schema 9 are unchanged.

Search state and its most recent anchored query survive recreation. Filters do not mutate Photos. Back returns to Search's originating context. Recent queries are not written to analytics, preferences, backup files or a new persistent table. Selection and preview-first viewing reuse existing components.

Release gates: unit/integration tests, zero lint findings, strict build warnings with previously approved tooling exceptions only, native API 29/35/37 checks, search/timeline 10k/100k measurements, screenshots, exact CI APK and established signer verification. S23 Ultra acceptance and the reported JPEG crash remain unverified.
