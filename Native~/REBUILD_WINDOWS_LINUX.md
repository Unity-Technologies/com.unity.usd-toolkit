# Rebuilding the native plugin for Windows and Linux — package 0.7.2 / native ABI 5

**Task:** build `UnityUSDToolkitNative` from the source in this repository for **Windows x64** and
**Linux x64**, and replace the payloads under `Runtime/Plugins/x86_64/`.

This is written to be executed step by step on each platform. Every step that can go wrong has an
explicit pass/fail gate. **Do not skip the gates, and stop where a gate says stop.** macOS is
already built and verified; do not touch `Runtime/Plugins/macOS/`.

One machine per platform: Windows needs Visual Studio, Linux needs a glibc old enough for your
target distros. Nothing here is cross-compiled.

> **Rebuilding for the LZ4 patch (branch `security/lz4-1.10.0`)?** Start from
> `Native~/HANDOFF_LZ4_WINDOWS_LINUX.md`. It is the task, in order, for both machines, with the
> commands, the expected output and the pitfalls; it points back here for the gates.

---

## 1. Why this is needed

> **Superseded status (2026-09-30):** both platforms are now ABI 5 and carry the LZ4 1.10.0
> OpenUSD patch — see `Native~/HANDOFF_LZ4_WINDOWS_LINUX.md` §0. The table below is the state
> *before* those rebuilds, kept for what each feature row means.

The two platforms are at different points, so read the column that applies to you:

- **Windows — ABI 4**, rebuilt 2026-09-11 for this release's import work and verified against every
  gate below. It is missing only the two **0.7.2 / ABI 5** usdz entry points, so that rebuild is
  a short pass: nothing about the toolchain or the OpenUSD build changes, and §2 no longer
  applies to it. Go through Gates 1, 3, 4, 5, 6 (both tests) and 7 (the usdz round trip).
- **Linux — ABI 2**, still the original payload, built before *all* of this release's import work.
  It needs the full run below, §2 included.

Both still load — the managed version check is a *minimum* (`UsdNative.MinimumApiVersion` = 2),
not an exact match — so imports keep working; what the native layer gained since their build is
silently unavailable.

| Fix / feature | Linux (ABI 2) | Windows (ABI 4) |
| --- | :--: | :--: |
| `faceVarying` and *indexed* UVs (`primvars:st:indices`) — i.e. most DCC output; currently imports with **no UVs at all** | **needed** | done |
| Normals authored as `primvars:normals`, and `faceVarying` normals | **needed** | done |
| Extra UV sets `st1`/`st2` → Unity `uv1`/`uv2` (ABI 3 entry points) | **needed** | done |
| Authored `opacity` — alpha cutout (`opacityThreshold` > 0) and transparency (== 0) | **needed** | done |
| Warnings for a material input that is connected but yields no texture path | **needed** | done |
| Every texture path comes back empty, so materials import as the flat `diffuseColor` constant (see §2) | **needed** | done |
| Export to `.usdz` (ABI 5 `RUsd_CreateUsdzPackage`) — without it the export throws with a message pointing here | **needed** | **needed** |
| Textures *inside* a `.usdz` (ABI 5 `RUsd_ReadImportAsset`) — without it a usdz imports geometry and flat material colours only | **needed** | **needed** |
| `UsdPreviewSurface` fallback colour no longer multiplied into the albedo texture | no — managed only | no — managed only |
| Native API version resolved on demand (v3–v5 features reachable from the import path) | no — managed only | no — managed only |

So a package update alone changes nothing here. The rebuild is the fix.

## 2. Windows: the *original* DLL was not built from this source

> Resolved for Windows by the 2026-09-11 ABI-4 rebuild — kept because it is the reason the
> string-literal gate (§6) exists, and because Linux has never been rebuilt since.

Independently of the ABI, the committed Windows DLL fails to read *any* texture path. Users see
untextured materials showing only the flat `diffuseColor` constant, with **no warning** in the
console. The cause is the trap documented in `BUILD_NOTES.md`:

> **Windows and Linux will hit the exact same bug if `IsHolding<pxrType>()` is reintroduced** —
> keep using `VtValueHoldsType(value, "TypeName")` + `UncheckedGet<T>()`.

`VtValue::IsHolding<T>()` compares `typeid`, which does **not** match across the
plugin ↔ monolithic-OpenUSD boundary because the wrapper is built with
`CXX_VISIBILITY_PRESET hidden` (`Native~/CMakeLists.txt`). It returns `false` silently — no error,
no diagnostic — so `inputs:file` is never read and the texture path stays empty.

The current source uses the correct `VtValueHoldsType(...)` form. A binary that predates the fix
does not contain the string literals that form requires, which is how you tell them apart (the
2026-09-11 Windows DLL passes this; the Linux payload does not):

