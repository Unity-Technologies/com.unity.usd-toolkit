# Unity USD Toolkit

Runtime USD export bridge for Unity applications.

This package intentionally does not depend on `com.unity.exporter.usd`. It uses a custom native C++ wrapper around Pixar OpenUSD. 

A C# integration layer for connecting Unity to NVIDIA's Omniverse/OpenUSD ecosystem, including Isaac Sim, Isaac Lab, and SimReady-validated assets. Wraps Pixar's OpenUSD 26.05 native binaries via P/Invoke so Unity can read, write, and round-trip USD stages authored for or consumed by Simulation pipelines, including live scene sync between Unity Runtime and Isaac Sim.

This is not a general-purpose USD interchange package, it's built specifically to make Unity a compatible runtime and authoring surface inside USD Simulation workflows (same USD stage, same Simulation validation and asset pipeline, running in Unity as the operator-facing runtime).

## Current Scope

- Windows x64 runtime target — Windows 10 version 21H1 or newer
- macOS Standalone runtime target when a macOS OpenUSD/native payload is built —
  macOS 12.0 (Monterey) or newer, the deployment target every dylib is built with
- Linux x64 runtime target (self-contained native payload under
  `Runtime/Plugins/x86_64/Linux/`; see `Native~/README.md`) — **Ubuntu 24.04 or
  newer**: that payload is built on 24.04 and needs glibc >= 2.38 and libstdc++
  with `GLIBCXX_3.4.32`, so it does not run on Ubuntu 22.04 even though Unity 6.3
  supports it. The minimums apply to players you build, not only to the Editor.
- Static mesh export from `MeshFilter` + `MeshRenderer`
- GPU readback export for non-readable meshes (no `Read/Write Enabled` required;
  works in Play mode), with a CPU fallback when `Read/Write` is enabled
- Root-local baked geometry by default, with optional hierarchy preservation as
  USD `Xform` prims through `UsdTransformPolicy.PreserveHierarchy`.
- Unity-to-USD basis conversion with X-axis flip and triangle winding correction
- negative scale and non-uniform scale handling for baked positions/normals
- local transform preservation for hierarchy export mode
- `UsdGeomMesh`
- submesh material binding via `UsdGeomSubset`
- multiple Unity materials per mesh
- authored mesh `extent`
- disabled renderer / inactive visibility preservation when included
- `UsdPreviewSurface` base color, opacity, metallic, roughness, emission
- **PBR texture export** as standard linked PNGs (`<usd-name>_textures/`):
  albedo → `diffuseColor`, normal (Unity DXT5nm decoded) → `inputs:normal`,
  `_MetallicGlossMap` → `metallic` / `roughness`, emission → `emissiveColor`,
  plus UV tiling/offset as `UsdTransform2d`. Toggle with
  `UsdExportOptions.ExportTextures`.
- `.usd`, `.usda`, `.usdc` depending on output file extension
- **`.usdz` packaging** — a `.usdz` output path writes a usdz package. usdz is a read-only zip, so
  the stage is written as a `.usdc` in a temporary staging folder and packaged from there, textures
  included; nothing is left next to the `.usdz`. `UsdExportOptions.UsdzArkitCompatible` applies
  ARKit's stricter constraints. Needs native API 5 (export throws with a clear message otherwise).

Not implemented yet:

- skinned mesh
- animation

## Runtime Import MVP

The toolkit also exposes a first-pass runtime import API for static USD geometry.
This MVP intentionally supports a practical subset:

- `.usd`, `.usda`, `.usdc`, `.usdz` (textures packaged inside a usdz are read through the stage's
  asset resolver, which needs native API 5; with an older plugin such a stage still imports its
  geometry and flat material colours)
- stage preview metadata through `UsdImporter.GetPreviewInfo(...)` — default prim, meters-per-unit,
  up axis, and stage-wide `MeshCount` / `MaterialCount` / `TriangleCount` / `VertexCount`, so a UI
  can warn about a heavy file before importing it
