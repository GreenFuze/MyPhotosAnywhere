# Drive feasibility — Stage 0, incomplete

Observed 2026-10-19 through the owner's selected Chrome session. **Immediate Gate A has not passed.** Browser access and a client registration are not API/refresh proof.

## Configuration observations

- Existing MyPhotosAnywhere Cloud project, Drive API enabled.
- External OAuth audience remains **Testing**. The published seven-day Testing authorization/refresh limit applies to Drive grants; moving to a personal-test audience does not avoid it.
- Existing configured scopes: `drive.file`, restricted `drive`, and Photos Picker's selected-media read scope. No new scope added.
- Existing Android clients preserved. Owner explicitly approved creation of a new Desktop client named MPA Processor. Configuration is kept outside tracked files.
- Separate empty disposable Drive folder created in the intended account; private folder ID and screenshots remain in local evidence. Existing photo library untouched.
- Production publishing is disabled until branding is completed. The owner supplied their existing GitHub Pages custom domain. MPA-specific homepage/privacy/terms are prepared and visually reviewed locally; public publication and the security-sensitive OAuth audience change await the actual approval response. Prepared Cloud fields have not been submitted.

## Executable preparation

`processor/probes/DriveFeasibility/` implements only bounded authorization/short-refresh preparation, using official Google.Apis.Auth 1.77.0, PKCE, explicit validated random state, forced loopback, and a Windows DPAPI datastore. It never copies phone credentials or stores plaintext tokens. Scope is chosen explicitly, and returned scopes are checked.

The synthetic storage command passed on Windows: encrypted round trip, expiry timestamp persistence, no plaintext marker, type isolation, corrupted-ciphertext rejection, deletion. The actual loopback receiver also passed synthetic matching-state, rejected-state and cancellation/temporary-file cleanup checks. A directory lease prevents concurrent probes; stale prepared requests fail explicitly. Compiler/analyzer build had zero warnings/errors. These checks did not contact Google or grant access.

## Required live evidence

| Probe | Status |
| --- | --- |
| Production configuration and personal-use limitations | Pending reviewed publication/Google confirmation |
| Fresh desktop consent and protected persistence | Not run; requires exact grant approval |
| `drive.file` case with actual returned scope | Not run |
| Manually created existing/nested/new file discovery | Not run |
| Android/Desktop shared protocol-file access | Not run |
| Immutable publication, range reads, checksum fields | Not run |
| Lost upload response and reserved-ID reconciliation | Not run |
| Short refresh after expiry, restart and network interruption | Not run |
| Denied/revoked grants, 401/403/429/5xx handling | Not run |
| Real beyond-seven-day authorization | Not started; do not claim a short refresh substitutes |

Restrict future API writes to the named disposable folder. Full Drive OAuth is technically account-wide; internal folder checks do not turn it into a Google-enforced folder grant. Do not revoke the existing app's working grants merely to manufacture an isolated narrow-scope test. Record any actual grant coupling and resolve it explicitly.

## Primary sources

- [Installed-app OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Refresh-token expiration](https://developers.google.com/identity/protocols/oauth2#expiration)
- [Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)
- [OAuth audience/publishing](https://support.google.com/cloud/answer/15549945)
- [Branding and app-domain links](https://support.google.com/cloud/answer/15549049)
- [Google .NET PKCE broker source, 1.77.0](https://github.com/googleapis/google-api-dotnet-client/blob/v1.77.0/Src/Support/Google.Apis.Auth/OAuth2/GoogleWebAuthorizationBroker.cs)

These sources explain supported behavior; the live rows above still need observed results. Public evidence must not contain tokens, authorization codes/URLs, private folders, media or account names.
