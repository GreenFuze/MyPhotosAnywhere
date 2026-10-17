from pathlib import Path
import tempfile
import unittest
from runtime_capture import RuntimeCaptureVerifier


class RuntimeCaptureTest(unittest.TestCase):
    def fixture(self, directory, log, case='<testcase classname="fixture.ViewerTest" name="opens"/>'):
        root = Path(directory)
        reports = root / "app/build/outputs/androidTest-results/stages/main"
        reports.mkdir(parents=True)
        (reports / "TEST-fixture.xml").write_text('<testsuite>' + case + '</testsuite>', encoding="utf-8")
        diagnostics = root / "build/diagnostics"
        diagnostics.mkdir(parents=True)
        (diagnostics / "logcat.txt").write_text(log, encoding="utf-8")
        return RuntimeCaptureVerifier(root)

    start = '10-17 12:00:00.000  1  2 I TestRunner: started: opens(fixture.ViewerTest)\n'
    finish = '10-17 12:00:01.000  1  2 I TestRunner: finished: opens(fixture.ViewerTest)\n'

    def test_complete_stream_matches_report_identities(self):
        with tempfile.TemporaryDirectory() as directory:
            report = self.fixture(directory, self.start + self.finish).verify()
            self.assertEqual((report['tests'], report['started'], report['finished']), (1, 1, 1))

    def test_bounded_buffer_losing_start_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'Incomplete runtime started'):
                self.fixture(directory, self.finish).verify()

    def test_missing_finish_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'Incomplete runtime finished'):
                self.fixture(directory, self.start).verify()

    def test_duplicate_capture_or_report_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'extra='):
                self.fixture(directory, self.start + self.start + self.finish).verify()
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'exactly once'):
                self.fixture(directory, self.start + self.finish,
                    '<testcase classname="fixture.ViewerTest" name="opens"/>' * 2).verify()

    def test_failed_or_skipped_report_cannot_pass_capture_gate(self):
        for result in ('failure', 'error', 'skipped'):
            with self.subTest(result=result), tempfile.TemporaryDirectory() as directory:
                with self.assertRaisesRegex(ValueError, 'successful test identities'):
                    self.fixture(directory, self.start + self.finish,
                        '<testcase classname="fixture.ViewerTest" name="opens"><' + result + '/></testcase>').verify()


if __name__ == '__main__':
    unittest.main()
