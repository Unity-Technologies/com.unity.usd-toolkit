# Unity USD Toolkit Development Summary - 2026-05-11

## Purpose

Unity USD Toolkit was developed as a Unity package that can export USD files at
runtime from a Windows Standalone x64 Unity application.

The package intentionally does not depend on Unity's Editor USD packages,
`com.unity.exporter.usd`, `com.unity.usd.core`, or USD.NET. Runtime export is
implemented through a custom native C++ DLL that calls Pixar OpenUSD directly,
with Unity C# calling that DLL through P/Invoke.

## Final Package Identity

- Package name: `com.unity.usd-toolkit`
- Display name: `Unity USD Toolkit`
- Runtime namespace: `Unity.USDToolkit`
- Runtime assembly: `Unity.USDToolkit`
- Editor assembly: `Unity.USDToolkit.Editor`
- Native plugin: `UnityUSDToolkitNative.dll`
- Target platform: Windows Standalone x64
- Package version at this milestone: `0.1.0`

## Initial Constraints

- Runtime export must work in built Windows Standalone x64 players, not only in
  the Unity Editor.
- Unity C# must not call OpenUSD C++ classes directly.
- Native calls must use a small C ABI exposed by the native DLL.
- Mesh data must be sent in batches, not vertex-by-vertex calls.
- The first production scope is static mesh export with normals, UV0, simple
  materials, submesh material binding, and hierarchy policy support.
- macOS, Linux, mobile, WebGL, skinned mesh, animation, textures, and USDZ are
  outside this milestone.

## Native OpenUSD Build

Pixar OpenUSD was built and installed locally for Windows x64.

- OpenUSD source: `D:\OpenUSDExporter\Build~\OpenUSD-dev`
- OpenUSD install root: `D:\OpenUSDExporter\Build~\OpenUSDPlugin`
- MSVC: Visual Studio Build Tools 2022 17.14.31, `cl.exe` 19.44.35226
- CMake: Kitware CMake 4.3.2
- Unity validation version: Unity 6000.4.0f1

The native wrapper build script is:

```powershell
D:\OpenUSDExporter\Native~\build_windows.ps1
```

The script configures CMake with `OPENUSD_ROOT`, builds
`UnityUSDToolkitNative.dll`, installs it into the Unity package runtime plugin
folder, and copies the required OpenUSD runtime DLL/resource tree.

## Native Wrapper Work

The C++ wrapper was updated to expose a package-owned C ABI over Pixar OpenUSD.
The native ABI remains internal and uses the `RUsd_*` prefix.

Implemented native capabilities include:

- Begin/end export context management
- Stage creation through OpenUSD
- Static mesh authoring as `UsdGeomMesh`
- Normals and UV0 authoring
- Simple `UsdPreviewSurface` material authoring
- Multiple material support
- Submesh material binding through `UsdGeomSubset`
- Mesh extent authoring
- Baked transform export
- Preserve hierarchy export through USD `Xform` prims
- Visibility authoring for included inactive objects and disabled renderers
- Native diagnostics capture from Pixar `TfDiagnosticMgr`
- Native API version reporting
- OpenUSD version reporting

The native DLL now exposes an API compatibility guard:

- Managed expected native API version: `1`
- Native function: `RUsd_GetApiVersion`
- Managed validation: `UsdExporter` checks the native API version before export

## Managed Runtime API

The public managed API was renamed and stabilized for the `0.1.0` milestone.

Supported public API surface:

- `Unity.USDToolkit.UsdExporter`
- `Unity.USDToolkit.UsdExportOptions`
- `Unity.USDToolkit.UsdExportResult`
- `Unity.USDToolkit.UsdExportedMeshInfo`
- `Unity.USDToolkit.UsdExportException`
- `Unity.USDToolkit.UsdRuntimeInfo`
- `Unity.USDToolkit.UsdTransformPolicy`

Main entry points:

```csharp
UsdExporter.ExportGameObject(root, outputPath, options);
UsdExportResult result = UsdExporter.ExportGameObjectWithResult(root, outputPath, options);
UsdRuntimeInfo info = UsdExporter.GetRuntimeInfo(options);
```

Everything under `Unity.USDToolkit.Native` is internal. Applications should not
call the P/Invoke layer or native `RUsd_*` functions directly.

No obsolete compatibility aliases were kept for the earlier pre-release names
such as `IP.RuntimeUsdExporter` or `RuntimeUsdExporter`.

## Export Feature Scope

Implemented geometry features:

- Static mesh export from `MeshFilter` and `MeshRenderer`
- Root-local baked mesh export by default
- Optional hierarchy preservation through `UsdTransformPolicy.PreserveHierarchy`
- Unity-to-USD basis conversion with X-axis flip
- Triangle winding correction
- Negative scale and non-uniform scale handling
- Normals
- UV0
- Mesh extents
- Large mesh index handling
- Multiple Unity materials per mesh
- Submesh material binding via USD face subsets
- Inactive and disabled renderer visibility handling

Implemented material features:

- Simple `UsdPreviewSurface`
- Base color
- Opacity
- Metallic
- Roughness

Not implemented in this milestone:

- Texture export
- Skinned mesh
- Animation
- Blend shapes
- USDZ packaging
- Importing USD into Unity
- macOS/Linux/mobile/WebGL runtime support

## Transform Policy

Two export policies are supported:

- `UsdTransformPolicy.BakedMesh`
- `UsdTransformPolicy.PreserveHierarchy`

`BakedMesh` is the default production path. Mesh points are transformed to the
export root's local space before crossing the native boundary.