- static `UsdGeomMesh`
- **Xform hierarchy reconstruction** — every transformable prim becomes a Unity `Transform`
  with its local position/rotation/scale (mesh points stay mesh-local), so imported objects
  keep the USD hierarchy and remain individually movable
- **multi-material meshes** — `materialBind` `UsdGeomSubset`s import as Unity submeshes with a
  matching `sharedMaterials` array
- **truly async import** via `UsdImporter.ImportAsync(...)` — the USD parse and file reads run
  off the main thread; Unity object creation runs on the main thread but is **sliced across
  frames** (budget: `UsdImportOptions.MaxMillisecondsPerFrame`, default 10 ms) so large imports
  don't freeze the editor/player, with an optional `UsdImportOptions.ProgressCallback`
  (`0..1`, phase). Call it from the main thread.
- **shared texture decoding** — each referenced texture file is read and decoded once and
  reused across all materials that share it (large material counts no longer re-decode the same
  PNGs)
- `UsdPreviewSurface` material values **and PBR textures** (albedo → `_BaseMap`, normal →
  `_BumpMap`, packed metallic+smoothness → `_MetallicGlossMap`, emission → `_EmissionMap`,
  plus UV tiling/offset from `UsdTransform2d`). Textures referenced by the `UsdUVTexture`
  `file` path are loaded relative to the imported USD file. Toggle with
  `UsdImportOptions.ImportTextures` (default `true`).
- **authored texture paths are confined to the stage folder.** A USD file authors its own
  `inputs:file` paths, so an imported stage is untrusted input: an absolute path, or one climbing
  out with `../`, used to be read straight off disk. Anything resolving outside the imported
  file's own directory (or, for a `.usdz`, outside the package) is now skipped with a warning.
  Set `UsdImportOptions.AllowExternalAssetPaths` to `true` for stages you trust that deliberately
  reference a shared texture library elsewhere on disk.
- runtime `GameObject`, `Mesh`, `MeshRenderer`, optional `MeshCollider` creation
- folder scanning through `UsdLibraryScanner.ScanFolder(...)`

Example:

```csharp
UsdImportPreviewInfo preview = UsdImporter.GetPreviewInfo(path);
if (preview.TriangleCount > 5_000_000)
{
    // Warn, or offer a cancel, before committing to the import.
}

UsdImportResult result = await UsdImporter.ImportAsync(path, new UsdImportOptions
{
    Parent = transform,
    ImportMaterials = true,
    ImportTextures = true,
    GenerateColliders = false
});
```

For a folder-driven UI:

```csharp
IReadOnlyList<UsdLibraryItem> items = UsdLibraryScanner.ScanFolder(folderPath);
```

The package includes matching `Runtime Export Example` and `Runtime Import
Browser` sample scenes under `Samples/`. They compile with the package — there is no
Package Manager import step — so open either scene and use the runtime UI to choose
paths after the app is built.

Animation, skinned mesh, variants, and payload streaming are later milestones.

The C# import API is shared by Windows and macOS, but the native plugin must be
rebuilt per platform whenever this ABI changes. This repository currently has a
rebuilt macOS payload for the import API; rebuild the Windows DLL with
`Native~/build_windows.ps1` before using runtime import in a Windows player.

## Build Native Runtime

Build Pixar OpenUSD first, then build this package's native wrapper.

### Windows

Run from an x64 Native Tools Command Prompt for Visual Studio.

Example:

```bat
git clone https://github.com/PixarAnimationStudios/OpenUSD.git C:\Dev\OpenUSD
cd C:\Dev\OpenUSD
git checkout v26.05
python build_scripts\build_usd.py ^
  --build-variant release ^
  --build-monolithic ^
  --no-python ^
  --no-imaging ^
  --no-usdview ^
  --no-examples ^
  --no-tutorials ^
  --no-tests ^
  --no-materialx ^
  C:\USD\OpenUSD-26.05-win-x64
```

