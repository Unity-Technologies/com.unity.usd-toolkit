#!/usr/bin/env python3
"""Records and verifies the digests of the OpenUSD/TBB files a native build copies into the package.

The build scripts copy a dependency tree straight off the build host into Runtime/Plugins. With
no check, a tampered local OpenUSD or TBB install rides into the shipped package unnoticed
(SECURITY-282834, CWE-347). This makes that step explicit: the digests of everything copied are
recorded in a checked-in file, and a later build fails if what it is about to copy no longer
matches.

    # verify before copying (what the build scripts call)
    python3 Native~/verify_dependency_digests.py --platform macos --root <openusd-root> --verify <files...>

    # deliberately accept the current dependency tree and update the record
    python3 Native~/verify_dependency_digests.py --platform macos --root <openusd-root> --record <files...>

    # enumerate the dependency tree here instead of being handed a file list, so a record can be
    # taken without running a build (--scan works with either --verify or --record)
    python3 Native~/verify_dependency_digests.py --platform windows --root <openusd-root> --scan --record

Recording is a deliberate act, and the resulting diff shows exactly which dependency files
changed, so a substitution has to survive code review instead of passing silently.

What this does NOT do: prove the dependency tree is authentic upstream code. These are locally
built artifacts and the build is not bit-reproducible, so a first record of a given tree is
trust-on-first-use. It exists to make later changes visible, and is meant to sit alongside
building from a pinned public tag (see BUILD_NOTES.md), not to replace it.
"""

import argparse
import hashlib
import pathlib
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
DIGEST_DIR = REPO / "Native~" / "dependency-digests"


