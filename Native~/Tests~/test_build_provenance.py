#!/usr/bin/env python3
"""Tests for the build-provenance gates (SECURITY-282834): the stamp, the archive-first OpenUSD
build, the record refusal and the unverified-payload marker.

    python3 -m unittest discover -s "Native~/Tests~" -p "test_*.py"

Nothing here builds OpenUSD or touches the network: build_usd.py is replaced by a stand-in that
writes an install tree, and archives are passed with --archive.
"""

import contextlib
import hashlib
import io
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest import mock

NATIVE = pathlib.Path(__file__).resolve().parent.parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(NATIVE))

import build_openusd  # noqa: E402
import generate_native_hashes  # noqa: E402
import verify_dependency_digests  # noqa: E402
import verify_upstream_sources as sources  # noqa: E402

PLATFORM = build_openusd.host_platform()
TBB_URL = "https://example.invalid/oneTBB/archive/v0.0.zip"

# One file per scan rule of the host platform, so a stamp over it is never empty.
INSTALL_FILES = {
    "macos": ["lib/libusd_ms.dylib", "lib/libtbb.dylib", "lib/usd/plugInfo.json"],
    "windows": ["lib/usd_ms.dll", "bin/tbb.dll", "lib/usd/plugInfo.json"],
    "linux": ["lib/libusd_ms.so", "lib/libtbb.so.2", "lib/usd/plugInfo.json"],
}[PLATFORM]


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def quiet():
    """Swallows the scripts' progress output so a failing assertion is what the log shows."""
    stack = contextlib.ExitStack()
    stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
    stack.enter_context(contextlib.redirect_stderr(io.StringIO()))
    return stack


def make_install(root: pathlib.Path) -> None:
    for relative in INSTALL_FILES:
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(f"contents of {relative}".encode())


def rows_for(commit: str, remote: str, archive_sha: str, patch_sha: str = None):
    rows = [
        sources.Row(component="OpenUSD", version="0.0", artifact="git-clone", url=remote, sha256="-",
                    git_commit=commit, evidence="test", recorded="test"),
        sources.Row(component="oneTBB", version="0.0", artifact="github-archive", url=TBB_URL,
                    sha256=archive_sha, git_commit="0" * 40, evidence="test", recorded="test"),
    ]
    if patch_sha:
        rows.append(sources.Row(component="OpenUSD-patch", version="test", artifact="patch",
                                url="Native~/patches/test.patch", sha256=patch_sha,
                                git_commit=commit, evidence="test", recorded="test"))
    return rows


class TempDirTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = pathlib.Path(self._tmp.name).resolve()

    def tearDown(self):
        self._tmp.cleanup()