```powershell
# Windows: the literals the fixed source must embed
$dll = "Runtime\Plugins\x86_64\Windows\UnityUSDToolkitNative.dll"
"SdfAssetPath","GfVec2f","GfVec3f","GfVec4f" | ForEach-Object {
  "{0,-14} {1}" -f $_, [bool](Select-String -Path $dll -Pattern $_ -Encoding ascii -Quiet)
}
```

On the DLL as committed today all four print `False`, while the control literals
(`UsdPreviewSurface`, `UsdUVTexture`, `diffuseColor`) print `True` — the check itself works, the
fix is simply not in the binary. Gate 5 below re-runs this on your rebuild and must print `True`.

## 3. Prerequisites

Both platforms:

- **CMake 3.20+** and **git** with **git-lfs** (`git lfs install` — the payloads are LFS objects).
- **Python 3.x** with `setuptools`, only to run OpenUSD's `build_scripts/build_usd.py`.
- ~15 GB free disk for the OpenUSD build tree.

Windows:

- **Visual Studio 2022** with the C++ workload. Run everything from an
  **x64 Native Tools Command Prompt for VS 2022** (or its PowerShell equivalent) so `cl.exe` and
  CMake are on `PATH`.

Linux:

- **g++ 11+** (C++17), `patchelf` (`pip install patchelf`), `binutils` (`nm`, `objdump`, `strip`).
- Build on the **oldest glibc you must support**. The current payload was built on Ubuntu 22.04
  (glibc 2.35, g++ 11.4.0) and requires **glibc ≥ 2.34** and `GLIBCXX_3.4.29`, which is the
  package's documented minimum for Linux (see `README.md` and the user manuals). Rebuilding on
  22.04 keeps that promise; building on a newer distro raises the floor, because the dependency
  comes entirely from the build host's headers, not from the source.

## 4. Get the repository

```bash
git clone https://github.cds.internal.unity3d.com/unity/com.unity.usd-toolkit.git
cd com.unity.usd-toolkit
git lfs install
git checkout security/lz4-1.10.0          # or the branch/tag carrying the SECURITY-282834 fixes
git lfs pull
```

**Gate 1 — you have the right source.** All eleven must be true:

```bash
grep -c 'kApiVersion = 5'            Native~/src/UsdExporter.cpp   # 1
grep -c '#define RUSD_MAX_UV_SETS 3' Native~/include/unity_usd_toolkit_native.h  # 1
grep -c 'RUsd_GetImportMaterialOpacity' Native~/include/unity_usd_toolkit_native.h  # >= 1
grep -c 'RUsd_CreateUsdzPackage'     Native~/include/unity_usd_toolkit_native.h  # >= 1
grep -c 'VtValueHoldsType'           Native~/src/UsdExporter.cpp   # >= 3
# DSO-unsafe only for OpenUSD value types: their typeid does not match across the
# plugin/monolithic boundary (hidden visibility). IsHolding<float>/<double> is fine, and
# two comments mention the pattern -- a bare grep for 'IsHolding<' returns 4 on good source.
grep -cE 'IsHolding<(Sdf|Gf|Vt|Tf|Usd)'  Native~/src/UsdExporter.cpp   # must be 0

# SECURITY-282834 — the fixes are internal, so nothing in the ABI version reveals
# their absence. A payload built without these is vulnerable and passes every other gate.
grep -c 'int64_t cursor'                   Native~/src/UsdExporter.cpp   # 1  (topology overflow)
grep -c 'IsResolvedAssetInsideStageRoot'   Native~/src/UsdExporter.cpp   # >= 2 (asset confinement)
grep -c 'MinimumApiVersion = 5'            Runtime/Native/UsdNative.cs   # 1  (ABI gate tightened)

# SECURITY-282834 — the OpenUSD patch (LZ4 1.10.0 + TfFastCompression bounds). It changes OpenUSD,
# not this source, so check it is pinned for your platform; Gate 10's lz4_bounds_test then proves
# it reached the binary.
grep -c '^OpenUSD-patch'                  Native~/dependency-sources/<platform>.tsv   # 1
grep -c 'LZ4_VERSION_MINOR   10'           Native~/patches/openusd-26.05-lz4-1.10.0.patch   # 1
```

If the `IsHolding<(Sdf|Gf|Vt|Tf|Usd)` count is non-zero, **stop**: you are on the wrong revision
(see §2). If any of the
SECURITY-282834 lines is 0, **stop** — you would ship a payload missing the security
fixes, and Gate 9's `security_test` is the only thing that would catch it, after the build.

## 5. Build OpenUSD (once per machine)

Monolithic, no Python, no imaging. **All three platforms use the public `v26.05` tag** (commit
`2095faf`) as of 2026-09-23 — macOS was realigned onto it from a non-public 26.08 drop so that
every payload is reproducible from a published commit (SECURITY-282834). Do not substitute a
different tag: matching versions across platforms is what makes the provenance claim checkable.

