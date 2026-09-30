#!/usr/bin/env python3
"""Regenerates Runtime/Native/NativeRuntimeHashes.g.cs from the shipped native payload.

The managed loader compares every native binary AND every plugin descriptor it finds against
the digest recorded here before the first P/Invoke, so a swapped, truncated or added file fails
loudly instead of being loaded. Run this after EVERY native rebuild -- the build output is a new
binary with a new digest, and a stale manifest makes the package refuse its own payload.

    python3 Native~/generate_native_hashes.py

Descriptors are covered, not just libraries: OpenUSD decides which library to load from
plugInfo.json, so hashing only the binaries leaves an attacker free to add a library and
repoint a descriptor at it (SECURITY-282834, CWE-345).

Entries are keyed by "<platform>/<path relative to that platform's payload root>", because
descriptor file names repeat across platforms -- every platform ships its own
lib/usd/ar/resources/plugInfo.json -- and a name-keyed table cannot tell them apart, nor stop a
file in an unexpected directory from matching an expected entry.

The digests live in a generated C# file rather than a data file next to the binaries on
purpose: a manifest shipped beside the payload can be edited by anyone who can edit the
payload, whereas replacing these values means patching a compiled assembly.

    python3 Native~/generate_native_hashes.py --check-attributes

checks something the digests silently depend on: that git hands every hashed file out byte for
byte. A file git treats as text is rewritten on checkout wherever core.autocrlf is set, and the
manifest then disagrees with that machine's disk while `git status` stays clean -- which is how
Windows checkouts came to refuse their own descriptors before .gitattributes marked them -text. The check
asks git, for each file this script would hash, whether its text attribute is unset, so a new
descriptor type added below without a matching .gitattributes rule fails CI instead of failing
on a Windows user's machine.
"""

import hashlib
import pathlib
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
PAYLOAD = REPO / "Runtime" / "Plugins"
OUTPUT = REPO / "Runtime" / "Native" / "NativeRuntimeHashes.g.cs"

# Platform id -> payload root, relative to Runtime/Plugins. The id must match
# NativeRuntimeHashes.PlatformId in the managed loader.
PLATFORM_ROOTS = {
    "macOS": pathlib.Path("macOS"),
    "windows": pathlib.Path("x86_64/Windows"),
    "linux": pathlib.Path("x86_64/Linux"),
}

DESCRIPTOR_NAMES = {"plugInfo.json"}
DESCRIPTOR_SUFFIXES = {".usda", ".glslfx"}
BINARY_SUFFIXES = {".dll", ".dylib", ".so"}

# A Git LFS pointer is a small text file that begins with this line. Hashing one would record
# the pointer's digest instead of the binary's, and the loader would then refuse the real file on
# every machine that has it -- a single-platform rebuild run with an incomplete LFS working copy
# would silently break the other two platforms. Refuse instead of guessing.
LFS_POINTER_PREFIX = b"version https://git-lfs.github.com/spec/"

# Left in a platform's payload root by Native~/CMakeLists.txt when the wrapper was built with the
# source-provenance gate skipped, and removed again by the next build that passes it. Hashing such
# a payload would record digests the loader then trusts, so the one step that makes a payload
# loadable -- and the CI jobs that run it -- refuses instead (SECURITY-282834, CWE-345).
UNVERIFIED_MARKER = ".unverified-build"


def is_lfs_pointer(path: pathlib.Path) -> bool:
    # Pointers are a few hundred bytes; a real binary never is.
    if path.stat().st_size > 1024:
        return False

    with path.open("rb") as handle:
        return handle.read(len(LFS_POINTER_PREFIX)) == LFS_POINTER_PREFIX


def is_native_binary(path: pathlib.Path) -> bool:
    if path.suffix == ".meta":
        return False
    if path.suffix in BINARY_SUFFIXES:
        return True
    # libtbb.so.2 and friends: a version suffix after .so
    return ".so." in path.name


def is_descriptor(path: pathlib.Path) -> bool:
    if path.suffix == ".meta":
        return False
    return path.name in DESCRIPTOR_NAMES or path.suffix in DESCRIPTOR_SUFFIXES


def check_attributes() -> int:
    """Every file the manifest covers must be exempt from line-ending conversion."""
    paths = []
    for relative_root in PLATFORM_ROOTS.values():
        root = PAYLOAD / relative_root
        if not root.is_dir():
            continue
        for path in sorted(root.rglob("*")):
            if path.is_file() and (is_native_binary(path) or is_descriptor(path)):
                paths.append(path.relative_to(REPO).as_posix())

    if not paths:
        print("error: found no payload files to check", file=sys.stderr)
        return 1

    # Bytes, not text=True: on Windows a text-mode pipe writes "\n" as "\r\n", git then looks up
    # "<path>\r", finds no attributes, and every file is reported as exposed.
    result = subprocess.run(["git", "check-attr", "--stdin", "text"], cwd=REPO,
                            input=("\n".join(paths) + "\n").encode("utf-8"), capture_output=True)
    result.stdout = result.stdout.decode("utf-8")
    result.stderr = result.stderr.decode("utf-8", "replace")
    if result.returncode != 0:
        print(f"error: git check-attr failed: {result.stderr.strip()}", file=sys.stderr)
        return 1

    # Output lines are "<path>: text: <state>". Anything but "unset" means git may convert it.
    exposed = [line for line in result.stdout.splitlines() if not line.endswith(": text: unset")]
    if exposed:
        print(f"error: {len(exposed)} hashed payload file(s) are not marked -text in .gitattributes,",
              file=sys.stderr)
        print("so core.autocrlf can rewrite them on checkout and the manifest will not match:",
              file=sys.stderr)
        for line in exposed[:20]:
            print(f"  {line}", file=sys.stderr)
        if len(exposed) > 20:
            print(f"  ... and {len(exposed) - 20} more", file=sys.stderr)
        return 1

    print(f"ok: all {len(paths)} hashed payload files are exempt from line-ending conversion")
    return 0


