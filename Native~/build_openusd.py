#!/usr/bin/env python3
"""Builds the OpenUSD install a native payload links against, from pinned source checked first.

Pixar's `build_usd.py` downloads what it needs and compiles it in one go, and passes an
`expectedSHA256` for Boost and nothing else. Under this package's flags the only thing it downloads
is oneTBB, so run on its own it unpacks, compiles and links TBB before anything has looked at the
archive -- a check afterwards only reports what has already run (SECURITY-282834, CWE-494). This
script puts the check first:

    python3 Native~/build_openusd.py --platform linux \
        --openusd-src /opt/usd-26.05/src --install /opt/usd-26.05/install --require-scan

1. The clone is verified against the commit pinned in Native~/dependency-sources/<platform>.tsv,
   and every Unity patch the record pins (Native~/patches) against its SHA-256. With patches,
   a worktree of the pinned commit is created inside the install and they are applied there;
   the clone itself is never modified, so "unmodified clone" keeps meaning exactly that.
2. Every archive that record pins is downloaded (or taken from --archive) into the directory
   `build_usd.py` downloads into, and checked against its recorded SHA-256. A mismatch stops here.
3. `build_usd.py` runs with the flags recorded in Native~/generate_sbom.py. It finds each archive
   already present and skips the download -- that is upstream behaviour this relies on, and step 4
   confirms it held. Its downloads are pointed at an unreachable proxy, so if it tries to fetch
   anything else the build fails instead of compiling something unchecked.
4. Afterwards the archives are re-checked, the download directory must hold nothing else, the
   clone must still be unmodified, and the patched worktree must still be the pinned commit with
   exactly the pinned patches applied.
5. Only then is the provenance stamp written into the install. It records that the archives were
   checked before the build, which patches were applied, and the SHA-256 of every file a wrapper
   build copies out of the install. `verify_upstream_sources.py --check-stamp` -- called by the build scripts and by
   Native~/CMakeLists.txt -- requires both, and re-hashes the files.

The install must be new or empty. `build_usd.py` skips extracting an archive whose directory already
exists and skips building a dependency that is already installed, so a leftover tree would be used
without the archive it came from ever being looked at.

Build the install at a path with no user name in it: OpenUSD embeds its source and install paths
in the library, and the payload ships that library as-is. On macOS and Linux this script refuses a
path under the home directory or /tmp; the shipped payloads use `/Users/Shared/usd-26.05` and
`/opt/usd-26.05`.
"""

import argparse
import hashlib
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import urllib.request

import verify_upstream_sources as sources
from generate_sbom import USD_BUILD_FLAGS

# Far above any archive a record pins (the TBB archives are 2-20 MB), low enough that a hostile
# server cannot fill the disk.
MAX_ARCHIVE_BYTES = 256 * 1024 * 1024

# Nothing listens on the discard port of the loopback interface, so any download build_usd.py
# attempts through curl or urllib fails at once. On Windows without curl, build_usd.py falls back to
# PowerShell, which ignores these variables; the check on the download directory afterwards still
# catches an extra download there, just after the fact.
UNREACHABLE_PROXY = "http://127.0.0.1:9"
PROXY_VARIABLES = ("http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY")


def fail(message: str) -> int:
    print(f"FAIL {message}", file=sys.stderr)
    return 1


def host_platform() -> str:
    if sys.platform == "darwin":
        return "macos"
    if sys.platform == "win32":
        return "windows"
    return "linux"


def archive_name(row) -> str:
    # The name build_usd.py gives a download: the last component of its URL.
    return row.url.rsplit("/", 1)[-1]


