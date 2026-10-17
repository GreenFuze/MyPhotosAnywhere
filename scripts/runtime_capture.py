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

    def __init__(self, root):
        self.root = Path(root)

    def verify(self):
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
        for line in raw.decode("utf-8", errors="replace").splitlines():
            match = self.event.search(line)
            if match:
                events[match[1]][(match[3], match[2])] += 1
        for name, observed in events.items():
            if observed != expected:
                missing = list((expected - observed).elements())[:5]
                extra = list((observed - expected).elements())[:5]
                raise ValueError(f"Incomplete runtime {name} evidence: missing={missing}, extra={extra}")
        return {"tests": len(expected), "started": sum(events["started"].values()),
                "finished": sum(events["finished"].values()), "logSha256": hashlib.sha256(raw).hexdigest(),
                "identities": [list(identity) for identity in sorted(expected)]}


if __name__ == "__main__":
    root = Path.cwd()
    report = RuntimeCaptureVerifier(root).verify()
    report["source_sha"] = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    (root / "build/diagnostics/runtime-capture.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Complete runtime stream verified for {report['tests']} native tests.")