Then build this package's native wrapper:

```powershell
cd C:\Path\To\UnityProject\Packages\com.unity.usd-toolkit\Native~
.\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64
```

The script installs `UnityUSDToolkitNative.dll` and copies OpenUSD runtime files into:

```text
Packages/com.unity.usd-toolkit/Runtime/Plugins/x86_64/Windows
```

Unity will include those files in a Windows Standalone build.

The package also includes an Editor build postprocessor that copies the OpenUSD
runtime tree into the built player's `*_Data/Plugins/x86_64` folder. This is
needed because `plugInfo.json` and `share/usd/plugins` are data files, not native
DLLs, and Unity's native plugin importer is not a general resource packager.

## Runtime Plugin Payload

The checked-in Windows payload is intentionally minimal:

```text
Runtime/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_rt.dll
  tbb_usdrt.dll
  lib/usd/**/plugInfo.json and schema resources
  plugin/usd/plugInfo.json and shader resources
```

Debug TBB DLLs, import libraries, PDBs, and native build intermediates are not
part of the runtime package. The native build cache and OpenUSD source/install
root live under `Build~` or `Native~/build~`, which Unity ignores. The native
source tree is stored in `Native~` so Unity does not try to compile the package's
C++ wrapper during IL2CPP player builds.

The `.dll.meta` files are included and pin the native plugins to Windows Editor
and Windows Standalone x64 only.

### macOS

Build Pixar OpenUSD on macOS as a monolithic shared runtime. From the package
root:

```bash
./Build~/build_openusd_macos.sh \
  --arch universal \
  --install-dir "$PWD/Build~/OpenUSDInstall/macos-universal"
```

Then build and install this package's native wrapper plus the OpenUSD dylib and
resource tree:

```bash
./Native~/build_macos.sh \
  --openusd-root "$PWD/Build~/OpenUSDInstall/macos-universal" \
  --arch universal
```

The script installs the macOS runtime payload into:

```text
Packages/com.unity.usd-toolkit/Runtime/Plugins/macOS
```

Expected files:

```text
Runtime/Plugins/macOS/
  UnityUSDToolkitNative.dylib
  libusd_ms.dylib
  libtbb*.dylib
  lib/usd/**/plugInfo.json and schema resources
  plugin/usd/plugInfo.json and shader resources
  share/** and resources/** when present in the OpenUSD install
```

The wrapper is linked with `@loader_path` RPATHs, and the build script rewrites
copied dylib install names/dependencies to prefer `@loader_path`. Check the
result with:

```bash
otool -L Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib
otool -L Runtime/Plugins/macOS/libusd_ms.dylib
```

For distribution outside local development, sign the final app bundle after
Unity builds it. If `install_name_tool` rewrites dependencies, codesigning must
happen after that rewrite step.

During macOS Standalone builds, the package build postprocessor copies the full
macOS payload into the app bundle's `Contents/PlugIns` folder so OpenUSD
`plugInfo.json` and schema/shader resources are present beside the native
libraries.

## License And Notices

The Unity package's own source code, samples, build scripts, and custom native
wrapper are proprietary Unity software. See `LICENSE.md`.

Third-party runtime notices are recorded in `ThirdPartyNotices.md`. Full license
texts copied from the bundled OpenUSD and oneTBB sources are stored under
`ThirdPartyNotices~/licenses`.

`ThirdPartyNotices.md` also records third-party products this package
*integrates with* but does not redistribute — notably NVIDIA Isaac Sim, used by
the `Live Sync Example` sample. No NVIDIA software is bundled here; the sample's
Kit extension calls NVIDIA-provided APIs resolved at run time from an Isaac Sim
installation the user obtains from NVIDIA under NVIDIA's own license terms.

### Third Party Product disclaimer

