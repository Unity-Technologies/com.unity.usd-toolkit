# Handoff — rebuild Windows and Linux for the LZ4 patch (branch `security/lz4-1.10.0`)

**Task:** on a Windows x64 machine and on a Linux x64 machine, rebuild OpenUSD with the pinned
Unity patch and rebuild the native payload against it, so all three platforms ship LZ4 1.10.0.
macOS is done. Follow this top to bottom on each machine; it is written so that no one has to be
asked anything. Where it says **STOP**, stop and report instead of working around it — every STOP
is a security gate, and a workaround ships exactly what the gate exists to catch.

This file is the task and its order. `Native~/REBUILD_WINDOWS_LINUX.md` is the reference it points
into for the gates (Gate 1–10), prerequisites (§3) and troubleshooting (Appendix A).

---

## 0. Where the branch stands

Why: SECURITY-282834 blocking finding *"Bundled OpenUSD dependency parsers as vulnerable/unscanned
components (LZ4 1.9.2)"* (CWE-1104, CVE-2021-3520). OpenUSD v26.05 vendors LZ4 1.9.2; its `.usdc`
reader could hand LZ4 a negative output size, which is the CVE trigger.
`Native~/patches/openusd-26.05-lz4-1.10.0.patch` replaces the vendored LZ4 with 1.10.0 and
bounds-checks every size OpenUSD passes to it (see the patch header).

| | macOS | Windows | Linux |
| --- | --- | --- | --- |
| OpenUSD built with the patch (`build_openusd.py`, stamp v3) | **done** | **done** (2026-09-29) | **done** (2026-09-30) |
| payload rebuilt, `dependency-digests/<platform>.sha256` re-recorded | **done** | **done** (`1c1e55f`) | **done** (`12378b3`) |
| wrapper ABI | 5 | **5** | **5** (already 5 since 2026-09-22; wrapper byte-identical) |
| `lz4_bounds_test` on the shipped library | 8/8 PASS (old payload: 4 fail, 3 faults) | 8/8 PASS | 8/8 PASS |
| Gate 7 in the Editor | **done** (§11, at `0920d0d`) | **done** (§9) | **done** (§10) |

Windows results, for the §6 report, are in §7 at the end of this file; Linux results and §4 in §8.

The Windows rebuild also brought its wrapper from ABI 4 to 5 (usdz export and usdz textures). The
Linux wrapper turned out to be ABI 5 already (rebuilt 2026-09-22) and came out byte-identical, so
only its OpenUSD and TBB changed. The full gate run was required either way, not just the LZ4 test.

**Status (2026-09-30): every platform is rebuilt, §4 is done, and Gate 7 has passed on all three.**
The manifest and SBOM were regenerated once, after all three payloads were in, and match the
binaries. What remains is the PR (§4 step 4). While this was in progress the SBOM said LZ4 1.10.0
for all three platforms before the Windows and Linux binaries carried it, and CI's
`integrity_check` failed between a payload commit and §4 — both expected mid-way, both resolved.

**Done means:** both platforms rebuilt and committed with their digest records, §4 run on macOS or
Linux, every gate green, pushed to `security/lz4-1.10.0`, and the report in §6 written.

---

## 1. Both machines — before building

```bash
git clone https://github.cds.internal.unity3d.com/unity/com.unity.usd-toolkit.git   # or reuse a clone
cd com.unity.usd-toolkit
git lfs install
git fetch origin
git checkout security/lz4-1.10.0
git pull --ff-only
git lfs pull
```

Check all of these; any failure is a **STOP**:

```bash
git status --porcelain                          # must print nothing
git lfs fsck --pointers                         # no pointer files where binaries should be
git check-attr text -- Native~/patches/openusd-26.05-lz4-1.10.0.patch   # "text: unset"
python3 Native~/verify_upstream_sources.py --platform <windows|linux> --list
#   must list three rows: OpenUSD 26.05, oneTBB, OpenUSD-patch lz4-1.10.0
python3 -m unittest discover -s "Native~/Tests~" -p "test_*.py"          # OK (27 tests)
```

On Windows use `python` instead of `python3` throughout, and make sure it is a real Python 3
(`python --version`), not the Microsoft Store stub.

Then run **Gate 1** of `REBUILD_WINDOWS_LINUX.md` §4 (the eleven `grep -c` checks) — all must hold.

---

## 2. Windows x64

Prerequisites: `REBUILD_WINDOWS_LINUX.md` §3 (Visual Studio 2022 C++ workload, CMake, git-lfs,
Python 3). Run everything from an **x64 Native Tools Command Prompt for VS 2022**. `curl.exe`
must be on `PATH` (it ships with Windows 10+; `where curl`) — see pitfall P8.

### 2.1 The OpenUSD clone

Reuse `C:\Dev\OpenUSD` if it exists, otherwise clone it (`REBUILD_WINDOWS_LINUX.md` §5). It must be
the pinned commit and completely clean:

