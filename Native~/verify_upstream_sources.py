#!/usr/bin/env python3
"""Pins where the third-party source in the payload comes from, and gates a build on it.

`verify_dependency_digests.py` covers the step after this one: it records the digest of every
OpenUSD/TBB file a build copies off the build host into Runtime/Plugins. Its own docstring says
what it cannot do -- "prove the dependency tree is authentic upstream code" -- because by then the
source has already been fetched and compiled. This script covers that earlier link: it pins the
exact upstream revision each payload is built from and refuses a build that is not on it
(SECURITY-282834, the trust-chain half of the code-signing review).

Under the flags this package builds OpenUSD with

    --build-variant release --build-monolithic --no-python --no-imaging --no-usdview
    --no-examples --no-tutorials --no-tests --no-materialx

`build_usd.py` resolves exactly one downloaded dependency, TBB; everything else in the payload is
compiled from the OpenUSD tree itself, including the four libraries OpenUSD vendors in
(`pxr/base/tf/pxrCLI11`, `pxrDoubleConversion`, `pxrLZ4`, `pxrTslRobinMap`). So the whole payload
is pinned by three things per platform, and that is what the records in Native~/dependency-sources
hold: the OpenUSD commit, the TBB archive, and the Unity patch applied to that commit before it is
built (Native~/patches -- it replaces the vendored LZ4 1.9.2 with 1.10.0, SECURITY-282834).

    # the clone is the pinned revision, unmodified, from the recorded remote
    python3 Native~/verify_upstream_sources.py --platform linux --openusd-src /opt/usd-26.05/src

    # an archive is the recorded one
    python3 Native~/verify_upstream_sources.py --platform windows --archive ~/Downloads/tbb-2020.3-win.zip

    # what the wrapper build scripts and Native~/CMakeLists.txt call
    python3 Native~/verify_upstream_sources.py --platform macos --check-stamp <openusd-root>

    python3 Native~/verify_upstream_sources.py --platform windows --list

The stamp itself is written only by Native~/build_openusd.py, which is the one way to build the
OpenUSD install a payload links against. That script checks every recorded archive against its
pin *before* `build_usd.py` runs -- `build_usd.py` pins Boost and nothing else, so TBB would
otherwise be unpacked and compiled before anything looked at it -- and records the digest of every
file in the finished install. `--check-stamp` re-hashes those files, so a stamp says something
about the tree it sits in rather than only about the run that wrote it.

What this does NOT do: prove the pinned revision is free of malicious code. A pin makes the source
identical for everyone who checks it; it says nothing about what the source contains. That is the
job of the scan recorded under Native~/security-scans, which `--require-scan` gates on.

Nor does it establish an upstream *signature*, because none exists to establish. OpenUSD's `v26.05`
is a lightweight tag whose commit is unsigned, and oneTBB publishes no checksum for v2020.3. The
integrity evidence these pins rest on is therefore the immutable, content-addressed commit id of a
public repository -- reproducible by anyone, and observed by every other consumer of the same tag.
The one exception runs the other way: the Windows TBB archive is a release asset whose DLLs Intel
signed, and the TBB this package ships is byte-identical to the one inside it (renamed to
tbb_usdrt.dll, which changes no bytes).
"""

import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

import verify_dependency_digests

REPO = pathlib.Path(__file__).resolve().parent.parent
RECORD_DIR = REPO / "Native~" / "dependency-sources"
SCAN_DIR = REPO / "Native~" / "security-scans"
STAMP_NAME = ".unity-usd-toolkit-source-provenance.json"
# 1 recorded only which sources were checked. 2 added which archives were checked before
# build_usd.py ran and the digest of every file in the install. 3 adds which Unity patches were
# applied to the pinned tree before the build, and is the only one accepted.
STAMP_VERSION = 3
# A row whose artifact is this pins a Unity patch to the pinned OpenUSD tree: `url` is the patch's
# path in this repository, `sha256` its digest and `git_commit` the commit it applies to.
PATCH_ARTIFACT = "patch"
# What a Native~/security-scans record has to fill in (see its _TEMPLATE.md), and the severities
# its result table counts. A finding at a blocking severity cannot be kept open by the record.
SCAN_FIELDS = ("Component", "Version", "Commit scanned", "Source", "Scanned by", "Date", "Tool")
SEVERITIES = ("Critical", "High", "Medium", "Low")
BLOCKING_SEVERITIES = ("Critical", "High")

