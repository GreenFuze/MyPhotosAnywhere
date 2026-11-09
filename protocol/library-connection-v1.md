# Public library connection — protocol 1 candidate

This is a bounded exchange of an independently approved public library pin. It is
not a signed authority document, a device grant or a network endpoint. Its parser
cannot enroll a controller. Both clients continue to communicate through Google
Drive; no phone-to-desktop transport is carried or implied.

## Export and wire form

Only an authenticated host-owner browser can request `/api/library-connection`.
An unapproved library returns `connection:null`. An approved library exports its
exact locally persisted controller/workspace pin, validating the P-256 key and its
fingerprint again. Reads never approve a workspace, publish metadata or create an
operation. LAN browser sessions cannot export the pin through this route.

The code is `mpa-library-v1:` followed by canonical unpadded base64url of canonical
UTF-8 JSON. The document is at most 2,048 bytes and the code at most 4,096 ASCII
characters. Decode rejects noncanonical pad bits, padding, whitespace, duplicate
or unknown members, noncanonical JSON and unsupported formats. The root has exactly:

- `format`: `mpa-library-connection`.
- `major`: integer 1.
- `libraryId`: canonical lowercase, nonzero hyphenated UUID.
- `controller`: exactly `actorId`, `epoch`, `publicKeySpki`, `keyId`.
- `workspace`: exactly `accountId`, `folderId`.

The controller ID is a canonical nonzero UUID. Epoch is a positive canonical Int64
decimal string. Its public key is canonical standard-base64 DER P-256 SPKI, at most
512 decoded bytes, with lowercase SHA-256 `keyId` calculated from those actual
canonical bytes. Wrong curves, RSA, private keys, trailing DER and altered hashes
fail. Workspace IDs retain existing opaque, case-sensitive ID rules; account is the
Drive permission ID, never an email, and folder is concrete rather than `root`.
The code contains no Google token, private key, hostname, port or original-write grant.

The export response carries only `code`, `documentSha256` and `controllerKeyId`.
The document hash binds later review to the exact decoded bytes, including workspace
and epoch. The browser offers explicit selection/copy and renders the fingerprint
as text. It does not claim that publication has finished or that another client
has enrolled. Connection codes identify a library; possession does not grant Drive
access. Shared fixtures contain only synthetic/public test material.

## Required receiving-client approval and adoption

Receiving UI must require an explicit human decision on this exact code obtained
from the independently trusted host, display the library/fingerprint and chosen
Google account, and bind approval to `documentSha256`. A Drive file, self-signature,
discovery result or successful decode cannot supply that independent approval.

Before committing a public pin, the actual owned Google connection must identify
its permission ID and observe the exact concrete workspace. An email/display name,
a cached success or a different account cannot satisfy this check. Rechecking after
an operational failure must retain the originally approved code and workspace.
A previously accepted different key/epoch/workspace must never be silently replaced.
Pin and accepted-history persistence must be excluded from Android backup/transfer
unless a separately designed, explicitly authorized trust restore is implemented.

Pin approval is distinct from catalog activation. The Drive reader must persist
controller history and highest accepted sequences, reject equivocation and rollback,
honor publisher revocation and source exclusions, bound downloaded/expanded data,
verify actual full dependencies, and stage a complete generation before switching
the current gallery atomically. Failed/partial adoption retains the last complete
gallery. Old signatures alone cannot revive retired epochs. Original media remains
unmodified by connection and catalog consumption.

Read-only connection grants no mobile operation-signing authority. Durable device
enrollment, operation inboxes, receipts and original-mutation approvals remain
separate required work. Shared codecs, host export and the owned Android public-pin
approval/store are implemented. The Android owner rechecks actual account/folder
metadata around review, requires the approved document digest, atomically stores
the code and its approval marker in private nontransferable SQLite, rejects changed
pins, and joins cancellation/cleanup. It does not activate a catalog or issue a grant.
The native Android connection UI performs actual account/folder review and requires an
explicit source acknowledgment before approval. Its private schema-2 account selector
migrates schema 1 without replacing accepted pins, remains excluded from transfer and
never supplies authority or stores a Google token. Foreground launcher attachments have
exact lifetime identities and transfer ownership before activation; pending consent is
memory-only. Process recreation requires fresh review rather than restoring approval.
Actual independent Drive catalog adoption, device write grants and final end-to-end
acceptance remain unfinished.