```bat
git -C C:\Dev\OpenUSD rev-parse HEAD            :: 2095fafafd033fa23386d7ec6d58c7cc33974518
git -C C:\Dev\OpenUSD remote get-url origin     :: https://github.com/PixarAnimationStudios/OpenUSD.git
git -C C:\Dev\OpenUSD status --porcelain        :: must print nothing
```

An earlier manual `build_usd.py` run leaves `build_scripts\__pycache__\` behind, which makes the
clone "modified". Delete just that folder (`rmdir /s /q C:\Dev\OpenUSD\build_scripts\__pycache__`)
and re-check. Anything else listed: **STOP**.

### 2.2 Build OpenUSD with the patch — into a NEW, short directory

```bat
python Native~\build_openusd.py --platform windows ^
  --openusd-src C:\Dev\OpenUSD --install C:\USD\u2605lz4
```

- Use a **new** directory. The old `C:\USD\OpenUSD-26.05-win-x64` has a version 1 stamp (or none)
  and is refused by every wrapper build now; `build_openusd.py` itself refuses a non-empty
  directory (P2). Keep the path **short** (P5).
- Do **not** add `--require-scan` (P11).
- Expect, in order: `ok OpenUSD 26.05: HEAD is the pinned commit`, `ok OpenUSD-patch ...
  matches the recorded digest`, `[0/3] Applying 1 pinned patch(es)`, `[1/3] ... oneTBB 2020.3 ...
  matches the recorded digest`, `[2/3] Running build_usd.py with downloads blocked`, then (after
  the build, ~30–60 min) `[3/3]`, three more `ok` lines and `ok wrote ...provenance.json`.
- Any `FAIL` line: **STOP** (P6–P10 cover the likely ones).

Check the install:

```bat
python Native~\verify_upstream_sources.py --platform windows --check-stamp C:\USD\u2605lz4
findstr /c:"Invalid chunk count" C:\USD\u2605lz4\lib\usd_ms.dll >nul && echo patched || echo NOT PATCHED
```

`--check-stamp` must print `ok ... install files unchanged since` (plus the `WARN ... without
--require-scan` line, which is expected). `NOT PATCHED`: **STOP**.

### 2.3 Build the payload

```powershell
Remove-Item -Recurse -Force Native~\build~ -ErrorAction SilentlyContinue
.\Native~\build_windows.ps1 -OpenUsdRoot C:\USD\u2605lz4 -RecordDependencyDigests -SkipSigning
```

- `-RecordDependencyDigests`: the OpenUSD tree changed, so the record is rewritten; it only works
  because the install carries a valid stamp (P13).
- `-SkipSigning`: the committed Windows DLLs are unsigned on purpose; CI's `sign_windows` signs
  them at release (`.yamato/README.md`). Never sign `tbb_usdrt.dll` (P12).
- Expect `Renamed OpenUSD monolithic usd_ms.dll -> usd_rt.dll ...` and `Renamed Intel tbb.dll ->
  tbb_usdrt.dll ...`.

### 2.4 Gates

Run `REBUILD_WINDOWS_LINUX.md` **Gates 3, 4, 5, 6** (Gate 6 = `import_uv_test` and `usdz_test`) and
the `security_test` (§9b), then the LZ4 test — build it as its header says (Windows section):

```bat
cd Native~\Tests~
cl /std:c++17 /EHsc /MD /DNOMINMAX lz4_bounds_test.cpp /I C:\USD\u2605lz4\include ^
    /link C:\USD\u2605lz4\lib\usd_ms.lib
