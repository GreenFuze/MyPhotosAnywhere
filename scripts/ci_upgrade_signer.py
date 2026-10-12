"""Materialize the established upgrade signer only in a trusted CI runner's temp directory."""
from pathlib import Path
import base64
import hashlib
import os
import subprocess


class UpgradeSigner:
    CERTIFICATE_SHA256 = "1262430ed1e257b54a4c09697cbc63bc148af11755485bcf7b2c66492f0379b4"

    def prepare(self):
        if os.environ.get("GITHUB_EVENT_NAME") != "push" or os.environ.get("GITHUB_REPOSITORY") != "GreenFuze/MyPhotosAnywhere":
            raise RuntimeError("Upgrade signer is restricted to trusted repository pushes")
        encoded = os.environ.get("MPA_UPGRADE_KEYSTORE_B64")
        if not encoded:
            raise RuntimeError("Protected upgrade signing secret is missing")
        destination = Path(os.environ["RUNNER_TEMP"]) / "mpa-upgrade.keystore"
        try:
            descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "wb") as output:
                output.write(base64.b64decode(encoded, validate=True))
            certificate = subprocess.check_output([
                "keytool", "-exportcert", "-alias", "androiddebugkey", "-storepass", "android",
                "-keystore", str(destination),
            ], stderr=subprocess.PIPE)
            if hashlib.sha256(certificate).hexdigest() != self.CERTIFICATE_SHA256:
                raise RuntimeError("Established upgrade signing certificate mismatch")
            with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as environment:
                environment.write(f"MPA_UPGRADE_KEYSTORE_PATH={destination}\n")
            print("Established upgrade certificate verified; temporary signer prepared.")
        except BaseException:
            destination.unlink(missing_ok=True)
            raise


if __name__ == "__main__":
    UpgradeSigner().prepare()
