#!/usr/bin/env python3
"""Run strict Gradle checks, accepting only the owner's AGP 9.4.1 exception."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys


class WarningGate:
    LABEL = "The Configuration.setVisible(boolean) method has been deprecated."
    LINK = "https://docs.gradle.org/9.8.2/userguide/upgrading_version_9.html#deprecate-visible-property"
    FAILURE = "Deprecated Gradle features were used in this build, making it incompatible with Gradle 10"

    def evaluate(self, status, output, report):
        diagnostics = []
        if report.exists():
            html = report.read_text(encoding="utf-8")
            match = re.search(r"// begin-report-data\s*(.*?)\s*// end-report-data", html, re.S)
            if not match:
                raise ValueError("Cannot read Gradle's structured diagnostic report.")
            data = json.loads(match.group(1))
            diagnostics = data.get("diagnostics", [])
            # Summaries can include diagnostics omitted from the detailed array.
            for summary in data.get("problemsReport", {}).get("summaries", []):
                if not self._known_id(summary.get("problemId", [])):
                    raise ValueError("An unapproved Gradle diagnostic summary was found.")
        for item in diagnostics:
            if not (self._known_id(item.get("problemId", []))
                    and item.get("severity") == "WARNING"
                    and item.get("contextualLabel") == self.LABEL
                    and item.get("problemDetails") == "This is scheduled to be removed in Gradle 11."
                    and item.get("documentationLink") == self.LINK
                    and item.get("locations") == [{"pluginId": "com.android.internal.application"}]):
                raise ValueError("An unapproved Gradle diagnostic was found: " + str(item))
        known_lines = 0
        for line in output.splitlines():
            if line == self.LABEL + " This is scheduled to be removed in Gradle 11. Consult the upgrading guide for further information: " + self.LINK:
                known_lines += 1
                continue
            if re.search(r"(^w:|^warning:|\bWARNING\b|has been deprecated|^.*warning:|Unable to strip the following libraries|System\.log[WE]:|Explicit termination method 'close' not called)", line, re.I):
                raise ValueError("An unapproved warning was found: " + line)
        if status == 0:
            if known_lines or diagnostics:
                raise ValueError("Gradle did not enforce warning failure as configured.")
            if not re.search(r"^BUILD SUCCESSFUL in ", output, re.M):
                raise ValueError("Missing successful Gradle completion.")
            return {"gradleExitCode": 0, "acceptedUpstreamWarning": False}
        failures = re.findall(r"\* What went wrong:\s*(.*?)\s*\* Try:", output, re.S)
        if (status != 1 or not diagnostics or known_lines < 1
                or failures != [self.FAILURE]
                or re.search(r"^> Task .* FAILED$", output, re.M)
                or not re.search(r"^BUILD FAILED in ", output, re.M)):
            raise ValueError("Gradle failed for a reason beyond the approved AGP warning.")
        return {"gradleExitCode": status, "acceptedUpstreamWarning": True,
                "warning": self.LABEL, "diagnosticOccurrences": len(diagnostics)}

    @staticmethod
    def _known_id(problem_id):
        return [item.get("name") for item in problem_id] == [
            "deprecation", "the-configuration-setvisible-boolean-method-has-been-deprecated"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("tasks", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    tasks = args.tasks[1:] if args.tasks[:1] == ["--"] else args.tasks
    if not tasks:
        parser.error("At least one Gradle task is required.")
    if any("warning.mode" in task or "warning-mode" in task for task in tasks):
        parser.error("The strict Gradle warning policy cannot be overridden.")
    report = Path("build/reports/problems/problems-report.html")
    report.unlink(missing_ok=True)  # Never accept a report from an earlier invocation.
    lint_requested = any("lint" in task.lower() and not task.startswith("-") for task in tasks)
    lint_report = Path("app/build/reports/lint-results-debug.xml")
    if lint_requested:
        lint_report.unlink(missing_ok=True)
    args.log.with_suffix(".gate.json").unlink(missing_ok=True)
    args.log.parent.mkdir(parents=True, exist_ok=True)
    command = (["cmd", "/c", "gradlew.bat"] if sys.platform == "win32" else ["./gradlew"])
    command += tasks + ["--no-daemon", "--console=plain", "--warning-mode=fail"]
    with args.log.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace")
        for line in process.stdout:
            sys.stdout.write(line)
            log.write(line)
            log.flush()
        status = process.wait()
    try:
        result = WarningGate().evaluate(status, args.log.read_text(encoding="utf-8"), report)
        if result["acceptedUpstreamWarning"]:
            plugins = Path("build.gradle.kts").read_text(encoding="utf-8")
            wrapper = Path("gradle/wrapper/gradle-wrapper.properties").read_text(encoding="utf-8")
            if ('id("com.android.application") version "9.4.1"' not in plugins
                    or "gradle-9.8.2-bin.zip" not in wrapper):
                raise ValueError("The approved AGP 9.4.1 call must be reproduced on the pinned Gradle 9.8.2.")
        if lint_requested:
            import xml.etree.ElementTree as ET
            issues = ET.parse(lint_report).getroot().findall("issue")
            if issues:
                raise ValueError("Lint must contain zero findings, including informational findings.")
    except (ValueError, KeyError, TypeError, OSError) as error:
        print("Warning gate failed: " + str(error), file=sys.stderr)
        return 1
    args.log.with_suffix(".gate.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print("Warning gate passed: " + json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