class StampTests(TempDirTest):
    def setUp(self):
        super().setUp()
        self.install = self.tmp / "install"
        make_install(self.install)
        self.rows = rows_for("a" * 40, "https://example.invalid/OpenUSD.git", "b" * 64, "d" * 64)

    def stamp(self, archives=None, patches=None):
        with quiet():
            sources.write_stamp(self.install, PLATFORM, self.rows, False,
                                sources.archive_rows(self.rows) if archives is None else archives,
                                sources.patch_rows(self.rows) if patches is None else patches)

    def check(self) -> bool:
        with quiet():
            return sources.check_stamp(self.install, PLATFORM, self.rows)

    def test_fresh_stamp_passes(self):
        self.stamp()
        self.assertTrue(self.check())

    def test_missing_stamp_fails(self):
        self.assertFalse(self.check())

    def test_version_1_stamp_fails(self):
        (self.install / sources.STAMP_NAME).write_text(json.dumps({
            "platform": PLATFORM,
            "sources": [{k: r[k] for k in ("component", "version", "url", "sha256", "git_commit")}
                        for r in self.rows],
        }))
        self.assertFalse(self.check())

    def test_archive_not_checked_before_build_fails(self):
        self.stamp(archives=[])
        self.assertFalse(self.check())

    def test_patch_not_applied_fails(self):
        self.stamp(patches=[])
        self.assertFalse(self.check())

    def test_version_2_stamp_fails(self):
        self.stamp()
        path = self.install / sources.STAMP_NAME
        stamp = json.loads(path.read_text())
        stamp["stamp_version"] = 2
        del stamp["patches_applied"]
        path.write_text(json.dumps(stamp))
        self.assertFalse(self.check())

    def test_changed_install_file_fails(self):
        self.stamp()
        (self.install / INSTALL_FILES[0]).write_bytes(b"replaced")
        self.assertFalse(self.check())

    def test_added_install_file_fails(self):
        self.stamp()
        extra = self.install / INSTALL_FILES[-1]
        (extra.parent / "extra.json").write_text("{}")
        self.assertFalse(self.check())

    def test_removed_install_file_fails(self):
        self.stamp()
        (self.install / INSTALL_FILES[0]).unlink()
        self.assertFalse(self.check())

    def test_other_record_fails(self):
        self.stamp()
        self.rows = rows_for("c" * 40, "https://example.invalid/OpenUSD.git", "b" * 64, "d" * 64)
        self.assertFalse(self.check())

    def test_symlinked_stamp_is_refused(self):
        target = self.tmp / "elsewhere.json"
        target.write_text("{}")
        try:
            (self.install / sources.STAMP_NAME).symlink_to(target)
        except OSError as exc:
            # Windows allows symlinks only with Developer Mode or an elevated shell (WinError 1314).
            self.skipTest(f"cannot create a symlink here: {exc}")
        with self.assertRaises(SystemExit), quiet():
            sources.write_stamp(self.install, PLATFORM, self.rows, False, [], [])
        self.assertFalse(self.check())

    def test_unscanned_stamp_fails_when_scan_required(self):
        self.stamp()
        with quiet():
            self.assertFalse(sources.check_stamp(self.install, PLATFORM, self.rows, require_scan=True))

    def test_scanned_stamp_passes_when_scan_required(self):
        with quiet():
            sources.write_stamp(self.install, PLATFORM, self.rows, True,
                                sources.archive_rows(self.rows), sources.patch_rows(self.rows))
            self.assertTrue(sources.check_stamp(self.install, PLATFORM, self.rows, require_scan=True))

    def test_manual_stamp_option_is_gone(self):
        with mock.patch.object(sys, "argv", ["verify_upstream_sources.py", "--platform", PLATFORM,
                                             "--stamp", str(self.install)]), quiet():
            self.assertEqual(sources.main(), 2)
        self.assertFalse((self.install / sources.STAMP_NAME).exists())


class RecordTests(TempDirTest):
    def run_record(self, root: pathlib.Path, rows) -> int:
        record_dir = self.tmp / "digests"
        argv = ["verify_dependency_digests.py", "--platform", PLATFORM, "--root", str(root),
                "--scan", "--record"]
        with mock.patch.object(sys, "argv", argv), \
                mock.patch.object(verify_dependency_digests, "DIGEST_DIR", record_dir), \
                mock.patch.object(verify_dependency_digests, "REPO", self.tmp), \
                mock.patch.object(sources, "load", return_value=rows), quiet():
            return verify_dependency_digests.main()

    def test_record_refuses_unstamped_tree(self):
        install = self.tmp / "install"
        make_install(install)
        rows = rows_for("a" * 40, "https://example.invalid/OpenUSD.git", "b" * 64)
        self.assertEqual(self.run_record(install, rows), 1)
        self.assertFalse((self.tmp / "digests").exists())

    def test_record_accepts_stamped_tree(self):
        install = self.tmp / "install"
        make_install(install)
        rows = rows_for("a" * 40, "https://example.invalid/OpenUSD.git", "b" * 64)
        with quiet():
            sources.write_stamp(install, PLATFORM, rows, False, sources.archive_rows(rows), [])
        self.assertEqual(self.run_record(install, rows), 0)
        self.assertTrue((self.tmp / "digests" / f"{PLATFORM}.sha256").is_file())