set PATH=C:\USD\u2605lz4\lib;C:\USD\u2605lz4\bin;%PATH%
lz4_bounds_test.exe                              :: must end with PASS (0 failures)
```

Intel's signature must have survived:

```powershell
(Get-AuthenticodeSignature Runtime\Plugins\x86_64\Windows\tbb_usdrt.dll).SignerCertificate.Subject
# must contain "Intel Corporation"
```

### 2.5 Review, commit, push

Restore any `.meta` the install step deleted, then review (Gate 8):

```bash
git diff --name-only --diff-filter=D | grep '\.meta$' | xargs -r git checkout --
git status --porcelain
git diff Native~/dependency-digests/windows.sha256
```

Allowed to change: files under `Runtime/Plugins/x86_64/Windows/` and
`Native~/dependency-digests/windows.sha256`. **Nothing else** — in particular nothing under
`Runtime/Plugins/macOS/` or `x86_64/Linux/`, and no `Runtime/Native/NativeRuntimeHashes.g.cs` or
`ThirdPartyNotices~/sbom.cdx.json` (those are §4, never on Windows — P15).

In `windows.sha256` (91 entries) expect exactly one line to change: `lib/usd_ms.dll`, the compiled
OpenUSD library. (The 2026-09-29 rebuild changed 20, for a reason that is now gone — see §7. From
here on, a `build_openusd.py` install should move only `lib/usd_ms.dll`.) The five `bin/tbb*.dll` are Intel's prebuilt DLLs from the pinned archive and are
byte-identical every time, and the plugin descriptors under `lib/usd`, `plugin`, `share`,
`resources` come from the same OpenUSD source — none of them may change. If anything besides
`lib/usd_ms.dll` changed, or an entry was added or removed: **STOP** (P17). No `Runtime/Plugins/x86_64/Windows/.unverified-build` may
exist (P14).

```bash
git add Runtime/Plugins/x86_64/Windows Native~/dependency-digests/windows.sha256
git lfs status                                   # the DLLs listed as LFS objects
git commit -m "build(native): rebuild Windows x64 payload with the LZ4 1.10.0 OpenUSD patch"
git push origin security/lz4-1.10.0
```

Commit as yourself; do not add tool-attribution trailers.

---

## 3. Linux x64

Prerequisites: `REBUILD_WINDOWS_LINUX.md` §3 — g++ 11+, CMake, `patchelf`, binutils, Python 3,
git-lfs. **Build on Ubuntu 24.04** (the documented floor, glibc 2.38) unless you deliberately
lower it with a 22.04 container (P22).

`build_linux.sh` stops at once if any of `cmake python3 patchelf strip strings ldd` is missing, so
check them up front, and compare the versions with the `"Linux x64"` line of `TOOLCHAIN` in
`Native~/generate_sbom.py` (Ubuntu 24.04, g++ 13.3.0, CMake 3.28.3, patchelf 0.17). A difference
is not a STOP; write it down for §4, which updates that line.

```bash
for t in cmake python3 patchelf strip strings ldd g++ git-lfs; do command -v "$t" >/dev/null || echo "MISSING $t"; done
lsb_release -ds; g++ --version | head -1; cmake --version | head -1; patchelf --version
ldd --version | head -1                          # glibc of this machine = floor of the payload
```

§1's unit tests must report `OK (27 tests)` with **no** skip here: the one test that skips on
Windows (`test_symlinked_stamp_is_refused`, no symlink privilege) runs on Linux.

### 3.1 Paths without a user name

OpenUSD bakes the path of every compiled source into `libusd_ms.so`, which ships as-is. The
install must not be under `$HOME` or `/tmp` — `build_openusd.py` refuses both, and
`build_linux.sh` fails on any `/home/` string in the payload.

```bash
sudo mkdir -p /opt/usd-26.05-lz4 && sudo chown "$(id -u):$(id -g)" /opt/usd-26.05-lz4
```

### 3.2 The OpenUSD clone

Reuse `/opt/usd-26.05/src` if it exists, otherwise `git clone --branch v26.05
https://github.com/PixarAnimationStudios/OpenUSD.git /opt/usd-26.05/src`. Same checks as §2.1:
HEAD `2095fafafd033fa23386d7ec6d58c7cc33974518`, the Pixar origin, `git status --porcelain` empty
(delete a leftover `build_scripts/__pycache__/` only).

### 3.3 Build OpenUSD with the patch

```bash
python3 Native~/build_openusd.py --platform linux \
  --openusd-src /opt/usd-26.05/src --install /opt/usd-26.05-lz4/install
python3 Native~/verify_upstream_sources.py --platform linux --check-stamp /opt/usd-26.05-lz4/install
strings -a /opt/usd-26.05-lz4/install/lib/libusd_ms.so | grep -c 'Invalid chunk count'   # >= 1
strings -a /opt/usd-26.05-lz4/install/lib/libusd_ms.so | grep -c '/home/'               # 0
```

Same expected output and STOP rules as §2.2. Do not reuse `/opt/usd-26.05/install` (P1).

### 3.4 Build the payload

```bash
Native~/build_linux.sh --openusd-root /opt/usd-26.05-lz4/install --record-dependency-digests
```

**This is the first real run of the rewritten `build_linux.sh`** (it was only syntax-checked on
macOS; see P20). It checks the stamp, verifies/records the digests with `--scan`, builds in a clean
`Native~/build~/linux-x64`, assembles the payload, sets rpaths, strips, and checks `ldd` and
`/home/`.

### 3.5 Gates

`REBUILD_WINDOWS_LINUX.md` **Gates 3, 4, 5, 6** (`import_uv_test`, `usdz_test`) and `security_test`,
then the LZ4 test, which on Linux runs against the shipped payload itself. The test sources only
spell out the macOS commands; the Linux ones are below. Build the binaries outside the repo (here
`$RUN`) so nothing lands in `Native~/Tests~`:

