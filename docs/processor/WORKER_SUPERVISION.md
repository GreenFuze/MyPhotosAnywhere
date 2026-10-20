# Worker ownership implementation notes

Stage 2 candidate, not established runtime acceptance.

Core is the sole launcher of heavy processes. The first-party vision worker loads
ONNX/Skia in its own process and does not launch child executables. Native OCR/video
tools are separately launched by Core through fixed first-party adapters, so Core
can join every actual process directly. A plugin cannot request an arbitrary path,
shell command or downloaded executable. Native tools are dependencies of an approved
adapter; executable hashes, typed parameters and assigned I/O roots are validated.
The implementation must not rely on waiting for a root PID as proof that its unknown
descendants have exited.

Each launch owns its process, pipes, output pumps, diagnostic file, staging/cache
leases and a dedicated private parent-lifetime connection. The managed implementation
uses an authenticated loopback socket with a one-launch key passed in its minimal
environment, never a Google credential. This is internal IPC, not LAN pairing or
phone communication. A canceled/partial ownership handshake must close its accepted
socket; repeated asynchronous disposal joins the same monitor task. The worker cannot start analysis until
the startup handshake confirms ownership. Closing a pipe is not a success result.
Cancellation first fences the durable attempt, then requests stop, escalates after
the documented deadline, waits for actual exit and joins all pumps before releasing
readers/reservations or acknowledging Exit. Stop accepting new work before shutdown.

The first-party managed worker needs a parent-pipe guard independent of inference.
Windows containment uses a kernel job object with kill-on-close; assign it before
the startup acknowledgement and retain its safe handle through the complete worker
lifetime. Linux native launch can set PR_SET_PDEATHSIG before exec, while checking
the expected parent to close the startup race. macOS native tools need a tested
first-party parent-lifetime guard in their build or an equivalent supervised ownership
solution; do not pretend macOS provides Linux's prctl facility. A linked small native
guard can monitor the inherited dedicated pipe and terminate the direct tool on EOF;
its source/hash/license changes must be included in package evidence. Native tools
must not spawn untracked delegates. No permissive unsupported-platform fallback.

Process crash, controller crash, EOF during handshake, cancellation mid-write,
diagnostic overflow, stubborn child, repeated disposal, PID reuse boundaries and
shutdown with native work active require actual child-process tests on all targets.
Repository tests currently prove lease fences/checkpoints only. They deliberately
leave reopened running stages fenced until the recovery owner establishes exit.

Storage policy updates require stopping/joining active writers before replacing their
reservation manager. Keep default max(2 GiB, 5% volume) reserve and 10 GiB cache cap;
percentage changes require acknowledged settings and retain the 2 GiB floor. The
budget manager never decides which files may be deleted. Scoped cache cleanup must
separately prove disposable ownership, no readers/pins and a verified available remote
original, and protect local-only originals and human/job state.

Managed process supervision also samples actual resident bytes every 100 ms and enforces a monotonic per-process lifetime. Defaults are a 1 GiB resident stop threshold and 30-minute lifetime; installed adapters may select stricter validated limits. These are supervisor stop thresholds, not a claim of kernel allocation quotas. Measurement failure is actionable, never treated as zero usage. Resource failures terminate/join the exact child and retain its input/checkpoint; monitored tasks join before process handles are disposed. Windows local tests exercise actual memory/lifetime stops and unchanged input bytes. Native OS containment and real model allocation bounds remain separate acceptance work.

The single-heavy-work supervisor now transfers an accepted operation into explicit ownership, stops admission on Pause/Exit, cancels and joins the worker, and only then disposes that operation's staging/read/reservation leases. Rejected submissions remain caller-owned. Interrupted Exit retains ownership and reports failure rather than silently releasing readers; retained cleanup can be retried. Actual local tests hold operation disposal behind a barrier and prove Pause/Exit remain pending while its file reader/reservation remain held. Unexpected startup failure remains visible and blocks automatic restart.

Linux CI also exposed address-space teardown reporting zero RSS before exit notification. The monitor now waits at most 250 ms for confirmed exit in that case; a still-live process without a measurement remains an error. A 20-process rapid-exit regression supplements normal live RSS and resource-stop checks. Current Windows Core suite: 31 passing tests, no diagnostics. Cross-platform verification remains required.
