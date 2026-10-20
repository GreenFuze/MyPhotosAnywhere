# MPA Desktop and local-network web access

Owner-approved revision, 2026-10-20. This supersedes the small Avalonia setup/status interface in REDESIGN_PLAN. All original integrity, native packaging, processing, Android and release gates remain in force. The owner selected **same local network first**; internet access, port forwarding and a hosted relay are deferred.

**Latest owner correction:** Android communicates **only with Google Drive**, with no direct connection to Desktop/Processor in any circumstances. This supersedes every older phone LAN/API/pairing/discovery instruction. Browser-to-Desktop LAN access remains included. Do not implement phone LAN pairing, phone mDNS, phone private-network permission or a direct-transfer fallback.

## Product and responsibilities

MPA Desktop is a full photo client: Photos, viewer/Details, Collections, People, indexed Search, imports, explicit backup, Drive Trash/restore, sources and settings. It shares the photo-first vocabulary with Android and adapts to keyboard, mouse, large screens and accessible browser controls. Browser UI is React/TypeScript, bundled locally; the application/processing engine remains C#/.NET. No CDN, hosted website or Node runtime is required on an installed user's computer.

The engine owns credentials, catalog, jobs, plugins and synchronization. Web request/UI lifetimes never own durable jobs. Closing a browser, navigating away or losing a LAN connection leaves admitted jobs running. The small native integration owns tray/menu, Open MPA, pause, startup and explicit Exit. The same engine has an explicit headless lifecycle. Do not maintain a second full Avalonia photo interface.

Android remains a native thinner client with exclusive Drive communication, device photos, cached catalog/results, interactive viewing, explicit uploads and organization. It remains useful while the computer is asleep. Heavy processing waits for the engine. Desktop browser sessions do not become competing Processors. Drive-based enrollment establishes approved device keys; it does not require reaching the computer from the phone.

## Drive-only mobile coordination

Mobile feature settings, source requests and analysis requests use a bounded immutable Drive operation inbox and acknowledged result/receipt records in the managed workspace. This is application-level durable messaging, not a live connection or a shared SQLite file. The existing earlier decision to defer all Drive-delivered controls is superseded for these required mobile workflows.

Every request has library/actor identity, a unique operation ID, expected configuration revision, captured scope and an authenticated signature. The engine validates enrolled authority, deduplicates by ID plus payload hash and persists the received request before execution. Replayed identical requests return the same receipt; a reused ID with different content fails. Out-of-order/stale revisions produce an explicit conflict, not last-write-wins. Interrupted execution resumes its durable stages. A published receipt records applied/rejected/failed status and the resulting revision; merely uploading a request never means it applied.

Mobile shows **Pending on desktop**, **Applied**, **Conflict** or **Needs attention** from these durable facts. Poll Drive changes with bounded retries/backoff and incremental checkpoints; desktop sleep, quotas and network outages imply eventual delivery. Do not promise instant controls, invent an online heartbeat or silently start phone inference. Pause/cancel requests also need applied receipts; an offline command cannot immediately stop a running computer. Explicit original mutations retain their separately scoped user-approval/revision checks.

## Browser transport and authentication

Local desktop browsing uses a loopback-only endpoint. Other computers use a separate HTTPS LAN endpoint; plaintext remote sessions are prohibited. Discovery is an endpoint hint, never authority. Do not silently install a trusted root or expose an internet listener. Private-certificate browser trust/onboarding must be explicitly reviewed and tested. LAN access requires an owner-created invitation and a revocable authenticated browser session. A random port or private IP alone is not authentication.

Bootstrap links use strong, expiring, single-use secrets in URL fragments, never query strings, request logs or discovery. Exchange requires the exact allowed Origin and endpoint, bounded JSON and rate limits. Subsequent session cookies are HttpOnly, SameSite=Strict, Secure on HTTPS, endpoint-bound, with CSRF checks for mutations. Google refresh/access tokens remain in the native vault; neither browser receives them. Browser storage is not a Google credential store. Session revocation, expiry and host restart must require fresh authentication.

Remote browser users can manage the library after pairing. Initial Google authorization remains a host-owned installed-application flow through the host's system browser; do not incorrectly send its loopback callback to a different computer. Remote Google account setup must clearly direct the owner to the host until a separately designed supported flow exists.

## Revised implementation sequence

1. Build the real web host and exact-version frontend pipeline; authenticated bounded application API and independently tested session/Origin/CSRF controls.
2. Deliver a real read-only Drive photo/gallery/viewer slice, with no fixture data presented as the owner's library. Preserve failures separately from empty/loading states; do not claim this discovery view is the final canonical timeline.
3. Replace the full Avalonia account panel with Open MPA and native lifecycle controls, sharing existing account/job owners. Browser closure must not stop the engine. Test restart, independent clients and Exit joins.
4. Complete HTTPS LAN onboarding and browser session management for other computers. Implement Drive-only mobile enrollment, operations and acknowledged settings. Validate actual independent clients; localhost tests alone do not prove LAN operation.
5. Complete durable catalog, source configuration, Takeout, multiresolution media workers and recognition plugins; expose their actual progress and recovery in the web client.
6. Complete desktop photo-client functions and adapt Android to the same portable catalog and operation semantics. Preserve explicit original-mutation approval and human-decision precedence.
7. Complete native installers/startup/uninstall, scale/quality checks, final emulator/S23 acceptance, actual elapsed-time Google refresh and exact tested joint publication.

Existing native account/vault/job/TLS/protocol work is retained. Native validation on commit 1928b54 passed Windows, Linux, Apple Silicon and Intel macOS (Actions 39454956807); that establishes the tested foundations, not full web/LAN/install acceptance. Update IMPLEMENTATION_STATUS with actual results as this sequence advances.
