# Test Results - 2026-05-11

Environment inspected:

- Workspace: `D:\OpenUSDExporter`
- Shell: PowerShell
- OpenUSD source: `D:\OpenUSDExporter\Build~\OpenUSD-dev`
- OpenUSD install root: `D:\OpenUSDExporter\Build~\OpenUSDPlugin`
- MSVC: Visual Studio Build Tools 2022 17.14.31, `cl.exe` 19.44.35226
- CMake: Kitware CMake 4.3.2

Results:

- Package source inspection: completed
- Runtime C# API update: completed
- Native C++ wrapper update: completed
- CMake/build script update: completed
- Unity sample MonoBehaviour: completed
- Editor Windows compile: success in Unity 6000.4.0f1
- Native `UnityUSDToolkitNative.dll` build: success
- Native C ABI smoke export: success, generated `smoke_cube.usda`
- `usdchecker` / `usdcat`: success on `smoke_cube.usda`
- Unity Editor compile: success in Unity 6000.4.0f1 with a sibling smoke project
- Unity Editor export smoke test: success, generated `D:\RuntimeUsdSmokeProject\SmokeExports\unity_cube.usda`
- Windows Standalone export test: success, generated `player_cube.usda` under `Application.persistentDataPath`
- Package hygiene pass: success, runtime DLL payload trimmed to `UnityUSDToolkitNative.dll`, `usd_ms.dll`, and `tbb.dll`
- Unity plugin import settings: success, DLL `.meta` files pinned to Windows Editor and Windows Standalone x64
- Package rename: success, Unity resolves the local package as `com.unity.usd-toolkit` with display name `Unity USD Toolkit`
- Rename regression smoke: success, Unity Editor export generated `D:\RuntimeUsdSmokeProject\SmokeExports\unity_cube.usda`
- Rename regression `usdchecker`: success on `unity_cube.usda`
- Rename regression Windows Standalone: success, player export generated `player_cube.usda` under `Application.persistentDataPath`
- Rename regression player `usdchecker`: success on `player_cube.usda`
- Diagnostics API: success, `ExportGameObjectWithResult` reports `1 mesh(es), 24 vertices, 12 triangles`
- Native diagnostics ABI: success, rebuilt `UnityUSDToolkitNative.dll` with `RUsd_BeginExportEx` and `RUsd_GetDiagnostics`
- Native C ABI regression: success after setting `PXR_PLUGINPATH_NAME`, relinked smoke executable generated `smoke_cube.usda` and `usdchecker` passed
- OpenUSD plugin path diagnostics: success, invalid `PluginSearchPath` reports the missing directory plus `PXR_PLUGINPATH_NAME`, native base paths, and `PATH`
- Diagnostics Windows Standalone regression: success, player export summary written to `player_smoke_done.txt` and `usdchecker` passed
- Geometry 4A native build: success, `UnityUSDToolkitNative.dll` rebuilt with `RUsd_AddMeshEx`
- Geometry 4A submesh/material smoke: success, `geometry_coverage.usda` contains `GeomSubset` material bindings for two submeshes
- Geometry 4A transform smoke: success, negative scale and non-uniform scale baked export passed `usdchecker`
- Geometry 4A visibility/bounds smoke: success, disabled renderer exported with `visibility = "invisible"` and authored `extent`
- Geometry 4A large mesh smoke: success, `large_mesh.usdc` exported 90,601 vertices / 180,000 triangles and passed `usdchecker`
- Geometry 4A Windows Standalone regression: success, built player exported `player_cube.usda` and passed `usdchecker`
- Geometry 4B native build: success, `UnityUSDToolkitNative.dll` rebuilt with `RUsd_AddXform`
- Geometry 4B preserve hierarchy smoke: success, `preserve_hierarchy.usda` contains nested `Xform` prims, matrix `xformOp:transform`, mesh-local points, and two material `GeomSubset` bindings
- Geometry 4B preserve hierarchy `usdchecker`: success on `D:\RuntimeUsdSmokeProject\SmokeExports\preserve_hierarchy.usda`
- Geometry 4B baked geometry regression: success, `geometry_coverage.usda` still passes `usdchecker`
- Geometry 4B Windows Standalone clean rebuild: success, rebuilt `D:\RuntimeUsdSmokeProject\Build\RuntimeUsdSmoke.exe` without the earlier stale-DLL `UnauthorizedAccessException`
- Geometry 4B Windows Standalone runtime export: success, player export generated `player_cube.usda` under `Application.persistentDataPath` and passed `usdchecker`
- Third-party notices: added package-level `ThirdPartyNotices.md`, copied OpenUSD license/notice text, copied oneTBB Apache 2.0 license, and set the package's own code license notice to proprietary/private in `LICENSE.md`
- IL2CPP package hygiene: moved native C++ source/build files to `Native~` and removed OpenUSD `codegenTemplates` from the runtime payload so Unity IL2CPP does not compile package/resource C++ files as player sources
- IL2CPP module install: success, installed Unity 6000.4.0f1 Windows Build Support (IL2CPP) through Unity Hub CLI module id `windows-il2cpp`
- IL2CPP Windows Standalone build: success, generated `D:\RuntimeUsdSmokeProject\BuildIl2Cpp\RuntimeUsdSmoke.exe`
- IL2CPP build payload layout: success, native payload is present only under `RuntimeUsdSmoke_Data\Plugins\x86_64\Windows` after build postprocessor deduplication
- IL2CPP Windows Standalone runtime export: success, player export generated `player_cube.usda` under `Application.persistentDataPath`
- IL2CPP player `usdchecker`: success on `player_cube.usda`
- Final package/API naming: success, package name `com.unity.usd-toolkit`, display name `Unity USD Toolkit`, runtime namespace/assembly `Unity.USDToolkit`, editor assembly `Unity.USDToolkit.Editor`
- Final public API naming: success, `UsdExporter`, `UsdExportOptions`, `UsdExportResult`, `UsdExportException`, and `UsdTransformPolicy`
- Native plugin rename: success, rebuilt `UnityUSDToolkitNative.dll` from `Native~` against OpenUSD install root `D:\OpenUSDExporter\Build~\OpenUSDPlugin`
- Native API compatibility guard: success, managed API checks native ABI version before export and exposes `UsdExporter.GetRuntimeInfo()`
- Final automated validation: success, `D:\OpenUSDExporter\Build~\Validation\validation-20260511-143713.md` passed Editor cube, geometry coverage, preserve hierarchy, large mesh, invalid plugin path diagnostic, Mono Windows Standalone, and IL2CPP Windows Standalone
- Release package: success, generated `D:\OpenUSDExporter\Build~\Release\com.unity.usd-toolkit-0.1.0.zip`

Next command to run from an x64 Native Tools Command Prompt after installing or
building OpenUSD:

```powershell
cd D:\OpenUSDExporter\Native~
.\build_windows.ps1 -OpenUsdRoot D:\OpenUSDExporter\Build~\OpenUSDPlugin
```

Expected package output after that command:

```text
D:\OpenUSDExporter\Runtime\Plugins\x86_64\Windows\UnityUSDToolkitNative.dll
D:\OpenUSDExporter\Runtime\Plugins\x86_64\Windows\usd_ms.dll
D:\OpenUSDExporter\Runtime\Plugins\x86_64\Windows\lib\usd\plugInfo.json
D:\OpenUSDExporter\Runtime\Plugins\x86_64\Windows\plugin\usd\plugInfo.json
```
