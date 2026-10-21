# Native desktop HTTP acceptance

This manual probe uses the already authorized isolated product acceptance state. It is not an OAuth setup flow, does not open a browser, never downloads an original, and never mutates Drive files. CI compiles it on all four desktop targets without running it against an account.

Arguments, in order: packaged Desktop OAuth configuration; existing `build/desktop-account-*` state with its acceptance marker; packaged `wwwroot`; explicitly authorized disposable source folder ID; sanitized JSON report path. Stop the GUI engine and confirm that its instance lease has been released before running this probe. Never substitute an ordinary production state directory.

The selected-source fixture must contain exactly the repository's synthetic `ocr-latin.png`. The probe opens the actual engine with native protected credentials and loopback-only listeners, then verifies the local catalog, frozen viewer identity, a real bounded Drive thumbnail, idempotent browser reopen, independent browser isolation, sign-out and joined index/account shutdown. Credentials, invitation capabilities, cookie/CSRF values and media identities/names/URLs are excluded from reports and normal console output.

A successful report establishes native account/HTTP ownership acceptance only. Chrome rendering, keyboard/touch behavior, large libraries, physical Android acceptance and the final joint release remain separate checks.
