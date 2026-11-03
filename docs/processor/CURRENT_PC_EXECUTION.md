# Current PC implementation scope

Latest explicit owner direction, 2026-10-29: make the PC application and processor
work end to end first. Android may break and will be rebuilt or fixed afterward.
Android compatibility, Android checks, S23 and joint-release acceptance do not
gate PC implementation, PC acceptance or publication of the tested PC version.
The PC workflow must be usable without requiring the mobile app. Do no Android
implementation, investigation or test reruns in this stage. This direction
supersedes earlier instructions requiring joint acceptance before PC release.

Owner scope concern, 2026-11-02: the owner is concerned about implementing
unwanted things. Every new addition must map to an included user behavior in
REDESIGN_PLAN.md plus DESKTOP_WEB_REVISION.md and the later PC-only directions.
Finish usable PC workflows; do not add optional product features or general
infrastructure without a demonstrated need in the current approved workflow.
Prefer the existing owners/components and keep changes narrowly scoped. Technical
component evidence does not establish a usable product. A consequential product
scope departure needs a concrete owner decision; routine implementation stays
unattended. Original protection, human metadata, current permission, immutable
recovery, fail-fast, ownership and required PC validation remain mandatory.

Current next vertical step (2026-11-02): finish import metadata and portable catalog/
gallery integration. Native folder selection, review/start/progress/retry, shared
current-Root original transfers and scoped interrupted-upload recovery are now wired.
Completed copies request exactly one durable source scan; the actual scanner reads
the first Drive ID and preserves the human filename in discovered photo facts.
Evidence: current-takeout-import-ui-validation-v51.json in preflight-060 and
PC_IMPORT_VALIDATION_2026_11_02.md. The later isolated v54case proves native portable
activation/query with synthetic Google; actual browser/OS/live acceptance remains.
Committed5638738 PC CI is verified
across60jobs/72reports; do not rerun those passed checks on unchanged source. The
isolated v60worker apphost/bundle prerequisite passes native and actual Windows
publish execution checks. The locally validated continuation now binds optional sidecars to
verified canonical originals and runs the installed worker through a bounded native
import runner. The desktop queue now invokes it automatically after original
verification, retains processing counts and resumes private captured sidecars after
input removal. Grouped Pause/Exit joins the owners; metadata capacity is a per-import
issue rather than an engine fault. The UI separates metadata processing from
verified originals and pending catalog work. Native composition/integration,
retained-result integrity, the unchanged1024item bound and frontend contracts pass
local focused checks (current-takeout-metadata-desktop-validation-v89.json).
Per-item metadata details now retain the captured filename and cause in bounded
60-row pages. Restoring access retries only unavailable metadata while reusing
verified originals and permanent classifications. Local native/HTTP/API evidence
is current-takeout-issue-validation-v117.json; actual UI/live acceptance remains.
Supported imported facts now reach actual portable publication/Root activation and
canonical gallery/viewer reads with separate provenance, observed filename/capture
precedence, conflict/size/version attention and old-finished-import recovery. Evidence
current-imported-photo-validation-v156.json includes real native fresh/legacy flows,
127Protocol/49distinct Core/52Web/7Services/20frontend checks and unchanged100k staging.
Explicit original selection for supported unmatched sidecars is now wired through
bounded verified-original pages, host-only browser commands, immutable native
selection recovery and the installed worker into portable publication. Unsupported
or unavailable inputs cannot be reassigned. Selection and its scheduling wake-up
commit atomically; a concurrent selection cannot be hidden by an older pass marker.
Native browser-closure/publication, database reopen, rollback, capacity and API
checks are recorded in the current manual-association validation evidence.
Actual visual/live acceptance remains unverified. Next finish corrected-input and
version repair, cloud raw
metadata preservation and owned generated-cache retirement; complete the remaining
PC workflows and actual UI/live/install acceptance. The metadata workflow is not
fully accepted until those steps pass. The Windows UI tool failed to initialize;
keep visual acceptance unverified and continue independent work. Do not confuse
framework-dependent worker publish evidence with self-contained installers.
Canonical Drive asset identity stays library/account/file based. Inventory MediaId
is a separate stable processing/provenance identity requiring an explicit metadata
association; do not replace canonical IDs or infer a second asset defect. Preserve
human metadata. Complete hierarchy and ZIP/Drive ZIP input paths as approved.
Do not restart passed inventory/worker/signing foundations or use the overly broad
AccountServiceTests class filter.

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
   checksums, tag and publication. PC acceptance and publication stand on their
   own required gates. Android rebuilding and acceptance are subsequent work.

Do not restart passed checks without a source change, a failure or a concrete
unresolved concern. Do not increase a failing test's deadline, discard assertions,
accept unexplained resource warnings, or replace native evidence with compilation.
The implementation ledger and raw evidence remain authoritative for completed
components; this list records work still required, not completed features.
