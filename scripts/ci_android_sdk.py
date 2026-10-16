"""Install pinned stable SDK tools with Google's supported Android CLI."""
from pathlib import Path
import hashlib
import os
import subprocess
import sys
import urllib.request


class AndroidSdkSetup:
    CLI_SHA256 = "c8f88b14aa00e44a542dd3597cbdcb015e55511e33c2dd69920f41f5b086e228"
    CLI_VERSION = "1.0.16625370"

    def __init__(self):
        self.sdk = Path(os.environ["ANDROID_HOME"])
        self.bin = Path(os.environ["RUNNER_TEMP"]) / "mpa-android-cli"
        self.bin.mkdir(parents=True, exist_ok=True)
        self.cli = self.bin / "android"

    def run(self, *args):
        subprocess.run([str(self.cli), "--no-metrics", f"--sdk={self.sdk}", *args],
                       check=True, input="y\n" * 100, text=True)

    def install(self, api=None):
        # The published endpoint is mutable. Both the binary digest and version
        # are checked, so an upstream update fails rather than changing this build.
        with urllib.request.urlopen("https://dl.google.com/android/cli/latest/linux_x86_64/android", timeout=90) as response:
            binary = response.read()
        if hashlib.sha256(binary).hexdigest() != self.CLI_SHA256:
            raise RuntimeError("Android CLI changed; review and pin the new stable release")
        self.cli.write_bytes(binary)
        self.cli.chmod(0o755)
        version = subprocess.check_output([str(self.cli), "--no-metrics", "--version"], text=True)
        print(version, flush=True)
        if version.strip().splitlines()[-1] != self.CLI_VERSION:
            raise RuntimeError("Unexpected Android CLI version")
        packages = ["platform-tools@37.0.1", "platforms/android-37.0@2.0.0",
                    "build-tools/36.0.0@36.0.0", "ndk/30.0.16248370@30.0.16248370"]
        if api:
            image = {"29": "system-images/android-29/default/x86_64@8.0.0",
                     "35": "system-images/android-35/default/x86_64@2.0.0",
                     "37.0": "system-images/android-37.0/google_apis/x86_64@6.0.0"}[api]
            packages += ["emulator@37.2.12", image]
        self.run("sdk", "install", *packages)
        with open(os.environ["GITHUB_PATH"], "a", encoding="utf-8") as output:
            output.write(f"{self.bin}\n{self.sdk / 'platform-tools'}\n{self.sdk / 'emulator'}\n")


if __name__ == "__main__":
    AndroidSdkSetup().install(sys.argv[1] if len(sys.argv) == 2 else None)