`PreserveHierarchy` authors child GameObjects as USD `Xform` prims. Mesh points
remain mesh-local, and Unity local transforms are written as matrix
`xformOp:transform` values.

## Diagnostics Work

Diagnostics were upgraded from a basic native error string to a more useful
runtime reporting surface.

Implemented diagnostics include:

- Friendly `DllNotFoundException` / dependency load guidance
- Missing native runtime payload checks
- Missing OpenUSD plugin/resource path checks
- Diagnostic output including checked base paths, `PATH`, and
  `PXR_PLUGINPATH_NAME`
- Mesh unreadable errors including GameObject path and mesh name
- Editor-only asset path reporting for unreadable mesh sources
- Export result summary with mesh count, vertex count, triangle count, and
  output path
- Optional native Pixar diagnostic capture
- Optional native diagnostics log file output
- `UsdExporter.GetRuntimeInfo()` for native DLL name, native ABI version,
  OpenUSD version, DLL search path, and plugin search path

## Unity Plugin Packaging

The Windows runtime payload is placed under:

```text
Runtime/Plugins/x86_64/Windows
```

Included runtime files:

- `UnityUSDToolkitNative.dll`
- `usd_ms.dll`
- `tbb.dll`
- `plugin/usd/plugInfo.json`
- `lib/usd/plugInfo.json`
- Required OpenUSD schema/resource folders

Package hygiene work included:

- Native source moved to `Native~` so Unity ignores it during player builds
- Native build output kept under `Native~/build~`
- OpenUSD source/install/build trees kept under `Build~`
- Debug TBB DLLs, import libraries, PDBs, and native intermediates removed from
  the runtime payload
- OpenUSD `codegenTemplates` removed from the runtime payload so IL2CPP does
  not attempt to compile resource C++ files
- DLL `.meta` files pinned to Windows Editor and Windows Standalone x64

An Editor build postprocessor copies the full OpenUSD runtime resource tree into
the built player's `*_Data/Plugins/x86_64/Windows` folder. This is required
because Unity's native plugin importer handles DLLs but does not package
OpenUSD plugin/resource directories as native plugin dependencies.

## Package Renaming

The package was renamed from the earlier development identity to the final
milestone identity:

- Package ID changed to `com.unity.usd-toolkit`
- Display name changed to `Unity USD Toolkit`
- Namespace changed to `Unity.USDToolkit`
- Runtime assembly changed to `Unity.USDToolkit`
- Editor assembly changed to `Unity.USDToolkit.Editor`
- Native DLL changed to `UnityUSDToolkitNative.dll`
- Sample component changed to `UsdExportExample`

The native internal C ABI retained the `RUsd_*` prefix to avoid a risky ABI
rename late in the milestone. The public managed API is the supported surface.

## License And Third-Party Notices

The package's own code is documented as proprietary/private Unity software.

Third-party notices were added for redistributed runtime components:

- Pixar OpenUSD
- oneTBB
- Microsoft Visual C++ Runtime guidance

Full third-party license text is stored under:

```text
ThirdPartyNotices~/licenses
```

## Automation And Release Packaging

Validation and release packaging were scripted.

Validation script:

```powershell
Build~\run_validation.ps1
```

Release script:

```powershell
Build~\create_release_package.ps1
```

The release script stages the package, excludes build caches and native
intermediates, validates required ZIP entries, rejects forbidden runtime payload
files, and creates:

```text
Build~\Release\com.unity.usd-toolkit-0.1.0.zip
```

Latest generated release package SHA256 at this milestone:

```text
A9082A92129418596149BFBD421F83B367D044F4C986F9CA77B5DFEBABFF3B0B
```

## Validation Results

The final automated validation passed.

Summary file:

```text
D:\OpenUSDExporter\Build~\Validation\validation-20260511-143713.md
```

Passed checks:

- Unity Editor cube export
- Unity Editor geometry coverage export
- Unity Editor preserve hierarchy export
- Unity Editor large mesh export
- Invalid OpenUSD plugin path diagnostic
- Mono Windows Standalone runtime export
- IL2CPP Windows Standalone runtime export
- `usdchecker` validation of generated USD files
- Built player native payload layout verification

Final player export result summary:

```text
USD export wrote 1 mesh(es), 24 vertices, 12 triangles to
C:\Users\beatchoi\AppData\LocalLow\DefaultCompany\RuntimeUsdSmokeProject\player_cube.usda
```

## Public API Policy

The `0.1.0` API is documented as a pre-1.0 contract.

- `0.x` versions may still introduce breaking API changes.
- Starting with `1.0.0`, public managed API changes should follow SemVer.
- Breaking changes require a major version bump.
- Compatible feature additions require a minor version bump.
- Fixes require a patch version bump.
- Earlier pre-release names are not preserved as obsolete aliases.

## Current Known Limits

- Windows x64 only
- Export only, no USD import
- Runtime mesh source must be readable in the built player
- Textures are not exported
- Skinned meshes are not exported
- Animation is not exported
- USDZ packaging is not implemented
- Material support is limited to simple `UsdPreviewSurface` scalar values

## Recommended Next Upgrades

Suggested next development steps:

- Add an Editor diagnostic menu such as
  `Tools > Unity USD Toolkit > Validate Native Runtime`
- Add a package health report for DLL/import settings/OpenUSD plugin path/API
  version checks
- Add texture export policy and implementation
- Add vertex color, tangents, and multiple UV set support
- Add skinned mesh bake export
- Add golden USD structure tests
- Add clean-machine package install validation
- Add version bump and changelog automation
- Consider converting `UsdExportOptions` fields to a more formal 1.0 API shape
  before declaring the first stable release
