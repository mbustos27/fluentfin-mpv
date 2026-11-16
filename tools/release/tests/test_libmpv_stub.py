"""The release's libmpv folder with a stub of the library it leaves out (libdvdcss-2.dll): libmpv_check.py, sources.py
and notices.py on fixture folders (pe_fixture's DLLs, fake source packages).

    <build venv>\\Scripts\\python.exe -B -I -m unittest discover -s tools\\release\\tests -t tools\\release\\tests
(build-release.ps1 runs them with the tests; pefile comes with PyInstaller.)
"""

import hashlib
import io
import json
import os
import shutil
import sys
import tarfile
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [os.path.dirname(HERE), HERE]

import libmpv_check  # noqa: E402
import notices  # noqa: E402
import sources  # noqa: E402
from pe_fixture import write_dll  # noqa: E402

CSS = ["dvdcss_close", "dvdcss_error", "dvdcss_is_scrambled", "dvdcss_open", "dvdcss_open_stream", "dvdcss_read",
       "dvdcss_readv", "dvdcss_seek"]
EXCLUDED = {"libdvdcss-2.dll"}
REAL_OWNER = "mingw-w64-ucrt-x86_64-libdvdcss 1.6.0-1"
STUB_OWNER = "fluentfin-dvdcss-stub 1.0.0"  # as the libmpv build writes it