**Build it with `Native~/build_openusd.py`, not with `build_usd.py` directly.**
`Native~/dependency-sources/<platform>.tsv` pins the OpenUSD commit, the TBB archive and the Unity
patch to OpenUSD each payload is built from. `build_usd.py` passes `expectedSHA256` for Boost and for nothing else, so run
on its own it downloads TBB and compiles it before anything has looked at the archive — a check
afterwards only reports what has already run (SECURITY-282834, CWE-494). `build_openusd.py` puts
the check first:

1. the clone is verified: the pinned commit, from the recorded remote, unmodified — and the Unity
   patch `Native~/patches/openusd-26.05-lz4-1.10.0.patch` against its recorded SHA-256. The
   patch is applied to a worktree of the pinned commit inside the install (`openusd-src`), never
   to the clone. It replaces the LZ4 1.9.2 OpenUSD vendors with 1.10.0 and bounds-checks the
   sizes OpenUSD passes to it (SECURITY-282834, CVE-2021-3520);
2. every pinned archive is downloaded into the directory `build_usd.py` downloads into and checked
   against its recorded SHA-256 — a mismatch stops here, before anything is built;
3. `build_usd.py` runs with the recorded flags and finds the archive already there. Its downloads
   go through an unreachable proxy, so if it tries to fetch anything else the build fails;
4. afterwards the archives are re-checked, the download directory must hold nothing unpinned, the
   clone must still be clean, and the worktree must still be the pinned commit plus exactly the
   pinned patch;
5. only then is the install stamped. The stamp records that the archives were checked before the
   build, that the patch was applied, and the SHA-256 of every file a wrapper build copies out of
   the install.

The install directory must be new or empty: `build_usd.py` reuses an extracted archive or an
installed dependency it finds there without looking at it. `build_macos.sh`, `build_windows.ps1`,
`build_linux.sh` and `Native~/CMakeLists.txt` all refuse an install without a valid stamp, and
re-hash its files, so an install changed after its build is refused too. Stamps written before
this (version 1, by the old `verify_upstream_sources.py --stamp`, and version 2, from before the
patch) are refused: rebuild the install.

**Windows** (x64 Native Tools prompt):

```bat
git clone https://github.com/PixarAnimationStudios/OpenUSD.git C:\Dev\OpenUSD
cd C:\Dev\OpenUSD
git checkout v26.05

python <package>\Native~\build_openusd.py --platform windows ^
  --openusd-src C:\Dev\OpenUSD --install C:\USD\OpenUSD-26.05-win-x64 --require-scan
```

**Linux:**

Build from a path that contains **no user name** — `__FILE__` and `__PRETTY_FUNCTION__` bake the
absolute source and install paths into `libusd_ms.so` (536 strings), so a build under `$HOME`
ships the developer's user name to everyone who unpacks the package. Not `/tmp` either: the path
is permanent in the binary and `/tmp` is not. `/opt/usd-26.05` is what the shipped payload used,
and `build_openusd.py` refuses a path under the home directory or `/tmp` on Linux:

```bash
sudo mkdir -p /opt/usd-26.05 && sudo chown "$(id -u):$(id -g)" /opt/usd-26.05
git clone --branch v26.05 \
  https://github.com/PixarAnimationStudios/OpenUSD.git /opt/usd-26.05/src
cd /opt/usd-26.05/src && git log -1 --format=%h        # 2095faf

python3 <package>/Native~/build_openusd.py --platform linux \
  --openusd-src /opt/usd-26.05/src --install /opt/usd-26.05/install --require-scan
strings -a /opt/usd-26.05/install/lib/libusd_ms.so | grep -c '/home/'   # must be 0
```

**macOS:**

```bash
python3 <package>/Native~/build_openusd.py --platform macos \
  --openusd-src /Users/Shared/usd-26.05/src --install /Users/Shared/usd-26.05/install \
  --build-target universal --require-scan
```

It sets `MACOSX_DEPLOYMENT_TARGET=12.0` (`--deployment-target`), the value `build_macos.sh` builds
the wrapper with. The same no-user-name rule applies as on Linux — clang bakes the path into
`libusd_ms.dylib` 1134 times — so the script refuses an install under the home directory or `/tmp`;
`/Users/Shared/usd-26.05` is what the shipped payload uses. (The OpenUSD clone may live anywhere:
with the patch, what is compiled is the worktree inside the install.)

Drop `--require-scan` for local experiments and the stamp records that it was dropped, which
makes the wrapper build warn. A payload that will be committed needs it: it checks that
`Native~/security-scans/` holds a record for every pinned version, which is the review's
"scan the source before compiling it" requirement. See that directory's README.

