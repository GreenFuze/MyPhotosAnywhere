# MPA protocol 1 — candidate contracts

Stage 1, under implementation. These are isolated codecs/domain rules; no live catalog,
pairing or original-modification path is enabled by adding them.

## Portable bytes and signatures

UTF-8 JSON without BOM, invalid Unicode, duplicate property names, comments, trailing
commas or trailing content. The canonical form uses RFC 8785 escaping and UTF-16
ordinal property order, with a deliberately narrower integer-only number domain:
`-9007199254740991` through `9007199254740991`. No floating point/exponents. Negative
zero canonicalizes to zero. Strings are not case-folded or Unicode-normalized.

64-bit counters, byte lengths and UTC epoch milliseconds are canonical decimal
strings. Positive counters are `1` through `9223372036854775807`, without signs or
leading zeroes. Optional unknown fields remain inside the signed canonical bytes.
Unknown required capabilities or a different major version produce an actionable
compatibility error; consumers retain the last verified view. A newer minor version
alone is compatible when every required capability is supported.

NIST P-256 / SHA-256 (`ES256`), 64-byte IEEE P1363 `r || s`, canonical unpadded
base64url. `keyId` is lowercase SHA-256 of canonical DER SubjectPublicKeyInfo. The
signature travels separately from the body; it covers the complete canonical body.
Android converts its provider's DER ECDSA format at the boundary. Signing keys remain
owned by their secure-vault lease. A valid signature is not authority: consumers must
already trust that actor/public key for this library, epoch and document kind from
an explicitly approved pairing record. Received self-supplied keys are never trusted.

Signed document header: `format: "mpa"`, nonnegative integer `major`, `minor`, nonzero
lowercase hyphenated UUID `libraryId`, `actorId`, positive decimal-string `epoch`,
`sequence`, document `kind`, ordinally sorted unique `requiredCapabilities`, and an
object `payload`. The signed envelope bound is 256 KiB. Payload-specific original
action fields will use a closed schema before any mutation is enabled.

JSON limits: 16 MiB expanded per catalog chunk, 256 KiB per signed document, 64 KiB
per string, 128 UTF-8 bytes per property name, 256 members per object, 1,000 entries
per array, nesting depth 32. Catalog chunks additionally have a 4 MiB compressed
bound. Readers enforce both transport and expanded bounds before activation.

## Representation identity

Use the connected account's Drive permission ID (never email/display name) plus
opaque case-sensitive Drive file ID within the library namespace. SHA-256 framing:
UTF-8 `mpa:drive-representation:1\0`, 16 network-order library UUID bytes, then each
account/file UTF-8 byte string prefixed with its four-byte unsigned big-endian byte
length. The first 16 digest bytes become UUID version 8 with the RFC variant bits.
Golden vectors were generated independently with Python hashlib/uuid/struct.
This does not deduplicate equal content or names. Verified transfer/alias contracts
must preserve a previously issued device-origin asset ID and old lookup identities.

## Human fields and conflicts

One operation has a unique nonzero ID, actor ID, positive persisted sequence,
target identity, field name, up to 64 distinct causal parents and a canonical value.
An exact operation replay is idempotent. Reusing an operation ID with different
content or an actor sequence is an explicit failure, not an overwrite.

The bounded field register accepts out-of-order delivery. Missing-parent operations
stay pending. Known-parent cycles and non-increasing same-actor ancestry fail
without replacing accepted state. Causally later edits replace only their observed
ancestors. Concurrent alternatives and maximal common prior values remain available
for conflict presentation. A resolution names every observed conflicting parent;
a later-arriving concurrent edit can reopen the conflict. No wall-clock winner.
This primitive does not authorize actors, persist an outbox or implement structural
person merges. The durable owner must do those checks/transactions separately.
The 4,096-operation per-field budget requires acknowledged compaction; no silent
history eviction. Global actor sequence uniqueness also belongs to the durable owner.

Shared fixtures contain only synthetic/public test material. The ephemeral private
key used to generate the signature fixture was disposed without export; its public
key is never an authorized product actor. C# and Kotlin consume identical fixtures.

Publication/recovery schemas, deterministic aliases, closed approved-action scopes,
worker IPC and staged-generation indexes are the remaining Stage 1 contracts.
