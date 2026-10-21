import unittest
import json
from pathlib import Path
import tempfile
from emulator_heap import EmulatorHeapOwner, EmulatorHeapProfile


class EmulatorHeapProfileTest(unittest.TestCase):
    @staticmethod
    def log(heap="576m", pid="123"):
        arguments = ["-Xzygote", "-Xmx" + heap, "-Ximage-compiler-option", "-Xmx64m",
                     "-Xcompiler-option", "-Xmx512m"]
        return "\n".join(f"10-21 17:46:21.624 {pid} {pid} I zygote64: option[{i}]={arg}"
                         for i, arg in enumerate(arguments))

    def test_uses_runtime_not_compiler_heaps_or_another_pid(self):
        self.assertEqual("576m", EmulatorHeapProfile.runtime_heap(self.log("16m", "99") + "\n" + self.log(), "123"))

    def test_incomplete_duplicate_or_ambiguous_evidence_fails(self):
        for log in ("", self.log().replace("option[1]", "option[0]"),
                    self.log() + "\n10-21 17:46:21.624 123 123 I zygote64: option[6]=-Xmx16m"):
            with self.subTest(log=log), self.assertRaises(ValueError):
                EmulatorHeapProfile.runtime_heap(log, "123")

    def test_only_confirmed_old_image_mismatch_is_repairable(self):
        profile = {"sdk": "29", "configuredHeap": "576m", "runtimeHeap": "16m"}
        self.assertTrue(EmulatorHeapProfile.needs_repair(profile))
        self.assertFalse(EmulatorHeapProfile.needs_repair(dict(profile, runtimeHeap="576m")))
        for change in ({"sdk": "35"}, {"configuredHeap": "1024m"}, {"runtimeHeap": "32m"}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                EmulatorHeapProfile.needs_repair(dict(profile, **change))

    def test_owner_changes_nothing_on_valid_profile_or_physical_device(self):
        with tempfile.TemporaryDirectory() as directory:
            owner = FakeOwner(directory, "576m")
            owner.verify()
            self.assertFalse(any(command[0] == "root" for command in owner.commands))
            self.assertTrue(json.loads((Path(directory) / "heap-profile.json").read_text())["verified"])
            physical = FakeOwner(directory, "16m", serial="physical-phone")
            with self.assertRaisesRegex(ValueError, "disposable CI emulator"):
                physical.verify()
            self.assertEqual([("get-serialno",)], physical.commands)

    def test_known_mismatch_restarts_before_verifying_new_pid_without_changing_heap(self):
        with tempfile.TemporaryDirectory() as directory:
            owner = FakeOwner(directory, "16m")
            owner.verify()
            report = json.loads((Path(directory) / "heap-profile.json").read_text())
            self.assertTrue(report["verified"])
            self.assertTrue(report["restartedFramework"])
            self.assertNotEqual(report["before"]["pid"], report["after"]["pid"])
            self.assertEqual([("root",), ("wait-for-device",),
                ("shell", "setprop", "sys.boot_completed", "0"), ("shell", "stop"),
                ("shell", "start"), ("shell", "getprop", "sys.boot_completed")], owner.commands[2:])

    def test_failed_repair_or_wrong_image_cannot_produce_verified_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            owner = FakeOwner(directory, "16m", repair=False)
            with self.assertRaisesRegex(ValueError, "not corrected"):
                owner.verify()
            self.assertFalse(json.loads((Path(directory) / "heap-profile.json").read_text())["verified"])
        with tempfile.TemporaryDirectory() as directory:
            owner = FakeOwner(directory, "16m", sdk="35")
            with self.assertRaisesRegex(ValueError, "another Android image"):
                owner.verify()
            self.assertFalse(any(command[0] == "root" for command in owner.commands))


class FakeOwner(EmulatorHeapOwner):
    def __init__(self, directory, heap, serial="emulator-5554", repair=True, sdk="29"):
        super().__init__(directory)
        self.heap, self.serial, self.repair, self.sdk = heap, serial, repair, sdk
        self.commands = []
        self.snapshots = 0

    def adb(self, *arguments):
        self.commands.append(arguments)
        if arguments == ("get-serialno",):
            return self.serial
        if arguments in (("shell", "getprop", "ro.kernel.qemu"), ("shell", "getprop", "sys.boot_completed")):
            return "1"
        return ""

    def snapshot(self, name):
        self.snapshots += 1
        corrected = self.snapshots > 1 and self.repair
        return {"sdk": self.sdk, "pid": "2" if corrected else "1",
                "configuredHeap": "576m", "growthLimit": "64m",
                "runtimeHeap": "576m" if corrected else self.heap}


if __name__ == "__main__":
    unittest.main()