If the build machine cannot reach GitHub directly, download the archive the record pins another
way and pass it with `--archive <file>`; it is checked against the record exactly as a download
would be. GitHub generates source archives on demand and has changed their compression before, so
a mismatch on a `github-archive` row is not by itself proof of tampering — but it still stops the
build, and the record is re-pinned deliberately, in review, after comparing the contents with the
pinned commit.

**Gate 2 — the right OpenUSD, from the pinned source.** `PXR_VERSION` must read `2605`, the
monolithic library must exist, and the install root must carry the provenance stamp:

```bash
grep 'define PXR_VERSION' <openusd-root>/include/pxr/pxr.h     # 2605
ls <openusd-root>/lib | grep -E 'usd_ms'                       # usd_ms.dll / libusd_ms.so
python3 Native~/verify_upstream_sources.py --platform <platform> \
  --check-stamp <openusd-root>                                 # exit 0
```

A version number says which release this claims to be; the stamp says which revision it was
actually built from and what the install held when it was. Only the second is evidence, which is
why the wrapper build scripts and `Native~/CMakeLists.txt` check it and not `PXR_VERSION`.

## 6. Build the wrapper — clean, every time

A stale CMake cache is the most common cause of "I rebuilt and nothing changed". Delete the build
tree first.

**Windows:**

```powershell
Remove-Item -Recurse -Force Native~\build~ -ErrorAction SilentlyContinue
.\Native~\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64
```

The script configures with `-A x64`, installs into `Runtime\Plugins\x86_64\Windows`, copies the
OpenUSD runtime DLLs and plugin resources, strips `.lib/.exp/.pdb`, and then **renames the
monolithic `usd_ms.dll` to `usd_rt.dll`**, patching the import name inside
`UnityUSDToolkitNative.dll` in place. That rename exists to avoid a base-name collision with
another package's OpenUSD (e.g. `com.unity.pixyz.sdk-plus`), which otherwise wins the load and
causes `DllNotFound` / `PROC_NOT_FOUND`. Expect the line
`Renamed OpenUSD monolithic usd_ms.dll -> usd_rt.dll ...` in the output.

**Linux:**

```bash
Native~/build_linux.sh --openusd-root /opt/usd-26.05/install
```

The script used to be a list of manual commands here, which left the provenance check to whoever
ran them; it now enforces the same gates as the other two platforms. It checks the stamp and the
dependency digests, deletes and reconfigures `Native~/build~/linux-x64` (clean every time), builds
with CMake, and assembles the payload under `Runtime/Plugins/x86_64/Linux/` in the committed shape
— the managed layer points `PXR_PLUGINPATH_NAME` at `lib/usd` and `plugin/usd`:

```
libUnityUSDToolkitNative.so        rpath $ORIGIN:$ORIGIN/lib
lib/libusd_ms.so                   rpath $ORIGIN
lib/libtbb.so.2
lib/usd/**                         (without usd/resources/codegenTemplates)
plugin/usd/**
```

It then sets those rpaths with `patchelf`, runs `strip --strip-unneeded` on the three binaries, and
fails if `ldd` reports anything "not found" or if any of the three contains a `/home/` path.
`Native~/CMakeLists.txt` adds `-ffile-prefix-map` for the package root (`/usd-toolkit`) and
`OPENUSD_ROOT` (`/openusd`), so the wrapper's own strings are neutral wherever the clone lives;
a `/home/` hit means OpenUSD itself was built under a home directory. Unity `.meta` files in the
payload are left in place.

It no longer builds the packman + Python layout (Isaac/Omniverse) it once targeted; that layout
was never what the package shipped, and its OpenUSD is not the pinned v26.05.

**Gate 3 — the build produced the binary.** Exit code 0 and:

- Windows: `Runtime\Plugins\x86_64\Windows\UnityUSDToolkitNative.dll` exists, **plus**
  `usd_rt.dll` and `tbb_usdrt.dll` next to it, and `lib\usd\plugInfo.json` + `plugin\usd\plugInfo.json`.
- Linux: the tree above, and `ldd` resolves everything through `$ORIGIN`.

**Gate 4 — the ABI-5 entry points are exported.** All 25 `RUsd_*` symbols must be present; these
seven are the ones added after ABI 2:

```powershell
# Windows
dumpbin /exports Runtime\Plugins\x86_64\Windows\UnityUSDToolkitNative.dll |
  Select-String -Pattern 'RUsd_GetImportMeshUvSetInfo|RUsd_CopyImportMeshUvSet|RUsd_GetImportMaterialOpacity|RUsd_CreateUsdzPackage|RUsd_ReadImportAsset|RUsd_GetImportNodeInfo|RUsd_CopyImportSubmeshes'
```

