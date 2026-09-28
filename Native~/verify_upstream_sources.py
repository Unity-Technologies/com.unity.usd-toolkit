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
is pinned by two things per platform, and that is what the records in Native~/dependency-sources
hold: the OpenUSD commit, and the TBB archive.

    # before running build_usd.py: the clone is the pinned revision, unmodified, from the
    # recorded remote
    python3 Native~/verify_upstream_sources.py --platform linux --openusd-src /opt/usd-26.05/src

    # the archive build_usd.py downloaded is the recorded one
    python3 Native~/verify_upstream_sources.py --platform windows --archive ~/Downloads/tbb-2020.3-win.zip

    # after OpenUSD is installed: leave a stamp the wrapper build can require
    python3 Native~/verify_upstream_sources.py --platform macos --openusd-src /Users/Shared/usd-26.05/src \
        --stamp /Users/Shared/usd-26.05/install

    # what the wrapper build scripts call
    python3 Native~/verify_upstream_sources.py --platform macos --check-stamp <openusd-root>

    python3 Native~/verify_upstream_sources.py --platform windows --list

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
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

REPO = pathlib.Path(__file__).resolve().parent.parent
RECORD_DIR = REPO / "Native~" / "dependency-sources"
SCAN_DIR = REPO / "Native~" / "security-scans"
STAMP_NAME = ".unity-usd-toolkit-source-provenance.json"

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


def match_archive(rows, path: pathlib.Path, component):
    candidates = [r for r in rows if r.sha256 != "-"]
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


def verify_scan(rows) -> bool:
    ok = True
    for row in rows:
        path = scan_record(row)
        if path.is_file():
            print(f"ok   {row.label}: source scan recorded in {path.relative_to(REPO)}.")
        else:
            print(f"FAIL {row.label}: no source scan recorded at {path.relative_to(REPO)}.")
            print("     The code-signing review requires each version to be scanned before we compile it.")
            print("     See Native~/security-scans/README.md.")
            ok = False
    return ok


def write_stamp(install_root: pathlib.Path, platform: str, rows, scans_verified: bool) -> None:
    if not install_root.is_dir():
        sys.exit(f"--stamp expects the OpenUSD install root; {install_root} is not a directory.")
    payload = {
        "platform": platform,
        "verified_at": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
        "record": str(record_path(platform).relative_to(REPO)),
        # Whether --require-scan was part of this verification. A release build must be able to
        # say the source was scanned before it was compiled, and the only honest way to know that
        # afterwards is to have recorded it here at the time.
        "scans_verified": scans_verified,
        "sources": [{k: row[k] for k in ("component", "version", "url", "sha256", "git_commit")}
                    for row in rows],
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
            stream.write(json.dumps(payload, indent=2) + "\n")
        os.replace(temporary, stamp)
    except BaseException:
        try:
            os.unlink(temporary)
        except OSError:
            pass
        raise
    print(f"ok   wrote {stamp}")


def check_stamp(install_root: pathlib.Path, platform: str, rows) -> bool:
    path = install_root / STAMP_NAME
    if not path.is_file():
        print(f"FAIL {install_root} carries no source-provenance stamp ({STAMP_NAME}).")
        print("     This OpenUSD install was not verified against Native~/dependency-sources, so the")
        print("     payload built from it would have no chain back to a published revision. Run:")
        print(f"       python3 Native~/verify_upstream_sources.py --platform {platform} \\")
        print(f"           --openusd-src <clone> --stamp {install_root}")
        return False
    try:
        stamped = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        print(f"FAIL {path} is not readable JSON: {exc}")
        return False

    expected = {(r.component, r.version, r.git_commit, r.sha256) for r in rows}
    found = {(s.get("component"), s.get("version"), s.get("git_commit"), s.get("sha256"))
             for s in stamped.get("sources", [])}
    if stamped.get("platform") != platform or expected != found:
        print(f"FAIL {path} was written for a different source record than {record_path(platform).name}.")
        print("     Re-verify and re-stamp, or the payload and the record disagree about its origin.")
        return False
    print(f"ok   {install_root.name}: source provenance stamped {stamped.get('verified_at')}.")
    if not stamped.get("scans_verified"):
        print("WARN this install root was verified without --require-scan, so nothing here says the")
        print("     upstream source was scanned before it was compiled. Fine for local work; a")
        print("     release payload needs the scan. See Native~/security-scans/README.md.")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--platform", required=True, choices=("macos", "linux", "windows"))
    parser.add_argument("--openusd-src", type=pathlib.Path,
                        help="OpenUSD clone to verify against the pinned commit.")
    parser.add_argument("--archive", type=pathlib.Path,
                        help="Downloaded dependency archive to verify against its recorded digest.")
    parser.add_argument("--component", help="Disambiguate --archive when the record has several.")
    parser.add_argument("--stamp", type=pathlib.Path, metavar="INSTALL_ROOT",
                        help="Write the verification result into an OpenUSD install root.")
    parser.add_argument("--check-stamp", type=pathlib.Path, metavar="INSTALL_ROOT",
                        help="Require a stamp written by --stamp for this platform's record.")
    parser.add_argument("--require-scan", action="store_true",
                        help="Also require a Native~/security-scans record for every pinned version.")
    parser.add_argument("--list", action="store_true", help="Print the record and exit.")
    args = parser.parse_args()

    rows = load(args.platform)

    if args.list:
        width = max(len(r.label) for r in rows)
        for row in rows:
            pin = row.git_commit if row.sha256 == "-" else f"sha256:{row.sha256}"
            print(f"{row.label:<{width}}  {row.artifact:<15} {pin}")
            print(f"{'':<{width}}  {row.url}")
            print(f"{'':<{width}}  upstream evidence: {row.evidence}")
        return 0

    if not any((args.openusd_src, args.archive, args.check_stamp, args.require_scan)):
        parser.error("nothing to do: pass --openusd-src, --archive, --check-stamp, --require-scan or --list")

    ok = True

    if args.openusd_src:
        git_rows = [r for r in rows if r.sha256 == "-"]
        if not git_rows:
            sys.exit(f"The {args.platform} record pins no git source.")
        for row in git_rows:
            ok &= verify_git(row, args.openusd_src)

    if args.archive:
        if not args.archive.is_file():
            sys.exit(f"{args.archive} is not a file.")
        ok &= verify_archive(match_archive(rows, args.archive, args.component), args.archive)

    if args.require_scan:
        ok &= verify_scan(rows)

    if args.check_stamp:
        ok &= check_stamp(args.check_stamp, args.platform, rows)

    if args.stamp:
        if not ok:
            print("Refusing to stamp an install root whose sources did not verify.")
            return 1
        write_stamp(args.stamp, args.platform, rows, args.require_scan)

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
