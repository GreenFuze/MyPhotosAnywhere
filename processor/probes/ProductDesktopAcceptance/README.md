# Native desktop HTTP acceptance

This manual probe uses the already authorized isolated product acceptance state. It is not an OAuth setup flow, does not open a browser, never downloads an original, and never mutates Drive files. CI compiles it on all four desktop targets without running it against an account.

Arguments, in order: packaged Desktop OAuth configuration; existing `build/desktop-account-*` state with its acceptance marker; packaged `wwwroot`; explicitly authorized disposable source folder ID; sanitized JSON report path. Stop the GUI engine and confirm that its instance lease has been released before running this probe. Never substitute an ordinary production state directory.

The selected-source fixture must contain exactly the repository's synthetic `ocr-latin.png`. The probe opens the actual engine with native protected credentials and loopback-only listeners, then verifies the local catalog, frozen viewer identity, a real bounded Drive thumbnail, idempotent browser reopen, independent browser isolation, sign-out and joined index/account shutdown. Credentials, invitation capabilities, cookie/CSRF values and media identities/names/URLs are excluded from reports and normal console output.

A successful report establishes native account/HTTP ownership acceptance only. Chrome rendering, keyboard/touch behavior, large libraries, physical Android acceptance and the final joint release remain separate checks.

The separate `--workspace-read` mode takes five arguments: packaged OAuth configuration,
the same isolated account state, explicitly authorized disposable source folder, existing
local `manual-source-fixture.json`, and sanitized report. It accepts only the pinned
92-byte synthetic JSON already present in that source. It starts no listener and verifies
the real metadata adapter's exact bytes plus wrong-folder, size, digest and original-MIME
rejections. Connection, vault and state owners join before the report is written. It does
not upload anything, process user media, establish publisher trust or activate a catalog.
CI compiles this mode on every desktop target without account execution.

The same read-only mode also verifies actual setup inspection (canonical folder identity,
read/write capabilities, sharing metadata, revision and a bounded child listing). Its
pinned synthetic folder is nonempty and must never be reported empty for fresh bootstrap.
# Read-only state prerequisite

The HTTP acceptance path refuses a state with an approved library controller before it
starts the desktop engine. Normal application startup can now resume an approved setup
and publish its initial metadata policies. This probe retains its read-only Drive scope;
live setup publication requires separate acceptance and cannot be inferred from its pass.
