from pathlib import Path
import os
import hashlib
import io
from unittest.mock import patch
import tempfile
import unittest
from ci_android_sdk import AndroidSdkSetup, InstalledAndroidSdk


class InstalledAndroidSdkTest(unittest.TestCase):
    image = 'system-images/android-37.0/google_apis/x86_64'

    def fixture(self, root, revision='<major>8</major>', identity=None):
        directory = Path(root) / self.image
        directory.mkdir(parents=True)
        identity = identity or self.image.replace('/', ';')
        (directory/'package.xml').write_text(f'<repository><localPackage path="{identity}"><revision>{revision}</revision></localPackage></repository>', encoding='utf-8')
        for name in ('system.img', 'ramdisk.img'):
            (directory/name).write_bytes(b'synthetic image fixture')
        return directory

    def test_exact_installed_revision_and_complete_image_are_required(self):
        with tempfile.TemporaryDirectory() as root:
            directory = self.fixture(root)
            report = InstalledAndroidSdk(root).verify([self.image+'@8.0.0'])
            self.assertEqual('8.0.0', report[0]['revision'])
            self.assertEqual(64, len(report[0]['metadataSha256']))
            (directory/'system.img').unlink()
            with self.assertRaisesRegex(RuntimeError, 'incomplete'):
                InstalledAndroidSdk(root).verify([self.image+'@8.0.0'])

    def test_zero_exit_installer_cannot_turn_a_skipped_package_into_success(self):
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaisesRegex(RuntimeError, 'did not install'):
                InstalledAndroidSdk(root).verify([self.image+'@8.0.0'])

    def test_wrong_identity_revision_preview_or_duplicate_revision_is_rejected(self):
        for revision, identity in [('<major>6</major>', None), ('<major>8</major><preview>1</preview>', None),
                ('<major>8</major><major>8</major>', None), ('<major>8</major>', 'system-images;foreign')]:
            with self.subTest(revision=revision, identity=identity), tempfile.TemporaryDirectory() as root:
                self.fixture(root, revision, identity)
                with self.assertRaises(RuntimeError):
                    InstalledAndroidSdk(root).verify([self.image+'@8.0.0'])

    def test_empty_critical_file_and_oversized_metadata_fail(self):
        with tempfile.TemporaryDirectory() as root:
            directory = self.fixture(root)
            (directory/'ramdisk.img').write_bytes(b'')
            with self.assertRaisesRegex(RuntimeError, 'incomplete'):
                InstalledAndroidSdk(root).verify([self.image+'@8.0.0'])
            (directory/'package.xml').write_bytes(b'x'*(256*1024+1))
            with self.assertRaisesRegex(RuntimeError, 'exceeds'):
                InstalledAndroidSdk(root).verify([self.image+'@8.0.0'])

    def test_all_sdk_package_roles_verify_their_own_payload(self):
        executable = '.exe' if os.name == 'nt' else ''
        packages = {'platforms/android-37.0@2.0.0': ['android.jar'],
            'platform-tools@37.0.1': ['adb'+executable], 'emulator@37.2.12': ['emulator'+executable],
            'build-tools/36.0.0@36.0.0': ['aapt2'+executable, 'lib/apksigner.jar'],
            'ndk/30.0.16248370@30.0.16248370': ['source.properties', 'ndk-build.cmd' if os.name == 'nt' else 'ndk-build']}
        with tempfile.TemporaryDirectory() as root:
            for specification, names in packages.items():
                package, version = specification.split('@'); major, minor, micro = version.split('.')
                directory = Path(root)/package; directory.mkdir(parents=True)
                (directory/'package.xml').write_text(f'<repository><localPackage path="{package.replace("/", ";")}"><revision><major>{major}</major><minor>{minor}</minor><micro>{micro}</micro></revision></localPackage></repository>', encoding='utf-8')
                for name in names:
                    path = directory/name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(b'fixture')
            self.assertEqual(5, len(InstalledAndroidSdk(root).verify(packages)))

    def test_paths_and_versions_must_be_exact_and_canonical(self):
        for value in ('../platform-tools@37.0.1', '/platform-tools@37.0.1', 'C:/platform-tools@37.0.1',
                      'platform-tools@37.0.+', 'platform-tools@037.0.1', 'platform-tools@37.0.1-beta'):
            with self.subTest(value=value), tempfile.TemporaryDirectory() as root, self.assertRaises(ValueError):
                InstalledAndroidSdk(root).verify([value])


class AndroidCliPinTest(unittest.TestCase):
    def test_changed_download_cannot_write_or_execute_an_unreviewed_cli(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            with patch.dict(os.environ, {'ANDROID_HOME':str(directory/'sdk'),'RUNNER_TEMP':str(directory/'tools')}), \
                 patch('ci_android_sdk.urllib.request.urlopen', return_value=io.BytesIO(b'changed-download')), \
                 patch('ci_android_sdk.subprocess.check_output') as execute:
                setup = AndroidSdkSetup()
                with self.assertRaisesRegex(RuntimeError, 'Android CLI changed'):
                    setup.install()
                self.assertFalse(setup.cli.exists())
                execute.assert_not_called()

    def test_matching_binary_with_wrong_version_cannot_start_sdk_installation(self):
        binary = b'synthetic-reviewed-cli'
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            with patch.dict(os.environ, {'ANDROID_HOME':str(directory/'sdk'),'RUNNER_TEMP':str(directory/'tools')}), \
                 patch.object(AndroidSdkSetup, 'CLI_SHA256', hashlib.sha256(binary).hexdigest()), \
                 patch('ci_android_sdk.urllib.request.urlopen', return_value=io.BytesIO(binary)), \
                 patch('ci_android_sdk.subprocess.check_output', return_value='unreviewed-version\n') as execute, \
                 patch.object(AndroidSdkSetup, 'run') as install:
                setup = AndroidSdkSetup()
                with self.assertRaisesRegex(RuntimeError, 'Unexpected Android CLI version'):
                    setup.install()
                self.assertEqual(binary, setup.cli.read_bytes())
                execute.assert_called_once_with([str(setup.cli), '--no-metrics', '--version'], text=True)
                install.assert_not_called()


if __name__ == '__main__':
    unittest.main()
