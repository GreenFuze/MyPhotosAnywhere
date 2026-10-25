# Stage 0 authorization probe

This bounded Windows feasibility tool uses Google.Apis.Auth 1.77.0 with PKCE, a random validated OAuth state, the official loopback receiver forced to `127.0.0.1`, and DPAPI CurrentUser storage. It is not the cross-platform product authorization implementation or proof that long-term refresh works. Linux Secret Service and macOS Keychain adapters remain Stage 2/3 work.

Build with the pinned SDK from `processor/`:

```text
dotnet restore probes/DriveFeasibility/DriveFeasibility.csproj --locked-mode
dotnet build probes/DriveFeasibility/DriveFeasibility.csproj --no-restore -c Release
```

The `vault-check` command uses random synthetic markers only. It checks protected round trip, expiry timestamps, absence of plaintext markers, type isolation, rejection of corrupted ciphertext and scoped record deletion. It additionally injects Drive HTTP permission/auth/rate-limit/server failures, redirects, malformed responses, known/unknown-length oversized bodies and cancellation. It also exercises the actual loopback receiver with synthetic responses: matching state, rejected mismatched state, cancellation propagation and temporary-request cleanup. It does not contact Google or need a client configuration file:

```text
dotnet probes/DriveFeasibility/bin/Release/net10.0/DriveFeasibility.dll vault-check unused.json .private/synthetic-vault file
```

Live authorization **requires explicit account/permission consent**. Do not run merely because credentials were registered in Cloud. After that consent is ready, use a private directory outside tracked files:

```text
dotnet probes/DriveFeasibility/bin/Release/net10.0/DriveFeasibility.dll authorize .private/desktop-client.json .private/account-probe file
dotnet probes/DriveFeasibility/bin/Release/net10.0/DriveFeasibility.dll refresh .private/desktop-client.json .private/account-probe file
```

The last argument selects `file` (`drive.file`) or `drive` (full Drive permission). These use separate vault subdirectories. Full Drive permission is not folder-scoped by Google. Feasibility API writes will be restricted to the separately approved disposable folder; the tool supports authorization/refresh and bounded read-only folder visibility checks; it does not mutate Drive files.

The authorization command prepares its URL in the private `authorization-url.txt` and waits up to 15 minutes. Open that exact URL in the owner's selected system browser and review Google's account/grant screen. It does not launch a different default browser or presume a grant. The request state is checked before code exchange, and the temporary URL is removed after completion/cancellation. A private file lease prevents concurrent probes in the same directory. A pre-existing authorization URL is rejected rather than overwritten; cleanup removes only a request this probe created. Tokens are encrypted with DPAPI before an atomic local file promotion; no plaintext fallback exists. Credentials are not printed or included in the observation reports. Reports record whether an interactive request actually occurred; loading an existing protected grant is not evidence of fresh consent.

Refresh deliberately runs in a new process from the protected stored record and never opens a browser. Requested scopes are compared with actual returned scopes; a missing required grant fails rather than reporting success. Revocation/denial is an operational case for the eventual product, not permission to repeatedly clear grants or silently reauthorize this probe.

Keep private token/configuration/URL/observation files out of CI, Git, public diagnostics and build caches. The client registration alone does not grant account access. OAuth Testing retains Google's seven-day limit; a successful immediate refresh does not prove the required real-duration acceptance.

## Conditional media diagnostics

`conditional-media` accepts the approved disposable root, creates and journals its own
tiny synthetic file, and exposes no existing-file target or permanent deletion. It uses
v2 media updates with captured strong entity tags, checks stale-tag412 responses and
complete downloaded bytes, and durably discards one actual200 completion before its
body reaches the caller. Exact reads retain both metadata fences before rejection.
The20-request bound and every existing transport guard remain.

The live synthetic sequence passes after checksum-bearing v3 metadata is captured
before the exact v2 body-read window; within-capture fences remain exact. Five earlier
failed exploratory logs/identity journals are retained. Numeric versions can move
between operations with unchanged content tags: an initial create/read also moved1->2
before any rejected upload, and independent same-ID recovery later observed delta0.
The cause of the advances is unverified. The original exact+1 hypothesis is falsified
and retained as a diagnostic observation, not a product/backend contract. Rewinds,
changed content tags/bytes, wrong status/scope and moving within-capture fences still
fail. Numeric versions never substitute for actual entity tags as write preconditions.

`conditional-media-recover` accepts only a retained journal basename in that same private
directory, requires its actual discarded-completion receipt and fixed original pending
identity/condition, and rechecks the actual parent, owner, binary MIME and exact synthetic
checksum/bytes. It allocates no new ID and submits only the retained stale pre-completion
tag to the same file. Live recovery in a new process passes with exact pending bytes,
HTTP412 and no replacement identity. Offline checks reject corrupt/missing receipts,
aliases, conditions, rollback, changed content tags, size overruns and unsafe paths.

This proves one small owned synthetic path using the existing Windows grant, not
production publication authority, shared-drive/additional-account behavior, physical
socket/process failure or final joint-release acceptance. Actual failures are preserved;
no application assertion, warning gate, existing deadline or request bound was relaxed.
Neither mode grants original writes.

## Read-only folder visibility

After consent, the following checks the selected disposable folder with `files.get` and a bounded first page of direct children with `files.list`:

```text
dotnet probes/DriveFeasibility/bin/Release/net10.0/DriveFeasibility.dll inspect .private/desktop-client.json .private/account-probe file <disposable-folder-id>
```

Only HTTPS Drive GETs are sent, redirects are not followed, and response bodies are limited to 64 KiB. A denied/not-found folder observation is recorded explicitly; it is not reported as an empty library or a successful recursive discovery. Child-list HTTP failures remain failures. A private report records actual scopes/statuses and visible counts, without credentials. Permission observations are provisional feasibility evidence, not final release acceptance.