```bash
# Linux
nm -D --defined-only Runtime/Plugins/x86_64/Linux/libUnityUSDToolkitNative.so | grep -c RUsd_   # 25
nm -D --defined-only Runtime/Plugins/x86_64/Linux/libUnityUSDToolkitNative.so |
  grep -E 'RUsd_GetImportMeshUvSetInfo|RUsd_CopyImportMeshUvSet|RUsd_GetImportMaterialOpacity|RUsd_CreateUsdzPackage|RUsd_ReadImportAsset'
```

Missing `RUsd_GetImportMaterialOpacity` means you built source older than this release's
import work, and missing
`RUsd_CreateUsdzPackage` means it predates the usdz work — either way, go back to Gate 1.

**Gate 5 — the binary really contains the fixed source.** These literals only exist in the
DSO-safe form, and the UV/normal type names only exist in the 0.7.2 primvar reader:

| literal | why |
| --- | --- |
| `SdfAssetPath` | `VtValueHoldsType` texture-path read (§2) |
| `GfVec2f`, `GfVec3f`, `GfVec4f` | DSO-safe colour/vector reads |
| `texCoord2f[]`, `float2[]` | 0.7.2 UV primvar type dispatch |
| `normal3f[]` | 0.7.2 normal primvar type dispatch |
| `USD import: ` | the new import-warning prefix |
| `Failed to write the usdz package` | 0.7.2 usdz packaging (`RUsd_CreateUsdzPackage`) |

```powershell
# Windows
$dll = "Runtime\Plugins\x86_64\Windows\UnityUSDToolkitNative.dll"
"SdfAssetPath","GfVec2f","GfVec3f","GfVec4f","texCoord2f[]","float2[]","normal3f[]","USD import: ","Failed to write the usdz package" |
  ForEach-Object { "{0,-16} {1}" -f $_, [bool](Select-String -Path $dll -Pattern ([regex]::Escape($_)) -Encoding ascii -Quiet) }
```

```bash
# Linux
for lit in SdfAssetPath GfVec2f GfVec3f GfVec4f 'texCoord2f[]' 'float2[]' 'normal3f[]' 'USD import: ' 'Failed to write the usdz package'; do
  printf '%-16s ' "$lit"
  strings -a Runtime/Plugins/x86_64/Linux/libUnityUSDToolkitNative.so | grep -qF "$lit" && echo present || echo ABSENT
done
```

Every row must be `True`/`present`. **If any is missing, stop** — the binary is not built from
this source and shipping it reproduces the original bug.

## 7. Gate 6 — functional verification without Unity

`Native~/Tests~/import_uv_test.cpp` exercises exactly what this rebuild is for: faceVarying and
indexed UVs, vertex splitting, primvar aliases, `primvars:normals`, three UV sets, authored
opacity (cutout + blend), and the connected-but-unreadable-texture warning. It prints `PASS` and
exits 0, or lists the failed checks.

**Windows** (x64 Native Tools prompt, from `Native~\Tests~`). Link against the import library
from the build tree — the install step deliberately strips `.lib` out of the payload — and run the
harness next to a copy of the payload so the DLLs resolve:

```bat
set PLUGINS=..\..\Runtime\Plugins\x86_64\Windows
set IMPLIB=..\build~\windows-x64\Release\UnityUSDToolkitNative.lib

cl /std:c++17 /EHsc /I..\include import_uv_test.cpp /Fe:import_uv_test.exe /link %IMPLIB%

mkdir run & cd run
copy ..\import_uv_test.exe . & copy ..\import_uv_fixture.usda .
copy %PLUGINS%\*.dll .
xcopy /E /I /Y %PLUGINS%\lib lib
xcopy /E /I /Y %PLUGINS%\plugin plugin
set PXR_PLUGINPATH_NAME=lib\usd;plugin\usd
import_uv_test.exe import_uv_fixture.usda
```

**Linux** (from `Native~/Tests~`):

```bash
PLUGINS=$(cd ../../Runtime/Plugins/x86_64/Linux && pwd)
g++ -std=c++17 import_uv_test.cpp -I../include \
    -L"$PLUGINS" -lUnityUSDToolkitNative -Wl,-rpath,"$PLUGINS":"$PLUGINS/lib" -o import_uv_test
PXR_PLUGINPATH_NAME="$PLUGINS/lib/usd:$PLUGINS/plugin/usd" ./import_uv_test import_uv_fixture.usda
```

Expected:

```
API 5, fixture import_uv_fixture.usda
PASS
```

**Also run `Native~/Tests~/usdz_test.cpp`**, which covers the 0.7.2 additions: it packages a stage
it writes itself into a `.usdz`, reopens the package, and reads the packaged texture's bytes back
byte-for-byte. It takes no fixture argument and needs the same `PXR_PLUGINPATH_NAME`. Build it
exactly like `import_uv_test` (same compiler line, `usdz_test.cpp` in place of
`import_uv_test.cpp`) and expect:

```
RUsd_CreateUsdzPackage
RUsd_OpenStage on the package
RUsd_ReadImportAsset
PASS
```

`PXR_PLUGINPATH_NAME` is mandatory for a standalone host: without it OpenUSD aborts the process
with *"Failed to find the plugInfo.json file that declares the plugin for ArDefaultResolver"*.
Unity sets it itself at runtime, so this is a harness-only requirement.

> **`security_test` may be run from anywhere, including a `run\` copy of the fixture.** Its
> out-of-folder check needs an *existing* file one level above the stage — to prove an asset is
> refused on its **location** and not merely for being absent — and it creates and deletes that
> probe file itself, next to the fixture's parent. It used to read `../CMakeLists.txt`, which only
> existed while the fixture sat in `Native~/Tests~`; running from a copied directory then failed
> with `FAIL the refusal is reported as an out-of-folder rejection` on a perfectly healthy
> payload. If the probe cannot be created (read-only parent directory) the check reports `skip`
> rather than failing.

## 8. Gate 7 — verification in the Unity Editor

1. Point a project at this package (embedded or `file:` reference) and open it.
2. **Restart the Editor after replacing the binary.** Windows, Linux and macOS all keep a loaded
   native library for the process lifetime, so a rebuilt binary is *not* picked up by a running
   Editor — you will keep seeing the old behaviour and conclude the rebuild failed.
3. Check the loaded runtime:

   ```csharp
   var info = Unity.USDToolkit.UsdExporter.GetRuntimeInfo();
   Debug.Log($"API={info.NativeApiVersion} OpenUSD={info.OpenUsdVersion}");
   // expect: API=5 OpenUSD=0.26.5
   ```

   `API=2` means the old payload is still being loaded (wrong folder, or the Editor was not
   restarted).
4. Import a faceVarying/indexed USD and check UVs and textures. `McUsd.usda`
   (<https://github.com/erich666/McUsd>, keep `McUsd_materials/` next to it) is the reference
   case; these are the numbers the macOS build produces:

   | check | expected |
   | --- | --- |
   | meshes / vertices / triangles | 23 / 1760 / 880 (McUsd authors 1760 points with no sharing; an earlier 1385 here was wrong) |
   | meshes with per-vertex UVs | **23 / 23** (was 7/23 before the fix) |
   | meshes with per-vertex normals | **23 / 23** (was 0/23) |
   | materials with `_BaseMap` | 23 / 23 |
   | alpha-cutout materials (`_AlphaClip=1`, `_Cutoff=0.50`, queue 2450) | 8 |
   | alpha-blended material (`_Surface=1`, queue 3000) | 1 (`purple_stained_glass`) |
   | console warnings | none |

   Then check the usdz round trip, which is what 0.7.2 added:

   ```csharp
   // Export a usdz, then import it back: the textures must come out of the package.
   Unity.USDToolkit.UsdExporter.ExportGameObject(go, "/tmp/probe.usdz",
       new Unity.USDToolkit.UsdExportOptions { ExportTextures = true });
   var back = Unity.USDToolkit.UsdImporter.Import("/tmp/probe.usdz");
   ```

   | check | expected |
   | --- | --- |
   | the `.usdz` file is written and starts with `PK\x03\x04` | yes |
   | no `_textures` folder left next to it | yes (it is packaged, and the staging copy is removed) |
   | re-import mesh count / materials with `_BaseMap` | same as the source GameObject |

   ```csharp
   var r = Unity.USDToolkit.UsdImporter.Import(path,
       new Unity.USDToolkit.UsdImportOptions { ImportMaterials = true, ImportTextures = true });
   foreach (var mf in r.RootObject.GetComponentsInChildren<MeshFilter>(true))
       Debug.Log($"{mf.name} verts={mf.sharedMesh.vertexCount} uv={mf.sharedMesh.uv.Length}");
   ```

   `uv=0` means the UV fix is not in the loaded binary (go back to Gate 5). A material whose
   `_BaseMap` is null while the USD does reference textures means the texture-path read is still
   broken (§2) — and 0.7.2 now logs *why*, as `USD import: material '...' input 'diffuseColor'
   is connected to <...> but no texture file path could be read: ...`.

## 9. Gate 8 — review what changed before committing

```bash
git status --porcelain
```

Only your platform's payload may appear. Specifically:

- **Nothing under `Runtime/Plugins/macOS/` may change.** If it does, you ran the macOS script by
  mistake — `git checkout -- Runtime/Plugins/macOS`.
- **No `.meta` file may be deleted.** The install step copies OpenUSD's resource trees and can
  wipe the `.meta` files Unity generated for them (the macOS script deletes 149). Unity would
  regenerate them with **new GUIDs**, breaking references. Restore them:

  ```bash
  git diff --name-only --diff-filter=D | grep '\.meta$' | xargs -r git checkout --
  git diff --name-only --diff-filter=D            # must now print nothing
  ```
- No `.lib`, `.exp`, `.pdb`, or `codegenTemplates/` in the payload.
- `git lfs status` must list the rebuilt binary under "Objects to be committed" — the payloads are
  LFS-tracked via `.gitattributes`. A plain (non-LFS) binary commit is a mistake; re-run
  `git lfs install` and re-add.

## 9a. Gate 9 — record the dependency digests you are shipping

The build scripts copy the OpenUSD/TBB tree off this machine straight into `Runtime/Plugins`, so
a tampered local dependency tree would ride into the package unnoticed (SECURITY-282834,
CWE-347). Each script now verifies what it is about to copy against
`Native~/dependency-digests/<platform>.sha256` and **refuses to build** when that record is
missing or no longer matches.

On a rebuild the dependency tree legitimately changes, so record it deliberately and commit the
result — the diff is what makes a substitution visible in review:

```powershell
# Windows, as part of a build
.\Native~\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64 -RecordDependencyDigests `
    -SigningCertificateThumbprint <thumbprint>
```

