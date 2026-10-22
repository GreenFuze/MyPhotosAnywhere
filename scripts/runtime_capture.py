"""Require an entire native run in the preserved runtime stream."""
from collections import Counter
from pathlib import Path
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET


class RuntimeCaptureVerifier:
    event = re.compile(r"\bTestRunner: (started|finished): (.+)\(([^()]+)\)$")
    trace = re.compile(r"\bMpaResourceDiagnostics:\s+Closable allocation tracing enabled before application construction; pid=(\d+)$")
    sender = re.compile(r"^\S+\s+\S+\s+(\d+)\s+\d+\s+[A-Z]\s+")

    def __init__(self, root):
        self.root = Path(root)

    def verify(self, require_resource_tracing=True):
        # Explicit false is only for independent inspection of archived runs created
        # before the diagnostic runner. The CI CLI always requires tracing.
        reports = sorted((self.root / "app/build/outputs/androidTest-results").rglob("TEST-*.xml"))
        if not reports:
            raise ValueError("Runtime capture requires native XML reports.")
        expected = Counter()
        for path in reports:
            for case in ET.parse(path).getroot().iter("testcase"):
                identity = (case.get("classname"), case.get("name"))
                if not all(identity) or any(case.find(tag) is not None for tag in ("failure", "error", "skipped")):
                    raise ValueError("Runtime capture requires complete successful test identities.")
                expected[identity] += 1
        if not expected or any(count != 1 for count in expected.values()):
            raise ValueError("Native report identities must occur exactly once.")
        log = self.root / "build/diagnostics/logcat.txt"
        raw = log.read_bytes()
        events = {name: Counter() for name in ("started", "finished")}
        traced = set()
        for line in raw.decode("utf-8", errors="replace").splitlines():
            trace = self.trace.search(line)
            sender = self.sender.match(line)
            if require_resource_tracing and trace:
                if sender is None or sender[1] != trace[1]:
                    raise ValueError("Resource tracing marker does not match its actual process.")
                traced.add(sender[1])
            match = self.event.search(line)
            if match:
                if require_resource_tracing and (sender is None or sender[1] not in traced):
                    raise ValueError("Native test event preceded resource allocation tracing in its actual process.")
                events[match[1]][(match[3], match[2])] += 1
        for name, observed in events.items():
            if observed != expected:
                missing = list((expected - observed).elements())[:5]
                extra = list((observed - expected).elements())[:5]
                raise ValueError(f"Incomplete runtime {name} evidence: missing={missing}, extra={extra}")
        report = {"tests": len(expected), "started": sum(events["started"].values()),
                "finished": sum(events["finished"].values()), "logSha256": hashlib.sha256(raw).hexdigest(),
                "identities": [list(identity) for identity in sorted(expected)]}
        if require_resource_tracing:
            report["resourceTracingPids"] = sorted(traced, key=int)
        return report


if __name__ == "__main__":
    root = Path.cwd()
    report = RuntimeCaptureVerifier(root).verify(require_resource_tracing=True)
    report["source_sha"] = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    (root / "build/diagnostics/runtime-capture.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Complete runtime stream verified for {report['tests']} native tests.")
