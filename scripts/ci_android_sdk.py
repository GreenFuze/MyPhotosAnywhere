"""Install pinned stable SDK tools with Google's supported Android CLI."""
from pathlib import Path
import hashlib
import os
import subprocess
import sys
import urllib.request
import json
import re
import xml.etree.ElementTree as ET


class InstalledAndroidSdk:
    """Verify exact installed metadata and usable files; CLI success alone is insufficient."""
    def __init__(self, root):
        self.root = Path(root)

    def verify(self, packages):
        records = []
        executable = '.exe' if os.name == 'nt' else ''
        for specification in packages:
            package, version = specification.split('@')
            parts = package.split('/')
            if any(part in ('.', '..') or not re.fullmatch(r'[A-Za-z0-9_.-]+', part) for part in parts):
                raise ValueError('SDK package must be a canonical relative path.')
            if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version):
                raise ValueError('SDK packages require an exact stable revision.')
            expected = tuple(int(value) for value in version.split('.'))
            if len(expected) != 3 or any(value < 0 for value in expected):
                raise ValueError('SDK packages require an exact stable revision.')
            directory = self.root.joinpath(*parts)
            metadata = directory / 'package.xml'
            if not metadata.is_file():
                raise RuntimeError('SDK CLI did not install the required package: ' + specification)
            with metadata.open('rb') as source:
                raw = source.read(256 * 1024 + 1)
            if len(raw) > 256 * 1024:
                raise RuntimeError('Installed SDK metadata exceeds its bound: ' + specification)
            entries = [element for element in ET.fromstring(raw).iter() if element.tag.rsplit('}', 1)[-1] == 'localPackage']
            if len(entries) != 1 or entries[0].get('path') != package.replace('/', ';'):
                raise RuntimeError('Installed SDK metadata has a different package identity: ' + specification)
            revisions = [element for element in entries[0] if element.tag.rsplit('}', 1)[-1] == 'revision']
            if len(revisions) != 1:
                raise RuntimeError('Installed SDK revision is missing or ambiguous: ' + specification)
            fields = [element.tag.rsplit('}', 1)[-1] for element in revisions[0]]
            if len(fields) != len(set(fields)) or set(fields) - {'major', 'minor', 'micro', 'preview'}:
                raise RuntimeError('Installed SDK revision is ambiguous: ' + specification)
            values = {element.tag.rsplit('}', 1)[-1]: int(element.text) for element in revisions[0]}
            actual = tuple(values.get(key, 0) for key in ('major', 'minor', 'micro'))
            if actual != expected or values.get('preview', 0) != 0:
                raise RuntimeError('Installed SDK revision differs from the stable pin: ' + specification)
            required = []
            if parts[0] == 'system-images': required = ['system.img', 'ramdisk.img']
            elif parts[0] == 'platforms': required = ['android.jar']
            elif parts[0] == 'platform-tools': required = ['adb' + executable]
            elif parts[0] == 'emulator': required = ['emulator' + executable]
            elif parts[0] == 'build-tools': required = ['aapt2' + executable, 'lib/apksigner.jar']
            elif parts[0] == 'ndk': required = ['source.properties', 'ndk-build.cmd' if os.name == 'nt' else 'ndk-build']
            else: raise ValueError('SDK package role has no verification policy: ' + package)
            for name in required:
                path = directory / name
                if not path.is_file() or path.stat().st_size == 0:
                    raise RuntimeError('SDK package is incomplete: ' + specification + ' / ' + name)
            records.append({'package': package, 'revision': version, 'metadataSha256': hashlib.sha256(raw).hexdigest(), 'requiredFiles': required})
        return records


class AndroidSdkSetup:
    CLI_SHA256 = "43204dcc42955f4698b7a7ca945f26c26f594f9239f5f8e11b3b73d4ceaa11ae"
    CLI_VERSION = "1.1.16717390"

    def __init__(self):
        self.sdk = Path(os.environ["ANDROID_HOME"])
        self.bin = Path(os.environ["RUNNER_TEMP"]) / "mpa-android-cli"
        self.bin.mkdir(parents=True, exist_ok=True)
        self.cli = self.bin / "android"

    def run(self, *args):
        result = subprocess.run([str(self.cli), "--no-metrics", f"--sdk={self.sdk}", *args],
                                input="y\n" * 100, text=True, capture_output=True, timeout=900)
        diagnostics = Path('build/diagnostics'); diagnostics.mkdir(parents=True, exist_ok=True)
        (diagnostics / 'sdk-install.log').write_text(result.stdout + result.stderr, encoding='utf-8')
        print(result.stdout, end='', flush=True); print(result.stderr, end='', file=sys.stderr, flush=True)
        result.check_returncode()

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
                     "37.0": "system-images/android-37.0/google_apis/x86_64@8.0.0"}[api]
            packages += ["emulator@37.2.12", image]
        self.run("sdk", "install", *packages)
        verified = InstalledAndroidSdk(self.sdk).verify(packages)
        Path('build/diagnostics/sdk-installation.json').write_text(json.dumps({'cliVersion': self.CLI_VERSION,
            'cliSha256': self.CLI_SHA256, 'packages': verified}, indent=2) + '\n', encoding='utf-8')
        with open(os.environ["GITHUB_PATH"], "a", encoding="utf-8") as output:
            output.write(f"{self.bin}\n{self.sdk / 'platform-tools'}\n{self.sdk / 'emulator'}\n")


if __name__ == "__main__":
    AndroidSdkSetup().install(sys.argv[1] if len(sys.argv) == 2 else None)