def main() -> int:
    if len(sys.argv) > 1:
        if sys.argv[1:] == ["--check-attributes"]:
            return check_attributes()
        print(f"usage: {sys.argv[0]} [--check-attributes]", file=sys.stderr)
        return 2

    if not PAYLOAD.is_dir():
        print(f"error: no payload directory at {PAYLOAD}", file=sys.stderr)
        return 1

    unverified = [PAYLOAD / root / UNVERIFIED_MARKER for root in PLATFORM_ROOTS.values()
                  if (PAYLOAD / root / UNVERIFIED_MARKER).exists()]
    if unverified:
        print("error: these payloads were built with the source-provenance gate skipped:", file=sys.stderr)
        for path in unverified:
            print(f"  {path.relative_to(REPO)}", file=sys.stderr)
        print(
            "\nA payload with no chain back to a published OpenUSD revision is for local experiments\n"
            "only, so no manifest is written for it. Rebuild against an install that\n"
            "Native~/build_openusd.py stamped, without --skip-source-provenance, and the marker goes\n"
            "away with the rebuild.",
            file=sys.stderr,
        )
        return 1

    entries = {}
    binary_count = 0
    descriptor_count = 0
    pointers = []

    for platform_id, relative_root in PLATFORM_ROOTS.items():
        root = PAYLOAD / relative_root
        if not root.is_dir():
            print(f"warning: no payload for '{platform_id}' at {root}", file=sys.stderr)
            continue

        for path in sorted(root.rglob("*")):
            if not path.is_file():
                continue

            binary = is_native_binary(path)
            if not binary and not is_descriptor(path):
                continue

            if binary:
                if is_lfs_pointer(path):
                    pointers.append(path)
                    continue
                binary_count += 1
            else:
                descriptor_count += 1

            key = f"{platform_id}/{path.relative_to(root).as_posix()}"
            entries[key] = hashlib.sha256(path.read_bytes()).hexdigest()

    if pointers:
        print("error: these payload files are Git LFS pointers, not real binaries:", file=sys.stderr)
        for path in pointers:
            print(f"  {path.relative_to(REPO)}", file=sys.stderr)
        print(
            "\nThe manifest covers every platform, not just the one you rebuilt, so writing it\n"
            "now would record the pointers' digests and break those platforms at load time.\n"
            "Fetch the real content first:\n"
            "    git lfs install && git lfs pull\n"
            "then re-run this script.",
            file=sys.stderr,
        )
        return 1

    if not entries:
        print(f"error: found nothing to hash under {PAYLOAD}", file=sys.stderr)
        return 1

    lines = [
        "// <auto-generated>",
        "//     SHA-256 digests of the native runtime files shipped with this package -- both the",
        "//     libraries and the plugin descriptors that decide which library OpenUSD loads --",
        "//     checked by UsdExporter before the first P/Invoke so a substituted or added file",
        "//     fails loudly rather than being loaded and executed in-process.",
        "//",
        "//     Do not edit by hand. Regenerate after every native rebuild:",
        "//         python3 Native~/generate_native_hashes.py",
        "// </auto-generated>",
        "",
        "using System;",
        "using System.Collections.Generic;",
        "",
        "namespace Unity.USDToolkit.Native",
        "{",
        "    internal static class NativeRuntimeHashes",
        "    {",
        "        // Identifies which platform's entries apply at run time. Must match the keys the",
        "        // generator writes.",
        "#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX",
        '        internal const string PlatformId = "macOS";',
        "#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX",
        '        internal const string PlatformId = "linux";',
        "#else",
        '        internal const string PlatformId = "windows";',
        "#endif",
        "",
        "        // \"<platform>/<path relative to that platform's payload root>\" -> SHA-256 (lowercase",
        "        // hex). Keyed by path, not by file name: every platform ships its own copy of",
        "        // lib/usd/ar/resources/plugInfo.json, and a name-keyed table could neither tell them",
        "        // apart nor stop a file in an unexpected directory from matching an expected entry.",
        "        internal static readonly Dictionary<string, string> Expected =",
        "            new Dictionary<string, string>(StringComparer.Ordinal)",
        "        {",
    ]

    for key in sorted(entries):
        lines.append(f'            {{ "{key}", "{entries[key]}" }},')

    lines += [
        "        };",
        "    }",
        "}",
        "",
    ]

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(
        f"wrote {OUTPUT.relative_to(REPO)}: {len(entries)} entries "
        f"({binary_count} binaries, {descriptor_count} descriptors)"
    )
    for platform_id in PLATFORM_ROOTS:
        count = sum(1 for key in entries if key.startswith(platform_id + "/"))
        print(f"  {platform_id:8s} {count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
