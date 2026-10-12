import json
from pathlib import Path
import tempfile
import unittest
from gradle_checks import WarningGate


class WarningGateTest(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.report = Path(self.directory.name) / "report.html"
        self.gate = WarningGate()
        self.diagnostic = {
            "problemId": [{"name": "deprecation"}, {"name": "the-configuration-setvisible-boolean-method-has-been-deprecated"}],
            "severity": "WARNING", "contextualLabel": self.gate.LABEL,
            "problemDetails": "This is scheduled to be removed in Gradle 11.",
            "documentationLink": self.gate.LINK,
            "locations": [{"pluginId": "com.android.internal.application"}],
        }
        self.output = (self.gate.LABEL + " This is scheduled to be removed in Gradle 11. Consult the upgrading guide for further information: " + self.gate.LINK
            + "\n* What went wrong:\n" + self.gate.FAILURE + "\n\n* Try:\nBUILD FAILED in 1s\n")

    def write(self, diagnostics):
        self.report.write_text("// begin-report-data\n" + json.dumps({"diagnostics": diagnostics}) + "\n// end-report-data", encoding="utf-8")

    def test_only_documented_upstream_warning_is_accepted(self):
        self.write([self.diagnostic])
        self.assertTrue(self.gate.evaluate(1, self.output, self.report)["acceptedUpstreamWarning"])

    def test_warning_from_another_plugin_is_rejected(self):
        self.diagnostic["locations"] = [{"pluginId": "another.plugin"}]
        self.write([self.diagnostic])
        with self.assertRaises(ValueError):
            self.gate.evaluate(1, self.output, self.report)

    def test_failed_task_is_never_accepted(self):
        self.write([self.diagnostic])
        with self.assertRaises(ValueError):
            self.gate.evaluate(1, "> Task :app:testDebugUnitTest FAILED\n" + self.output, self.report)

    def test_missing_structured_evidence_is_rejected(self):
        with self.assertRaises(ValueError):
            self.gate.evaluate(1, self.output, self.report)

    def test_other_console_warning_is_rejected(self):
        self.write([self.diagnostic])
        with self.assertRaises(ValueError):
            self.gate.evaluate(1, self.output + "warning: unrelated warning\n", self.report)

    def test_native_stripping_diagnostic_is_rejected(self):
        self.write([self.diagnostic])
        with self.assertRaises(ValueError):
            self.gate.evaluate(1, self.output + "Unable to strip the following libraries, packaging them as they are: libLiteRt.so\n", self.report)

    def test_disabled_gradle_warning_enforcement_is_rejected(self):
        self.write([self.diagnostic])
        with self.assertRaises(ValueError):
            self.gate.evaluate(0, self.output, self.report)

    def test_clean_build_without_exception_is_accepted(self):
        self.assertFalse(self.gate.evaluate(0, "BUILD SUCCESSFUL in 1s\n", self.report)["acceptedUpstreamWarning"])

    def test_other_failure_is_rejected(self):
        self.write([self.diagnostic])
        with self.assertRaises(ValueError):
            self.gate.evaluate(1, self.output.replace(self.gate.FAILURE, "Compilation failed"), self.report)


if __name__ == "__main__":
    unittest.main()