```bash
REPO=$(pwd)
PLUGINS="$REPO/Runtime/Plugins/x86_64/Linux"
RUN=$(mktemp -d)
cp Native~/Tests~/import_uv_fixture.usda Native~/Tests~/security_fixture.usda \
   Native~/Tests~/security_inside.txt "$RUN"/
cd "$RUN"
for t in import_uv_test usdz_test security_test; do
  g++ -std=c++17 -pthread "$REPO/Native~/Tests~/$t.cpp" -I "$REPO/Native~/include" \
      -L"$PLUGINS" -lUnityUSDToolkitNative -Wl,-rpath,"$PLUGINS:$PLUGINS/lib" -o "$t"
done
g++ -std=c++17 -pthread "$REPO/Native~/Tests~/lz4_bounds_test.cpp" \
    -I /opt/usd-26.05-lz4/install/include \
    "$PLUGINS/lib/libusd_ms.so" -Wl,-rpath,"$PLUGINS/lib" -o lz4_bounds_test
export PXR_PLUGINPATH_NAME="$PLUGINS/lib/usd:$PLUGINS/plugin/usd"
./import_uv_test import_uv_fixture.usda          # "API 5, fixture ..." then PASS
./usdz_test                                      # three RUsd_* lines then PASS
./security_test security_fixture.usda            # 9 x ok, PASS (0 failures)
./lz4_bounds_test                                # 8 x ok, PASS (0 failures)
cd "$REPO"
```

`lz4_bounds_test` prints `Runtime Error: ... Failed to decompress data ...` lines on stderr: those
are the refusals it tests for, not failures. Its verdict is the last line.

Record the glibc floor you built with: `objdump -T Runtime/Plugins/x86_64/Linux/lib/libusd_ms.so |
grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1`.

### 3.6 Review, commit, push

As §2.5 with `x86_64/Linux` and `linux.sha256`. `build_linux.sh` keeps `.meta` files, so none
should be deleted. In `linux.sha256` expect `lib/libusd_ms.so` and `lib/libtbb.so.2` to change
(TBB is compiled from source on Linux, as on macOS, where the equivalent four dylibs changed and
nothing else), `lib/libtbb.so` possibly (it is a small linker script or symlink), and **no plugin
descriptor**; a descriptor change, or an added or removed entry, is a **STOP** (P17). The file has
88 entries now and must still have 88.

Cross-check from the Windows rebuild: Windows re-recorded 19 text files (the `codegenTemplates`
and five `.glslfx`) whose previous record came from a CRLF checkout, and their new LF hashes are
**exactly the ones already in `linux.sha256`** (e.g. `a463250b…` for
`lib/usd/usd/resources/codegenTemplates/CMakeLists.txt`). So on Linux those lines must not move.
If any descriptor does change, still STOP, but put two facts in the report for each one: whether
the old and new install copies differ only in line endings
(`cmp <(tr -d '\r' < /opt/usd-26.05/install/<f>) <(tr -d '\r' < /opt/usd-26.05-lz4/install/<f>)`),
and whether the new copy equals the upstream blob
(`git -C /opt/usd-26.05/src show 2095faf:<source path> | sha256sum`). The source path is not the
install path — e.g. `lib/usd/usd/resources/codegenTemplates/api.h` comes from
`pxr/usd/usd/codegenTemplates/api.h`; do not match by file name, many share it.

```bash
git add Runtime/Plugins/x86_64/Linux Native~/dependency-digests/linux.sha256
git commit -m "build(native): rebuild Linux x64 payload with the LZ4 1.10.0 OpenUSD patch"
git push origin security/lz4-1.10.0
```

---

## 4. Finish — once, on macOS or Linux, after both platforms are pushed

Never on Windows (P15). Windows is already pushed, so the Linux machine can go straight on to this
section after §3.6. The Windows half of Gate 7 (step 2) still needs the Windows machine — close
every Editor that has the package loaded before the payload is replaced there (P19).

```bash
git pull --ff-only && git lfs pull && git lfs fsck --pointers
python3 Native~/generate_native_hashes.py --check-attributes
python3 Native~/generate_native_hashes.py
python3 Native~/generate_sbom.py
git diff --stat Runtime/Native/NativeRuntimeHashes.g.cs ThirdPartyNotices~/sbom.cdx.json
```

Only Windows and Linux entries may change in `NativeRuntimeHashes.g.cs`; a macOS change means the
LFS copy was incomplete (**STOP**). If a build machine's toolchain differs from `TOOLCHAIN` in
`Native~/generate_sbom.py` (compiler, CMake, OS, glibc), update that constant first and rerun
`generate_sbom.py`; if the Linux glibc floor changed, update the Linux requirement in `README.md`
and both user manuals too.

Then:

1. `python3 -m unittest discover -s "Native~/Tests~" -p "test_*.py"` — OK.
2. `REBUILD_WINDOWS_LINUX.md` **Gate 7** in the Unity Editor **on Windows and on Linux** (restart
   the Editor first — P19): `GetRuntimeInfo()` → `API=5 OpenUSD=0.26.5`, the McUsd numbers, and
   the usdz round trip. Also export a scene to `.usdc` and import it back; mesh/vertex/triangle
   counts must match. (Done on macOS: 8 meshes, 2156 vertices, 3120 triangles both ways.)
3. `BUILD_NOTES.md`: record for each platform the OpenUSD install path, toolchain, glibc (Linux)
   and gate results, and remove the "payloads are stale" warnings that no longer apply.
