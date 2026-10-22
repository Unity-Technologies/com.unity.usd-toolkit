#!/usr/bin/env python3
"""Builds the OpenUSD install a native payload links against, from pinned source checked first.

Pixar's `build_usd.py` downloads what it needs and compiles it in one go, and passes an
`expectedSHA256` for Boost and nothing else. Under this package's flags it downloads oneTBB,
MaterialX, OpenEXR, Alembic and Draco (and zlib on Windows), so run on its own it unpacks, compiles
and links each of them before anything has looked at the archive -- a check afterwards only reports
what has already run (CWE-494). This script puts the check first:

    python3 Native~/build_openusd.py --platform linux \
        --openusd-src /opt/usd-26.05/src --install /opt/usd-26.05/install --require-scan

1. The clone is verified against the commit pinned in Native~/dependency-sources/<platform>.tsv,
   and every Unity patch the record pins (Native~/patches) against its SHA-256. With patches,
   a worktree of the pinned commit is created inside the install and they are applied there;
   the clone itself is never modified, so "unmodified clone" keeps meaning exactly that.
2. Every archive that record pins is downloaded (or taken from --archive) into the directory
   `build_usd.py` downloads into, and checked against its recorded SHA-256. A mismatch stops here.
   A fetchcontent-archive row -- source a dependency's own CMake would fetch, Imath for OpenEXR --
   is then extracted here, member names checked, and that build is pointed at the extracted tree
   (FETCHCONTENT_SOURCE_DIR_<NAME>) while its flags keep FetchContent disconnected. An archive an
   archive-patch row applies to (Alembic) is extracted here too, where build_usd.py would extract
   it, and its pinned patches applied; build_usd.py finds the directory and builds that tree.
3. `build_usd.py` runs with the flags recorded in Native~/generate_sbom.py. It finds each archive
   already present and skips the download -- that is upstream behaviour this relies on, and step 4
   confirms it held. Its downloads are pointed at an unreachable proxy, so if it tries to fetch
   anything else the build fails instead of compiling something unchecked.
4. Afterwards the archives are re-checked, the download directory must hold nothing else, the
   clone must still be unmodified, the patched worktree must still be the pinned commit with
   exactly the pinned patches applied, and every tree extracted here must be as extracted (and
   patched).
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
import stat
import sys
import tempfile
import urllib.request
import zipfile

import verify_upstream_sources as sources
from generate_sbom import usd_build_flags

# Far above any archive a record pins (the TBB archives are 2-20 MB), low enough that a hostile
# server cannot fill the disk.
MAX_ARCHIVE_BYTES = 256 * 1024 * 1024

# Nothing listens on the discard port of the loopback interface, so any download build_usd.py
# attempts through curl or urllib fails at once. On Windows without curl, build_usd.py falls back to
# PowerShell, which ignores these variables; the check on the download directory afterwards still
# catches an extra download there, just after the fact.
UNREACHABLE_PROXY = "http://127.0.0.1:9"

# For each component a fetchcontent-archive row may pin: the build_usd.py dependency whose CMake
# fetches it, and the name FetchContent declares it under (FETCHCONTENT_SOURCE_DIR_<NAME>).
FETCHCONTENT_TARGETS = {"Imath": ("OpenEXR", "IMATH")}
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


def extract_source(row, archive: pathlib.Path, downloads: pathlib.Path,
                   keep_name: bool = False) -> pathlib.Path:
    """Extracts a checked archive and returns its one top-level directory.

    A FetchContent source goes to <Component>-<version>-src; with `keep_name`, the directory keeps
    the archive's own name, which is where build_usd.py extracts it and looks for it. The digest
    says the bytes are the pinned ones; the member names are checked anyway, so an archive cannot
    write outside its directory whatever it contains.
    """
    destination = None if keep_name else downloads / f"{row.component}-{row.version}-src"
    if destination is not None and destination.exists():
        raise RuntimeError(f"{destination} already exists")
    staging = pathlib.Path(tempfile.mkdtemp(prefix=f".{row.component}.", dir=downloads))
    try:
        with zipfile.ZipFile(archive) as bundle:
            for member in bundle.infolist():
                name = pathlib.PurePosixPath(member.filename)
                mode = member.external_attr >> 16
                if name.is_absolute() or ".." in name.parts or "\\" in member.filename:
                    raise RuntimeError(f"{row.label}: archive member {member.filename!r} leaves its directory")
                if stat.S_ISLNK(mode):
                    raise RuntimeError(f"{row.label}: archive member {member.filename!r} is a link")
            bundle.extractall(staging)
        tops = [p for p in staging.iterdir()]
        if len(tops) != 1 or not tops[0].is_dir():
            raise RuntimeError(f"{row.label}: the archive does not hold exactly one directory")
        if destination is None:
            destination = downloads / tops[0].name
            if destination.exists():
                raise RuntimeError(f"{destination} already exists")
        tops[0].rename(destination)
    finally:
        shutil.rmtree(staging, ignore_errors=True)
    print(f"     {row.label}: extracted to {destination}")
    return destination


def apply_archive_patches(tree: pathlib.Path, patches) -> None:
    """Applies pinned patches to an extracted archive tree, each checked to apply cleanly first.

    The tree is not a repository, and git must not take it for part of one further up (an install
    inside a checkout would have git apply the patch relative to that checkout instead), so
    repository discovery stops at the tree.
    """
    env = dict(os.environ, GIT_CEILING_DIRECTORIES=str(tree.parent))
    for row in patches:
        path = sources.patch_path(row)
        base = ["git", "-c", "core.autocrlf=false", "apply", "--whitespace=nowarn"]
        for command in (base + ["--check", str(path)], base + [str(path)]):
            result = subprocess.run(command, cwd=tree, env=env, capture_output=True, text=True)
            if result.returncode != 0:
                raise RuntimeError(f"{row.label}: {path.name} does not apply to {tree.name}: "
                                   f"{result.stderr.strip()}")
        print(f"     {row.label}: applied to {tree.name}")


def tree_digest(root: pathlib.Path) -> str:
    """SHA-256 over every file's relative path and contents, to tell whether a tree was changed."""
    digest = hashlib.sha256()
    for path in sorted(p for p in root.rglob("*") if p.is_file()):
        digest.update(path.relative_to(root).as_posix().encode() + b"\0")
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def cmake_major() -> int:
    """The major version of the cmake on PATH, which build_usd.py runs; 0 when there is none."""
    cmake = shutil.which("cmake")
    if not cmake:
        return 0
    out = subprocess.run([cmake, "--version"], capture_output=True, text=True).stdout
    try:
        return int(out.split()[2].split(".")[0])
    except (IndexError, ValueError):
        return 0


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
    archive_patches = sources.archive_patch_rows(rows)
    fetched = sources.fetchcontent_rows(rows)
    flags = usd_build_flags(platform)
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
    ok &= all([sources.verify_patch(row) for row in patches + archive_patches])
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
    # Each archive patch names its archive by that row's commit, which has to be one archive's.
    patched_archives = []
    for row in archive_patches:
        targets = [a for a in archives if a.git_commit == row.git_commit]
        if len(targets) != 1:
            return fail(f"{row.label} applies to {row.git_commit[:12]}, which is not the commit of "
                        "exactly one pinned archive.")
        for entry in patched_archives:
            if entry[0] is targets[0]:
                entry[1].append(row)
                break
        else:
            patched_archives.append((targets[0], [row]))

    # With patches, build_usd.py builds a worktree of the pinned commit with them applied, so the
    # clone itself is never modified and the clean-clone check means what it says.
    tree = install / "openusd-src" if patches else src

    build_usd = src / "build_scripts" / "build_usd.py"
    if not build_usd.is_file():
        return fail(f"{build_usd} is missing.")
    build_usd_text = build_usd.read_text(encoding="utf-8")
    for row in fetched:
        if row.component not in FETCHCONTENT_TARGETS:
            return fail(f"{row.label}: no build is known to fetch {row.component}; nothing would use it.")
    for row in archives:
        # If the pinned clone would fetch a different file than the record pins, the pre-fetched
        # archive would sit unused while build_usd.py downloads its own. A FetchContent source is
        # not build_usd.py's to download.
        if row in fetched:
            continue
        if f'"{row.url}"' not in build_usd_text:
            return fail(f"{row.label}: build_usd.py at the pinned commit does not download {row.url}. "
                        "The record and the clone disagree; fix the record.")

    # Alembic 1.8.5 sets policy CMP0042 to OLD, which CMake 4 no longer accepts: its configure step
    # fails outright. CMake 3.31 builds it, and the rest of the install, the same way.
    if "--alembic" in flags.split() and cmake_major() >= 4:
        return fail("the cmake on PATH is 4.x, which cannot configure Alembic 1.8.5 (policy CMP0042 OLD). "
                    "Put CMake 3.31 first on PATH for this build; nothing was built.")

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

    fetch_args = []
    extracted = {}
    fetched_trees = []
    for row in fetched:
        try:
            tree_root = extract_source(row, downloads / archive_name(row), downloads)
        except (RuntimeError, OSError, zipfile.BadZipFile) as exc:
            return fail(f"{exc}; nothing was built.")
        dependency, name = FETCHCONTENT_TARGETS[row.component]
        # Forward slashes: build_usd.py hands this to CMake unquoted, and CMake reads a Windows
        # backslash as an escape (C:\USD\x arrives as C:USDx).
        fetch_args.append(f"{dependency},-DFETCHCONTENT_SOURCE_DIR_{name}={tree_root.as_posix()}")
        extracted[tree_root] = tree_digest(tree_root)
        fetched_trees.append((row, tree_root))

    for row, row_patches in patched_archives:
        try:
            tree_root = next((t for r, t in fetched_trees if r is row), None)
            if tree_root is None:
                tree_root = extract_source(row, downloads / archive_name(row), downloads, keep_name=True)
            apply_archive_patches(tree_root, row_patches)
        except (RuntimeError, OSError, zipfile.BadZipFile) as exc:
            return fail(f"{exc}; nothing was built.")
        extracted[tree_root] = tree_digest(tree_root)

    # --- build ----------------------------------------------------------------------------
    # usd_build_flags() ends with its --build-args entries, so these join that list.
    command = [sys.executable, str(build_usd)] + flags.split() + fetch_args + [
        "--src", str(downloads), "--jobs", str(args.jobs)]
    env = dict(os.environ)
    # Windows environment names are case-insensitive. dict(os.environ) holds them upper-cased, so
    # adding the lower-case spellings yields two entries for one variable, and MSBuild then dies
    # configuring the first CMake dependency ("Item has already been added. Key in dictionary:
    # 'ALL_PROXY'  Key being added: 'all_proxy'"). One spelling per variable is all Windows needs.
    proxy_names = {n.upper() for n in PROXY_VARIABLES} if os.name == "nt" else PROXY_VARIABLES
    for name in proxy_names:
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
    for tree_root, digest in extracted.items():
        if tree_digest(tree_root) != digest:
            print(f"FAIL {tree_root} changed during the build; it is no longer the extracted (and "
                  "patched) archive.", file=sys.stderr)
            ok = False
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

    sources.write_stamp(install, platform, rows, args.require_scan, archives, patches + archive_patches)
    print()
    print(f"Built and stamped {install}. Build the wrapper against it, for example:")
    script = {"macos": "Native~/build_macos.sh --openusd-root",
              "linux": "Native~/build_linux.sh --openusd-root",
              "windows": r".\Native~\build_windows.ps1 -OpenUsdRoot"}[platform]
    print(f"    {script} {install}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
