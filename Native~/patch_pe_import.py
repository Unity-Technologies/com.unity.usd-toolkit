#!/usr/bin/env python3
"""Rewrites one import-library name inside a Windows PE, so a payload DLL can be renamed.

Windows resolves an import by **base name** against the modules already loaded in the process. A
package that ships `tbb.dll` next to its own plugin does not get its own copy: if anything in the
process loaded a `tbb.dll` first -- the Unity Editor ships one in its Frameworks directory -- that
is what the loader binds to, and the payload silently runs against a stranger's build. This
package already hit that with OpenUSD's monolithic library, which is why it ships as `usd_rt.dll`
rather than `usd_ms.dll` (a clash with com.unity.pixyz.sdk-plus). Renaming is the fix; this script
is what makes an already-linked binary agree with the new name.

`build_windows.ps1` renames `usd_ms.dll` with a plain equal-length byte replacement, which works
because "usd_rt.dll" is exactly as long. That trick does not generalise: an import name string sits
in a packed table with no room after it, so anything longer overwrites the next entry. In
`usd_rt.dll` the "tbb.dll" string is followed by a single NUL and then the next import hint --
seven characters is the whole budget, which is not enough for a name that is recognisably ours.

So instead of overwriting the string in place, this rewrites the import descriptor's Name RVA to
point at a longer name written into a section's trailing slack. Sections are mapped a page at a
time, so the bytes between a section's virtual size and its raw size are present in memory and
addressable; they are padding the linker zero-filled. Writing there changes no existing data and
moves nothing, and the only structural edit is four bytes of RVA.

    python3 Native~/patch_pe_import.py <pe> --from tbb.dll --to tbb_usdrt.dll

The patch is idempotent -- a file already importing the new name is left alone and reports
success -- so a rebuild that re-runs it is safe. It refuses anything it does not fully understand
rather than writing a half-patched binary, and re-parses the file afterwards to confirm the import
table still reads correctly.

What this does NOT do: touch the renamed DLL itself. The file keeps its bytes, so a third-party
signature on it stays valid -- Authenticode covers a PE's contents, not its filename. That matters
here, because the file being renamed on Windows is Intel's signed TBB build (SECURITY-282834).
"""

import argparse
import pathlib
import struct
import sys


class PEError(Exception):
    pass


class PE:
    def __init__(self, data: bytearray):
        self.data = data
        if data[:2] != b"MZ":
            raise PEError("not a PE file: no MZ signature")
        self.pe_off = struct.unpack_from("<I", data, 0x3C)[0]
        if data[self.pe_off:self.pe_off + 4] != b"PE\0\0":
            raise PEError("not a PE file: no PE signature")
        coff = self.pe_off + 4
        self.section_count = struct.unpack_from("<H", data, coff + 2)[0]
        opt_size = struct.unpack_from("<H", data, coff + 16)[0]
        opt = coff + 20
        magic = struct.unpack_from("<H", data, opt)[0]
        if magic not in (0x10B, 0x20B):
            raise PEError(f"unknown optional header magic 0x{magic:x}")
        self.data_dirs = opt + (112 if magic == 0x20B else 96)
        self.sections = []
        base = opt + opt_size
        for index in range(self.section_count):
            entry = base + index * 40
            name = bytes(data[entry:entry + 8]).rstrip(b"\0").decode("ascii", "replace")
            vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, entry + 8)
            flags = struct.unpack_from("<I", data, entry + 36)[0]
            self.sections.append({"name": name, "vaddr": vaddr, "vsize": vsize,
                                  "raddr": raddr, "rsize": rsize,
                                  "executable": bool(flags & 0x20000000)})

    def offset_of(self, rva: int):
        for s in self.sections:
            if s["vaddr"] <= rva < s["vaddr"] + max(s["vsize"], s["rsize"]):
                return s["raddr"] + (rva - s["vaddr"])
        return None

    def cstring(self, offset: int) -> bytes:
        end = self.data.index(b"\0", offset)
        return bytes(self.data[offset:end])

    def import_descriptors(self):
        """(index, name, file offset of the Name RVA field, file offset of the name)."""
        rva, size = struct.unpack_from("<II", self.data, self.data_dirs + 8)
        if rva == 0 or size == 0:
            raise PEError("the file has no import table")
        base = self.offset_of(rva)
        if base is None:
            raise PEError(f"import table RVA 0x{rva:x} is outside every section")
        out = []
        index = 0
        while True:
            entry = base + index * 20
            descriptor = bytes(self.data[entry:entry + 20])
            if len(descriptor) < 20 or descriptor == b"\0" * 20:
                break
            name_rva = struct.unpack_from("<I", descriptor, 12)[0]
            if name_rva == 0:
                break
            name_off = self.offset_of(name_rva)
            if name_off is None:
                raise PEError(f"import name RVA 0x{name_rva:x} is outside every section")
            out.append((index, self.cstring(name_off).decode("ascii", "replace"),
                        entry + 12, name_off))
            index += 1
        return out

    def find_slack(self, needed: int):
        """Mapped, zero-filled padding at the end of a section: (file offset, rva).

        Read-only data sections are preferred over executable ones. A name string is data, and
        .text padding, while perfectly readable, puts a new string in a page marked executable --
        which is both untidy and the kind of thing a binary scanner remarks on.
        """
        for s in sorted(self.sections, key=lambda s: (s["executable"], s["name"] != ".rdata")):
            if s["rsize"] <= s["vsize"]:
                continue
            start = s["raddr"] + s["vsize"]
            available = s["rsize"] - s["vsize"]
            if available < needed:
                continue
            if any(self.data[start:start + available]):
                continue
            return start, s["vaddr"] + s["vsize"], s["name"], available
        return None