4. Commit (`chore: regenerate native manifests and SBOM for the LZ4 1.10.0 payloads`), push, and
   open the PR to `main`. CI `integrity_check` must be green on it.

---

## 5. Pitfalls — read before starting

**P1. An existing OpenUSD install is refused.** Stamps before version 3 are rejected by every
wrapper build (they do not record the patch), and a hand-edited stamp fails the re-hash. Build a
new install with `build_openusd.py`; never edit or copy a stamp.

**P2. "is not empty".** `build_openusd.py` requires a new or empty install directory, because
`build_usd.py` silently reuses an extracted archive or an installed dependency it finds there.
Pick a new directory or delete the whole old one — do not delete only parts of it.

**P3. "modified or untracked path(s)" on the OpenUSD clone.** Almost always
`build_scripts/__pycache__/` from an earlier direct `build_usd.py` run; delete that folder. Any
other change: STOP — do not `git stash`/`git clean` a clone you did not inspect.

**P4. Paths with a user name.** Linux: install under `/opt`, never `$HOME` or `/tmp`
(`build_openusd.py` refuses; `build_linux.sh` fails on `/home/` in the payload). The clone may be
anywhere, because what is compiled is the worktree inside the install. Windows is exempt (MSVC
records `__FILE__` relative).

**P5. Windows path length.** OpenUSD's build tree is deep and lands inside the install directory
(`<install>\build\openusd-src\...`). Keep the install path short (`C:\USD\u2605lz4`); if you still
see "path too long" / missing-file errors, enable long paths
(`git config --system core.longpaths true` and the `LongPathsEnabled` registry setting) and start
again in a fresh directory.

**P6. `OpenUSD-patch ... is <digest>` / patch digest mismatch.** The patch file was changed or
converted. `git check-attr text -- Native~/patches/*.patch` must say `unset`; re-checkout the file
(`git checkout -- Native~/patches/openusd-26.05-lz4-1.10.0.patch`). Never re-pin the digest to make
it pass.

