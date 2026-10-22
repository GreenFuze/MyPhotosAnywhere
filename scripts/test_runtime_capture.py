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
    trace = '10-17 11:59:59.000  1  2 I MpaResourceDiagnostics: Closable allocation tracing enabled before application construction; pid=1\n'

    def test_resource_trace_precedes_every_actual_test_process(self):
        with tempfile.TemporaryDirectory() as directory:
            report = self.fixture(directory, self.trace + self.start + self.finish).verify(require_resource_tracing=True)
            self.assertEqual(report['resourceTracingPids'], ['1'])

    def test_missing_late_or_another_process_trace_cannot_qualify_a_run(self):
        for log in (self.start + self.finish, self.start + self.trace + self.finish,
                    self.trace.replace('  1  2 I', '  3  2 I').replace('pid=1', 'pid=3') + self.start + self.finish):
            with self.subTest(log=log), tempfile.TemporaryDirectory() as directory:
                with self.assertRaisesRegex(ValueError, 'preceded resource allocation tracing'):
                    self.fixture(directory, log).verify(require_resource_tracing=True)

    def test_forged_trace_pid_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'actual process'):
                self.fixture(directory, self.trace.replace('pid=1', 'pid=3') + self.start + self.finish).verify(require_resource_tracing=True)

    def test_later_native_process_requires_its_own_early_trace(self):
        later_start = self.start.replace('  1  2 I', '  3  4 I').replace('opens(', 'later(')
        later_finish = self.finish.replace('  1  2 I', '  3  4 I').replace('opens(', 'later(')
        later_trace = self.trace.replace('  1  2 I', '  3  4 I').replace('pid=1', 'pid=3')
        cases = '<testcase classname="fixture.ViewerTest" name="opens"/><testcase classname="fixture.ViewerTest" name="later"/>'
        prefix = self.trace + self.start + self.finish
        with tempfile.TemporaryDirectory() as directory:
            report = self.fixture(directory, prefix + later_trace + later_start + later_finish, cases).verify()
            self.assertEqual(report['resourceTracingPids'], ['1', '3'])
            self.assertEqual(report['tests'], 2)
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'preceded resource allocation tracing'):
                self.fixture(directory, prefix + later_start + later_finish, cases).verify()

    def test_complete_stream_matches_report_identities(self):
        with tempfile.TemporaryDirectory() as directory:
            report = self.fixture(directory, self.trace + self.start + self.finish).verify()
            self.assertEqual((report['tests'], report['started'], report['finished']), (1, 1, 1))

    def test_bounded_buffer_losing_start_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'Incomplete runtime started'):
                self.fixture(directory, self.trace + self.finish).verify()

    def test_missing_finish_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'Incomplete runtime finished'):
                self.fixture(directory, self.trace + self.start).verify()

    def test_duplicate_capture_or_report_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'extra='):
                self.fixture(directory, self.trace + self.start + self.start + self.finish).verify()
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, 'exactly once'):
                self.fixture(directory, self.trace + self.start + self.finish,
                    '<testcase classname="fixture.ViewerTest" name="opens"/>' * 2).verify()

    def test_failed_or_skipped_report_cannot_pass_capture_gate(self):
        for result in ('failure', 'error', 'skipped'):
            with self.subTest(result=result), tempfile.TemporaryDirectory() as directory:
                with self.assertRaisesRegex(ValueError, 'successful test identities'):
                    self.fixture(directory, self.start + self.finish,
                        '<testcase classname="fixture.ViewerTest" name="opens"><' + result + '/></testcase>').verify()


if __name__ == '__main__':
    unittest.main()