class MarkerTests(TempDirTest):
    def test_manifest_refuses_unverified_payload(self):
        payload = self.tmp / "Plugins"
        root = payload / "x86_64" / "Linux"
        root.mkdir(parents=True)
        (root / "libexample.so").write_bytes(b"\x7fELF not really")
        output = self.tmp / "NativeRuntimeHashes.g.cs"
        with mock.patch.object(generate_native_hashes, "PAYLOAD", payload), \
                mock.patch.object(generate_native_hashes, "OUTPUT", output), \
                mock.patch.object(generate_native_hashes, "REPO", self.tmp), \
                mock.patch.object(sys, "argv", ["generate_native_hashes.py"]), quiet():
            (root / generate_native_hashes.UNVERIFIED_MARKER).write_text("skipped")
            self.assertEqual(generate_native_hashes.main(), 1)
            self.assertFalse(output.exists())

            (root / generate_native_hashes.UNVERIFIED_MARKER).unlink()
            self.assertEqual(generate_native_hashes.main(), 0)
            self.assertTrue(output.exists())

    def test_release_manifest_refuses_unscanned_payload(self):
        payload = self.tmp / "Plugins"
        root = payload / "x86_64" / "Linux"
        root.mkdir(parents=True)
        (root / "libexample.so").write_bytes(b"\x7fELF not really")
        (root / generate_native_hashes.UNSCANNED_MARKER).write_text("unscanned")
        output = self.tmp / "NativeRuntimeHashes.g.cs"
        with mock.patch.object(generate_native_hashes, "PAYLOAD", payload), \
                mock.patch.object(generate_native_hashes, "OUTPUT", output), \
                mock.patch.object(generate_native_hashes, "REPO", self.tmp), quiet():
            with mock.patch.object(sys, "argv", ["generate_native_hashes.py", "--release"]):
                self.assertEqual(generate_native_hashes.main(), 1)
            self.assertFalse(output.exists())

            # Hashed with a warning outside a release, so local builds and CI keep working.
            with mock.patch.object(sys, "argv", ["generate_native_hashes.py"]):
                self.assertEqual(generate_native_hashes.main(), 0)
            self.assertTrue(output.exists())


def scan_record_text(component="OpenUSD", version="0.0", commit="a" * 40, patches="none",
                     counts=None, kept_open="None.", conclusion="Acceptable to compile; decided by test."):
    counts = counts or {"Critical": (0, 0), "High": (0, 0), "Medium": (0, 0), "Low": (0, 0)}
    result = "\n".join(f"| {name} | {found} | {kept} |" for name, (found, kept) in counts.items())
    return textwrap.dedent("""\
        # {component} {version} -- source scan

        | | |
        | --- | --- |
        | Component | {component} |
        | Version | {version} |
        | Commit scanned | {commit} |
        | Source | https://example.invalid/{component}.git |
        | Scanned by | test |
        | Date | 2026-10-01 |
        | Tool | Cycode CLI 0.0, SAST |
        | Patches covered | {patches} |

        ## Command

        ```bash
        cycode scan -t sast repository /src
        ```

        ## Result

        | Severity | Count | Kept open |
        | --- | --- | --- |
        {result}

        ## Findings kept open

        {kept_open}

        ## Coverage gaps

        None.

        ## Conclusion

        {conclusion}
        """).format(component=component, version=version, commit=commit, patches=patches,
                    result=result.replace("\n", "\n        "), kept_open=kept_open,
                    conclusion=conclusion)


class ScanRecordTests(TempDirTest):
    def setUp(self):
        super().setUp()
        self.rows = rows_for("a" * 40, "https://example.invalid/OpenUSD.git", "b" * 64, "d" * 64)
        self.openusd = self.rows[0]

    def problems(self, text):
        return sources.scan_record_problems(self.openusd, self.rows, text)

    def test_complete_record_passes(self):
        self.assertEqual(self.problems(scan_record_text(patches="d" * 64)), [])

    def test_empty_record_fails(self):
        self.assertTrue(self.problems(""))

    def test_unfilled_template_fails(self):
        template = (NATIVE / "security-scans" / "_TEMPLATE.md").read_text(encoding="utf-8")
        self.assertTrue(self.problems(template))

    def test_other_commit_fails(self):
        self.assertTrue(self.problems(scan_record_text(commit="c" * 40, patches="d" * 64)))

    def test_abbreviated_commit_fails(self):
        self.assertTrue(self.problems(scan_record_text(commit="a" * 12, patches="d" * 64)))

    def test_other_version_fails(self):
        self.assertTrue(self.problems(scan_record_text(version="9.9", patches="d" * 64)))

    def test_unpatched_tree_fails(self):
        self.assertTrue(self.problems(scan_record_text()))

    def test_missing_count_fails(self):
        counts = {"Critical": (0, 0), "High": ("", ""), "Medium": (0, 0), "Low": (0, 0)}
        self.assertTrue(self.problems(scan_record_text(patches="d" * 64, counts=counts)))

    def test_high_finding_kept_open_fails(self):
        counts = {"Critical": (0, 0), "High": (1, 1), "Medium": (0, 0), "Low": (0, 0)}
        self.assertTrue(self.problems(scan_record_text(patches="d" * 64, counts=counts,
                                                       kept_open="rule X in a file not compiled")))

    def test_medium_finding_kept_open_needs_a_reason(self):
        counts = {"Critical": (0, 0), "High": (0, 0), "Medium": (2, 1), "Low": (0, 0)}
        self.assertTrue(self.problems(scan_record_text(patches="d" * 64, counts=counts)))
        self.assertEqual(self.problems(scan_record_text(
            patches="d" * 64, counts=counts,
            kept_open="rule X, pxr/imaging/foo.cpp:12: not compiled under --no-imaging.")), [])

    def test_empty_conclusion_fails(self):
        self.assertTrue(self.problems(scan_record_text(patches="d" * 64, conclusion="<decision>")))

    def test_verify_scan_reads_the_file(self):
        with mock.patch.object(sources, "SCAN_DIR", self.tmp), \
                mock.patch.object(sources, "REPO", self.tmp), quiet():
            (self.tmp / "openusd-0.0.md").write_text("")
            (self.tmp / "onetbb-0.0.md").write_text(
                scan_record_text(component="oneTBB", commit="0" * 40))
            self.assertFalse(sources.verify_scan(self.rows))
            (self.tmp / "openusd-0.0.md").write_text(scan_record_text(patches="d" * 64))
            self.assertTrue(sources.verify_scan(self.rows))