The following concerns a product or service (each a "Third Party Product") that
is not developed, owned, or operated by Unity. This information may not be
up-to-date or complete, and is provided to you for your information and
convenience only. Your access and use of any Third Party Product is governed
solely by the terms and conditions of such Third Party Product. Unity makes no
express or implied representations or warranties regarding such Third Party
Products, and will not be responsible or liable, directly or indirectly, for any
actual or alleged damage or loss arising from your use thereof (including damage
or loss arising from any content, advertising, products or other materials on or
available from the provider of any Third Party Products).

### Trademarks

NVIDIA, NVIDIA Isaac Sim, and NVIDIA Omniverse are trademarks and/or registered
trademarks of NVIDIA Corporation in the U.S. and other countries. Universal
Scene Description (USD), OpenUSD, and Pixar are trademarks and/or registered
trademarks of Pixar in the U.S. and other countries. Intel and oneAPI are
trademarks of Intel Corporation or its subsidiaries. Microsoft, Windows, and
Visual C++ are trademarks of the Microsoft group of companies. macOS is a
trademark of Apple Inc. Unity, the Unity logo, and other Unity trademarks are
trademarks or registered trademarks of Unity Technologies or its affiliates in
the U.S. and elsewhere. All other trademarks are the property of their
respective owners. Use of a third-party name or mark in this package is for
identification only and does not imply any endorsement, sponsorship, or
affiliation.

## Runtime Usage

```csharp
using Unity.USDToolkit;
using UnityEngine;

public class ExportButton : MonoBehaviour
{
    [SerializeField] private GameObject exportRoot;

    public void Export()
    {
        string path = System.IO.Path.Combine(
            Application.persistentDataPath,
            "robot.usdc");

        UsdExportResult result = UsdExporter.ExportGameObjectWithResult(exportRoot, path, new UsdExportOptions
        {
            RootPrimName = "Robot",
            MetersPerUnit = 1.0f,
            IncludeInactive = false,
            ExportBounds = true,
            TransformPolicy = UsdTransformPolicy.BakedMesh,
            PreserveInactiveAndDisabledVisibility = true,
            CaptureNativeDiagnostics = true,
            NativeDiagnosticsLogPath = System.IO.Path.Combine(
                Application.persistentDataPath,
                "robot-usd-diagnostics.log")
        });

        Debug.Log(result.ToString());
    }
}
```

Source meshes do **not** need `Read/Write Enabled` when `RequireReadableMeshes`
is `false`: non-readable meshes are exported through a GPU readback path (this
also works in Play mode). Leave `RequireReadableMeshes = true` only if you want the
exporter to throw on non-readable meshes instead. GPU readback needs a graphics
device; for headless / `-nographics` runs, enable `Read/Write Enabled` and use the
CPU path.

## Threading And Initialization

**One rule: the native runtime must be configured before OpenUSD is asked to open a stage.**
Every public entry point does this for you — `UsdImporter.GetPreviewInfo`, `UsdImporter.Import`,
`UsdImporter.ImportAsync`, `UsdExporter.ExportGameObject*`, and `UsdExporter.GetRuntimeInfo` all
call the runtime configuration first, which sets `PXR_PLUGINPATH_NAME` so OpenUSD can find its
`plugInfo.json` files. Call any of them and there is nothing else to do.

If that configuration has not run, OpenUSD fails inside `PlugFindPluginResource` with a
`TfFatalError`. That is a **native abort, not a managed exception**: it bypasses every
`try`/`catch`, returns no error code, and terminates the process. Since 0.6.5 the internal
`UsdNative` entry points guard against this themselves, but the rule is worth knowing because
the failure mode is so abrupt.

**Threading.** Running the USD parse off the main thread is supported:

| Call | Thread |
| --- | --- |
| `UsdImporter.ImportAsync` | Call from the main thread. The USD parse, buffer copies, and texture file reads run on a worker; Unity object creation runs on the main thread, sliced across frames. |
| `UsdImporter.Import`, `UsdImporter.GetPreviewInfo` | Run entirely on the calling thread. `GetPreviewInfo` touches no Unity objects, so a worker thread is fine. |
| `UsdExporter.ExportGameObject*` | Main thread — it reads Unity meshes, materials, and textures. |

Anything that creates or reads Unity objects must be on the main thread; that is a Unity
constraint, not a USD one. OpenUSD stage reads themselves are not main-thread-bound.

Only the managed API above is supported. `Unity.USDToolkit.Native` and the native `RUsd_*` C ABI
are internal implementation details — reaching them by reflection skips the initialization above
and the validation that turns a misconfigured payload into a catchable `UsdImportException`.

## Public API And Versioning

The supported 0.1.0 runtime API surface is intentionally small:

- `Unity.USDToolkit.UsdExporter`
- `Unity.USDToolkit.UsdExportOptions`
- `Unity.USDToolkit.UsdExportResult`
- `Unity.USDToolkit.UsdExportedMeshInfo`
- `Unity.USDToolkit.UsdExportException`
- `Unity.USDToolkit.UsdRuntimeInfo`
- `Unity.USDToolkit.UsdTransformPolicy`

Everything under `Unity.USDToolkit.Native` and the native `RUsd_*` C ABI is an
internal implementation detail. Applications should call only the managed API
above.

Version `0.x` may still introduce breaking API changes while the toolkit moves
toward a production `1.0.0` contract. Starting with `1.0.0`, public managed API
changes should follow SemVer: breaking changes require a major version bump,
new compatible features require a minor version bump, and fixes require a patch
version bump.

There are no obsolete compatibility aliases in 0.1.0. The previous
`IP.RuntimeUsdExporter` / `RuntimeUsdExporter` names were pre-release names and
are intentionally not kept as aliases.

### UsdExportOptions Defaults

| Option | Default | Notes |
| --- | --- | --- |
| `RootPrimName` | `null` | Uses the export root GameObject name when empty. |
| `MetersPerUnit` | `1.0f` | Must be greater than zero. |
| `IncludeInactive` | `false` | Excludes inactive child GameObjects by default. |
| `RequireReadableMeshes` | `true` | Throws when a mesh is not readable in the player. |
| `ExportNormals` | `true` | Authors normals when the Unity mesh has them. |
| `ExportUv0` | `true` | Authors UV0 when the Unity mesh has it. |
| `ExportBounds` | `true` | Authors mesh extent. |
| `ExportDisabledRenderers` | `true` | Includes disabled renderers as invisible by default. |
| `PreserveInactiveAndDisabledVisibility` | `true` | Authors `visibility = "invisible"` for included inactive/disabled objects. |
| `UsdzArkitCompatible` | `false` | `.usdz` output only: packages under ARKit (AR Quick Look) constraints, which may drop features such as variant sets. |
| `IgnoreAlbedoInMetallicSlot` | `true` | Drops the metallic map (uses scalar `_Metallic`) when it is the same texture as the albedo map — a common misassignment that otherwise exports as a near-mirror metal (black in viewers without environment reflection). Logs a warning when triggered. |
| `TransformPolicy` | `UsdTransformPolicy.BakedMesh` | Bakes transforms into mesh points unless set to `PreserveHierarchy`. |
| `ValidateNativeRuntime` | `true` | Checks native plugin and OpenUSD runtime payload before export (presence only). |
| `VerifyNativeRuntimeIntegrity` | `true` | Compares each shipped native binary against the SHA-256 digest recorded in `Runtime/Native/NativeRuntimeHashes.g.cs` before the first P/Invoke, and refuses a payload that does not match. Runs once per process. Regenerate the manifest with `python3 Native~/generate_native_hashes.py` after rebuilding the native plugin yourself. |
| `ValidateOpenUsdPluginPath` | `true` | Checks OpenUSD plugin/resource discovery paths. |
| `CaptureNativeDiagnostics` | `false` | Captures Pixar OpenUSD diagnostics when enabled. |
| `LogExportSummary` | `false` | Logs `UsdExportResult.ToString()` after a successful export. |
| `PluginSearchPath` | `null` | Optional override for OpenUSD plugin discovery. Must resolve inside the package's own native runtime folders — OpenUSD loads and executes any library a `plugInfo.json` under this path names, so anything outside them is refused unless the static `UsdExporter.AllowExternalPluginSearchPath` is set from code. |
| `NativeDiagnosticsLogPath` | `null` | Optional output path for captured diagnostics. |

