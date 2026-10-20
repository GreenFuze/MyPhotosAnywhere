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

## Read-only folder visibility

After consent, the following checks the selected disposable folder with `files.get` and a bounded first page of direct children with `files.list`:

```text
dotnet probes/DriveFeasibility/bin/Release/net10.0/DriveFeasibility.dll inspect .private/desktop-client.json .private/account-probe file <disposable-folder-id>
```

Only HTTPS Drive GETs are sent, redirects are not followed, and response bodies are limited to 64 KiB. A denied/not-found folder observation is recorded explicitly; it is not reported as an empty library or a successful recursive discovery. Child-list HTTP failures remain failures. A private report records actual scopes/statuses and visible counts, without credentials. Permission observations are provisional feasibility evidence, not final release acceptance.