FAKE_BUILD_USD = textwrap.dedent('''\
    """Stand-in for build_usd.py. The real one would download from TBB_URL = "{url}"."""
    import os, pathlib, sys
    args = sys.argv[1:]
    install = pathlib.Path(args[-1])
    src = pathlib.Path(args[args.index("--src") + 1])
    assert os.environ["https_proxy"] == "{proxy}", "downloads were not blocked"
    assert (src / "{archive}").is_file(), "the archive was not fetched first"
    (src / "extracted").mkdir(exist_ok=True)
    for relative in {files!r}:
        path = install / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("built " + relative)
    if os.environ.get("FAKE_BUILD_USD_EXTRA_DOWNLOAD"):
        (src / "boost_1_0_0.zip").write_text("fetched behind our back")
    if os.environ.get("FAKE_BUILD_USD_TOUCH_TREE"):
        (pathlib.Path.cwd() / "pxr.txt").write_text("changed by the build")
    ''')


class BuildOpenUsdTests(TempDirTest):
    def setUp(self):
        super().setUp()
        self.clone = self.tmp / "OpenUSD"
        (self.clone / "build_scripts").mkdir(parents=True)
        (self.clone / "pxr.txt").write_text("original\n")
        (self.clone / "build_scripts" / "build_usd.py").write_text(FAKE_BUILD_USD.format(
            url=TBB_URL, proxy=build_openusd.UNREACHABLE_PROXY,
            archive=TBB_URL.rsplit("/", 1)[-1], files=INSTALL_FILES))
        remote = "https://example.invalid/OpenUSD.git"
        git = ["git", "-C", str(self.clone), "-c", "user.name=t", "-c", "user.email=t@example.invalid"]
        subprocess.run(["git", "init", "-q", str(self.clone)], check=True)
        subprocess.run(git + ["add", "-A"], check=True)
        subprocess.run(git + ["commit", "-q", "-m", "fake"], check=True)
        subprocess.run(git + ["remote", "add", "origin", remote], check=True)
        commit = subprocess.run(git + ["rev-parse", "HEAD"], check=True, capture_output=True,
                                text=True).stdout.strip()

        self.archive = self.tmp / "downloads" / TBB_URL.rsplit("/", 1)[-1]
        self.archive.parent.mkdir()
        self.archive.write_bytes(b"pinned archive bytes")
        self.rows = rows_for(commit, remote, sha256(b"pinned archive bytes"))
        self.install = self.tmp / "install"

    def run_main(self, *extra) -> int:
        argv = ["build_openusd.py", "--openusd-src", str(self.clone), "--install", str(self.install),
                "--archive", str(self.archive), *extra]
        with mock.patch.object(sys, "argv", argv), \
                mock.patch.object(sources, "load", return_value=self.rows), \
                mock.patch.object(build_openusd, "is_under", return_value=False), quiet():
            return build_openusd.main()

    def stamped(self) -> bool:
        with quiet():
            return sources.check_stamp(self.install, PLATFORM, self.rows)

    def test_builds_and_stamps(self):
        self.assertEqual(self.run_main(), 0)
        self.assertTrue(self.stamped())
        self.assertFalse((self.clone / "build_scripts" / "__pycache__").exists())

    def test_tampered_archive_stops_before_build(self):
        self.archive.write_bytes(b"not the pinned bytes")
        self.assertEqual(self.run_main(), 1)
        self.assertFalse(any((self.install / f).exists() for f in INSTALL_FILES))
        self.assertFalse((self.install / sources.STAMP_NAME).exists())

    def test_non_empty_install_is_refused(self):
        (self.install / "src" / "extracted").mkdir(parents=True)
        self.assertEqual(self.run_main(), 1)
        self.assertFalse((self.install / sources.STAMP_NAME).exists())

    def test_modified_clone_is_refused(self):
        (self.clone / "local-edit.txt").write_text("surprise")
        self.assertEqual(self.run_main(), 1)
        self.assertFalse(self.install.exists())

    def test_record_url_must_be_what_build_usd_fetches(self):
        self.rows[1] = sources.Row(dict(self.rows[1], url="https://example.invalid/other.zip"))
        self.assertEqual(self.run_main(), 1)

    def test_unexpected_download_is_not_stamped(self):
        with mock.patch.dict(os.environ, {"FAKE_BUILD_USD_EXTRA_DOWNLOAD": "1"}):
            self.assertEqual(self.run_main(), 1)
        self.assertFalse((self.install / sources.STAMP_NAME).exists())

    def add_patch(self, body: str) -> None:
        """Pins a patch that edits pxr.txt in the fake clone; the fake repo lives in self.tmp."""
        patches = self.tmp / "Native~" / "patches"
        patches.mkdir(parents=True, exist_ok=True)
        # Bytes, not text: write_text translates "\n" to CRLF on Windows, and the digest pinned
        # below is of the LF body -- as a checked-out patch is, with text conversion unset.
        (patches / "test.patch").write_bytes(body.encode())
        self.rows = rows_for(self.rows[0].git_commit, self.rows[0].url, self.rows[1].sha256,
                             sha256(body.encode()))

    PATCH = textwrap.dedent('''\
        A patch file may start with prose; git apply skips to the first diff.

        diff --git a/pxr.txt b/pxr.txt
        --- a/pxr.txt
        +++ b/pxr.txt
        @@ -1 +1 @@
        -original
        +patched
        ''')

    def run_patched(self, *extra) -> int:
        with mock.patch.object(sources, "REPO", self.tmp), \
                mock.patch.object(sources, "RECORD_DIR", self.tmp / "Native~" / "dependency-sources"):
            return self.run_main(*extra)

    def test_patch_is_applied_to_a_worktree_not_the_clone(self):
        self.add_patch(self.PATCH)
        self.assertEqual(self.run_patched(), 0)
        self.assertEqual((self.install / "openusd-src" / "pxr.txt").read_text(), "patched\n")
        self.assertEqual((self.clone / "pxr.txt").read_text(), "original\n")
        with mock.patch.object(sources, "REPO", self.tmp):
            self.assertTrue(self.stamped())

    def test_patch_applies_when_the_clone_converts_line_endings(self):
        # Git for Windows defaults to core.autocrlf=true, which checks sources out with CRLF; an LF
        # patch would then fail to apply. autocrlf=true converts on every platform, so this is the
        # Windows case reproduced here.
        subprocess.run(["git", "-C", str(self.clone), "config", "core.autocrlf", "true"], check=True)
        self.add_patch(self.PATCH)
        self.assertEqual(self.run_patched(), 0)
        self.assertEqual((self.install / "openusd-src" / "pxr.txt").read_bytes(), b"patched\n")

    def test_tampered_patch_stops_before_build(self):
        self.add_patch(self.PATCH)
        (self.tmp / "Native~" / "patches" / "test.patch").write_bytes(
            self.PATCH.replace("patched", "evil").encode())
        self.assertEqual(self.run_patched(), 1)
        self.assertFalse(self.install.exists())

    def test_patch_that_does_not_apply_stops_before_build(self):
        self.add_patch(self.PATCH.replace("-original", "-something else"))
        self.assertEqual(self.run_patched(), 1)
        self.assertFalse(any((self.install / f).exists() for f in INSTALL_FILES))

    def test_tree_changed_by_build_is_not_stamped(self):
        self.add_patch(self.PATCH)
        with mock.patch.dict(os.environ, {"FAKE_BUILD_USD_TOUCH_TREE": "1"}):
            self.assertEqual(self.run_patched(), 1)
        self.assertFalse((self.install / sources.STAMP_NAME).exists())

    def test_unpinned_archive_is_refused(self):
        stray = self.tmp / "downloads" / "something-else.zip"
        stray.write_bytes(b"x")
        argv_archive, self.archive = self.archive, stray
        try:
            self.assertEqual(self.run_main(), 1)
        finally:
            self.archive = argv_archive


if __name__ == "__main__":
    unittest.main()