## Geometry Policy

The default production path is **baked static mesh export**. Each exported mesh
is authored as root-local points in a `UsdGeomMesh`; Unity hierarchy transforms,
including scale, are applied to positions before crossing the native boundary.
Normals are transformed with the inverse-transpose normal matrix, and triangle
winding is corrected for the Unity-to-USD basis conversion plus negative scale.

Set `UsdExportOptions.TransformPolicy` to
`UsdTransformPolicy.PreserveHierarchy` to author Unity child transforms as
USD `Xform` prims. In this mode, mesh points stay in mesh-local space and each
child transform is written as a matrix `xformOp:transform`. The selected export
root becomes the USD root prim and is treated as the local export origin; child
local position, rotation, and scale are preserved below that root.

Unity submeshes are exported as USD face subsets in the `materialBind` family,
with per-subset `UsdShadeMaterial` bindings. If a submesh references a material
slot that is missing in Unity, the native layer falls back to material 0.

`IncludeInactive` controls whether inactive child GameObjects are considered.
When included, inactive objects and disabled renderers are authored with
`visibility = "invisible"` by default. Set `ExportDisabledRenderers = false` to
skip disabled renderers entirely.

## Materials And Textures

With `UsdExportOptions.ExportTextures = true`, each material's textures are written
as PNGs into a `<usd-name>_textures/` folder next to the USD and referenced by
relative path through `UsdUVTexture`:

- albedo (`_BaseMap` / `_MainTex` / `_BaseColorMap`) → `diffuseColor` (sRGB)
- normal (`_BumpMap` / `_NormalMap`) → `inputs:normal` (Unity DXT5nm/BC5 is decoded
  back to standard tangent-space RGB and remapped `[0,1] → [-1,1]`)
- metallic+smoothness (`_MetallicGlossMap` / `_MetallicMap`) → `metallic` (`.r`)
  and `roughness` (`1 - .a`)
- emission (`_EMISSION` materials, `_EmissionMap`) → `emissiveColor`, with
  `opacity` taken from the base-color alpha
- `mainTextureScale` / `mainTextureOffset` → a `UsdTransform2d` node (only when
  not 1:1)

Textures are read through a GPU `Blit` / `ReadPixels`, so source textures do not
need `Read/Write Enabled`. Each texture is exported once and cached by reference.

### Metallic-map misassignment guard

A common Unity authoring mistake is to drop a base-color texture into the metallic
slot (`_MetallicGlossMap`), often with the `_METALLICSPECGLOSSMAP` keyword enabled.
The exporter would then read `metallic` from that texture's red channel and
`roughness` from `1 - alpha`, turning the surface into a near-mirror metal
(`metallic` ≈ 0.7, `roughness` ≈ 0).

Such a surface looks fine in Unity — there is always a skybox / reflection probe
for the mirror to reflect — but renders **solid black** in viewers that do not
supply an environment/IBL reflection (for example Isaac Sim's real-time raster
viewport, where the mirror simply reflects the empty background). Adding lights
does not help, because a mirror reflection is not filled in by direct lights.