def sha256(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def make_folder(root, css=None, css_exports=CSS):
    """A libmpv folder: libmpv-2.dll -> libdvdnav-4.dll -> libdvdread-8.dll, both DVD libraries importing
    libdvdcss-2.dll (as the MSYS2 build's do). css: libdvdcss-2.dll's BUILD-MANIFEST.txt owner, or None for none."""
    os.makedirs(root)
    owners = {"libmpv-2.dll": "mingw-w64-ucrt-x86_64-mpv 0.42.0-2", "libdvdnav-4.dll": "mingw-w64-ucrt-x86_64-libdvdnav 7.0.0-2",
              "libdvdread-8.dll": "mingw-w64-ucrt-x86_64-libdvdread 7.1.1-1"}
    write_dll(os.path.join(root, "libmpv-2.dll"), "libmpv-2.dll", ["mpv_create"], {"libdvdnav-4.dll": ["dvdnav_open"]})
    write_dll(os.path.join(root, "libdvdnav-4.dll"), "libdvdnav-4.dll", ["dvdnav_open"],
              {"libdvdread-8.dll": ["DVDOpen"], "libdvdcss-2.dll": ["dvdcss_open", "dvdcss_read"]})
    write_dll(os.path.join(root, "libdvdread-8.dll"), "libdvdread-8.dll", ["DVDOpen"], {"libdvdcss-2.dll": CSS})
    if css:
        write_dll(os.path.join(root, "libdvdcss-2.dll"), "libdvdcss-2.dll", css_exports)
        owners["libdvdcss-2.dll"] = css
    with open(os.path.join(root, "BUILD-MANIFEST.txt"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# Seaglass libmpv: a test fixture\n")
        for dll, owner in owners.items():
            path = os.path.join(root, dll)
            fh.write(f"{dll} {os.path.getsize(path)} {sha256(path)} {owner}\n")
    return root


def read(path):
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def fake_package(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as fh:
        fh.write(text.encode())
    with open(path + ".sig", "wb") as fh:
        fh.write(b"sig")


def make_sources(root, stub_line=True, real_package=True):
    """The libmpv sources: MSYS2 source packages (fake bytes), the stub's tarball, licence folders, SOURCES.txt."""
    os.makedirs(root)
    rows = []
    for component, version, licence, dll in [("mpv", "0.42.0-2", "GPL-2.0-or-later", "libmpv-2.dll"),
                                             ("libdvdnav", "7.0.0-2", "GPL-2.0-or-later", "libdvdnav-4.dll"),
                                             ("libdvdread", "7.1.1-1", "GPL-2.0-or-later", "libdvdread-8.dll")] + (
                                                [("libdvdcss", "1.6.0-1", "GPL-2.0-or-later", "libdvdcss-2.dll")] if real_package else []):
        package = f"mingw-w64-{component}-{version}.src.tar.zst"
        fake_package(os.path.join(root, package), component)
        os.makedirs(os.path.join(root, "licenses", f"mingw-w64-ucrt-x86_64-{component}"))
        with open(os.path.join(root, "licenses", f"mingw-w64-ucrt-x86_64-{component}", "COPYING"), "w") as fh:
            fh.write(f"{component} licence")
        rows.append(f"mingw-w64-ucrt-x86_64-{component} | {version} | spdx:{licence} | {dll} | {package} | "
                    f"{sha256(os.path.join(root, package))}")
    stub = os.path.join(root, "stub", "libdvdcss-stub-1.0.0.tar.gz")
    os.makedirs(os.path.dirname(stub))
    with tarfile.open(stub, "w:gz") as tar:
        data = b"/* every dvdcss_* call fails */\n"
        info = tarfile.TarInfo("libdvdcss-stub-1.0.0/dvdcss_stub.c")
        info.size = len(data)
        tar.addfile(info, io.BytesIO(data))
    if stub_line:
        rows.append("fluentfin-mpv-libdvdcss-stub | 1.0.0 | GPL-3.0-or-later: Seaglass's own stub (GPLv3); replaces libdvdcss"
                    f" | libdvdcss-2.dll | stub/libdvdcss-stub-1.0.0.tar.gz | {sha256(stub)}")
    with open(os.path.join(root, "SOURCES.txt"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# Licence files: licenses/<package>/ (COPYING and the like)\n"
                 "# component | version | licence (package metadata) | DLLs | file | SHA-256\n" + "\n".join(rows) + "\n")
    return root


class Fixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="ffm-stub-test-")
        self.addCleanup(shutil.rmtree, self.tmp, True)

    def path(self, *parts):
        return os.path.join(self.tmp, *parts)

    def run_sources(self, folder, sources_dir, repo_files=frozenset()):
        lines = []
        status, problems = sources.libmpv_sources(folder, sources_dir, self.path("out"), lines, repo_files, sorted(EXCLUDED))
        return status, problems, "\n".join(lines)


class StubRule(unittest.TestCase):
    OWNERS = {STUB_OWNER: True, "built here: stub of libdvdcss": True, "stub": True, "Stub (GPL-3.0)": True, "built here (stub)": True, "built here": False,
              REAL_OWNER: False, "mingw-w64-ucrt-x86_64-libdvdcss-stub 1.0": False, "stubborn": False}

    def test_a_stub_is_an_owner_with_the_word_stub_that_is_no_msys2_package(self):
        for owner, expected in self.OWNERS.items():
            self.assertEqual(libmpv_check.is_stub(owner), expected, owner)

    def test_sources_py_has_the_same_rule(self):
        for owner, expected in self.OWNERS.items():
            self.assertEqual(sources.is_stub(owner), expected, owner)


class Check(Fixture):
    def test_a_stub_that_exports_everything_imported_from_it_passes(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        result, problems = libmpv_check.check(folder, EXCLUDED)
        self.assertEqual(problems, [])
        [stub] = result["stubs"]
        self.assertEqual(stub["name"], "libdvdcss-2.dll")
        self.assertEqual(sorted(stub["importedBy"]), ["libdvdnav-4.dll", "libdvdread-8.dll"])
        self.assertEqual(stub["importedBy"]["libdvdread-8.dll"], sorted(CSS))
        self.assertEqual(stub["missing"], {})
        self.assertIn("libdvdcss-2.dll", result["dlls"])

    def test_a_stub_without_a_function_the_folder_imports_fails(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER, css_exports=[f for f in CSS if f != "dvdcss_readv"])
        result, problems = libmpv_check.check(folder, EXCLUDED)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("libdvdread-8.dll imports dvdcss_readv from libdvdcss-2.dll", problems[0])
        self.assertEqual(result["stubs"][0]["missing"], {"libdvdread-8.dll": ["dvdcss_readv"]})

    def test_the_real_library_fails_when_the_release_leaves_it_out(self):
        folder = make_folder(self.path("libmpv"), REAL_OWNER)
        _result, problems = libmpv_check.check(folder, EXCLUDED)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(f"libdvdcss-2.dll ({REAL_OWNER}) is the library the release leaves out", problems[0])

    def test_the_real_library_passes_when_nothing_is_left_out(self):
        folder = make_folder(self.path("libmpv"), REAL_OWNER)
        result, problems = libmpv_check.check(folder, set())
        self.assertEqual((problems, result["stubs"]), ([], []))

    def test_importing_a_left_out_library_that_is_not_there_fails(self):
        folder = make_folder(self.path("libmpv"), None)
        _result, problems = libmpv_check.check(folder, EXCLUDED)
        self.assertEqual(len(problems), 2, problems)
        self.assertTrue(all("libdvdcss-2.dll, which the release leaves out" in p for p in problems), problems)

    def test_a_function_missing_from_any_dll_of_the_folder_fails(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        write_dll(os.path.join(folder, "libdvdnav-4.dll"), "libdvdnav-4.dll", ["dvdnav_close"],
                  {"libdvdread-8.dll": ["DVDOpen"], "libdvdcss-2.dll": ["dvdcss_open"]})
        manifest = os.path.join(folder, "BUILD-MANIFEST.txt")
        lines = [ln for ln in read(manifest).splitlines() if not ln.startswith("libdvdnav-4.dll ")]
        path = os.path.join(folder, "libdvdnav-4.dll")
        lines.append(f"libdvdnav-4.dll {os.path.getsize(path)} {sha256(path)} mingw-w64-ucrt-x86_64-libdvdnav 7.0.0-2")
        with open(manifest, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(lines) + "\n")
        _result, problems = libmpv_check.check(folder, EXCLUDED)
        self.assertEqual(problems, ["libmpv-2.dll imports dvdnav_open from libdvdnav-4.dll, which libdvdnav-4.dll does not export"])


class Prepare(Fixture):
    def test_the_real_library_and_its_manifest_line_are_left_out(self):
        origin = make_folder(self.path("origin"), REAL_OWNER)
        left_out, stubs = libmpv_check.prepare(origin, self.path("copy"), EXCLUDED)
        self.assertEqual((left_out, stubs), (["libdvdcss-2.dll"], []))
        self.assertFalse(os.path.exists(self.path("copy", "libdvdcss-2.dll")))
        self.assertNotIn("libdvdcss-2.dll", read(self.path("copy", "BUILD-MANIFEST.txt")))
        self.assertTrue(os.path.isfile(self.path("copy", "libdvdread-8.dll")))

    def test_a_stub_stays_with_its_manifest_line(self):
        origin = make_folder(self.path("origin"), STUB_OWNER)
        left_out, stubs = libmpv_check.prepare(origin, self.path("copy"), EXCLUDED)
        self.assertEqual((left_out, stubs), ([], ["libdvdcss-2.dll"]))
        self.assertEqual(sha256(self.path("copy", "libdvdcss-2.dll")), sha256(os.path.join(origin, "libdvdcss-2.dll")))
        self.assertIn(STUB_OWNER, read(self.path("copy", "BUILD-MANIFEST.txt")))
        _result, problems = libmpv_check.check(self.path("copy"), EXCLUDED)
        self.assertEqual(problems, [])


class Sources(Fixture):
    def test_the_stub_ships_its_source_and_the_real_library_none_of_its_own(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        status, problems, text = self.run_sources(folder, make_sources(self.path("sources")))
        self.assertEqual(problems, [])
        stub = next(d for d in status["dlls"] if d["name"] == "libdvdcss-2.dll")
        self.assertTrue(stub["stub"])
        self.assertEqual((stub["sources"], stub["licence"]), (["stub/libdvdcss-stub-1.0.0.tar.gz"], "GPL-3.0-or-later"))
        self.assertEqual(status["declared"]["stub/libdvdcss-stub-1.0.0.tar.gz"], "GPL-3.0-or-later")
        shipped = [os.path.relpath(os.path.join(r, f), self.path("out")).replace(os.sep, "/")
                   for r, _d, fs in os.walk(self.path("out")) for f in fs]
        self.assertIn("libmpv/stub/libdvdcss-stub-1.0.0.tar.gz", shipped)
        self.assertEqual([f for f in shipped if "mingw-w64" in f and "libdvdcss" in f], [])
        own = read(self.path("out", "libmpv", "SOURCES-libmpv.txt"))
        self.assertNotIn("mingw-w64-ucrt-x86_64-libdvdcss |", own)
        self.assertIn("fluentfin-mpv-libdvdcss-stub |", own)
        self.assertIn("a stub, not the library of that name", text)
        self.assertIn("mingw-w64-libdvdcss-1.6.0-1.src.tar.zst", text)  # named among what the release leaves out
        # its licence folder is named as a folder, and a line that names COPYING is no line about it
        self.assertIn("licenses/mingw-w64-ucrt-x86_64-libdvdcss/", text)
        self.assertNotIn(", COPYING", text)
        self.assertIn("# Licence files: licenses/<package>/ (COPYING and the like)", own)

    def test_a_line_for_the_real_package_goes_even_when_its_package_is_not_in_the_sources(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        sources_dir = make_sources(self.path("sources"))
        os.remove(os.path.join(sources_dir, "mingw-w64-libdvdcss-1.6.0-1.src.tar.zst"))
        os.remove(os.path.join(sources_dir, "mingw-w64-libdvdcss-1.6.0-1.src.tar.zst.sig"))
        _status, problems, _text = self.run_sources(folder, sources_dir)
        self.assertEqual(problems, [])
        own = read(self.path("out", "libmpv", "SOURCES-libmpv.txt"))
        self.assertNotIn("mingw-w64-ucrt-x86_64-libdvdcss |", own)

    def test_a_stub_without_a_line_in_sources_txt_fails(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        _status, problems, _text = self.run_sources(folder, make_sources(self.path("sources"), stub_line=False))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("libdvdcss-2.dll is a stub", problems[0])
        self.assertIn("has no line for it", problems[0])

    def test_a_stub_whose_source_is_in_the_repository_says_so(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        sources_dir = make_sources(self.path("sources"))
        shutil.rmtree(os.path.join(sources_dir, "stub"))
        repo = {"stub/libdvdcss-stub-1.0.0.tar.gz"}
        status, problems, text = self.run_sources(folder, sources_dir, repo)
        self.assertEqual(problems, [])
        stub = next(d for d in status["dlls"] if d["name"] == "libdvdcss-2.dll")
        self.assertEqual((stub["sources"], stub["repoSources"]), ([], ["stub/libdvdcss-stub-1.0.0.tar.gz"]))
        self.assertIn("stub/libdvdcss-stub-1.0.0.tar.gz  in Seaglass's source", text)

    def test_a_stub_whose_source_is_nowhere_fails(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        sources_dir = make_sources(self.path("sources"))
        shutil.rmtree(os.path.join(sources_dir, "stub"))
        _status, problems, _text = self.run_sources(folder, sources_dir)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("neither in the libmpv sources nor in Seaglass's repository", problems[0])

    def test_shipping_the_real_library_source_fails_when_it_is_left_out(self):
        # the real libdvdcss in the folder (as when the check is bypassed): its package would be copied
        folder = make_folder(self.path("libmpv"), REAL_OWNER)
        _status, problems, _text = self.run_sources(folder, make_sources(self.path("sources")))
        self.assertTrue(problems)
        self.assertTrue(all("which the release leaves out" in p for p in problems), problems)
        self.assertTrue(any("mingw-w64-libdvdcss-1.6.0-1.src.tar.zst" in p for p in problems), problems)
        self.assertTrue(any("licenses/mingw-w64-ucrt-x86_64-libdvdcss" in p for p in problems), problems)


class Notices(Fixture):
    def test_the_notices_say_what_the_stub_is_and_that_encrypted_dvds_are_not_supported(self):
        folder = make_folder(self.path("libmpv"), STUB_OWNER)
        status, problems, _text = self.run_sources(folder, make_sources(self.path("sources")))
        self.assertEqual(problems, [])
        status = json.loads(json.dumps(status))  # as build-release.ps1 hands it over
        lines, texts = notices.libmpv_section(status, self.path("cache"))
        text = "\n".join(lines)
        self.assertIn("Encrypted DVDs aren't supported", text)
        block = text[text.index("libdvdcss-2.dll (" + STUB_OWNER):]
        block = block.split("\n\n")[0]
        self.assertIn("A stub, not the library of that name", block)
        self.assertIn("Licence: GPL-3.0-or-later (its text: LICENSE in this folder)", block)
        self.assertIn("Source:  libmpv/stub/libdvdcss-stub-1.0.0.tar.gz", block)
        self.assertFalse(any("libdvdcss licence" in t for _h, t in texts))

    def test_no_dvd_sentence_without_a_stub(self):
        folder = make_folder(self.path("libmpv"), REAL_OWNER)
        lines = []
        status, problems = sources.libmpv_sources(folder, make_sources(self.path("sources")), self.path("out"), lines)
        self.assertEqual(problems, [])
        section, _texts = notices.libmpv_section(json.loads(json.dumps(status)), self.path("cache"))
        self.assertNotIn("Encrypted DVDs", "\n".join(section))


if __name__ == "__main__":
    unittest.main()