def is_under(path: pathlib.Path, parent: pathlib.Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


# Every git call on the patched worktree runs with line-ending conversion off. OpenUSD's
# .gitattributes leaves C++ sources to core.autocrlf, which Git for Windows sets to true by
# default: the worktree would then be checked out with CRLF and a patch written with LF would not
# apply. LF sources compile the same everywhere, and the patch's digest stays the one pinned.
TREE_GIT = ("-c", "core.autocrlf=false", "-c", "core.eol=lf")


def tree_git(tree: pathlib.Path, *args: str, **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(["git", *TREE_GIT, "-C", str(tree), *args], **kwargs)


def tree_state(tree: pathlib.Path):
    """(HEAD, digest of the working-tree diff, untracked paths) -- what a patched tree must keep."""
    def out(*args):
        return tree_git(tree, *args, capture_output=True, check=True).stdout
    head = out("rev-parse", "HEAD").decode().strip()
    diff = out("diff", "--no-color", "--binary", "HEAD")
    untracked = out("ls-files", "--others", "--exclude-standard").decode().strip()
    return head, hashlib.sha256(diff).hexdigest(), untracked


def make_patched_tree(clone: pathlib.Path, tree: pathlib.Path, commit: str, patches) -> tuple:
    """A worktree of the pinned commit with the pinned patches applied, the clone left untouched.

    Returns the tree's state right after patching; the build must leave it exactly so.
    """
    print(f"     worktree of {commit[:12]} at {tree}")
    tree_git(clone, "worktree", "add", "--quiet", "--detach", str(tree), commit,
             check=True, stdout=subprocess.DEVNULL)
    for row in patches:
        path = sources.patch_path(row)
        print(f"     applying {row.url}")
        # --check first so a patch that does not apply cleanly leaves the tree untouched.
        for extra in (["--check"], []):
            result = tree_git(tree, "apply", *extra, str(path))
            if result.returncode != 0:
                raise RuntimeError(f"{row.url} does not apply to {commit[:12]}")
    state = tree_state(tree)
    if state[2]:
        raise RuntimeError(f"applying the patches left untracked files: {state[2]}")
    return state


def fetch(row, destination: pathlib.Path, local: pathlib.Path = None) -> bool:
    """Puts the archive at `destination` only if its bytes match the record."""
    handle, temporary = tempfile.mkstemp(prefix=f"{destination.name}.", suffix=".part",
                                         dir=destination.parent)
    temporary = pathlib.Path(temporary)
    try:
        with os.fdopen(handle, "wb") as out:
            if local:
                print(f"     {row.label}: using {local}")
                with local.open("rb") as source:
                    shutil.copyfileobj(source, out)
            else:
                print(f"     {row.label}: downloading {row.url}")
                request = urllib.request.Request(
                    row.url, headers={"User-Agent": "com.unity.usd-toolkit build_openusd.py"})
                with urllib.request.urlopen(request, timeout=120) as response:
                    if not response.geturl().startswith("https://"):
                        print(f"FAIL {row.label}: redirected off HTTPS to {response.geturl()}",
                              file=sys.stderr)
                        return False
                    received = 0
                    for chunk in iter(lambda: response.read(1024 * 1024), b""):
                        received += len(chunk)
                        if received > MAX_ARCHIVE_BYTES:
                            print(f"FAIL {row.label}: more than {MAX_ARCHIVE_BYTES} bytes; not the "
                                  "recorded archive.", file=sys.stderr)
                            return False
                        out.write(chunk)
        if not sources.verify_archive(row, temporary):
            return False
        os.replace(temporary, destination)
        return True
    except OSError as exc:  # URLError, TLS failures and timeouts are all OSErrors
        print(f"FAIL {row.label}: {exc}", file=sys.stderr)
        print("     Download it another way and pass it with --archive; it is checked the same.",
              file=sys.stderr)
        return False
    finally:
        if temporary.exists():
            temporary.unlink()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--platform", choices=("macos", "linux", "windows"), default=host_platform(),
                        help="Defaults to this machine's. Nothing here cross-compiles.")
    parser.add_argument("--openusd-src", type=pathlib.Path, required=True,
                        help="OpenUSD git clone at the pinned commit.")
    parser.add_argument("--install", type=pathlib.Path, required=True,
                        help="OpenUSD install root to create. Must be new or empty.")
    parser.add_argument("--archive", type=pathlib.Path, action="append", default=[],
                        help="A pinned archive already on disk, used instead of downloading it. "
                             "Checked against the record the same way. Repeatable.")
    parser.add_argument("--require-scan", action="store_true",
                        help="Require a Native~/security-scans record for every pinned version. "
                             "Needed for a payload that will be committed.")
    parser.add_argument("--jobs", "-j", type=int, default=os.cpu_count() or 4)
    parser.add_argument("--build-target", choices=("native", "x86_64", "arm64", "universal"),
                        default="universal", help="macOS only. Defaults to universal.")
    parser.add_argument("--deployment-target", default="12.0",
                        help="macOS only: MACOSX_DEPLOYMENT_TARGET for OpenUSD and TBB. Defaults to "
                             "12.0, the value Native~/build_macos.sh builds the wrapper with.")
    args = parser.parse_args()

    platform = args.platform
    if platform != host_platform():
        return fail(f"--platform {platform} on a {host_platform()} machine; build on the target platform.")

    rows = sources.load(platform)
    git_rows = sources.git_rows(rows)
    archives = sources.archive_rows(rows)
    patches = sources.patch_rows(rows)
    src = args.openusd_src.resolve()
    install = args.install.resolve()
    downloads = install / "src"

    # --- before anything is built ---------------------------------------------------------
    # __FILE__ and __PRETTY_FUNCTION__ bake the path of every compiled source into libusd_ms, which
    # ships as-is, so a tree under a home directory ships the user name (1134 strings on macOS).
    # With patches the compiled tree is the worktree inside the install, so the install is what
    # counts; without, the clone is compiled in place and counts too. MSVC records __FILE__
    # relative, so Windows is exempt.
    if platform in ("linux", "macos"):
        home = pathlib.Path.home().resolve()
        temp = pathlib.Path("/tmp").resolve()
        compiled = [("--install", install)] + ([] if patches else [("--openusd-src", src)])
        suggestion = "/opt/usd-26.05" if platform == "linux" else "/Users/Shared/usd-26.05"
        for label, path in compiled:
            if is_under(path, home) or is_under(path, temp):
                return fail(f"{label} {path} is under the home directory or /tmp. OpenUSD embeds this "
                            f"path in libusd_ms, which ships as-is; use e.g. {suggestion}.")

    # build_usd.py is imported by Python as it runs, which writes build_scripts/__pycache__ into the
    # clone unless told not to -- and an untracked file there fails the clean-clone check below.
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    ok = all([sources.verify_git(row, src) for row in git_rows])
    ok &= all([sources.verify_patch(row) for row in patches])
    if args.require_scan:
        ok &= sources.verify_scan(rows)
    if not ok:
        return fail("the source does not match the record; nothing was built.")
    if patches and len(git_rows) != 1:
        return fail("a patch needs exactly one pinned git source to apply to.")
    for row in patches:
        if row.git_commit != git_rows[0].git_commit:
            return fail(f"{row.label} applies to {row.git_commit[:12]}, but the record pins "
                        f"{git_rows[0].git_commit[:12]}.")

    # With patches, build_usd.py builds a worktree of the pinned commit with them applied, so the
    # clone itself is never modified and the clean-clone check means what it says.
    tree = install / "openusd-src" if patches else src

    build_usd = src / "build_scripts" / "build_usd.py"
    if not build_usd.is_file():
        return fail(f"{build_usd} is missing.")
    build_usd_text = build_usd.read_text(encoding="utf-8")
    for row in archives:
        # If the pinned clone would fetch a different file than the record pins, the pre-fetched
        # archive would sit unused while build_usd.py downloads its own.
        if f'"{row.url}"' not in build_usd_text:
            return fail(f"{row.label}: build_usd.py at the pinned commit does not download {row.url}. "
                        "The record and the clone disagree; fix the record.")

    if install.exists() and any(install.iterdir()):
        return fail(f"{install} is not empty. build_usd.py would reuse whatever it finds there -- an "
                    "extracted archive, an installed dependency -- without checking it. Remove it or "
                    "pick a new directory.")
    downloads.mkdir(parents=True, exist_ok=True)

    patched_state = None
    if patches:
        print(f"[0/3] Applying {len(patches)} pinned patch(es) to a worktree of the pinned commit")
        try:
            patched_state = make_patched_tree(src, tree, git_rows[0].git_commit, patches)
        except (RuntimeError, subprocess.CalledProcessError) as exc:
            return fail(f"{exc}; nothing was built.")
        build_usd = tree / "build_scripts" / "build_usd.py"

    local = {}
    for path in args.archive:
        if not path.is_file():
            return fail(f"--archive {path} is not a file.")
        local[path.name] = path.resolve()
    for name in local:
        if name not in {archive_name(r) for r in archives}:
            return fail(f"--archive {name} is not an archive the {platform} record pins.")

    print(f"[1/3] Checking {len(archives)} pinned archive(s) before build_usd.py runs")
    for row in archives:
        if not fetch(row, downloads / archive_name(row), local.get(archive_name(row))):
            return fail(f"{row.label}: not the recorded archive; nothing was built.")

    # --- build ----------------------------------------------------------------------------
    command = [sys.executable, str(build_usd)] + USD_BUILD_FLAGS.split() + [
        "--src", str(downloads), "--jobs", str(args.jobs)]
    env = dict(os.environ)
    for name in PROXY_VARIABLES:
        env[name] = UNREACHABLE_PROXY
    env.pop("no_proxy", None)
    env.pop("NO_PROXY", None)
    if platform == "macos":
        command += ["--build-target", args.build_target]
        env["MACOSX_DEPLOYMENT_TARGET"] = args.deployment_target
    command.append(str(install))

    print("[2/3] Running build_usd.py with downloads blocked")
    print("     " + " ".join(command))
    if subprocess.run(command, env=env, cwd=tree).returncode != 0:
        return fail("build_usd.py failed; the install was not stamped.")

    # --- after ----------------------------------------------------------------------------
    print("[3/3] Checking what the build used")
    ok = all([sources.verify_archive(row, downloads / archive_name(row)) for row in archives])
    expected = {archive_name(r) for r in archives}
    unexpected = sorted(p.name for p in downloads.iterdir() if p.is_file() and p.name not in expected)
    if unexpected:
        print(f"FAIL {downloads} holds files no record pins: {', '.join(unexpected)}", file=sys.stderr)
        print("     build_usd.py fetched something unchecked, or an archive failed to extract.",
              file=sys.stderr)
        ok = False
    ok &= all([sources.verify_git(row, src) for row in git_rows])
    if patched_state and tree_state(tree) != patched_state:
        print(f"FAIL {tree} changed during the build: it is no longer the pinned commit with exactly",
              file=sys.stderr)
        print("     the pinned patches applied.", file=sys.stderr)
        ok = False
    if not ok:
        return fail("the build did not use only the recorded source; the install was not stamped.")

    if platform == "macos":
        # Some build_usd.py versions leave the monolithic dylib in the build tree only.
        dylib = install / "lib" / "libusd_ms.dylib"
        built = install / "build" / tree.name / "libusd_ms.dylib"
        if not dylib.is_file() and built.is_file():
            shutil.copy2(built, dylib)

    sources.write_stamp(install, platform, rows, args.require_scan, archives, patches)
    print()
    print(f"Built and stamped {install}. Build the wrapper against it, for example:")
    script = {"macos": "Native~/build_macos.sh --openusd-root",
              "linux": "Native~/build_linux.sh --openusd-root",
              "windows": r".\Native~\build_windows.ps1 -OpenUsdRoot"}[platform]
    print(f"    {script} {install}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
