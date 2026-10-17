#!/usr/bin/env python3
"""Isolate camera permissions; retain every immutable native invocation and report."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET


class NativeStages:
    DEVICE = "com.myphotosanywhere.mpa.device.DeviceLibraryDeviceTest"
    ACCESS = "com.myphotosanywhere.mpa.device.DeviceLibraryAccessDeviceTest"
    PACKAGE = "com.myphotosanywhere.mpa"

    def __init__(self, root):
        self.root = root.resolve()
        self.records = []
        self.identities = set()

    def owned(self, relative):
        path = (self.root / relative).resolve()
        if not path.is_relative_to(self.root):
            raise ValueError("Native report path escaped the checkout.")
        return path

    def archive(self, stage, status):
        outputs = self.owned("app/build/outputs/androidTest-results/connected")
        reports = self.owned("app/build/reports/androidTests/connected")
        destination = self.owned("build/diagnostics/native-stage-archives/" + stage)
        destination.mkdir(parents=True, exist_ok=False)
        for source, name in ((outputs, "results"), (reports, "reports")):
            if source.exists():
                shutil.move(str(source), str(destination / name))
        tests = []
        files = []
        for report in (destination / "results").rglob("TEST-*.xml"):
            files.append({"path": str(Path("app/build/outputs/androidTest-results/stages") / stage / report.relative_to(destination / "results")),
                          "sha256": hashlib.sha256(report.read_bytes()).hexdigest()})
            root=ET.parse(report).getroot()
            cases=list(root.iter("testcase"))
            if status==0:
                if root.tag not in ("testsuite","testsuites"):
                    raise ValueError("Native XML has an unsupported report root.")
                suites=[root,*[item for item in root.iter("testsuite") if item is not root]]
                for suite in suites:
                    if (int(suite.get("tests",-1))!=len(list(suite.iter("testcase")))
                            or any(int(suite.get(name,0)) for name in ("failures","errors","skipped"))):
                        raise ValueError("Native XML declares missing, failing or skipped test evidence.")
            tests.extend(cases)
        for test in tests:
            identity = (test.get("classname"), test.get("name"))
            if identity in self.identities:
                raise ValueError("Native invocation repeated an already counted test: " + str(identity))
            self.identities.add(identity)
        failures = sum(any(test.find(name) is not None for name in ("failure", "error", "skipped")) for test in tests)
        record = {"stage": stage, "exitCode": status, "tests": len(tests), "failedOrSkipped": failures,
                  "reports": files, "classes": sorted({test.get("classname") for test in tests})}
        self.records.append(record)
        self.owned("build/diagnostics/native-stages.json").write_text(json.dumps(self.records, indent=2) + "\n", encoding="utf-8")
        if status == 0:
            if not tests or failures:
                raise ValueError("A passing invocation needs complete passing native XML evidence.")
            expected = {self.DEVICE} if stage == "device-library" else {self.ACCESS} if stage == "revoked-access" else None
            actual = set(record["classes"])
            if expected is not None and actual != expected:
                raise ValueError("Native class selector did not execute the intended isolated suite.")
            if expected is None and (self.DEVICE in actual or self.ACCESS in actual):
                raise ValueError("Camera permissions contaminated the ordinary native suite.")

    def restore_archives(self):
        for stage in self.records:
            source = self.owned("build/diagnostics/native-stage-archives/" + stage["stage"])
            for name, relative in (("results", "app/build/outputs/androidTest-results/stages/"),
                                   ("reports", "app/build/reports/androidTests/stages/")):
                saved = source / name
                if saved.exists():
                    destination = self.owned(relative + stage["stage"])
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(saved), str(destination))

    def revoke(self):
        fixture=subprocess.run(["adb","shell","run-as",self.PACKAGE,"test","-f",
                                "shared_prefs/device-revocation-fixture.xml"],capture_output=True)
        if fixture.returncode:
            raise ValueError("Camera-access fixture was not retained between native invocations.")
        sdk = int(subprocess.check_output(["adb", "shell", "getprop", "ro.build.version.sdk"], text=True).strip())
        permissions = ["READ_MEDIA_IMAGES", "READ_MEDIA_VIDEO"] if sdk >= 33 else ["READ_EXTERNAL_STORAGE"]
        if sdk >= 34:
            permissions.append("READ_MEDIA_VISUAL_USER_SELECTED")
        for permission in permissions:
            subprocess.run(["adb", "shell", "pm", "revoke", self.PACKAGE, "android.permission." + permission], check=True)

    def run(self):
        serial=subprocess.check_output(["adb","get-serialno"],text=True).strip()
        if not serial.startswith("emulator-"):
            raise ValueError("Synthetic native suites and permission revocation require an emulator.")
        apk = self.owned("app/build/outputs/apk/debug/app-debug.apk")
        checksum = hashlib.sha256(apk.read_bytes()).hexdigest()
        stages = [("main", "notAnnotation", "com.myphotosanywhere.mpa.device.DeviceLibraryStage"),
                  ("device-library", "class", self.DEVICE), ("revoked-access", "class", self.ACCESS)]
        status = 1
        try:
            for stage, selector, classes in stages:
                if stage == "revoked-access":
                    self.revoke()
                command = [sys.executable, "scripts/gradle_checks.py", "--log", "build/diagnostics/device-" + stage + ".log",
                           "--", "connectedDebugAndroidTest", "-Pandroid.injected.androidTest.leaveApksInstalledAfterRun=true",
                           "-Pandroid.testInstrumentationRunnerArguments." + selector + "=" + classes]
                status = subprocess.run(command, cwd=self.root).returncode
                self.archive(stage, status)
                if hashlib.sha256(apk.read_bytes()).hexdigest() != checksum:
                    raise ValueError("Application APK changed between native invocations.")
                if status:
                    return status
            return 0
        finally:
            self.restore_archives()


if __name__ == "__main__":
    sys.exit(NativeStages(Path.cwd()).run())
