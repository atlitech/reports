"""The deployment helper must keep credentials private and produce matching verifiers."""
import base64
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "scripts/create-hosted-reports-secrets.py"


class HostedCredentialTests(unittest.TestCase):
    def test_credentials_match_verifiers_and_are_not_printed_or_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "private" / "hosted.env"
            result = subprocess.run([sys.executable, str(SCRIPT), str(path)],
                                    capture_output=True, text=True, check=True)
            original = path.read_bytes()
            values = dict(line.split("=", 1) for line in original.decode().splitlines()
                          if not line.startswith("#"))
            self.assertEqual(len(values), 4)
            for name, key_id in (("reports", "application"), ("provisioner", "gateway")):
                credential = values[f"Parameters__{name}Key"]
                self.assertTrue(credential.startswith(key_id + "."))
                self.assertEqual(len(credential.split(".")[1]), 64)
                expected = base64.b64encode(hashlib.sha256(credential.encode()).digest()).decode()
                self.assertEqual(values[f"Parameters__{name}KeyHash"], expected)
                self.assertNotIn(credential, result.stdout + result.stderr)
            if os.name != "nt":
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            second = subprocess.run([sys.executable, str(SCRIPT), str(path)],
                                    capture_output=True, text=True)
            self.assertEqual(second.returncode, 2)
            self.assertEqual(path.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