FIELDS = ("component", "version", "artifact", "url", "sha256", "git_commit", "evidence", "recorded")


class Row(dict):
    def __getattr__(self, name):
        try:
            return self[name]
        except KeyError as exc:  # pragma: no cover - programming error
            raise AttributeError(name) from exc

    @property
    def label(self):
        return f"{self.component} {self.version}"


def record_path(platform: str) -> pathlib.Path:
    return RECORD_DIR / f"{platform}.tsv"


def load(platform: str):
    path = record_path(platform)
    if not path.is_file():
        sys.exit(f"No source record for platform '{platform}': {path} is missing.")
    rows = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) != len(FIELDS):
            sys.exit(f"{path}:{number}: expected {len(FIELDS)} tab-separated fields, got {len(parts)}.")
        rows.append(Row(zip(FIELDS, (p.strip() for p in parts))))
    if not rows:
        sys.exit(f"{path} records no sources.")
    return rows


def sha256_of(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def git(src: pathlib.Path, *args: str) -> str:
    result = subprocess.run(("git", "-C", str(src)) + args,
                            capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout.strip()


def normalise_remote(url: str) -> str:
    return url.rstrip("/").removesuffix(".git").lower()


def verify_git(row: Row, src: pathlib.Path) -> bool:
    """The clone is the pinned commit, from the recorded remote, with nothing modified."""
    if not (src / ".git").exists():
        print(f"FAIL {row.label}: {src} is not a git clone, so its revision cannot be established.")
        print("     Download a tarball and the revision becomes a claim rather than a fact; clone instead.")
        return False

    ok = True
    try:
        head = git(src, "rev-parse", "HEAD")
        remote = git(src, "remote", "get-url", "origin")
        dirty = git(src, "status", "--porcelain")
    except RuntimeError as exc:
        print(f"FAIL {row.label}: {exc}")
        return False

    if head != row.git_commit:
        print(f"FAIL {row.label}: {src} is at {head[:12]}, the record pins {row.git_commit[:12]}.")
        ok = False
    else:
        print(f"ok   {row.label}: HEAD is the pinned commit {head[:12]}.")

    if normalise_remote(remote) != normalise_remote(row.url):
        print(f"FAIL {row.label}: origin is {remote}, the record pins {row.url}.")
        ok = False

    if dirty:
        count = len(dirty.splitlines())
        print(f"FAIL {row.label}: {count} modified or untracked path(s) in {src}; the pin describes")
        print("     unmodified upstream source, so a local edit has to be a reviewed change, not a surprise.")
        ok = False

    # A tag is mutable -- it can be moved to another commit upstream at any time -- so the commit is
    # what the record pins and the tag is only checked for agreement where it exists locally.
    tag = f"v{row.version}"
    try:
        tagged = git(src, "rev-parse", f"{tag}^{{commit}}")
    except RuntimeError:
        print(f"note {row.label}: tag {tag} is not present in this clone; the commit pin still holds.")
    else:
        if tagged != row.git_commit:
            print(f"FAIL {row.label}: tag {tag} resolves to {tagged[:12]} here, not {row.git_commit[:12]}.")
            print("     Upstream moved the tag, or this is a different repository. Investigate before building.")
            ok = False
    return ok


def verify_archive(row: Row, path: pathlib.Path) -> bool:
    if row.sha256 == "-":
        print(f"FAIL {row.label}: the record pins a commit, not an archive; verify the clone instead.")
        return False
    actual = sha256_of(path)
    if actual != row.sha256:
        print(f"FAIL {row.label}: {path.name} is {actual}")
        print(f"     the record pins   {row.sha256}")
        if row.artifact == "github-archive":
            print("     GitHub generates this archive on demand and has changed its compression before, so a")
            print("     mismatch is not proof of tampering. Confirm the contents against the pinned commit")
            print(f"     ({row.git_commit[:12]}) and re-record deliberately if they agree.")
        return False
    print(f"ok   {row.label}: {path.name} matches the recorded digest.")
    return True


def git_rows(rows):
    """The rows that pin a git revision."""
    return [r for r in rows if r.sha256 == "-"]


def patch_rows(rows):
    """The rows that pin a Unity patch applied to the pinned tree."""
    return [r for r in rows if r.artifact == PATCH_ARTIFACT]


def patch_path(row: Row) -> pathlib.Path:
    path = (REPO / row.url).resolve()
    if not path.is_relative_to(REPO / "Native~" / "patches"):
        sys.exit(f"{row.label}: a patch row must point into Native~/patches, not {row.url}.")
    return path


def verify_patch(row: Row) -> bool:
    """The patch file in this repository is the one the record pins."""
    path = patch_path(row)
    if not path.is_file():
        print(f"FAIL {row.label}: {row.url} is missing.")
        return False
    actual = sha256_of(path)
    if actual != row.sha256:
        print(f"FAIL {row.label}: {row.url} is {actual}")
        print(f"     the record pins   {row.sha256}")
        print("     A patch changes what is compiled; re-pin it deliberately, in review.")
        return False
    print(f"ok   {row.label}: {path.name} matches the recorded digest.")
    return True


def match_archive(rows, path: pathlib.Path, component):
    candidates = archive_rows(rows)
    if component:
        candidates = [r for r in candidates if r.component.lower() == component.lower()]
    if len(candidates) == 1:
        return candidates[0]
    by_name = [r for r in candidates if r.url.rsplit("/", 1)[-1] == path.name]
    if len(by_name) == 1:
        return by_name[0]
    sys.exit(f"Cannot tell which record {path.name} belongs to; pass --component.")


def scan_record(row: Row) -> pathlib.Path:
    return SCAN_DIR / f"{row.component.lower()}-{row.version}.md"


def table_cells(text: str):
    """Every Markdown table row in `text` as a list of stripped cells, separator rows left out."""
    rows = []
    for line in text.splitlines():
        line = line.strip()
        if len(line) > 1 and line.startswith("|") and line.endswith("|") and not set(line) <= set("|-: "):
            rows.append([cell.strip() for cell in line[1:-1].split("|")])
    return rows


def section(text: str, heading: str) -> str:
    match = re.search(rf"^## {re.escape(heading)}[ \t]*$(.*?)(?=^## |\Z)", text, re.M | re.S)
    return match.group(1).strip() if match else ""


def is_placeholder(value: str) -> bool:
    return not value or bool(re.fullmatch(r"<[^>]*>", value.strip()))


def scan_record_problems(row: Row, rows, text: str):
    """What stops this record from vouching for the pinned version; empty when nothing does.

    The record is read, not just found: an empty or half-filled template, a scan of another
    version or commit, or one that leaves a Critical or High finding open does not pass. Medium
    and Low findings may stay open, but only with the reason written down -- accepting a Critical
    or High one is a security exception for AppSec to grant, not a line in this file.
    """
    problems = []
    if "Delete this line" in text:
        problems.append("still carries the template's instructions")

    fields = {cells[0]: cells[1] for cells in table_cells(text) if len(cells) == 2}
    for name in SCAN_FIELDS:
        if is_placeholder(fields.get(name, "")):
            problems.append(f"'{name}' is not filled in")
    if problems:
        return problems

    if fields["Component"].lower() != row.component.lower():
        problems.append(f"is for {fields['Component']}, not {row.component}")
    if fields["Version"] != row.version:
        problems.append(f"is for version {fields['Version']}, the record pins {row.version}")
    if fields["Commit scanned"] != row.git_commit:
        problems.append(f"scanned commit {fields['Commit scanned'][:12]}, the record pins {row.git_commit[:12]} "
                        "(give the full SHA)")
    try:
        datetime.strptime(fields["Date"], "%Y-%m-%d")
    except ValueError:
        problems.append(f"date '{fields['Date']}' is not YYYY-MM-DD")

    # What is compiled is the pinned tree with its patches applied, so that is what has to have
    # been scanned -- and the record has to say so by naming each patch it covered.
    for patch in patch_rows(rows):
        if patch.git_commit == row.git_commit and patch.sha256 not in text:
            problems.append(f"does not name {patch.url} (sha256 {patch.sha256[:12]}...) as part of the "
                            "tree scanned")

    counts = {cells[0]: cells[1:] for cells in table_cells(text) if len(cells) == 3 and cells[0] in SEVERITIES}
    kept_total = 0
    for severity in SEVERITIES:
        if severity not in counts:
            problems.append(f"the result table has no {severity} row")
            continue
        found, kept = counts[severity]
        if not (found.isdigit() and kept.isdigit()):
            problems.append(f"{severity}: count and kept-open must both be numbers, '0' included")
            continue
        if int(kept) > int(found):
            problems.append(f"{severity}: {kept} kept open of {found} found")
        elif int(kept) and severity in BLOCKING_SEVERITIES:
            problems.append(f"{severity}: {kept} finding(s) kept open; fix them, or get an AppSec exception "
                            "and record it")
        kept_total += int(kept)

    kept_section = section(text, "Findings kept open")
    if kept_total and (is_placeholder(kept_section) or kept_section.lower().rstrip(".") == "none"):
        problems.append(f"{kept_total} finding(s) kept open, but 'Findings kept open' does not say why")
    if is_placeholder(section(text, "Conclusion")):
        problems.append("'Conclusion' is not filled in")
    return problems


def verify_scan(rows) -> bool:
    ok = True
    # A patch is scanned as part of the tree it is applied to, so it needs no record of its own;
    # the record of that tree has to name it.
    for row in [r for r in rows if r.artifact != PATCH_ARTIFACT]:
        path = scan_record(row)
        if not path.is_file():
            print(f"FAIL {row.label}: no source scan recorded at {path.relative_to(REPO)}.")
            print("     The code-signing review requires each version to be scanned before we compile it.")
            print("     See Native~/security-scans/README.md.")
            ok = False
            continue
        problems = scan_record_problems(row, rows, path.read_text(encoding="utf-8"))
        if problems:
            print(f"FAIL {row.label}: {path.relative_to(REPO)} does not record a passing scan of this version:")
            for problem in problems:
                print(f"     - {problem}")
            ok = False
        else:
            print(f"ok   {row.label}: source scan of {row.git_commit[:12]} recorded in {path.relative_to(REPO)}.")
    return ok


def archive_rows(rows):
    """The rows that pin a downloaded file."""
    return [r for r in rows if r.sha256 != "-" and r.artifact != PATCH_ARTIFACT]


def install_digests(install_root: pathlib.Path, platform: str) -> dict:
    """Digest of every file a wrapper build copies out of this install, keyed by relative path.

    The same enumeration verify_dependency_digests.py checks against the committed record, so the
    stamp and the record describe one set of files: the stamp says this tree came out of a
    verified build on this machine, the record says it is the tree that was reviewed.
    """
    # collect() resolves every file, so the root has to be resolved too or a path reached through a
    # symlink (macOS /tmp, a mounted volume) would key the same file differently at stamp and check.
    root = install_root.resolve()
    return verify_dependency_digests.collect(root, verify_dependency_digests.scan(root, platform))


def write_stamp(install_root: pathlib.Path, platform: str, rows, scans_verified: bool,
                archives_verified: list, patches_applied: list) -> None:
    """Called by build_openusd.py once build_usd.py has finished and every check has passed.

    `archives_verified` lists the archive rows whose files were checked against their pin before
    build_usd.py was allowed to run, and `patches_applied` the patch rows applied to the tree it
    built. check_stamp requires every archive and every patch row to be in them.
    """
    if not install_root.is_dir():
        sys.exit(f"The stamp needs the OpenUSD install root; {install_root} is not a directory.")
    digests = install_digests(install_root, platform)
    if not digests:
        sys.exit(f"{install_root} holds none of the files a {platform} wrapper build copies; "
                 "refusing to stamp an empty install.")
    payload = {
        "stamp_version": STAMP_VERSION,
        "platform": platform,
        "verified_at": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
        "record": str(record_path(platform).relative_to(REPO)),
        # Whether --require-scan was part of this verification. A release build must be able to
        # say the source was scanned before it was compiled, and the only honest way to know that
        # afterwards is to have recorded it here at the time.
        "scans_verified": scans_verified,
        "sources": [{k: row[k] for k in ("component", "version", "url", "sha256", "git_commit")}
                    for row in rows],
        "archives_verified_before_build": [{"component": r.component, "sha256": r.sha256}
                                           for r in archives_verified],
        "patches_applied": [{"component": r.component, "sha256": r.sha256}
                            for r in patches_applied],
        "install_digests": digests,
    }
    # The directory comes from the command line; the file name never does. A symlink planted at
    # the stamp's path would otherwise redirect this write wherever it points, so it is refused,
    # and the stamp is written beside its final name and swapped in so no half-written stamp is
    # ever read as a verification result.
    stamp = install_root / STAMP_NAME
    if stamp.is_symlink():
        sys.exit(f"{stamp} is a symbolic link; refusing to write the stamp through it.")
    handle, temporary = tempfile.mkstemp(prefix=f"{STAMP_NAME}.", suffix=".tmp", dir=install_root)
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            stream.write(json.dumps(payload, indent=2, sort_keys=True) + "\n")
        os.replace(temporary, stamp)
    except BaseException:
        try:
            os.unlink(temporary)
        except OSError:
            pass
        raise
    print(f"ok   wrote {stamp} ({len(digests)} install files recorded)")


def rebuild_hint(platform: str, install_root: pathlib.Path) -> None:
    print("     Build the install with the script that writes the stamp:")
    print(f"       python3 Native~/build_openusd.py --platform {platform} \\")
    print(f"           --openusd-src <clone> --install {install_root}")


def check_stamp(install_root: pathlib.Path, platform: str, rows, require_scan: bool = False) -> bool:
    path = install_root / STAMP_NAME
    if path.is_symlink() or not path.is_file():
        print(f"FAIL {install_root} carries no source-provenance stamp ({STAMP_NAME}).")
        print("     This OpenUSD install was not verified against Native~/dependency-sources, so the")
        print("     payload built from it would have no chain back to a published revision.")
        rebuild_hint(platform, install_root)
        return False
    try:
        stamped = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        print(f"FAIL {path} is not readable JSON: {exc}")
        return False
    if not isinstance(stamped, dict):
        print(f"FAIL {path} is not a stamp.")
        return False

    if stamped.get("stamp_version") != STAMP_VERSION:
        print(f"FAIL {path} is a version {stamped.get('stamp_version', 1)} stamp; version "
              f"{STAMP_VERSION} is required.")
        print("     Earlier stamps did not record whether TBB was checked before build_usd.py compiled")
        print("     it, which patches the tree carried, or what the install contained, so they cannot")
        print("     vouch for this tree.")
        rebuild_hint(platform, install_root)
        return False

    expected = {(r.component, r.version, r.git_commit, r.sha256) for r in rows}
    found = {(s.get("component"), s.get("version"), s.get("git_commit"), s.get("sha256"))
             for s in stamped.get("sources", []) if isinstance(s, dict)}
    if stamped.get("platform") != platform or expected != found:
        print(f"FAIL {path} was written for a different source record than {record_path(platform).name}.")
        print("     Rebuild and re-stamp, or the payload and the record disagree about its origin.")
        return False

    prebuilt = {(a.get("component"), a.get("sha256"))
                for a in stamped.get("archives_verified_before_build", []) if isinstance(a, dict)}
    unchecked = [r for r in archive_rows(rows) if (r.component, r.sha256) not in prebuilt]
    if unchecked:
        for row in unchecked:
            print(f"FAIL {row.label}: the stamp does not say this archive was checked before build_usd.py")
            print("     compiled it. Checking afterwards is too late: the archive's build logic has")
            print("     already run and its output is already linked in.")
        rebuild_hint(platform, install_root)
        return False

    applied = {(a.get("component"), a.get("sha256"))
               for a in stamped.get("patches_applied", []) if isinstance(a, dict)}
    missing_patches = [r for r in patch_rows(rows) if (r.component, r.sha256) not in applied]
    if missing_patches:
        for row in missing_patches:
            print(f"FAIL {row.label}: the stamp does not say {row.url} was applied before the build.")
        rebuild_hint(platform, install_root)
        return False

    recorded = stamped.get("install_digests")
    if not isinstance(recorded, dict) or not recorded:
        print(f"FAIL {path} records no install digests.")
        return False
    current = install_digests(install_root, platform)
    changed = sorted(k for k in current if k in recorded and recorded[k] != current[k])
    added = sorted(k for k in current if k not in recorded)
    missing = sorted(k for k in recorded if k not in current)
    if changed or added or missing:
        print(f"FAIL {install_root} no longer holds the tree that was stamped:")
        for key in changed[:20]:
            print(f"     changed:  {key}")
        for key in added[:20]:
            print(f"     added:    {key}")
        for key in missing[:20]:
            print(f"     missing:  {key}")
        extra = len(changed) + len(added) + len(missing) - 60
        if extra > 0:
            print(f"     ... and up to {extra} more")
        print("     Something modified the install after its verified build. Rebuild it rather than")
        print("     re-stamping it: the stamp is only evidence if it describes what the build produced.")
        return False

    print(f"ok   {install_root.name}: source provenance stamped {stamped.get('verified_at')}, "
          f"{len(current)} install files unchanged since.")
    if not stamped.get("scans_verified"):
        if require_scan:
            print(f"FAIL {install_root.name} was built without --require-scan, so nothing says its upstream")
            print("     source was scanned before it was compiled. Rebuild it with build_openusd.py")
            print("     --require-scan once Native~/security-scans holds a passing record for each version.")
            return False
        print("WARN this install root was verified without --require-scan, so nothing here says the")
        print("     upstream source was scanned before it was compiled. Fine for local work; the wrapper")
        print("     build marks the payload (.unscanned-build) and the release pack refuses it.")
        print("     See Native~/security-scans/README.md.")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--platform", required=True, choices=("macos", "linux", "windows"))
    parser.add_argument("--openusd-src", type=pathlib.Path,
                        help="OpenUSD clone to verify against the pinned commit.")
    parser.add_argument("--archive", type=pathlib.Path,
                        help="Downloaded dependency archive to verify against its recorded digest.")
    parser.add_argument("--component", help="Disambiguate --archive when the record has several.")
    # Kept only to explain where it went: a stamp written after build_usd.py has already
    # compiled an unchecked archive would vouch for exactly the build it cannot vouch for.
    parser.add_argument("--stamp", type=pathlib.Path, help=argparse.SUPPRESS)
    parser.add_argument("--check-stamp", type=pathlib.Path, metavar="INSTALL_ROOT",
                        help="Require the stamp Native~/build_openusd.py wrote into this install, "
                             "and that the install still holds what it recorded.")
    parser.add_argument("--require-scan", action="store_true",
                        help="Also require a passing Native~/security-scans record for every pinned "
                             "version, and with --check-stamp, that the install was built with it.")
    parser.add_argument("--list", action="store_true", help="Print the record and exit.")
    args = parser.parse_args()

    if args.stamp:
        print("--stamp is gone. The stamp is written by Native~/build_openusd.py, which checks every")
        print("recorded archive before build_usd.py runs rather than after it has compiled them:")
        print(f"    python3 Native~/build_openusd.py --platform {args.platform} "
              f"--openusd-src <clone> --install {args.stamp}")
        return 2

    rows = load(args.platform)

    if args.list:
        width = max(len(r.label) for r in rows)
        for row in rows:
            pin = row.git_commit if row.sha256 == "-" else f"sha256:{row.sha256}"
            if row.artifact == PATCH_ARTIFACT:
                pin += f" onto {row.git_commit[:12]}"
            print(f"{row.label:<{width}}  {row.artifact:<15} {pin}")
            print(f"{'':<{width}}  {row.url}")
            print(f"{'':<{width}}  upstream evidence: {row.evidence}")
        return 0

    if not any((args.openusd_src, args.archive, args.check_stamp, args.require_scan)):
        parser.error("nothing to do: pass --openusd-src, --archive, --check-stamp, --require-scan or --list")

    ok = True

    if args.openusd_src:
        if not git_rows(rows):
            sys.exit(f"The {args.platform} record pins no git source.")
        for row in git_rows(rows):
            ok &= verify_git(row, args.openusd_src)
        for row in patch_rows(rows):
            ok &= verify_patch(row)

    if args.archive:
        if not args.archive.is_file():
            sys.exit(f"{args.archive} is not a file.")
        ok &= verify_archive(match_archive(rows, args.archive, args.component), args.archive)

    if args.require_scan:
        ok &= verify_scan(rows)

    if args.check_stamp:
        ok &= check_stamp(args.check_stamp, args.platform, rows, args.require_scan)

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
