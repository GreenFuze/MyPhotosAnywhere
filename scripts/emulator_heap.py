"""Verify API 29's actual zygote heap, not just its late boot property."""
from pathlib import Path
import json
import re
import subprocess
import sys
import time


class EmulatorHeapProfile:
    option = re.compile(r"^\S+\s+\S+\s+(\d+)\s+\d+\s+[A-Z]\s+zygote64\s*:\s+option\[(\d+)\]=(.*)$")

    @staticmethod
    def primary_pid(processes, named_pids):
        # During boot a freshly forked child can still share the zygote name.
        # The primary init-owned process must independently match both inventories.
        pids = named_pids.split()
        if (not pids or len(pids) > 256 or len(set(pids)) != len(pids)
                or any(not re.fullmatch(r"[1-9][0-9]*", pid) for pid in pids)):
            raise ValueError("Invalid named 64-bit zygote process inventory.")
        lines = processes.splitlines()
        if not lines or lines[0].split() != ["PID", "PPID", "NAME"]:
            raise ValueError("Missing primary zygote parent identity evidence.")
        primary = []
        seen = set()
        for line in lines[1:]:
            fields = line.split(maxsplit=2)
            if (len(fields) != 3 or not re.fullmatch(r"[1-9][0-9]*", fields[0])
                    or not re.fullmatch(r"0|[1-9][0-9]*", fields[1]) or fields[0] in seen):
                raise ValueError("Invalid primary zygote process inventory.")
            seen.add(fields[0])
            if fields[1:] == ["1", "zygote64"]:
                primary.append(fields[0])
        if len(primary) != 1 or primary[0] not in pids:
            raise ValueError("Expected exactly one matching init-owned 64-bit zygote.")
        return primary[0]

    @classmethod
    def runtime_heap(cls, log, pid):
        arguments = []
        for line in log.splitlines():
            match = cls.option.fullmatch(line.strip())
            if match and match[1] == pid:
                index = int(match[2])
                if index != len(arguments):
                    raise ValueError("Missing or repeated zygote startup arguments.")
                arguments.append(match[3])
        if not arguments or arguments[0] != "-Xzygote":
            raise ValueError("Missing actual zygote startup evidence.")
        heaps = [value[4:] for index, value in enumerate(arguments)
                 if value.startswith("-Xmx") and (index == 0 or arguments[index-1]
                     not in ("-Ximage-compiler-option", "-Xcompiler-option"))]
        if len(heaps) != 1 or not re.fullmatch(r"[1-9][0-9]*m", heaps[0]):
            raise ValueError("Ambiguous actual zygote heap.")
        return heaps[0]

    @staticmethod
    def needs_repair(profile):
        if profile["configuredHeap"] == profile["runtimeHeap"]:
            return False
        if (profile["sdk"] == "29" and profile["configuredHeap"] == "576m"
                and profile["runtimeHeap"] == "16m"):
            return True
        raise ValueError("Unexpected emulator heap mismatch; device tests cannot start.")


class EmulatorHeapOwner:
    def __init__(self, directory):
        self.directory = Path(directory)

    @staticmethod
    def adb(*arguments):
        return subprocess.run(["adb", *arguments], check=True, capture_output=True,
                              text=True, encoding="utf-8", timeout=60).stdout.strip()

    def snapshot(self, name):
        sdk = self.adb("shell", "getprop", "ro.build.version.sdk")
        named_pids = self.adb("shell", "pidof", "zygote64")
        processes = self.adb("shell", "ps", "-A", "-o", "PID,PPID,NAME")
        log = self.adb("logcat", "-b", "main", "-d", "-v", "threadtime")
        (self.directory / (name + "-identity.json")).write_text(json.dumps(
            {"sdk": sdk, "namedPids": named_pids, "processes": processes}, indent=2) + "\n", encoding="utf-8")
        (self.directory / (name + ".log")).write_text(log + "\n", encoding="utf-8")
        pid = EmulatorHeapProfile.primary_pid(processes, named_pids)
        return {"sdk": sdk, "pid": pid,
                "configuredHeap": self.adb("shell", "getprop", "dalvik.vm.heapsize"),
                "growthLimit": self.adb("shell", "getprop", "dalvik.vm.heapgrowthlimit"),
                "runtimeHeap": EmulatorHeapProfile.runtime_heap(log, pid)}

    def verify(self):
        serial = self.adb("get-serialno")
        if not re.fullmatch(r"emulator-[0-9]+", serial) or self.adb("shell", "getprop", "ro.kernel.qemu") != "1":
            raise ValueError("Heap preparation is restricted to the disposable CI emulator.")
        self.directory.mkdir(parents=True, exist_ok=True)
        before = self.snapshot("heap-before")
        if before["sdk"] != "29":
            raise ValueError("API 29 heap preparation must not change another Android image.")
        report = {"before": before, "restartedFramework": False, "verified": False}
        path = self.directory / "heap-profile.json"
        path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        if EmulatorHeapProfile.needs_repair(before):
            # API 29 can launch zygote64 before qemu-props supplies the existing
            # 576m AVD heap. Restart only this known mismatch, before any app is
            # installed/tested. Do not increase a heap or retry failed tests.
            self.adb("root")
            self.adb("wait-for-device")
            self.adb("shell", "setprop", "sys.boot_completed", "0")
            self.adb("shell", "stop")
            self.adb("shell", "start")
            for _ in range(120):
                if self.adb("shell", "getprop", "sys.boot_completed") == "1":
                    break
                time.sleep(1)
            else:
                raise ValueError("Reinitialized emulator did not complete boot.")
            after = self.snapshot("heap-after")
            if after["pid"] == before["pid"] or EmulatorHeapProfile.needs_repair(after):
                raise ValueError("Known emulator heap mismatch was not corrected.")
            if after["configuredHeap"] != before["configuredHeap"] or after["growthLimit"] != before["growthLimit"]:
                raise ValueError("Emulator heap configuration changed during preparation.")
            report.update(after=after, restartedFramework=True)
        report["verified"] = True
        path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print("API 29 actual runtime heap verified; configuration and all test assertions preserved.")


if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] != "29":
        raise ValueError("This narrowly scoped preparation applies only to API 29.")
    EmulatorHeapOwner("build/diagnostics").verify()