A first record is trust-on-first-use, so recording requires the tree to carry a valid stamp from
`build_openusd.py` — the verifier's `--record` refuses anything else, and the build scripts refuse
`--record-dependency-digests` together with `--skip-source-provenance`. What becomes the reviewed
baseline is therefore always a tree whose origin was checked.

### Recording without a build

A record can also be taken straight from a dependency tree, with no compile and no change to the
payload — which is what you want when the record is simply missing for a platform, as
`windows.sha256` and `linux.sha256` were. `--scan` makes the verifier enumerate the tree itself
instead of being handed a file list:

```powershell
# Windows
python Native~\verify_dependency_digests.py --platform windows `
    --root C:\USD\OpenUSD-26.05-win-x64 --scan --record
```

```bash
# Linux
python3 Native~/verify_dependency_digests.py --platform linux \
    --root /opt/usd-26.05/install --scan --record
```

Then commit `Native~/dependency-digests/<platform>.sha256`. Nothing else changes: no binary is
rebuilt, so `Runtime/Native/NativeRuntimeHashes.g.cs` and the SBOM stay as they are.

Swap `--record` for `--verify` to check a tree against the committed record the same way a build
would. The scan rules live in `SCAN_RULES` in the verifier and mirror what each build copies —
`build_linux.sh` verifies with `--scan`, so for Linux they are the build's own list. They were
checked against the macOS record, which they reproduce exactly (89/89 files, same digests, no
build); the Windows rules are structurally identical but have not been run against a real tree,
so read the file count in the output before committing. If the rules ever drift from what a build
copies, the next real build fails its gate rather than shipping something unrecorded.

**Signing is no longer optional-by-default.** `build_windows.ps1` requires
`-SigningCertificateThumbprint` (Authenticode is applied *after* the in-place import patch, since
patching would invalidate an earlier signature) or an explicit `-SkipSigning`, and
`build_macos.sh` requires `--codesign-id`, `--adhoc-codesign` or `--skip-codesign`. A build that
cannot sign now fails instead of quietly producing an unattributable binary. Linux has no
equivalent OS signing; publish detached digests or signatures for the `.so` files alongside the
release instead.

## 9b. Gate 10 — regenerate the native digest manifest

The managed loader compares every shipped native binary against the SHA-256 digest recorded in
`Runtime/Native/NativeRuntimeHashes.g.cs` before the first P/Invoke (SECURITY-282834, CWE-494).
A rebuild produces a new binary with a new digest, so a stale manifest makes the package refuse
its own payload:

```bash
python3 Native~/generate_native_hashes.py   # macOS or Linux only -- see below
git diff --stat Runtime/Native/NativeRuntimeHashes.g.cs   # your platform's entries only
```

A payload built with the provenance gate skipped carries a `.unverified-build` marker in its
platform root, written by the CMake install step, and the generator refuses to write a manifest
while any platform has one — so does CI, which runs the same script. The next build that passes
the gate removes the marker.

**Generate the manifest on macOS or Linux, never on Windows** — even for a Windows rebuild. The
digests are of the bytes on disk, and a Windows checkout has a history of not holding the bytes
the repository stores: before `.gitattributes` marked the plugin descriptors `-text`,
`core.autocrlf=true` rewrote every `.usda` and `.glslfx` to CRLF while `git status` stayed clean.
A manifest generated there records CRLF digests, and the CI `integrity_check` (Ubuntu), the
published tarball and every macOS and Linux user then refuse the payload. So after a Windows
rebuild, commit the binaries, pull them on a macOS or Linux machine, and generate there. The
`-text` rule makes this safe today; the rule of thumb is what keeps it safe when a new file type
joins the payload before anyone thinks to mark it.

The generator hashes **every** platform's binaries, not just the one you rebuilt, so make sure the
other platforms' payloads are real content and not unfetched Git LFS pointer files before you run
it — it cannot tell the difference and would record the pointers' digests, breaking those
platforms at load time. `git lfs fsck --pointers` and a size check are enough. A single-platform
rebuild should change **only that platform's entries**; anything else in the diff means the LFS
working copy was incomplete.

The manifest now covers the plugin descriptors (`plugInfo.json`, `.usda`, `.glslfx`) as well as
the libraries, keyed by path relative to the platform's payload root, and the runtime verifier
**fails** on any unlisted file inside `lib/usd`, `plugin/usd` or `share/usd`. A stale plugin
directory left behind by a previous OpenUSD version therefore now breaks the load rather than
being silently blessed — `usdLuxValidators` (26.08 only) survived the Windows 26.05 rebuild
exactly this way, because the Windows copy did not clear its destination first. It does now.

The regenerated file goes in the **same commit** as the payload. Then confirm the check passes in
the Editor (Gate 7's project works): `Unity.USDToolkit.UsdExporter.GetRuntimeInfo()` must return
without throwing — a digest mismatch throws `UsdExportException` naming the offending file.

Also re-run the security regression test against the new payload:

```bash
cd Native~/Tests~   # build per the header comment in security_test.cpp
./security_test security_fixture.usda        # must print PASS (9 checks)
```

and the LZ4 bounds test, which proves the OpenUSD patch is in the shipped `libusd_ms` — the
wrapper tests above cannot, because the patch changes OpenUSD, not the wrapper:

```bash
cd Native~/Tests~   # build per the header comment in lz4_bounds_test.cpp
./lz4_bounds_test                            # must print PASS (8 cases)
```

Against a payload built without the patch it fails four cases, three of them by faulting on the
guard page placed after the hostile buffer.

## 10. Commit and push

One commit per platform, payload only:

```bash
git add Runtime/Plugins/x86_64/Windows        # or .../Linux
git commit -m "build(native): rebuild Windows x64 payload for 0.7.2 / ABI 5"
git push
```

Then update `BUILD_NOTES.md`: move that platform out of the "payloads are stale" warning under
*Current state* and record the OpenUSD version, ABI, toolchain and (Linux) glibc baseline you
built with, plus the gate results. Leave the other platform's warning in place until it is done
too.

**Report back** with: the `GetRuntimeInfo()` line, the Gate 5 table, the Gate 6 output, and the
McUsd numbers from Gate 7.

---

## Appendix A — troubleshooting

**`DllNotFound` / `PROC_NOT_FOUND` when Unity loads the plugin (Windows).** The `usd_ms` →
`usd_rt` rename did not happen, and another package's OpenUSD won the base-name race. Re-check the
rename line in the build output; the managed loader probes `usd_rt.dll` first, then `usd_ms.dll`
and `usd_m.dll`.

**The Editor shows the old behaviour after a successful Gate 6.** The Editor kept the previously
loaded library for the session. Restart it (Gate 7 step 2). Gate 6 bypasses Unity entirely and is
the authority on what the binary does.

**Standalone harness aborts with "Failed to find the plugInfo.json file ... ArDefaultResolver".**
`PXR_PLUGINPATH_NAME` is unset or points at the wrong folder (Gate 6).

**`git status` shows the whole payload changed.** You built against a different OpenUSD version.
Check `PXR_VERSION` (Gate 2) — 2605 for Windows/Linux.

**The build succeeds but Gate 5 still fails.** `Native~/build~` was not actually removed, or CMake
reused a cached configuration. Delete the whole directory and re-run. If it still fails, confirm
the source on disk passes Gate 1 — you may be building a different working copy than the one you
inspected.

**Linux: the plugin loads but every import fails.** Check `ldd` for absolute build paths; the
payload must resolve purely through `$ORIGIN`. Also confirm you did not ship a Python-enabled
build (no `libpython*` in the payload).

**Linux: `GLIBC_2.xx not found` on the target machine.** Rebuild on an older distro or in a
container matching the oldest glibc you support (§3).

## Appendix B — what this rebuild must not change

- The managed C# layer. It ships as source and is already correct for ABI 5; the version check is
  a minimum, so do not "fix" it to an exact match.
- `kApiVersion`. It is 5 in this source; changing it invalidates the compatibility story.
- The other platforms' payloads, and any `.meta` file.
- `Native~/Tests~/import_uv_fixture.usda`. Its two fake texture paths
  (`fixture_cutout.png`, `fixture_blend.png`) are intentional — the importer only reports the
  path and threshold, and the missing files exercise the "texture not found, skipping" warning.
- `Native~/Tests~/usdz_test.cpp`. It writes its own source stage and PNG into a temp directory on
  purpose, so the repo carries no binary fixture for it.