To avoid this, when a material's metallic map resolves to the **same texture** as
its albedo map, the exporter treats it as a misassignment, **drops the metallic
map, and uses the scalar `_Metallic` value instead**, and logs a warning. The
surface is then exported as a normal diffuse material and renders consistently
across viewers. If you do want a genuinely metallic surface, give it a real
(distinct) metallic map.

This behavior is controlled by `UsdExportOptions.IgnoreAlbedoInMetallicSlot`
(default `true`; also a toggle in the Runtime Export Example sample). If you
intentionally share one texture between the base-color and metallic slots, set it
to `false` to keep the metallic map as authored.

## Diagnostics

The runtime API validates the platform native payload before the first P/Invoke
call. Missing `UnityUSDToolkitNative` plugin files, OpenUSD dylib/DLLs, TBB
dylib/DLLs, or OpenUSD `plugInfo.json` resource folders produce a
`UsdExportException` with the checked base paths, native library search
environment, and `PXR_PLUGINPATH_NAME`.

Use `ExportGameObjectWithResult(...)` when you need a summary for logs or UI:

```csharp
UsdExportResult result = UsdExporter.ExportGameObjectWithResult(root, path, options);
Debug.Log($"{result.MeshCount} meshes, {result.VertexCount} vertices -> {result.OutputPath}");
```

If `CaptureNativeDiagnostics` is enabled, Pixar OpenUSD warnings/errors/status
messages issued through `TfDiagnosticMgr` are captured and can be written to
`NativeDiagnosticsLogPath`. Mesh readability errors include the GameObject path,
mesh name, and, in the Editor, the asset path.

## Sample

The **Runtime Export Example** scene ships in `Samples/Export Example` and compiles
with the package. Add `UsdExportExample` to a GameObject, then call `ExportUsd()`
from UI, script, or the component context menu. By default it writes:

```text
Application.persistentDataPath/runtime-usd-export.usdc
```

## Live Sync Example

`Samples/Live Sync Example` adds a bidirectional, near-real-time transform channel between a running
Unity scene and an external tool — a Python script, a DCC, or NVIDIA Isaac Sim — with USD as the
on-disk record. It is a **sample**, not part of the supported runtime API below: it is built entirely
on the public `UsdExporter` surface and needs nothing from `Runtime/` that is not already documented
here.

Unity is the host. On start the sample exports the scene's geometry **once** as `base_stage.usda`
through `UsdExporter` (`PreserveHierarchy`), then streams only transform changes over loopback TCP as
newline-delimited JSON. Clients observe that stream, push edits back, and can checkpoint the live
state to a USD override layer that sublayers the baseline — so the heavy geometry is written once and
the live channel stays small.

Open `Samples/Live Sync Example/LiveSyncExample.unity`, press Play, then from the sample's `Tools~`
folder:

```sh
python usd_live_sync.py --watch                                           # stream the live transforms
python usd_live_sync.py --set /SyncRoot/PropCube --translate=0.5,1.2,-0.4 # push an edit back
python usd_live_sync.py --checkpoint                                      # write live_overrides.usda
```

`base_stage.usda` and `live_overrides.usda` land in `<project>/UsdSync` in the Editor, or
`<persistentDataPath>/UsdSync` in a player. Writing the override layer needs OpenUSD's Python
bindings (`pip install usd-core`); streaming and pushing edits use the standard library only.

### Components

| Type | Role |
| --- | --- |
| `UsdLiveSyncServer` | Prim-path table, baseline export, TCP listener, throttled dirty-diff broadcast, inbound commands. |
| `UsdSyncNode` | Per-object ownership marker plus position / rotation / scale toggles. |
| `UsdLiveSyncSample` | Builds the demo hierarchy from primitives, starts the server, draws the runtime HUD. |

`UsdLiveSyncServer` assigns prim paths with the same name sanitizer and hierarchy walk the exporter
uses, so a path on the wire names the same prim as in `base_stage.usda`. The agreement is
cross-checked against the export result at start and logged, rather than failing silently later when
a client authors an `over` that binds to nothing.