**P7. "does not apply".** `build_openusd.py` applies the patch with line-ending conversion
disabled, so `core.autocrlf=true` (Git for Windows' default) is handled. If it still does not
apply, the clone is not at `2095faf` — STOP.

**P8. A download fails with a connection error to `127.0.0.1:9`.** That is the block working:
`build_usd.py` tried to fetch something no record pins. STOP and report which URL — do not remove
the proxy variables. On Windows the block only covers `curl`; if `curl.exe` is missing,
`build_usd.py` falls back to PowerShell, which ignores it, and only the after-build check
("holds files no record pins") catches an extra download. So make sure `curl` is on `PATH`.

**P9. `oneTBB ...: <file> is <digest>, the record pins <digest>`.** The downloaded archive is not
the pinned one. For the Linux/macOS `github-archive` rows GitHub has regenerated archives before,
so this is not by itself proof of tampering — but it is still a STOP. Do not re-pin; report it.
The Windows row is a release asset and should never change.

**P10. TLS/proxy errors reaching GitHub.** Download the exact URL from `--list` another way and
pass it with `--archive <file>`; it is checked against the record the same way.

**P11. `--require-scan` fails.** No upstream source scan has been recorded yet
(`Native~/security-scans/README.md`, "Status"). Leave the flag off; the stamp records that and the
wrapper build prints a WARN. This is a known outstanding item for the release, not something to
fake with an empty record.

**P12. Signing.** Windows: build the committed payload with `-SkipSigning`; CI signs
`UnityUSDToolkitNative.dll` and `usd_rt.dll` at release and must never sign `tbb_usdrt.dll`, whose
Intel signature is the strongest provenance evidence in the payload. Linux has no binary signing.

**P13. `refusing to record dependency digests for a tree of unverified origin`.** The install has
no valid stamp — you pointed the wrapper at an old install. Use the one from §2.2/§3.3.

**P14. `.unverified-build` in the payload.** It means a build ran with
`--skip-source-provenance`/`-SkipSourceProvenance`. Never commit such a payload; rebuild without the
flag (the marker is removed by the next verified build).

**P15. Never run `generate_native_hashes.py` or `generate_sbom.py` on Windows.** A Windows checkout
can hold CRLF-converted bytes while `git status` is clean, and the manifest would then reject the
payload everywhere else. §4 does it on macOS or Linux.

**P16. LFS.** Run `git lfs pull` before anything and check `git lfs status` before committing:
binaries must go in as LFS objects. `generate_native_hashes.py` refuses LFS pointer files.

**P17. Unexpected digest changes.** In the re-recorded `<platform>.sha256`, only compiled binaries
may change. A changed plugin descriptor, a new or removed file, or (Windows) a changed `tbb.dll`
means the install is not the tree the record describes — STOP and report the diff. (For
reference, the macOS rebuild changed 4 of its 89 entries: the four compiled dylibs.)

**P18. `.meta` files.** `build_windows.ps1` clears and re-copies the payload's resource folders,
which deletes Unity's `.meta` files; restore them with the command in §2.5 before committing. A
deleted `.meta` makes Unity mint new GUIDs.

**P19. The Editor keeps the old native library.** Restart the Unity Editor after replacing a
payload, or Gate 7 shows the previous binary's behaviour.

**P20. `build_linux.sh` and (on Windows) `build_openusd.py` run for real here for the first time.**
They were unit-tested and syntax-checked on macOS, and `build_openusd.py` ran end to end on macOS.
If one fails for a scripting reason — not a gate — fix the smallest thing, keep every check intact,
commit the fix separately with the error it fixed, and note it in the report. Never make a gate
pass by removing or loosening it.

**P21. Intermediate CI failures are expected.** `integrity_check` fails between a payload commit
and §4, because the manifest is regenerated once at the end. Do not "fix" it by running the
generator on Windows or per platform.

**P22. Linux glibc floor.** Whatever distro you build on sets the minimum glibc of the payload.
Ubuntu 24.04 keeps today's floor (2.38). If you build elsewhere, record the new floor and update
`TOOLCHAIN`, `README.md` and the manuals in §4.

---

## 6. Report back

Per platform:

- the `build_openusd.py` summary lines (`ok ...` through `wrote ...provenance.json`);
- `GetRuntimeInfo()` output, Gate 4/5 results, `import_uv_test`, `usdz_test`, `security_test` and
  `lz4_bounds_test` output;
- the `git diff --stat` of `dependency-digests/<platform>.sha256` and which entries changed;
- toolchain versions (and the Linux glibc floor);
- anything that needed a STOP, or a script fix under P20.

Then, from §4: the manifest/SBOM diff summary, the Unity round-trip counts, and the PR link.

---

## 7. Windows results (2026-09-29)

Built on Windows 11 Pro 10.0.26200; VS Build Tools 2022 (MSVC 14.44), Windows SDK 10.0.26100,
CMake 4.3.3, Python 3.11.9 — identical to the `"Windows x64"` line of `TOOLCHAIN`, so §4 needs no
change there. Install: `C:\USD\u2605lz4`.

**`build_openusd.py`:** `ok OpenUSD 26.05: HEAD is the pinned commit` · `ok OpenUSD-patch
lz4-1.10.0 ... matches the recorded digest` · `[0/3]` · `[1/3] ok oneTBB 2020.3 ... matches` ·
`[2/3]` build · `[3/3] ok oneTBB 2020.3 ... matches` · `ok OpenUSD 26.05: HEAD is the pinned
commit` · `ok wrote ...provenance.json (91 install files recorded)`. `--check-stamp` ok (plus the
expected `--require-scan` WARN); `Invalid chunk count` present in the install's `usd_ms.dll` and in
the shipped `usd_rt.dll`.

**Gates:** 1 (after the `RUSD_MAX_UV_SETS` fix below) · 3 · 4, 25 `RUsd_*` exports · 5, all nine
literals · 6, `import_uv_test` `API 5` PASS and `usdz_test` PASS · `security_test` 9/9 PASS ·
`lz4_bounds_test` 8/8 PASS · `tbb_usdrt.dll` byte-identical to the install's `bin\tbb.dll`, Intel
Authenticode `Valid`. No `.unverified-build`; the 146 `.meta` files the install step deleted were
restored. Payload descriptors all still match `NativeRuntimeHashes.g.cs`; only
`UnityUSDToolkitNative.dll` and `usd_rt.dll` differ from it, which §4 regenerates.

**`windows.sha256`: 20 of 91 lines changed — a P17 STOP, reviewed and accepted.** Besides
`lib/usd_ms.dll`, 19 text files (`lib/usd/usd/resources/codegenTemplates/*` and five `.glslfx`)
changed line endings only. The previous record came from an install built directly out of
`C:\Dev\OpenUSD`, checked out CRLF by Git for Windows' `core.autocrlf=true`; `build_openusd.py`
builds from a worktree with conversion off, so they are now LF, byte-identical to their blobs at
`2095faf`, and equal to the hashes `linux.sha256` already records. No entry added or removed; the
five `bin/tbb*.dll` unchanged.

**Script fixes (P20), each its own commit:**

- `fcd82dd` — two `test_build_provenance` tests wrote their fake patch with `write_text()`, which
  is CRLF on Windows, so its digest never matched; the symlink test needs a privilege Windows does
  not grant by default and now skips when it cannot create the link.
- `969d579` — `generate_native_hashes.py --check-attributes` piped paths to `git check-attr` in
  text mode, so on Windows every path arrived as `<path>\r` and all 217 were reported exposed.
  Read-only check; CI (Ubuntu) was unaffected.
- Gate 1's `grep -c RUSD_MAX_UV_SETS ... # >= 3` returned 2 after `e9050b7` translated the
  header's comments; the check now matches the definition itself.

Windows Gate 7: done, see §9.

---

## 8. Linux results and §4 (2026-09-30)

Built on Ubuntu 24.04.5 LTS (glibc 2.39); g++ 13.3.0, CMake 3.28.3, patchelf 0.17.2, Python
3.12.3 — identical to the `"Linux x64"` line of `TOOLCHAIN`, so §4 needs no change there. Install:
`/opt/usd-26.05-lz4/install`, from `/opt/usd-26.05/src` (HEAD `2095faf`, Pixar origin, clean).
§1: `OK (27 tests)`, no skip; Gate 1 all eleven hold.

**`build_openusd.py`:** `ok OpenUSD 26.05: HEAD is the pinned commit` · `ok OpenUSD-patch
lz4-1.10.0 ... matches the recorded digest` · `[0/3]` · `[1/3] ok oneTBB 2020.3.1 ... matches` ·
`[2/3]` build · `[3/3] ok oneTBB 2020.3.1 ... matches` · `ok OpenUSD 26.05: HEAD is the pinned
commit` · `ok wrote ...provenance.json (88 install files recorded)`. `--check-stamp` ok (plus the
expected `--require-scan` WARN); `Invalid chunk count` ×1 and `/home/` ×0 in the install's
`libusd_ms.so`.

**`build_linux.sh`:** first real run, exit 0, no script fix needed (P20). `recorded 88 dependency
digests`.

**Gates:** 1 · 3, `ldd` resolves through `$ORIGIN` (RUNPATH `$ORIGIN:$ORIGIN/lib` / `$ORIGIN`) ·
4, 25 `RUsd_*` exports incl. the seven added after ABI 2 · 5, all nine literals · 6,
`import_uv_test` `API 5` PASS and `usdz_test` PASS · `security_test` 9/9 PASS (run from a copied
directory) · `lz4_bounds_test` 8/8 PASS against the shipped `libusd_ms.so`. `/home/` ×0 in all
three shipped ELF files; no `.unverified-build`; no `.meta` deleted.

**glibc floor:** `GLIBC_2.38` / `GLIBCXX_3.4.32` (`libusd_ms.so`; wrapper `GLIBC_2.32`, TBB
`GLIBC_2.34`) — unchanged, so `README.md` and the manuals stay as they are.

**`linux.sha256`: 2 of 88 entries changed** — `lib/libusd_ms.so` and `lib/libtbb.so.2`, as
expected. No descriptor moved (the `codegenTemplates` / `.glslfx` lines included), none added or
removed. `libUnityUSDToolkitNative.so` came out byte-identical to the committed one (it was
already ABI 5), so the payload commit `12378b3` does not touch it.

**§4:** `--check-attributes` ok (218 files exempt from conversion). `NativeRuntimeHashes.g.cs`
(218 entries): exactly four lines changed — `linux/lib/libtbb.so.2`, `linux/lib/libusd_ms.so`,
`windows/UnityUSDToolkitNative.dll`, `windows/usd_rt.dll`; no macOS entry and no descriptor. The
SBOM changed by the same four hashes. Unit tests `OK (27 tests)` after regeneration.

Gate 7 on Linux: §10.

---

## 9. Gate 7 on Windows, and a usdz regression it found (2026-09-30)

Run headless in a scratch project that embeds this repo through a junction (Unity 6000.4.10f1,
Built-in render pipeline, Editor started fresh so the rebuilt DLLs were loaded), with McUsd.usda:

| check | result |
| --- | --- |
| `GetRuntimeInfo()` | `API=5 OpenUSD=0.26.5`, digest check passing |
| McUsd meshes / vertices / triangles | 23 / 1385 / 880 (sic — McUsd has 1760 points and no sharing, so this can only be the runbook figure copied; Linux measured 1760, §10) |
| per-vertex UVs / normals | 23/23 / 23/23 |
| albedo textures / cutout / blended | 23/23 / 8 / 1 (`purple_stained_glass`), no console warning |
| `.usdz` written, `PK`, no `_textures` left | yes |
| `.usdz` re-import | 23 meshes, 23/23 textures, no console warning — **after the fix below** |
| `.usdc` export → re-import | mesh / vertex / triangle counts identical |
| negative case: same stage, `McUsd_materials` a junction | all 66 textures refused, 0/23 loaded |

Built-in stores the albedo in `_MainTex` and marks cutout/blend by render queue (2450 / 3000),
not in URP's `_BaseMap` / `_AlphaClip` / `_Surface`; count through those on a Built-in project, or
every material looks untextured.

**The `.usdz` re-import first failed: 0/23 textures, 66 × `texture path resolves outside the stage
folder, skipping: 0/<name>.png`.** Not the LZ4 work and not a payload: `a642082` (the symlink
check, on `main` since PR #12) refused any path component it could not stat, and a packaged
texture's `0/tex.png` names nothing on disk, so every `.usdz` texture on every platform was refused
before the package resolver was asked. The last usdz round trip that passed (2026-09-22) predates
that commit and checked UVs only. Fixed in `Runtime/UsdImporter.cs` on this branch (see
CHANGELOG, *Fixed*): a missing component is skipped, its parent still checked; the junction case
above proves the link defence still holds. Managed code only — **no payload, digest record,
manifest or SBOM changes**, and no rebuild on any platform.

The export loses cutout/blend (8/1 → 0/0 on re-import): the exporter writes a constant `opacity`
only, no opacity texture or `opacityThreshold`. That predates this branch and is out of its scope.

**Still to do:** ~~Gate 7 on Linux~~ (done, §10), ~~and the usdz round trip on macOS~~ (done, §11) (its Gate 7 predated this fix
and did not check usdz textures) — both must show the re-imported `.usdz` with the same texture
count as the source and no `resolves outside the stage folder` warning. Then the PR (§4 step 4).

---

## 10. Gate 7 on Linux (2026-09-30)

Run headless (`-batchmode -nographics -executeMethod`, fresh Editor process each run, so the
rebuilt payload was the one loaded) in a scratch project that references this repo with `file:`,
Unity 6000.4.11f1, Built-in render pipeline, at `7278fad` (the usdz fix included). McUsd from
<https://github.com/erich666/McUsd> at `fc78536`, `model/McUsd.usda` with `McUsd_materials/`.

| check | result |
| --- | --- |
| `GetRuntimeInfo()` | `API=5 OpenUSD=0.26.5`, digest check passing, loaded from `Runtime/Plugins/x86_64/Linux` |
| McUsd meshes / vertices / triangles | 23 / **1760** / 880 |
| per-vertex UVs / normals | 23/23 / 23/23 |
| albedo textures / cutout / blended | 23/23 / 8 / 1, no console warning |
| `.usdz` written, `PK\x03\x04`, no `_textures` left | yes |
| `.usdz` re-import | 23 meshes, **23/23 textures**, no console warning, no `resolves outside the stage folder` |
| `.usdc` export → re-import | 23 / 1760 / 880 both ways |
| negative case: same stage, `McUsd_materials` a **symlink** | 0/23 textures loaded, 67 × `texture path resolves outside the stage folder`, no other warning |

**1760, not 1385.** McUsd authors 1760 points over 23 meshes, all `vertex`-interpolated, with 1760
face-vertex indices — no point is shared, so no import can produce fewer, and every McUsd revision
since 2022-08 has the same 1760. `49a11df` (2026-09-11, Windows) already measured 1760 and noted
that the runbook's 1385 was unreachable; the runbook table is corrected here, and §9's Windows row
repeats the old figure.

As on Windows, the round trip drops cutout/blend (8/1 → 0/0): the exporter's constant-`opacity`
limitation, out of scope. The warnings from OpenUSD itself (`MaterialBindingAPI is not applied`)
go to stderr, not to the Unity console.

Still to do: ~~the usdz round trip on macOS (§9)~~ (done, §11), then the PR (§4 step 4).

---

## 11. Gate 7 on macOS, with the usdz fix (2026-09-30)

Run headless (`-batchmode -executeMethod`, fresh Editor process) in a scratch project that
references this repo with `file:`, Unity 6000.4.10f1, **Built-in** render pipeline (URP removed from
the scratch manifest so the numbers compare with §9/§10), at `0920d0d`. McUsd at `fc78536`,
`model/McUsd.usda` with `McUsd_materials/`.

| check | result |
| --- | --- |
| `GetRuntimeInfo()` | `API=5 OpenUSD=0.26.5`, digest check passing |
| McUsd meshes / vertices / triangles | 23 / 1760 / 880 |
| per-vertex UVs / normals | 23/23 / 23/23 |
| albedo textures / cutout / blended | 23/23 / 8 / 1 (`purple_stained_glass`), no console warning |
| `.usdz` written, `PK\x03\x04`, no `_textures` left | yes (5,527,870 bytes) |
| `.usdz` re-import | 23 / 1760 / 880, **23/23 textures**, no warning, no `resolves outside the stage folder` |
| `.usdc` export → re-import | 23 / 1760 / 880 and 23/23 textures both ways |
| negative: `McUsd_materials` a symlink to the real folder | 0/23 loaded, 67 × `resolves outside the stage folder` |
| negative: every texture a **dangling** symlink | 0/23 loaded, 67 × `resolves outside the stage folder` |
| negative: every texture a symlink to the real file outside the stage folder | 0/23 loaded, 67 × `resolves outside the stage folder` |

The dangling-symlink case is the one `7278fad` could have opened: that fix lets a path component
that does not exist pass, so a link whose target does not exist must still be seen as a link, not
as "missing". On macOS it is — `File.GetAttributes` reports the link itself (lstat), so the
`ReparsePoint` check fires before the not-found branch is reached. Windows (§9) and Linux (§10)
ran the folder-link case; the dangling case was checked on macOS only.

As on the other platforms, the round trip drops cutout/blend (8/1 → 0/0): the exporter's
constant-`opacity` limitation, out of scope.

All three platforms have now passed Gate 7 with the usdz fix. Left: the PR (§4 step 4).

