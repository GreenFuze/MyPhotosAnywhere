"""Run native Secret Service tests in a disposable private D-Bus/login-keyring session.

No real Google credential or preexisting user's keyring is accepted. Own and join the daemon.
Invoke under dbus-run-session; the caller captures all diagnostics.
"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def main() -> None:
    if sys.platform != "linux" or not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
        raise RuntimeError("A private Linux D-Bus session is required")
    daemon = shutil.which("gnome-keyring-daemon")
    if daemon is None:
        raise RuntimeError("Native Secret Service test requires gnome-keyring-daemon")
    with tempfile.TemporaryDirectory(prefix="mpa-native-vault-") as scratch:
        root = Path(scratch)
        data = root / "data"
        runtime = root / "runtime"
        data.mkdir(mode=0o700)
        runtime.mkdir(mode=0o700)
        env = dict(os.environ, XDG_DATA_HOME=str(data), XDG_RUNTIME_DIR=str(runtime))
        log_path = Path("build/credential-tests/keyring.log")
        log_path.parent.mkdir(parents=True, exist_ok=True)
        with log_path.open("wb") as log:
            owner = subprocess.Popen(
                [daemon, "--foreground", "--unlock", "--components=secrets"],
                stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, env=env,
            )
            try:
                assert owner.stdin is not None
                owner.stdin.write(b"\n")  # Empty password for this disposable test keyring only.
                owner.stdin.close()
                subprocess.run([
                    "gdbus", "wait", "--session", "--timeout", "20", "org.freedesktop.secrets",
                ], env=env, check=True, timeout=25)
                if owner.poll() is not None:
                    raise RuntimeError("Owned Secret Service exited during startup")
                result = subprocess.run([
                    "dotnet", "test", "tests/Mpa.Processor.Credentials.Tests/Mpa.Processor.Credentials.Tests.csproj",
                    "--no-restore", "-c", "Release", "--logger", "trx;LogFileName=credentials.trx",
                    "--results-directory", "build/credential-tests",
                ], env=env, check=False, timeout=180)
                if result.returncode:
                    raise RuntimeError(f"Native credential tests exited {result.returncode}")
                if owner.poll() is not None:
                    raise RuntimeError("Secret Service exited before its owner stopped it")
            finally:
                if owner.poll() is None:
                    owner.terminate()
                try:
                    owner.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    owner.kill()
                    owner.wait(timeout=10)
                    raise RuntimeError("Secret Service did not stop within its owned deadline") from None


if __name__ == "__main__":
    main()
