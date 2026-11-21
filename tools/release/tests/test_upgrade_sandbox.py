"""Nixie 1.5: the upgrade test in Windows Sandbox (tools\\release\\upgrade-test-sandbox.ps1 and sandbox\\upgrade-inside.ps1),
checked without a Sandbox: the host script's -DryRun (the .wsb it writes, its plan), its refusals, the inside script's
-DryRun plan, and its refusal to run anywhere but in the Sandbox. Nothing here installs anything or starts a Sandbox; the
inputs are placeholder files named like the installers.

    python -B -I -m unittest discover -s tools\\release\\tests -t tools\\release\\tests -p "test_upgrade_sandbox.py"
"""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
RELEASE = os.path.dirname(HERE)
HOST = os.path.join(RELEASE, "upgrade-test-sandbox.ps1")
INSIDE = os.path.join(RELEASE, "sandbox", "upgrade-inside.ps1")
POWERSHELL = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")


def run(script, *args):
    """The script in Windows PowerShell 5.1 (what the Sandbox has), no window, its output and exit status."""
    p = subprocess.run([POWERSHELL, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, *args],
                       capture_output=True, text=True, timeout=120, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    return p.returncode, p.stdout + p.stderr


def read_json(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def placeholder(folder, name):
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, name)
    with open(path, "wb") as f:
        f.write(b"placeholder, not an installer")
    with open(path + ".sha256", "w") as f:
        f.write("0" * 64 + " *" + name + "\n")
    return path


class HostScriptTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="nixie-sandbox-test-")
        self.dist = os.path.join(self.tmp, "dist")
        placeholder(self.dist, "Nixie-1.5.0-win-x64.msi")
        placeholder(self.dist, "Nixie-1.4.1-win-x64.msi")
        self.test_msi = placeholder(os.path.join(self.tmp, "made"), "Nixie-1.5.90-win-x64.msi")
        self.out = os.path.join(self.tmp, "out")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def dry(self, *extra):
        return run(HOST, "-Out", self.out, "-Dist", self.dist, "-Version", "1.5.0", "-TestMsi", self.test_msi, "-DryRun", *extra)

    def test_the_dry_run_writes_the_wsb_maps_inputs_read_only_and_starts_nothing(self):
        code, said = self.dry()
        self.assertEqual(0, code, said)
        self.assertIn("dry run: the Sandbox was not started", said)
        config = ET.parse(os.path.join(self.out, "nixie-upgrade-test.wsb")).getroot()
        self.assertEqual("Configuration", config.tag)
        for switch in ("Networking", "VGpu", "AudioInput", "VideoInput", "ClipboardRedirection", "PrinterRedirection"):
            self.assertEqual("Disable", config.findtext(switch), switch)
        mapped = {m.findtext("SandboxFolder"): (m.findtext("HostFolder"), m.findtext("ReadOnly")) for m in config.iter("MappedFolder")}
        self.assertEqual({r"C:\nixie\dist", r"C:\nixie\test-build", r"C:\nixie\scripts", r"C:\nixie\results"}, set(mapped))
        self.assertEqual((self.dist, "true"), mapped[r"C:\nixie\dist"])
        self.assertEqual("true", mapped[r"C:\nixie\test-build"][1])
        self.assertEqual((os.path.join(RELEASE, "sandbox"), "true"), mapped[r"C:\nixie\scripts"])
        self.assertEqual((os.path.join(self.out, "results"), "false"), mapped[r"C:\nixie\results"])   # the one folder it writes
        logon = config.find("LogonCommand").findtext("Command")
        self.assertIn(r"-File C:\nixie\scripts\upgrade-inside.ps1 -Root C:\nixie", logon)
        self.assertIn(r"-NewMsi dist\Nixie-1.5.0-win-x64.msi -PreviousMsi dist\Nixie-1.4.1-win-x64.msi -TestMsi test-build\Nixie-1.5.90-win-x64.msi", logon)
        self.assertNotIn(os.path.expanduser("~"), logon)            # nothing of this PC's folders inside
        self.assertTrue(os.path.exists(os.path.join(self.out, "test-build", "Nixie-1.5.90-win-x64.msi.sha256")))
        plan = read_json(os.path.join(self.out, "plan.json"))
        self.assertEqual("off", plan["networking"])
        self.assertEqual(3, len(plan["legs"]))
        self.assertIn("old flow", plan["legs"][0])
        self.assertIn("Restart to update", plan["legs"][1])

    def test_a_previous_installer_elsewhere_gets_its_own_read_only_folder(self):
        elsewhere = placeholder(os.path.join(self.tmp, "downloads"), "Nixie-1.4.1-win-x64.msi")
        code, said = self.dry("-PreviousMsi", elsewhere)
        self.assertEqual(0, code, said)
        config = ET.parse(os.path.join(self.out, "nixie-upgrade-test.wsb")).getroot()
        mapped = {m.findtext("SandboxFolder"): (m.findtext("HostFolder"), m.findtext("ReadOnly")) for m in config.iter("MappedFolder")}
        self.assertEqual((os.path.dirname(elsewhere), "true"), mapped[r"C:\nixie\previous"])
        self.assertIn(r"-PreviousMsi previous\Nixie-1.4.1-win-x64.msi", config.find("LogonCommand").findtext("Command"))

    def test_it_empties_only_folders_it_made_itself(self):
        # review m9: an existing <Out>\results or <Out>\test-build without the script's marker is refused, not deleted
        os.makedirs(os.path.join(self.out, "results"))
        keep = os.path.join(self.out, "results", "someone-elses.txt")
        with open(keep, "w") as f:
            f.write("keep me")
        code, said = self.dry()
        self.assertEqual(2, code, said)
        self.assertIn("is not this script's", said)
        self.assertTrue(os.path.exists(keep))
        shutil.rmtree(os.path.join(self.out, "results"))
        code, said = self.dry()                                      # its own folders: made, marked, and made again
        self.assertEqual(0, code, said)
        self.assertTrue(os.path.exists(os.path.join(self.out, "results", ".nixie-upgrade-test")))
        self.assertTrue(os.path.exists(os.path.join(self.out, "test-build", ".nixie-upgrade-test")))
        code, said = self.dry()
        self.assertEqual(0, code, said)

    def test_it_refuses_inputs_that_do_not_make_an_upgrade(self):
        code, said = run(HOST, "-Out", self.out, "-Dist", self.dist, "-Version", "1.5.0", "-TestVersion", "1.5.0", "-DryRun")
        self.assertEqual(2, code, said)
        self.assertIn("numbered higher", said)
        code, said = run(HOST, "-Out", self.out, "-Dist", self.dist, "-Version", "1.4.0", "-DryRun")
        self.assertEqual(2, code, said)                              # no Nixie-1.4.0 installer
        code, said = run(HOST, "-Out", os.path.join(RELEASE, "out"), "-Dist", self.dist, "-Version", "1.5.0", "-DryRun")
        self.assertEqual(2, code, said)
        self.assertIn("outside the repository", said)


class InsideScriptTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="nixie-sandbox-root-")
        placeholder(os.path.join(self.root, "dist"), "Nixie-1.5.0-win-x64.msi")
        placeholder(os.path.join(self.root, "dist"), "Nixie-1.4.1-win-x64.msi")
        placeholder(os.path.join(self.root, "test-build"), "Nixie-1.5.90-win-x64.msi")
        self.results = os.path.join(self.root, "results")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def args(self):
        return ["-Root", self.root, "-NewMsi", r"dist\Nixie-1.5.0-win-x64.msi", "-PreviousMsi", r"dist\Nixie-1.4.1-win-x64.msi",
                "-TestMsi", r"test-build\Nixie-1.5.90-win-x64.msi", "-Results", self.results, "-NoShutdown"]

    def test_outside_windows_sandbox_it_refuses_before_anything(self):
        code, said = run(INSIDE, *self.args())
        self.assertEqual(3, code, said)
        self.assertIn("REFUSED", said)
        self.assertFalse(os.path.exists(self.results))              # not even its results folder

    def test_the_dry_run_plans_both_legs_and_the_elevation_proof(self):
        code, said = run(INSIDE, *self.args(), "-DryRun")
        self.assertEqual(0, code, said)
        self.assertIn("nothing installed, no user made, nothing started", said)
        plan = read_json(os.path.join(self.results, "plan.json"))
        leg1 = " ".join(plan["leg1"])
        self.assertIn("/qn /norestart /l*v leg1-previous.log", leg1)
        self.assertIn("as 1.4.1's updater does", leg1)
        self.assertIn("nothing reopened", leg1)
        leg2 = " ".join(plan["leg2"])
        self.assertIn("--install-update", leg2)
        self.assertIn("msiexec /passive /norestart /l*v Updates\\install.log", leg2)
        self.assertIn("opened again", leg2)
        self.assertIn("Medium Mandatory Level", " ".join(plan["elevation"]))

    def test_its_results_code_runs_in_windows_powershell_5_1(self):
        # the 1.5.0 Sandbox run lost results.json, done.json and the shutdown to @($checks) ("Argument types do not match"
        # in 5.1): run the script's own checks list, Check, the tester's results writer, then the outer's merge and writer
        with open(INSIDE, encoding="utf-8-sig") as f:
            lines = f.read().splitlines()
        take = lambda start: next(i for i, line in enumerate(lines) if line.lstrip().startswith(start))
        setup = lines[take("$checks = New-Object"):take("function Check(") + 1]
        tester = lines[take("$result = [ordered]@{"):take("catch { Step \"results.json not written") + 1]
        outer = lines[take("$r = $null"):take("catch { Write-Host \"results.json not written") + 1]
        os.makedirs(self.results)
        script = "\n".join(["$ErrorActionPreference = 'Stop'", f"$Results = '{self.results}'",
                            "$previous = $new = $test = [pscustomobject]@{ Version = '1.5.0' }; $started = Get-Date",
                            *setup, "Check 'one' $true 'a'; Check 'two' $false 'b'", *tester,
                            "$t = Get-Content -Raw (Join-Path $Results 'results.json') | ConvertFrom-Json",
                            "Write-Host \"tester $(@($t.checks).Count)\"", *outer,
                            "$o = Get-Content -Raw (Join-Path $Results 'results.json') | ConvertFrom-Json",
                            "Write-Host \"outer $(@($o.checks).Count) $($o.outer -ne $null)\""])
        probe = os.path.join(self.root, "results-probe.ps1")
        with open(probe, "w", encoding="utf-8-sig") as f:
            f.write(script)
        code, said = run(probe)
        self.assertEqual(0, code, said)
        self.assertNotIn("Argument types do not match", said)
        self.assertNotIn("not written", said)
        self.assertIn("tester 2", said)
        self.assertIn("outer 4 True", said)   # the tester's two read back, plus the outer's own two, and its notes
        self.assertIn("$null = $p.Handle", "\n".join(lines))   # the tester's exit code stays readable

    def test_versions_must_rise_through_the_legs(self):
        os.remove(os.path.join(self.root, "test-build", "Nixie-1.5.90-win-x64.msi"))
        placeholder(os.path.join(self.root, "test-build"), "Nixie-1.4.9-win-x64.msi")
        args = self.args()
        args[args.index("-TestMsi") + 1] = r"test-build\Nixie-1.4.9-win-x64.msi"
        code, said = run(INSIDE, *args, "-DryRun")
        self.assertEqual(2, code, said)
        self.assertIn("must rise", said)


if __name__ == "__main__":
    unittest.main()
