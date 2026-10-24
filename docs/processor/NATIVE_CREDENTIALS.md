# Native credential ownership

Candidate product adapter for 0.6.0; native validation status is in IMPLEMENTATION_STATUS.md. This does not activate OAuth or migrate the existing private probe grant.

`NativeCredentialVault` owns and serializes background native calls. A secret identity combines the installation UUID with SHA-256 of the opaque account/key identity; email and source paths do not become filenames or keyring labels. Records have a 128 KiB binary bound. Returned `SecretLease` buffers are cleared on disposal and excluded from JSON diagnostic serialization. Writer copies are cleared after the actual native operation completes. OS/SDK copies are outside that managed buffer's ownership; this is not a promise that all copies of a credential can be erased from process memory.

Windows uses DPAPI CurrentUser, installation-scoped entropy, bounded reads and atomic encrypted-file replacement. Pending files contain ciphertext only. Existing linked directories/files are rejected, including linked ancestors. Corrupt ciphertext is preserved and reported; it never becomes an empty/disconnected account silently.

macOS uses Security.framework generic-password items with an MPA service and exact installation/key account. CoreFoundation dictionaries, strings, data and returned objects have owned SafeHandles. Keychain access prohibits authentication prompts from these background operations; a locked/inaccessible keychain produces an actionable numeric OSStatus without copying its account-specific messages into logs.

Linux uses libsecret's **direct SecretService API**, avoiding configurable alternate file backends. Attributes identify one installation/key. Search does not unlock or prompt; locked and duplicate records fail explicitly instead of being interpreted as absent. Existing items are updated/deleted by identity. New items use the persistent default collection. A missing service has no plaintext fallback. Each call owns a GIO cancellable and 30-second deadline, native schema/hash tables, references, binary SecretValues and GErrors. GError messages are not logged. A headless installation therefore needs an available, unlocked Secret Service; unattended daemon packaging and locked-store acceptance remain separate integration work.

Cancellation does not mean an OS write was rolled back. A canceled or failed write must be reread before a retry is treated as new. Disposal fences new and already queued admissions, joins the actual active call and then releases its backend. It never abandons the native call using a timeout wrapper and claims Exit completed. The current managed tests hold a native fixture across cancellation and Exit to verify that boundary.

The native CI suite uses generated synthetic bytes only. Linux receives a private D-Bus session and disposable keyring; its foreground daemon is owned and joined. Windows tests inspect ciphertext and corrupt only their newly generated record. macOS tests use unique generated installation namespaces. Native installation, unlock/relock and Google refresh tests still need explicit evidence; synthetic round trips do not establish these.

Primary API references:

- [DPAPI ProtectedData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)
- [Apple generic password items](https://developer.apple.com/documentation/security/ksecclassgenericpassword)
- [Apple authentication UI failure mode](https://developer.apple.com/documentation/security/ksecuseauthenticationuifail)
- [libsecret service search](https://gnome.pages.gitlab.gnome.org/libsecret/method.Service.search_sync.html), [search flags](https://gnome.pages.gitlab.gnome.org/libsecret/flags.SearchFlags.html), [store](https://gnome.pages.gitlab.gnome.org/libsecret/method.Service.store_sync.html)
