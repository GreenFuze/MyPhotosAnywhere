#!/usr/bin/env python3
import hashlib
import json
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from subprocess import CompletedProcess

module_path = Path(__file__).with_name("native_stages.py")
if not module_path.exists():
    module_path = Path(__file__).parent / "stage-0531-worktree/scripts/native_stages.py"
spec = importlib.util.spec_from_file_location("native_stages", module_path)
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


class NativeStagesTest(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="mpa-native-stage-", dir=Path.cwd())
        self.root = Path(self.temporary.name)
        (self.root / "build/diagnostics").mkdir(parents=True)
        self.owner = native.NativeStages(self.root)

    def tearDown(self):
        self.assertTrue(self.root.resolve().is_relative_to(Path.cwd().resolve()))
        self.assertTrue(self.root.name.startswith("mpa-native-stage-"))
        self.temporary.cleanup()

    def report(self, classname="example.NativeTest", outcome=""):
        path = self.root / "app/build/outputs/androidTest-results/connected/debug/TEST-device.xml"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('<testsuite tests="1"><testcase classname="' + classname + '" name="example">' + outcome + '</testcase></testsuite>', encoding="utf-8")
        return path.read_bytes()

    def testPortableFixtureReadPrecedesRealApi29PermissionRevocation(self):
        raw=json.dumps([{"id":"one","captured":None},{"id":"two","captured":1000}])
        xml=("<map><string name='rows'>"+raw+"</string></map>").encode()
        with patch.object(native.subprocess,"run",return_value=CompletedProcess([],0,xml,b"")) as run, \
                patch.object(native.subprocess,"check_output",return_value="29\n"):
            self.owner.revoke()
        self.assertEqual(run.call_args_list[0].args[0],["adb","exec-out","run-as",self.owner.PACKAGE,"cat","shared_prefs/device-revocation-fixture.xml"])
        self.assertEqual(run.call_args_list[1].args[0],["adb","shell","pm","revoke",self.owner.PACKAGE,"android.permission.READ_EXTERNAL_STORAGE"])
        self.assertEqual(2,run.call_count)

    def testMissingFixtureCannotRevokePermissionsAndPreservesReadFailureReason(self):
        with patch.object(native.subprocess,"run",return_value=CompletedProcess([],1,b"",b"package data missing")) as run:
            with self.assertRaisesRegex(ValueError,"package data missing"):
                self.owner.revoke()
        self.assertEqual(1,run.call_count)

    def testCorruptOrIncompleteFixtureCannotRevokePermissions(self):
        for xml in (b"not XML",b"<map/>",b"<map><string name='rows'>[{\"id\":\"one\",\"captured\":null}]</string></map>"):
            with self.subTest(xml=xml),patch.object(native.subprocess,"run",return_value=CompletedProcess([],0,xml,b"")) as run:
                with self.assertRaises(ValueError):self.owner.revoke()
                self.assertEqual(1,run.call_count)

    def testEveryArchivedReportIsRestoredAtItsRecordedPathWithTheSameHash(self):
        original = self.report()
        self.owner.archive("main", 0)
        self.owner.restore_archives()
        record = self.owner.records[0]
        self.assertEqual(1, record["tests"])
        self.assertEqual(0, record["failedOrSkipped"])
        saved = self.root / record["reports"][0]["path"]
        self.assertEqual(original, saved.read_bytes())
        self.assertEqual(hashlib.sha256(original).hexdigest(), record["reports"][0]["sha256"])

    def testIncorrectIsolatedClassCannotBeReportedAsPassing(self):
        self.report()
        with self.assertRaises(ValueError):
            self.owner.archive("device-library", 0)

    def testMainSuiteCannotIncludeTheFullCameraPermissionSuite(self):
        self.report(native.NativeStages.DEVICE)
        with self.assertRaises(ValueError):
            self.owner.archive("main", 0)

    def testDuplicateCaseIsNotCountedAcrossInvocations(self):
        self.report(native.NativeStages.DEVICE)
        self.owner.identities.add((native.NativeStages.DEVICE, "example"))
        with self.assertRaises(ValueError):
            self.owner.archive("device-library", 0)

    def testPassingCommandWithFailedSkippedOrMissingXmlIsRejected(self):
        for index, outcome in enumerate(("<failure/>", "<error/>", "<skipped/>")):
            with self.subTest(outcome=outcome):
                self.owner = native.NativeStages(self.root)
                self.report(outcome=outcome)
                with self.assertRaises(ValueError):
                    self.owner.archive("invalid-" + str(index), 0)
        self.owner = native.NativeStages(self.root)
        with self.assertRaises(ValueError):
            self.owner.archive("missing-xml", 0)

    def testDeclaredCountMustMatchAllActualCases(self):
        self.report()
        path=self.root/"app/build/outputs/androidTest-results/connected/debug/TEST-device.xml"
        path.write_text(path.read_text(encoding="utf-8").replace('tests="1"','tests="2"'),encoding="utf-8")
        with self.assertRaises(ValueError):
            self.owner.archive("incomplete",0)

    def testAggregatedSuitesPreserveCompleteReportsAndIdentities(self):
        self.report()
        path=self.root/"app/build/outputs/androidTest-results/connected/debug/TEST-device.xml"
        raw='<testsuites tests="1">'+path.read_text(encoding="utf-8")+'</testsuites>'
        path.write_text(raw,encoding="utf-8")
        self.owner.archive("main",0)
        self.owner.restore_archives()
        record=self.owner.records[0]
        self.assertEqual(1,record["tests"])
        self.assertEqual(raw,(self.root/record["reports"][0]["path"]).read_text(encoding="utf-8"))

    def testAggregateCannotHideAnIncompleteNestedSuite(self):
        self.report()
        path=self.root/"app/build/outputs/androidTest-results/connected/debug/TEST-device.xml"
        nested=path.read_text(encoding="utf-8").replace('tests="1"','tests="2"')
        path.write_text('<testsuites tests="1">'+nested+'</testsuites>',encoding="utf-8")
        with self.assertRaises(ValueError):
            self.owner.archive("incomplete-aggregate",0)

    def testAggregateCannotHideNestedErrors(self):
        self.report()
        path=self.root/"app/build/outputs/androidTest-results/connected/debug/TEST-device.xml"
        nested=path.read_text(encoding="utf-8").replace('tests="1"','tests="1" errors="1"')
        path.write_text('<testsuites tests="1">'+nested+'</testsuites>',encoding="utf-8")
        with self.assertRaises(ValueError):
            self.owner.archive("invalid-aggregate",0)

    def testRootErrorsCannotBeHiddenByPassingChildCases(self):
        self.report()
        path=self.root/"app/build/outputs/androidTest-results/connected/debug/TEST-device.xml"
        path.write_text(path.read_text(encoding="utf-8").replace('tests="1"','tests="1" errors="1"'),encoding="utf-8")
        with self.assertRaises(ValueError):
            self.owner.archive("root-error",0)

    def testPhysicalDevicesAreRejectedBeforeAnySyntheticTestsOrPermissionChanges(self):
        with patch.object(native.subprocess,"check_output",return_value="physical-s23\n"):
            with self.assertRaises(ValueError):
                self.owner.run()

    def testArchivePathsCannotEscapeTheCheckout(self):
        with self.assertRaises(ValueError):
            self.owner.owned("../outside")


if __name__ == "__main__":
    unittest.main()
