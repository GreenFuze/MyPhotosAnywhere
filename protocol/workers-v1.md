# First-party workers — protocol 1 candidate

Worker stdin/stdout are exclusive framed IPC. Each frame is four unsigned big-endian
length bytes followed by that many canonical UTF-8 JSON bytes, maximum 1 MiB. EOF is
normal only between complete frames. Partial/canceled/invalid reads or writes poison
the channel; never search for the next brace or treat the remainder as another frame.
Stdout contains no logging. Stderr is separately captured diagnostics, not protocol.

Core owns the process group, pipes, diagnostics, staging directory and cache/input
leases. Channel helpers borrow those pipes. Core joins channel tasks before disposing
them. On cancellation it requests acknowledgement, waits at most five seconds, requests
exit and waits at most ten seconds, then kills the process tree and joins it. Leases
are released only after actual process exit. Cleanup failure is visible and blocking;
the app cannot report a clean Exit while native work still runs.

Launch uses only a verified installed first-party executable/plugin ID. Never execute
binaries, scripts, models or DLLs discovered in Drive. Core passes one newly assigned
staging root and a minimal environment without Google tokens. A worker sees only its
assigned immutable input snapshots/output directory. Same-user process isolation is a
crash/resource boundary, not a complete untrusted-code security sandbox.

Frame object header: `format: "mpa-worker"`, integer `major: 1`, `minor`, `type`, random
launch `sessionId`, monotonic per-direction decimal-string `sequence`, and object
`payload`. Job frames add exact `jobId`, `attemptId`, `leaseId`; an old attempt cannot
publish a result into a new attempt. A handshake/capability exchange must complete in
five seconds before jobs. Types: `hello`, `capabilities`, `prepare`, `prepared`, `run`,
`progress`, `checkpoint`, `result`, `failure`, `cancel`, `canceled`, `exit`, `exited`.
Invalid state/type/sequence/identity is a protocol failure, not empty success.

The installed plugin manifest declares ID, exact version, worker protocol range,
capabilities and known typed settings, supported OS/architecture/formats, pinned model
hashes/licenses, recipe fingerprint, resource requirements and checkpoint behavior.
There is no executable UI/schema script. The phone displays only supported known typed
settings; core persists and acknowledges settings revisions before effective use.

Inputs bind verified content fingerprints, captured source/destination/approval and
consent/settings revisions. Results list portable relative paths, declared lengths,
SHA-256 and format metadata; no arbitrary paths/URLs. Core rejects traversal, Windows
device names, symlinks/reparse escapes and paths outside the owned staging root, then
verifies bytes/format/source and current consent before promoting/publishing. A worker
report alone does not prove integrity or grant permission to alter an original.

Progress is transient; checkpoints commit before their acknowledgement. Durable stage
outcomes use the catalog's error taxonomy. Known malformed-item/unsupported/auth/network/
storage problems isolate the affected stage/item and preserve retry evidence. Unexpected
internal invariants terminate work promptly and surface an actionable failure. Native
warnings/crashes are retained for investigation; they are not silently downgraded.
