# PC folder import increment

The approved PC import workflow now has native folder selection, saved review,
explicit Import all, background original verification, progress and retry. Inputs
remain read-only. The browser sends captured identities, not local paths or claimed
backup receipts. Host session, Origin and CSRF checks protect import actions.

Original transfers use the existing account and current-Root owners. First upload
IDs, immutable destination/input context and pending offsets survive unknown outcomes.
Pause/Exit join actual requests. A missing input is recoverable for that import.
After originals verify, exactly one durable metadata-only source scan is requested.
The existing scanner preserves the Drive identity and human filename in photo facts.
This does not establish final portable catalog publication or gallery activation.

## Focused evidence

Private workspace evidence lives under preflight-060. The source manifest is
current-takeout-import-ui-input-v51.json; all730 files match the validation tree and
repository. The aggregate report is current-takeout-import-ui-validation-v51.json.

| Check | Actual result |
| --- | --- |
| Initial native import ownership and browser independence | 4 passed, v47 |
| Native original recovery/activity | 2 passed, v47 |
| Actual HTTP host-owner/CSRF/contract checks | 2 passed, v47 |
| Affected original upload, inventory, controls, composition and missing-input retry | 30 passed, v48 |
| Changed import-to-source-scan integration | 5 passed, v49 |
| Strengthened import check with actual source scanner and canonical discovered facts | 1 passed, v50 |
| Final frontend typecheck and API contracts | 14 passed, v51 |
| Final strict Desktop Release build | 0 warnings, 0 errors, v50 |
| Bundled frontend | All4 exact assets verified |

The last two native runs repeat existing cases with stronger integration; they are
not extra distinct coverage. Native state, crypto, held input IO, Root ownership,
original transfer and source scanning are real. Google responses are synthetic.
The initial missing frontend dependencies were restored with the unchanged lockfile;
the failure log remains retained. No test deadline, warning suppression or assertion
was weakened. Earlier worker, metadata, source, original transport and native approval
component evidence remains recorded in IMPLEMENTATION_STATUS.md.

## Remaining acceptance

The native picker compiles; this packet does not claim actual OS chooser or browser
screen acceptance. Live Google, metadata/provenance association, portable catalog
activation, supported hierarchy, ZIP/Drive ZIP UI and complete PC photo workflows
remain. Native installers, platform/architecture tests and exact release publication
are also unfinished. Android is deferred and has no PC release gate.