def patch(path: pathlib.Path, old: str, new: str, quiet: bool = False) -> int:
    data = bytearray(path.read_bytes())
    pe = PE(data)
    descriptors = pe.import_descriptors()

    if any(name.lower() == new.lower() for _, name, _, _ in descriptors):
        if not quiet:
            print(f"ok   {path.name} already imports {new}; nothing to do.")
        return 0

    matches = [d for d in descriptors if d[1].lower() == old.lower()]
    if not matches:
        print(f"FAIL {path.name} does not import {old}. Imports: "
              f"{', '.join(n for _, n, _, _ in descriptors)}", file=sys.stderr)
        return 1
    if len(matches) > 1:
        print(f"FAIL {path.name} imports {old} more than once; refusing to guess.", file=sys.stderr)
        return 1

    _, _, rva_field, name_off = matches[0]
    encoded = new.encode("ascii") + b"\0"

    if len(new) <= len(old):
        # Fits where it is. Blank the old string first so no tail of it survives.
        data[name_off:name_off + len(old) + 1] = b"\0" * (len(old) + 1)
        data[name_off:name_off + len(encoded)] = encoded
        where = f"in place at 0x{name_off:x}"
    else:
        slack = pe.find_slack(len(encoded))
        if slack is None:
            print(f"FAIL {path.name}: no mapped, zero-filled section padding large enough for "
                  f"'{new}' ({len(encoded)} bytes). Use a name of at most {len(old)} characters, "
                  f"or relink against an import library that names the DLL.", file=sys.stderr)
            return 1
        offset, rva, section, available = slack
        data[offset:offset + len(encoded)] = encoded
        struct.pack_into("<I", data, rva_field, rva)
        where = (f"in {section} padding at 0x{offset:x} (RVA 0x{rva:x}, {available} bytes free), "
                 f"Name RVA at 0x{rva_field:x} redirected")

    # Re-parse what we are about to write rather than what we hoped we wrote.
    verified = PE(data).import_descriptors()
    names = [n for _, n, _, _ in verified]
    if new not in names or old in names or len(names) != len(descriptors):
        print(f"FAIL {path.name}: the patched import table does not read back as expected "
              f"({names}). Nothing was written.", file=sys.stderr)
        return 1

    path.write_bytes(bytes(data))
    if not quiet:
        print(f"ok   {path.name}: import {old} -> {new}, {where}.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("pe", type=pathlib.Path)
    parser.add_argument("--from", dest="old", required=True, metavar="NAME")
    parser.add_argument("--to", dest="new", required=True, metavar="NAME")
    parser.add_argument("--list", action="store_true", help="Print the imports and exit.")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    if not args.pe.is_file():
        print(f"FAIL {args.pe} is not a file.", file=sys.stderr)
        return 1
    try:
        if args.list:
            for _, name, _, _ in PE(bytearray(args.pe.read_bytes())).import_descriptors():
                print(name)
            return 0
        return patch(args.pe, args.old, args.new, args.quiet)
    except PEError as exc:
        print(f"FAIL {args.pe.name}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
