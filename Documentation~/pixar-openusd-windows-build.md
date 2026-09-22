# Pixar OpenUSD Windows Build Notes

Use this path when you want the Unity app itself to export USD at runtime without Unity's USD Exporter package.

## Recommended Shape

```text
Unity C#
  -> P/Invoke
UnityUSDToolkitNative.dll
  -> Pixar OpenUSD C++ API
usd_ms.dll and dependencies
  -> .usd / .usda / .usdc
```

The package uses a monolithic shared OpenUSD build when available. In OpenUSD terminology this usually produces `usd_ms.dll`, which keeps the runtime DLL layout smaller than the default many-library build.

## Why Not Call OpenUSD Directly From C#?

Pixar OpenUSD is a C++ API. Unity C# cannot safely call C++ classes such as `UsdStage` or `UsdGeomMesh` directly. The wrapper exposes a small C ABI:

```cpp
RUsd_BeginExport(...)
RUsd_AddMesh(...)
RUsd_EndExport(...)
```

Unity only passes flat arrays and simple structs across the boundary.

## Folder Layout After Native Build

```text
Runtime/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_ms.dll
  tbb.dll
  zlib*.dll (only if required by the OpenUSD build)
  lib/
    usd/
      plugInfo.json
      */resources/plugInfo.json
  plugin/
    usd/
      plugInfo.json
      ...
  share/
    usd/
      plugins/
        ...
```

The C# layer sets `PXR_PLUGINPATH_NAME` from this layout before the first native export call.

For the current monolithic, no-Python, no-imaging Windows build, the runtime DLL
set is trimmed to `UnityUSDToolkitNative.dll`, `usd_ms.dll`, and `tbb.dll`. Debug TBB
DLLs, import libraries, PDB files, and native build intermediates should not be
published inside `Runtime/Plugins`.

In a Windows Standalone player, the runtime code checks these locations:

```text
<App>_Data/Plugins/x86_64/Windows
<App>_Data/Plugins/x86_64
<App>_Data/Plugins
```

The package's Editor post-build step copies the native runtime tree to both
`Plugins/x86_64` and `Plugins/x86_64/Windows` so OpenUSD's DLL loading and plugin
discovery work even if Unity changes where it places package native plugins.

## Product Notes

- Start with Mono scripting backend while validating the native plugin.
- Test IL2CPP separately after the native path is stable.
- Keep all mesh export calls coarse-grained. Avoid one P/Invoke call per vertex.
- Run `usdchecker` on exported files as part of validation.
- For USDZ, add a later packaging step after `.usdc` export is stable.

## Troubleshooting

- If CMake cannot find `pxr`, pass the OpenUSD install root, not the source tree,
  to `Native~/build_windows.ps1 -OpenUsdRoot`.
- If `usd_ms.dll` exists but CMake cannot find a `usd_m` target, the package CMake
  tries `usd_ms`, `usd_m`, then component targets and component library names.
- If Unity Play Mode works but the built player fails at export time, inspect
  `<App>_Data/Plugins/x86_64` and confirm that `plugin/usd/plugInfo.json` and
  `share/usd/plugins` were copied with the DLLs.
- If `Mesh is not readable` is thrown in the player, enable `Read/Write Enabled`
  on the mesh asset before building the player or AssetBundle.
