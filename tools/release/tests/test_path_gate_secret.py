"""1.7 trending: path_gate.py's --secret-env (the Trakt Client ID gate of build-release.ps1). The value comes from the
environment, its hits name the file and the variable only, it may be in the files --allow-secret-in names and must be in
those --expect-secret-in names. A made-up value; nothing here reads the real one.

    python -B -I -m unittest discover -s tools\\release\\tests -t tools\\release\\tests -p "test_path_gate_secret.py"
"""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
GATE = os.path.join(os.path.dirname(HERE), "path_gate.py")
SECRET = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
VAR = "NIXIE_GATE_TEST_SECRET"


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


class SecretGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="nixie-gate-secret-")
        self.package = os.path.join(self.tmp, "package")
        write(os.path.join(self.package, "app", "Nixie.dll"), b"MZ...NixieTraktClientId\x00" + SECRET.encode() + b"\x00...")
        write(os.path.join(self.package, "app", "Other.dll"), b"MZ... nothing to see")
        write(os.path.join(self.package, "README.txt"), b"Nixie")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_gate(self, *args, secret=SECRET):
        env = {k: v for k, v in os.environ.items() if k != VAR}
        if secret is not None:
            env[VAR] = secret
        p = subprocess.run([sys.executable, "-B", "-I", GATE, "--secret-env", VAR, "--dir", self.package, *args],
                           capture_output=True, text=True, timeout=120, env=env,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        out = p.stdout + p.stderr
        self.assertNotIn(SECRET, out)
        self.assertNotIn(SECRET.upper(), out)
        return p.returncode, out

    def test_only_in_the_allowed_file_passes(self):
        code, out = self.run_gate("--allow-secret-in", r"app\Nixie.dll", "--expect-secret-in", r"app\Nixie.dll")
        self.assertEqual(0, code, out)
        self.assertIn("0 hits", out)

    def test_anywhere_else_fails_naming_the_file_and_the_variable_only(self):
        write(os.path.join(self.package, "app", "appsettings.json"), ('{"trakt": "%s"}' % SECRET).encode())
        code, out = self.run_gate("--allow-secret-in", r"app\Nixie.dll")
        self.assertEqual(1, code, out)
        self.assertIn("SECRET HIT", out)
        self.assertIn("appsettings.json", out)
        self.assertIn("$" + VAR, out)
        self.assertNotIn("Nixie.dll: the value", out)

    def test_utf16_and_inside_a_zip_are_found_too(self):
        write(os.path.join(self.package, "app", "wide.bin"), SECRET.encode("utf-16-le"))
        source = os.path.join(self.tmp, "source.zip")
        with zipfile.ZipFile(source, "w") as zf:
            zf.writestr("fluentfin-mpv/config.txt", "id=" + SECRET.upper())
        code, out = self.run_gate("--allow-secret-in", r"app\Nixie.dll", "--archive", source)
        self.assertEqual(1, code, out)
        self.assertIn("wide.bin", out)
        self.assertIn("source.zip!fluentfin-mpv/config.txt", out)

    def test_missing_from_the_file_it_must_be_in_fails(self):
        write(os.path.join(self.package, "app", "Nixie.dll"), b"MZ... built without it")
        code, out = self.run_gate("--allow-secret-in", r"app\Nixie.dll", "--expect-secret-in", r"app\Nixie.dll")
        self.assertEqual(1, code, out)
        self.assertIn("MISSING", out)

    def test_without_the_variable_there_is_nothing_to_find(self):
        code, out = self.run_gate(secret=None)
        self.assertEqual(0, code, out)

    def test_the_ordinary_needles_still_work_beside_it(self):
        write(os.path.join(self.package, "app", "paths.txt"), b"C:\\Users\\builder\\checkout")
        code, out = self.run_gate("--allow-secret-in", r"app\Nixie.dll", "--needle", r"C:\Users\builder")
        self.assertEqual(1, code, out)
        self.assertIn("HIT", out)
        self.assertIn("paths.txt", out)


if __name__ == "__main__":
    unittest.main()