Set `trackMode` to `ExplicitNodesOnly` to track just the transforms carrying a `UsdSyncNode` — that
syncs a character's root without dragging in its animated skeleton. Prim paths are still computed
against the full hierarchy, so a tracked node's path still matches the baseline.

### Ownership

Every tracked object is streamed **out** regardless of `UsdSyncNode`. An inbound `set_transform` is
applied **only** where `UsdSyncNode.AcceptsRemoteWrites` is true; anything else is counted as
`ignored` in the ack. Without that rule an external edit to a Unity-authoritative object — a physics
body, an animation, anything under scripted motion — would be overwritten by Unity on the next frame
and immediately re-broadcast, fighting the client. After applying an inbound edit the server records
the applied value as that node's last-sent state, so the next diff pass does not echo it back.

### Coordinate conversion

The wire carries **raw Unity local-space values** (translate `float[3]`, rotation quaternion
`[x, y, z, w]`, scale `float[3]`). `base_stage.usda`, by contrast, is written with the exporter's
Unity-to-USD X-axis flip, so a client authoring USD must apply the same flip or its overrides
visually disagree with the baseline geometry:

| Component | Unity to USD |
| --- | --- |
| translate | negate X: `(-x, y, z)` |
| rotation | `(x, y, z, w)` becomes `Gf.Quatf(w, x, -y, -z)` |
| scale | unchanged |

`Tools~/usd_live_sync.py` does this in `unity_to_usd_translate` / `unity_to_usd_quat`.

### Isaac Sim

`Tools~/isaacsim/` holds a Kit extension, a standalone runner, and a mock Unity server for developing
a client without the Editor running. NVIDIA® Isaac Sim™ joins as just another client of the same port
and the wire schema is unchanged, so it and `usd_live_sync.py` can be connected at the same time.

Isaac Sim is **not included** and nothing NVIDIA-owned is redistributed: the extension calls
NVIDIA-provided Python APIs resolved at run time from an Isaac Sim installation you obtain from
NVIDIA under NVIDIA's own license terms, and it never writes into the Isaac Sim install directory.
See [License And Notices](#license-and-notices), `ThirdPartyNotices.md`, and
`Samples/Live Sync Example/Tools~/isaacsim/README.md`.

`Tools~` ends in a tilde so Unity does not import the Python and batch files as assets; the folder
still ships with the package.

### Security

This is a development tool. The server binds `127.0.0.1` by default — keep it there. It performs no
authentication and applies transform writes from any connected client, so do not expose the port
beyond loopback.

## Validation Checklist

1. Confirm `Runtime/Plugins/x86_64/Windows/UnityUSDToolkitNative.dll` exists after
   running `Native~/build_windows.ps1`, or `Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib`
   exists after running `Native~/build_macos.sh`.
2. Confirm the same platform folder also contains OpenUSD dylib/DLLs and
   `plugin/usd/plugInfo.json`. For OpenUSD 26.05 monolithic builds, also confirm
   `lib/usd/plugInfo.json`.
3. Open Unity on the target desktop platform and check that the package compiles
   without errors.
4. In Play Mode, export a Cube or readable mesh with `UsdExportExample`.
5. Run `usdchecker <file>` or `usdcat <file>` from the same OpenUSD install.
6. Build Windows Standalone x64 or macOS Standalone x64/arm64/Universal and
   repeat the same export in the built player.
7. Test an invalid `PluginSearchPath` and confirm the exception includes the
   missing directory and plugin search diagnostics.
8. Export a multi-submesh mesh and confirm `GeomSubset` material bindings with
   `usdcat`.
9. Export a mesh with more than 65k vertices and run `usdchecker` on the result.
10. Export once with `TransformPolicy = UsdTransformPolicy.PreserveHierarchy`
    and confirm nested `Xform` prims plus matrix `xformOp:transform` entries with
    `usdcat`.