def digest_of(path: pathlib.Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            sha.update(chunk)
    return sha.hexdigest()


def collect(root: pathlib.Path, files):
    """Maps each file to its path relative to the dependency root, so the record is portable
    across machines where that root lives somewhere else."""
    collected = {}
    for raw in files:
        path = pathlib.Path(raw).resolve()
        if not path.is_file():
            continue
        try:
            key = path.relative_to(root).as_posix()
        except ValueError:
            key = path.name
        collected[key] = digest_of(path)
    return collected


# Which files each platform's build copies out of the OpenUSD install root. Keeping this here,
# rather than only inside the build scripts, is what lets a digest record be taken or checked
# without running a build -- useful on a machine that has the dependency tree but is not the one
# producing this release's payload (SECURITY-282834, CWE-347).
#
# These mirror the build scripts, and drift between the two would show up as a failed gate on the
# next real build, not as a silently wrong record:
#   macos    build_macos.sh, collect_dependency_files()
#   windows  build_windows.ps1, the $DependencyFiles block
#   linux    the monolithic CMake assembly in Native~/REBUILD_WINDOWS_LINUX.md section 6 -- NOT
#            build_linux.sh, which targets the packman + Python layout and ships a much larger
#            closure than the payload this package ships.
SCAN_RULES = {
    "macos": {
        "flat": [("lib", "*.dylib*"), ("bin", "*.dylib*")],
        "trees": ["lib/usd", "plugin", "share", "resources"],
    },
    "windows": {
        "flat": [("bin", "*.dll"), ("lib", "*.dll")],
        "trees": ["lib/usd", "plugin", "share", "resources"],
    },
    "linux": {
        "flat": [("lib", "libusd_ms.so"), ("lib", "libtbb.so*")],
        "trees": ["lib/usd", "plugin/usd"],
    },
}

# Never recorded: debug variants are not shipped, and Unity .meta sidecars are not part of the
# dependency tree. Note what is deliberately NOT excluded -- lib/usd/usd/resources/codegenTemplates
# is pruned from the payload *after* the copy, but the gate runs before that, so those files are
# part of what the build reads off the machine and belong in the record. The record describes the
# dependency tree that is trusted, not the final payload; the payload's own digests live in
# Runtime/Native/NativeRuntimeHashes.g.cs.
SCAN_EXCLUDED_NAMES = (".meta",)


def scan(root: pathlib.Path, platform: str):
    """Enumerates the dependency files a build on this platform would copy out of `root`."""
    rules = SCAN_RULES[platform]
    found = []

    for directory, pattern in rules["flat"]:
        base = root / directory
        if not base.is_dir():
            continue
        found += [path for path in sorted(base.glob(pattern)) if path.is_file()]

    for directory in rules["trees"]:
        base = root / directory
        if not base.is_dir():
            continue
        found += [path for path in sorted(base.rglob("*")) if path.is_file()]

    keep = []
    for path in found:
        if path.suffix in SCAN_EXCLUDED_NAMES:
            continue
        if "debug" in path.name.lower():
            continue
        keep.append(str(path))
    return keep


def load(record_path: pathlib.Path):
    entries = {}
    for line in record_path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        sha, _, key = line.partition("  ")
        if sha and key:
            entries[key] = sha
    return entries


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", required=True, choices=["macos", "windows", "linux"])
    parser.add_argument("--root", required=True, help="Dependency root the paths are recorded relative to.")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--verify", action="store_true")
    mode.add_argument("--record", action="store_true")
    parser.add_argument(
        "--scan",
        action="store_true",
        help="Enumerate the dependency tree under --root instead of taking a file list, so a "
             "record can be taken or checked without running a build.",
    )
    parser.add_argument("files", nargs="*")
    args = parser.parse_args()

    root = pathlib.Path(args.root).resolve()
    record_path = DIGEST_DIR / f"{args.platform}.sha256"

    if args.scan:
        if args.files:
            print("error: --scan enumerates the tree itself; do not also pass a file list",
                  file=sys.stderr)
            return 1
        files = scan(root, args.platform)
        print(f"scanned {len(files)} dependency files under {root}")
    else:
        files = args.files

    current = collect(root, files)

    if not current:
        print("error: no dependency files were given to check", file=sys.stderr)
        return 1

    if args.record:
        DIGEST_DIR.mkdir(parents=True, exist_ok=True)
        lines = [
            f"# SHA-256 of the {args.platform} OpenUSD/TBB files the native build copies into",
            "# Runtime/Plugins. Regenerate deliberately with --record and review the diff; see",
            "# Native~/verify_dependency_digests.py.",
        ]
        lines += [f"{current[key]}  {key}" for key in sorted(current)]
        record_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"recorded {len(current)} dependency digests in {record_path.relative_to(REPO)}")
        return 0

    if not record_path.exists():
        print(
            f"error: no dependency digest record for '{args.platform}' at "
            f"{record_path.relative_to(REPO)}.\n"
            "The build refuses to copy an unverified dependency tree into the package. If this\n"
            "tree is the one you mean to ship, record it deliberately and commit the result:\n"
            f"    python3 Native~/verify_dependency_digests.py --platform {args.platform} "
            f"--root '{root}' --record <the same files>\n"
            "The build scripts pass --record for you when given --record-dependency-digests.",
            file=sys.stderr,
        )
        return 1

    expected = load(record_path)
    changed, added = [], []
    for key in sorted(current):
        if key not in expected:
            added.append(key)
        elif expected[key] != current[key]:
            changed.append(f"{key} (recorded {expected[key][:16]}…, found {current[key][:16]}…)")

    missing = sorted(key for key in expected if key not in current)

    if changed or added or missing:
        print(f"error: the dependency tree under {root} does not match "
              f"{record_path.relative_to(REPO)}", file=sys.stderr)
        for key in changed:
            print(f"  changed: {key}", file=sys.stderr)
        for key in added:
            print(f"  not recorded: {key}", file=sys.stderr)
        for key in missing:
            print(f"  recorded but absent: {key}", file=sys.stderr)
        print(
            "\nIf you intended to change dependencies (a new OpenUSD build, for instance), re-run\n"
            "the build script with --record-dependency-digests and commit the updated record so\n"
            "the change is visible in review.",
            file=sys.stderr,
        )
        return 1

    print(f"dependency digests match ({len(current)} files)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
