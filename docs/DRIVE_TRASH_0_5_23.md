# MPA 0.5.23 — Drive Trash and Restore

The owner authorizes this as the first of seven remaining UX stages. Add viewer/selection Trash icons and Collections > Trash, with cached previews, Restore, exact account/folder confirmation, source recovery and truthful pending/failure states. This uses Google Drive Trash and its normal 30-day retention, not a phone recycle bin. Only media tracked by MPA is included; unrelated account files and account-wide Empty trash are excluded. Local-only originals cannot enter Drive Trash. Distinct live copies remain visible.

## Storage and identity design

Schema 9 cannot distinguish a trashed file from a moved/deleted file or durably reconcile a lost mutation reply. Schema 10 adds only drive_trash and its query indexes; all 21 existing entity schemas are unchanged, and the schema-9 Git blob is identical to the published predecessor. Originals, IDs, annotations, relationships, drafts, pins, consents, import jobs and checkpoints remain. The additive upgrade is tested against every previous table, plus the existing version-1-through-9 preservation fixtures. No destructive migration fallback is used.

Each intent captures a source/account, physical Drive ID, content hashes/size and expected revision. The separately observed server revision prevents stale sync snapshots from undoing newer actions. Account identity, source ancestry, capabilities and metadata ETag are checked before files.update(trashed=true/false). A fresh state read reconciles a lost reply before retrying. Replacement content cannot inherit a previous local identity or stale confirmation. Original bytes are never overwritten, uploaded or removed by this operation. Source aliases share the physical result; distinct files are not silently deduplicated. Restore uses the existing content/identity projection and other source aliases await their own scope verification.

Persist operations before scheduling, retain actionable partial results, bound worker execution and paging, and hide originals from normal grids only when verified trashed with no other live representation. Unfinished transfers block new Trash mutations; their destination and checkpoint are preserved. Photo-cache eviction cannot clear a local-only or unverified last original. The Trash preview identifies its source and remains scrollable with enlarged fonts.

## Validation gate

Full unit/integration, lint, APK builds and native API29/35/37 checks are required for one final application SHA, including all previous assertions and captures. Native browser/restore-confirmation/local-only-protection captures and 10k/100k ordered Trash paging benchmarks are mandatory. Publish only the exact verified CI APK with the established upgrade signer. Final evidence and physical-device limitations will be appended after immutable CI acceptance; implementation does not claim S23 Ultra or real Google permission testing.
