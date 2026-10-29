# Current PC implementation scope

Owner direction, 2026-10-27: focus this stage on the PC application and processor.
Defer new Android implementation. Preserve the existing Drive-only Android
boundary; the phone never connects to Desktop or its LAN API. This changes work
priority, not the final joint-release acceptance requirements.

The product remains substantially unfinished. A passing component build is not
a usable, validated 0.6 release. Complete these five milestones in order where
dependent, and perform independent desktop work while checks run:

1. Finish current-authority recovery and integrate it into the running desktop
   engine. Preserve immutable pending work, exact original identities, selected
   catalog continuity and joined resource ownership. Handle signed partial work,
   revoked authority, handover, ambiguous Root outcomes and verified dependency
   reuse explicitly. Do not upload obsolete facts to make recovery convenient.
2. Finish the first-party processing pipeline: Takeout metadata, previews, people,
   object labels, OCR and video, with durable per-item retry and protected inputs.
   Publish results and human operations through Google Drive with real receipts.
3. Finish the web photo client and real workflows: gallery and viewer continuity,
   People rename/merge, indexed Search, collections, imports, backup, offline space
   cleanup, explicit Trash/restore, accounts/settings and background progress.
   Development continues to listen only on 127.0.0.1.
4. Produce and exercise Windows/Linux/macOS Intel and ARM distributions, including
   startup, tray/Exit where supported, upgrade, uninstall and retained user data.
   Use the approved documented personal-test signing path where paid publisher
   credentials are unavailable; do not claim unperformed native platform tests.
5. Validate an immutable release revision, actual independent Drive clients,
   warnings, performance, safety and installers. Verify exact assets, signatures,
   checksums, tag and publication. Keep deferred Android/final-device acceptance
   explicit before claiming the originally approved joint release complete.

Do not restart passed checks without a source change, a failure or a concrete
unresolved concern. Do not increase a failing test's deadline, discard assertions,
accept unexplained resource warnings, or replace native evidence with compilation.
The implementation ledger and raw evidence remain authoritative for completed
components; this list records work still required, not completed features.
