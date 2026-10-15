# MPA 0.5.23 — Google Drive Trash and Restore

Viewers and multi-selection now offer **Trash**. Confirm the actual files, Google account and source folder before moving them into Google Drive's Trash. **Collections > Trash** provides cached previews, Restore, refresh, pending-operation recovery and access to Sources. Drive automatically deletes trashed files after 30 days. This release does not add permanent deletion or an account-wide Empty trash action.

MPA only changes indexed media from its connected sources. Local-only originals are protected and explain why Drive Trash is unavailable. Distinct other Drive copies are preserved. Cached originals, metadata, albums, people/face associations, edits, drafts, pins, consents and import checkpoints are retained. Removing a phone download remains a separate operation.

Operations capture stable file identity, content hashes, source account and revision. Fresh account/source ancestry, capabilities and revision checks precede conditional Drive updates. Lost responses reconcile actual server state before retry; stale or replaced content cannot redirect a saved operation. Expected transport failures retain durable intents for retry, while authorization, missing revision, permission and changed-content failures remain actionable. Cancellation never claims to undo an already completed server change. Source synchronization distinguishes verified Trash from moved/deleted/unavailable records and uses monotonic revisions to reject stale results.

Confirmed Trash is hidden consistently from ordinary Photos, filters, Search and Collections unless another distinct live representation remains. Restore uses the existing source-bound media projection; other source aliases require their own scope verification. Active imports/backups cannot be silently trashed and their checkpoints/destinations remain unchanged. Work is bounded, cancellable and recoverable after restart.

## Validation

Application `00a7efbc2503eb5d15414a2e9741a70b84b47d99` passed **373 unit/integration tests**, **zero lint findings**, and **109 native tests on each of API 29/35/37 (327 total)** with no failures, errors or skips. [Host checks](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38704117187) · [Native checks and artifacts](https://github.com/GreenFuze/MyPhotosAnywhere/actions/runs/38704116852).

Tests cover exact account/source/content/revision guards, lost responses, unsupported revisions, operational Google SDK failures, conditional update rejection, restore, aliases, replacement content, active import protection, preserved local-only originals, migration data preservation and bounded bidirectional Trash paging. Native synthetic captures verify Trash browsing, restore confirmation, Back/rotation and local-only protection; all previous assertions and 42 capture scenarios remain, plus three Trash captures per API. Navigation coverage checks that collection tap points clear floating controls and verifies the Places destination before native system Back. Timeline, Search, imported-People and Trash fixtures exercise 10,000 and 100,000 records. Direct visual review, complete logs, runtime comparisons, exact successful job/artifact IDs and archive digests, benchmarks, schema and source-object proofs are retained in BUILD_EVIDENCE.json. Tests did not mutate the owner's real Drive.

Room upgrades additively from **schema 9 to 10**, adding only the `drive_trash` table and its indexes. All 21 existing entity definitions and the historical schema 9 file are unchanged. Migration tests preserve every existing table and representative data. Dependencies and min/target SDKs remain unchanged. The established upgrade signer matches, and this is the exact signed CI APK without a rebuild. Publication metadata changes documentation only.

Kotlin, Java, lint and Gradle warning gates remain enforced. The narrowly approved AGP/Gradle Configuration.setVisible(boolean) deprecation and previously documented emulator platform diagnostics remain documented; unknown application/runtime warnings and all unapproved crashes, ANRs and test failures block publication. Final complete logs contain no crash, ANR, OOM or failed-test marker. One new 167.123ms Material 3 verifier timing sample was traced to Android 17's elapsed-time emitter and the exact method definition in a DEX byte-identical to published 0.5.22. Its raw sample, source and DEX hashes are retained. No warning baseline, severity reduction, blanket suppression or weakened assertion was introduced.

## Remaining acceptance and next stages

Physical Samsung S23 Ultra, real Google Trash/Restore/expired access, TalkBack and translated Hebrew acceptance remain unverified. The owner could not reproduce the earlier JPEG crash again; its root cause is unconfirmed. Emulator measurements do not establish phone performance. The existing 250MiB import/large-video limitation is a separate backend project.

Six UX stages remain and continue unattended: sliding gallery app bar and shared motion; visual Collections and Details; adaptive sharper thumbnails and richer mosaic; continuous drag selection; direct date scrubbing; final management/accessibility audit. [Complete delivery ledger](https://github.com/GreenFuze/MyPhotosAnywhere/blob/main/docs/PHOTO_FIRST_REFINEMENT_ROADMAP.md).

Version **0.5.23**, code **34**, minimum SDK **26**, target SDK **37**, Room schema **10**.

APK SHA-256: `c1e2c0901140af4290dd47934733d18406e1d8579a26e451bac968942039ae6b`.

Upgrade certificate SHA-256: `1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4`.
