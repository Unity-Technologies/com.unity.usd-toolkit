# Cross-Platform Build Notes

Records the macOS development state and the exact steps to rebuild the native plugin for
**Windows** and **Linux**. The managed (C#) code is platform-agnostic — only the native C++
wrapper must be rebuilt per platform.

## Current state (2026-09-11)

- **Package version:** `0.7.2-exp.1`
- **Native ABI version:** `5` (`RUsd_GetApiVersion()` in C++; C# checks
  `UsdNative.MinimumApiVersion`, which is `2`). The managed check is a **minimum**, not an exact
  match: ABI 3, 4 and 5 only *added* entry points (`RUsd_GetImportMeshUvSetInfo`,
  `RUsd_CopyImportMeshUvSet`, `RUsd_GetImportMaterialOpacity`, `RUsd_CreateUsdzPackage`,
  `RUsd_ReadImportAsset`), so a plugin reporting ABI 2 still loads and works — it just cannot
  deliver UV sets 1–2, authored opacity, usdz packaging or textures read out of a package, all of
  which C# skips based on `UsdNative.LoadedApiVersion`. ABI 1 is still rejected.
- **⚠️ The Windows payload is one ABI behind as of 0.7.2** (ABI 4: rebuilt 2026-09-11 for the
  import work in this release, before the usdz entry points existed). Everything it already does keeps
  working; what it cannot do is package `.usdz` (an export to that extension throws, naming the
  rebuild guide) or read a texture out of a package (such a stage imports geometry and flat
  material colours). Rebuilding it is a small, isolated follow-up — same guide, same gates.
- **⚠️ The Linux payload is stale as of 0.7.2** (still ABI 2, built before the UV
  import fix). It loads and imports, but every mesh whose UVs are `faceVarying` or indexed —
  most real DCC output — arrives with **no UVs**, `primvars:normals` is ignored, and authored
  opacity (alpha cutout / transparency) is not applied. Rebuilding it from the current source
  is the larger of the two open items: **`Native~/REBUILD_WINDOWS_LINUX.md`** is the step-by-step
  guide with gates for both platforms (`Native~/WINDOWS_REBUILD.md` remains as the record of the
  stale-DLL texture bug it supersedes). **Windows was rebuilt against the import work (ABI 4) on 2026-09-11**
  — see the Windows payload entry below.
- **Import UV/normal primvars (0.7.2, all platforms once rebuilt):** `BuildImportedMesh` resolves
  UVs and normals per face-corner (`constant`/`uniform`/`vertex`/`varying`/`faceVarying`, indexed
  or not) and splits vertices where corners disagree. Reading goes through `ReadVec2Array` /
  `ReadVec3Array`, which pick the `UncheckedGet` branch from the **registry type name**
  (`attr.GetTypeName().GetAsToken()`) for the same DSO reason as `VtValueHoldsType` below.
  **Keep the unsplit path allocation-free** when touching this code: it bulk-copies per-point
  values, reads the corner table directly from `faceVertexIndices.cdata()`, and builds neither a
  corner→face nor a corner→vertex buffer. Materializing those (the first 0.7.2 draft did) cost
  +60% on the native stage read of a 564-mesh / 1.19M-vertex scene, which is also the shape of
  regression to re-measure after changes: `Native~/Tests~/import_uv_test.cpp` covers correctness,
  not cost.
- **OpenUSD version:** built **monolithic** (`libusd_ms`/`usd_ms`) with
  `--no-python --no-imaging --no-usdview --no-examples --no-tutorials --no-tests --no-materialx`.
  The OpenUSD *version differs per platform* (see below) — this is **not** an ABI concern: the
  native ABI (`RUsd_GetApiVersion`) is `5` regardless of the OpenUSD minor version, and the
  managed C# layer only validates the ABI version. The toolkit uses only stable OpenUSD APIs
  (mesh, xform, `UsdPreviewSurface`, `UsdGeomSubset`), so 26.05 and 26.08 behave the same here.
- **macOS payload:** built against **26.08** (`PXR_VERSION 2608`, binary tag `pxrInternal_v0_26_8`),
  ABI v2, **verified** (export/import round-trips, hierarchy + multi-material, async import of a
  real 96-mesh / 251-material / 2K-texture scene). Universal (`x86_64` + `arm64`), under
  `Runtime/Plugins/macOS/`.
- **Windows payload:** rebuilt against **26.05** (`PXR_VERSION 2605`, binary tag
  `pxrInternal_v0_26_5`), **ABI 4**, x64, under `Runtime/Plugins/x86_64/Windows/`. **Rebuilt and
  verified on 2026-09-11** for 0.7.2 following `Native~/REBUILD_WINDOWS_LINUX.md` — all gates
  pass: 23 `RUsd_*` exports incl. the five ABI 3/4 additions; the DSO-safe literals
  (`SdfAssetPath`, `GfVec2f/3f/4f`) and the 0.7.2 primvar/diagnostic literals (`texCoord2f[]`,
  `float2[]`, `normal3f[]`, `USD import: `) all present; `Native~/Tests~/import_uv_test.cpp`
  reports `API 4` + `PASS`; in-Editor `GetRuntimeInfo()` → `API 4, OpenUSD 0.26.5`; and McUsd
  (23 materials / 75 `UsdUVTexture` refs) imports 23 meshes / 1760 vertices / 880 triangles with
  **23/23 meshes carrying UVs and normals**, 23/23 `_BaseMap` bound, 8 alpha-cutout materials
  (`_Cutoff` 0.50, queue 2450), 1 alpha-blended (`purple_stained_glass`, queue 3000) and **no
  console warnings**. Toolchain: VS Build Tools 2022 (MSVC 14.44) + Windows SDK 10.0.26100,
  CMake 4.3.3. The earlier 2026-06-24 build was ABI v2 and additionally predated the
  `VtValueHoldsType` texture fix. **Why 26.05, not 26.08:** OpenUSD has **no public `v26.08` tag** (latest public is
  `v26.05`); the macOS 26.08 build came from a non-public source. 26.05 is used for Windows by
  decision — functionally equivalent for this toolkit. To match exactly, rebuild against the same
  26.08 source the macOS build used. **0.7.2 moved the ABI to 5** (usdz packaging and packaged
  asset reads), so this payload now needs one more rebuild for those two features only —
  everything verified above is unaffected.
- **Linux payload:** rebuilt against **26.05** (`PXR_VERSION 2605`, binary tag `pxrInternal_v0_26_5`),
  ABI v2, x64, under `Runtime/Plugins/x86_64/Linux/`. **Verified** on 2026-06-24: `RUsd_GetApiVersion()`
  → `2`, `RUsd_GetOpenUsdVersion()` → `0.26.5`, and a standalone native harness round-trips
  export→import (incl. multi-material/`UsdGeomSubset`, Xform hierarchy with the v2 transform-node
  table, and `usdchecker` "Success!" on the output). Built **monolithic + `--no-python`** (the
  same minimal flag set as macOS/Windows) and assembled self-contained via `$ORIGIN`, so **no
  packman / Python / boost closure is shipped** (unlike the `build_linux.sh` default path — see
  below). **Why 26.05, not 26.08:** OpenUSD has no public `v26.08` tag; 26.05 is functionally
  equivalent for this toolkit (identical decision to the Windows build).
  **glibc baseline:** built on Ubuntu 24.04, so `libusd_ms.so` requires **GLIBC ≥ 2.38**
  (Ubuntu 23.10+). To run on older distros (e.g. Ubuntu 22.04 / glibc 2.35, the packman
  manylinux_2_35 baseline) rebuild on an older toolchain or in a manylinux_2_35 container.

## What changed this cycle (already in the macOS build)

### Native — `Native~/src/UsdExporter.cpp`, `Native~/include/unity_usd_toolkit_native.h`
- **Import texture extraction** (`ExtractPreviewMaterial`): follows each `UsdPreviewSurface`
  input connection to its `UsdUVTexture` and reads the `file` asset path
  (albedo/normal/metallic/emissive), the `UsdTransform2d` UV scale/translation, and the
  constant `emissiveColor`.
- **DSO-safe `VtValue` type checks** (`VtValueHoldsType`): compares the registered type *name*
  instead of `VtValue::IsHolding<T>()`. `IsHolding<T>()` / `TfType::Find<T>()` rely on `typeid`,
  which does **not** match across the plugin ↔ monolithic-OpenUSD boundary because the plugin is
  built with `CXX_VISIBILITY_PRESET hidden` (`Native~/CMakeLists.txt`). **Windows and Linux will
  hit the exact same bug if `IsHolding<pxrType>()` is reintroduced** — keep using
  `VtValueHoldsType(value, "TypeName")` + `UncheckedGet<T>()`. (Builtin `float`/`double` are fine.)
- **ABI v2 additions:**
  - Transform node table: `RUsd_GetImportNodeCount` / `RUsd_GetImportNodeInfo` — every
    `UsdGeomXformable` prim's local matrix; the importer no longer bakes world transforms into
    mesh vertices (`BuildImportedMesh` keeps points/normals mesh-local).
  - `UsdGeomSubset` multi-material: `RUsd_GetImportSubmeshCount` / `RUsd_CopyImportSubmeshes`
    (`BuildImportedSubmeshes` groups triangles by `materialBind`-family subset).
  - `kApiVersion = 2`.
- **ABI v3 additions:**
  - Extra UV sets: `RUsd_GetImportMeshUvSetInfo` / `RUsd_CopyImportMeshUvSet`
    (`RUSD_MAX_UV_SETS` = 3; set 0 still ships inside `RUsd_CopyImportMesh`).
  - `kApiVersion = 3`. Purely additive — see the minimum-version note under *Current state*.
- **ABI v4 additions:**
  - Authored opacity: `RUsd_GetImportMaterialOpacity` — the texture whose alpha drives
    transparency (empty when `opacity` is not connected) plus `opacityThreshold`
    (`ExtractOpacityInfo`). `kApiVersion = 4`, also purely additive.
- **ABI v5 additions (usdz):**
  - Packaging: `RUsd_CreateUsdzPackage` wraps `UsdUtilsCreateNewUsdzPackage` /
    `UsdUtilsCreateNewARKitUsdzPackage`. usdz cannot be written as a stage (`SdfUsdzFileFormat`
    is `supportsWriting: false`), so `UsdExporter` writes a `.usdc` into a temp staging directory
    and packages that; `editLayersInPlace` stays false so source layers on disk are never
    rewritten, and the call runs after `RUsd_EndExport` has released the stage, as OpenUSD asks.
  - Asset reads: `RUsd_ReadImportAsset` resolves an authored asset path and reads its bytes
    through `ArResolver::OpenAsset`, which is the only way to reach a texture *inside* a package.
    It prefers the resolution USD already performed for that path (recorded per context in
    `assetResolutions` while reading materials, so per-layer anchoring is exact) and otherwise
    anchors on the root layer with `SdfComputeAssetPathRelativeToLayer`.
  - Both come from the monolithic OpenUSD library already shipped on **every** platform
    (`UsdSkel`/`UsdUtils` symbols and the `usdz` file format + package resolver are all present in
    the macOS, Windows and Linux payloads), so this needed **no OpenUSD rebuild** — only the
    wrapper. Non-monolithic builds need `usdUtils` and `ar` in the link list (already added to
    `Native~/CMakeLists.txt`).
  - `kApiVersion = 5`, purely additive.
  - Coverage: `Native~/Tests~/usdz_test.cpp` (self-contained — it writes its own usda + PNG, so
    there is no binary fixture in the repo).

### Managed (C#) — platform-agnostic, ships as-is (no per-platform rebuild)
- `UsdImporter`: PBR texture binding, Xform hierarchy reconstruction (USD→Unity basis flip:
  `M_unity[i,j] = sign(i)·sign(j)·M_usd[j][i]`, sign(0) = −1), submesh→material array, true async
  (`ReadStageData` on a worker thread + main-thread build), per-import texture **dedup cache**,
  **frame-sliced** build with progress, and an off-main-thread managed PNG decoder
  (`UsdPngDecoder`).
- `UsdImportOptions`: `ImportTextures`, `MaxMillisecondsPerFrame`, `ProgressCallback`.

See `CHANGELOG.md` (`0.4.0`–`0.6.0`) for the full per-feature list.

## Rebuild for Windows (x64)

Run from an **x64 Native Tools Command Prompt for Visual Studio**.

1. Build OpenUSD monolithic (once). The shipped Windows payload uses **`v26.05`** (the latest
   public tag — there is no public `v26.08`). Substitute the 26.08 source tag/commit only if you
   have access to the same non-public source the macOS build used.
   ```bat
   git clone https://github.com/PixarAnimationStudios/OpenUSD.git C:\Dev\OpenUSD
   cd C:\Dev\OpenUSD
   git checkout v26.05
   python build_scripts\build_usd.py --build-variant release --build-monolithic ^
     --no-python --no-imaging --no-usdview --no-examples --no-tutorials --no-tests ^
     --no-materialx C:\USD\OpenUSD-26.05-win-x64
   ```
   Requires Python 3.x on PATH plus CMake and an x64 MSVC toolset. Only external dep is TBB.
2. Build this package's native wrapper:
   ```powershell
   cd <pkg>\Native~
   .\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64
   ```
3. Expected payload in `Runtime/Plugins/x86_64/Windows/`:
   `UnityUSDToolkitNative.dll`, `usd_ms.dll`, `tbb.dll`, `lib/usd/**/plugInfo.json`,
   `plugin/usd/plugInfo.json`.

## Rebuild for Linux (x64)

The shipped Linux payload was built with the **monolithic + CMake** path below (the same
`usd_ms` link path as Windows/macOS), **not** `build_linux.sh`. `build_linux.sh` targets the
alternative **packman component + Python** layout (Isaac/Omniverse), which ships a much larger
closure (libpython, libboost_python). Prefer the monolithic path for a minimal payload.

1. Build OpenUSD monolithic (public `v26.05` — there is no public `v26.08`):
   ```bash
   git clone --depth 1 --branch v26.05 \
     https://github.com/PixarAnimationStudios/OpenUSD.git /tmp/OpenUSD-src
   python build_scripts/build_usd.py --build-variant release --build-monolithic \
     --no-python --no-imaging --no-usdview --no-examples --no-tutorials --no-tests \
     --no-materialx /path/to/OpenUSD-26.05-linux-x64
   ```
   Needs CMake, g++ (C++17), and a Python 3.x with `setuptools` to run `build_usd.py`. Only
   external dep is TBB (built by the script).
2. Build this package's native wrapper via CMake (links `usd_ms`):
   ```bash
   cmake -S Native~ -B Native~/build~ -DCMAKE_BUILD_TYPE=Release \
     -DOPENUSD_ROOT=/path/to/OpenUSD-26.05-linux-x64
   cmake --build Native~/build~ -j
   ```
3. Assemble the **self-contained** payload under `Runtime/Plugins/x86_64/Linux/`:
   - `libUnityUSDToolkitNative.so` at the root (rpath `$ORIGIN:$ORIGIN/lib`).
   - `lib/libusd_ms.so` (rpath `$ORIGIN`) + `lib/libtbb.so.2`.
   - USD schema plugins under `lib/usd` (drop `usd/resources/codegenTemplates`) and shader
     plugins under `plugin/usd`.
   Resolve via `$ORIGIN` (`ldd` must show no "not found" and no absolute build paths), `strip
   --strip-unneeded` the `.so`s (keeps the `RUsd_*` dynamic exports), and **do not** ship a
   Python-enabled or unstripped build. `patchelf` (e.g. `pip install patchelf`) sets the rpaths.
   The C# layer sets `PXR_PLUGINPATH_NAME` to `lib/usd` + `plugin/usd` at runtime.

## Verify (per platform)

1. Package compiles; `read_console` has no errors.
2. `Unity.USDToolkit.UsdExporter.GetRuntimeInfo()` reports **`API 2, OpenUSD 0.26.X`** (macOS
   `0.26.8`, Windows `0.26.5`). The `API 2` part is what must match across platforms.
3. Round-trip: export a cube, then `UsdImporter.GetPreviewInfo` / `Import`.
4. Export with `TransformPolicy.PreserveHierarchy` + a 2-submesh / 2-material mesh, import, and
   confirm the hierarchy transforms and submesh materials round-trip.
5. `UsdImporter.ImportAsync` on a large textured scene stays responsive (no multi-second freeze).

## Notes
- macOS caches a loaded native dylib for the Editor session — restart the Editor to reload a
  rebuilt plugin. Windows/Linux behave similarly in a running player.
- The OpenUSD install and native build caches live under `Build~/` and `Native~/build~/`, which
  are git-ignored. On macOS the install used here is OpenUSD 26.08 universal.
