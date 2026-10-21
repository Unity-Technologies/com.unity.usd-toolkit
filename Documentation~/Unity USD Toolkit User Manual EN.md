# Unity USD Toolkit User Manual

Read and write OpenUSD stages from Unity at runtime, on Windows, macOS and Linux.

| | |
| --- | --- |
| **Package version** | 0.8.1-exp.1 |
| **Unity** | 6.4 and newer |
| **OpenUSD** | 26.05 |

[한국어](Unity%20USD%20Toolkit%20User%20Manual%20KR.md)

## Contents

1. [Overview](#1-overview)
2. [Supported platforms and features](#2-supported-platforms-and-features)
3. [Install the package](#3-install-the-package)
4. [Export at runtime](#4-export-at-runtime)
5. [Import at runtime](#5-import-at-runtime)
6. [Threading](#6-threading)
7. [Materials and textures](#7-materials-and-textures)
8. [Untrusted USD files](#8-untrusted-usd-files)
9. [Build a standalone player](#9-build-a-standalone-player)
10. [The USD Live Sync sample](#10-the-usd-live-sync-sample)
11. [The AGV Fleet LiveSync Demo sample](#11-the-agv-fleet-livesync-demo-sample)
12. [The Isaac Robot Physics sample](#12-the-isaac-robot-physics-sample)
13. [Check exported USD files](#13-check-exported-usd-files)
14. [Troubleshooting](#14-troubleshooting)
- [Appendix A. API reference](#appendix-a-api-reference)
- [Appendix B. Verify the package's integrity](#appendix-b-verify-the-packages-integrity)
- [Appendix C. Rebuild the native plugin](#appendix-c-rebuild-the-native-plugin)

## 1. Overview

The Unity USD Toolkit writes and reads USD files inside a running Unity application, in the Editor and in built players. It converts GameObjects to `.usd`, `.usda`, `.usdc` and `.usdz` files, and loads USD stages back into GameObjects. Through OpenUSD's file format plugins it also reads and writes Alembic (`.abc`) and Draco (`.drc`) meshes.

The package has three layers:

- **C# API.** The public API in the `Unity.USDToolkit` namespace.
- **Native plugin.** `UnityUSDToolkitNative`, a C++ library that wraps the Pixar OpenUSD API behind a C interface. The C# API calls it through P/Invoke.
- **OpenUSD.** Pixar's OpenUSD 26.05 libraries and oneTBB, built for each platform.

The package does not depend on Unity's other USD packages or on USD.NET.

The C# API checks the API version of the native plugin when it loads. It accepts any plugin from 0.8.0 or newer (`UsdNative.MinimumApiVersion`) and refuses an older one, because fixes that aren't gated on the API version are missing from an older binary. Features newer than the loaded plugin are skipped while the rest of the import works. See [Native plugin versions](#native-plugin-versions).

## 2. Supported platforms and features

### Platforms

| Platform | Editor | Player | Minimum OS |
| --- | --- | --- | --- |
| Windows x64 | Yes | Yes | Windows 10 version 21H1 |
| macOS, Universal (x86_64 + arm64) | Yes | Yes | macOS 12.0 |
| Linux x64 | Yes | Yes | Ubuntu 22.04 |
| Mobile, WebGL, consoles | No | No | — |

The minimum OS applies to players you build as well as to the Editor, because the same native libraries are copied into the player.

On Linux, the native libraries require glibc 2.34 or later and libstdc++ with `GLIBCXX_3.4.29` (GCC 11). Ubuntu 22.04 and newer meet both. When the requirements aren't met, the toolkit throws an error that names the missing requirement.

On Windows, the target machine needs the Microsoft Visual C++ Redistributable.

### Export

| Feature | Supported | Notes |
| --- | --- | --- |
| Static meshes | Yes | GameObjects with a `MeshFilter` and `MeshRenderer`. |
| Meshes without **Read/Write Enabled** | Yes | Read back from the GPU in Play mode. |
| Normals and UV0 | Yes | Each can be turned off. |
| Per-submesh materials | Yes | Authored as `UsdGeomSubset`. |
| Transform hierarchy | Yes | With `UsdTransformPolicy.PreserveHierarchy`. |
| Baked transforms | Yes | The default. |
| `UsdPreviewSurface` materials | Yes | Base color, opacity, metallic, roughness, emission. |
| PBR textures | Yes | Written as PNGs to `<usd-name>_textures/`. |
| `.usdz` packages | Yes | With an optional ARKit-compatible mode. |
| Alembic (`.abc`) | Yes | Flattened only; skinned meshes are baked (every frame in a recording), and the scene is written in centimeters. No materials. Needs the usdAbc plugin. See [Alembic and Draco files](#alembic-and-draco-files). |
| Draco (`.drc`) | Yes | One mesh: every visible mesh merged, skinned meshes in their pose, in centimeters. No hierarchy, materials or animation. Needs the 0.8.1 native plugin (the Windows, macOS and Linux payloads). See [Alembic and Draco files](#alembic-and-draco-files). |
| Inactive objects and disabled renderers | Yes | Authored as `visibility = "invisible"`. |
| Keep USD composition | Yes | Sublayers, references, instancing and overrides, with `UsdCompositionMode.Preserve`. See [Keep USD composition](#keep-usd-composition). Needs the 0.8.0 native plugin. |
| Skinned meshes | Yes | As UsdSkel with `PreserveHierarchy` and in `UsdCompositionMode.Preserve` exports; baked in their current pose otherwise. See [Export skinned meshes and animation](#export-skinned-meshes-and-animation). Needs the 0.8.1 native plugin. |
| Animation | Yes | Recorded with `UsdAnimationRecorder`, or with the Unity Recorder when com.unity.recorder is installed. Needs the 0.8.1 native plugin. |

### Import

| Feature | Supported | Notes |
| --- | --- | --- |
| `.usd`, `.usda`, `.usdc`, `.usdz` | Yes | Textures inside a `.usdz` are supported. |
| Alembic (`.abc`), Draco (`.drc`) | Yes | Through OpenUSD's usdAbc and usdDraco plugins, in the macOS, Windows and Linux payloads. See [Alembic and Draco files](#alembic-and-draco-files). |
| Static `UsdGeomMesh` | Yes | Smooth normals are generated when none are authored. Face-varying normals and UVs that match are merged into shared vertices. |
| Implicit surfaces | Yes | `Cube`, `Sphere`, `Cylinder`, `Cone`, `Capsule` and `Plane` become meshes. |
| `PointInstancer` | Yes | Each prototype is imported once and hidden. The instancer becomes one GameObject with a `UsdPointInstancer` component that draws every placement from the instance arrays with `Graphics.RenderMeshInstanced`, culled by cell. Instances can't be selected individually and get no collider. `invisibleIds` is honoured. See [Point instancers](#point-instancers). Needs the 0.8.0 native plugin. |
| Subdivision surfaces | Partly | The control cage is imported without subdividing, with a warning. |
| Transform hierarchy | Yes | Every transformable prim becomes a Unity `Transform`. Typeless and custom-schema prims become empty GameObjects. Prims with `proxy` or `guide` purpose are skipped. |
| Multiple materials per mesh | Yes | Each `materialBind` subset becomes a submesh. |
| `UsdPreviewSurface` materials and PBR textures | Yes | Albedo, normal, metallic-smoothness, emission. |
| MaterialX materials | Yes | `standard_surface`, `open_pbr_surface` and `UsdPreviewSurface` nodes, authored in the stage or in a referenced `.mtlx` file. See [Materials and textures](#7-materials-and-textures). |
| MDL materials | Partly | Colors and textures of `OmniPBR`, `OmniSurface` and `OmniGlass`. Other MDL materials aren't read. |
| Primvar colors | Yes | A `UsdPrimvarReader` on `diffuseColor`, such as `displayColor`, sets the base color. |
| Meshes without a material | Yes | Get a shared grey `UsdDefaultMaterial`. See `AssignDefaultMaterial`. |
| Shared meshes | Yes | Instances with identical geometry share one Unity `Mesh`. See `ShareIdenticalMeshes`. |
| Lights | Yes | UsdLux distant, sphere, disk, cylinder and rect lights become Unity `Light`s. Intensity mapping is heuristic. Needs the 0.8.0 native plugin. |
| Dome lights | Partly | Recorded on a `UsdDomeLight` component. With `ApplyDomeLightToEnvironment`, they set the ambient color and a panoramic skybox. Needs the 0.8.0 native plugin. |
| Cameras | Yes | `UsdGeomCamera` becomes a disabled Unity `Camera`. Needs the 0.8.0 native plugin. |
| Spatial audio | Partly | `UsdMediaSpatialAudio` becomes an `AudioSource` with a `UsdSpatialAudio` component. The clip isn't loaded. Needs the 0.8.0 native plugin. |
| Asynchronous import | Yes | Parses off the main thread and creates objects across several frames. `ImportAsync` returns before the textures are attached, and `UsdImportResult.TexturesReady` completes when they are (`StreamTextures`, on by default). |
| Statistics preview | Yes | Mesh, material, triangle and vertex counts before importing. |
| Folder scanning | Yes | `UsdLibraryScanner.ScanFolder`. |
| Up axis and unit conversion | Yes | Applied on the import root by default. See [Up axis and units](#up-axis-and-units). |
| Variant selection and payload loading | Yes | Choose variants and which payloads load. See [Variant sets and payloads](#variant-sets-and-payloads). Needs the 0.8.0 native plugin. |
| Composition, instancing | Yes | References, payloads, inherits, specializes and instances are composed; each instance becomes its own GameObjects. Instances with identical geometry share one `Mesh` (`ShareIdenticalMeshes`; `UsdImportResult.UniqueMeshCount` reports the distinct count). |
| Time samples | Yes | Values are read at the stage's start time, so transforms authored only as samples arrive in place. See [Skinning and animation](#skinning-and-animation). Needs the 0.8.1 native plugin. |
| Skinned meshes | Yes | UsdSkel skeletons become joint hierarchies and skinned meshes `SkinnedMeshRenderer`s, posed as OpenUSD skins them; dual-quaternion meshes get a `UsdDualQuaternionSkin`. Needs the 0.8.1 native plugin. |
| Blend shapes | Yes | Including inbetweens between 0 and 1. Needs the 0.8.1 native plugin. |
| Animation | Yes | Transforms, visibility, skeletons and blend-shape weights, played by `UsdAnimationPlayer`, with a Timeline track when com.unity.timeline is installed. Animated mesh points play as point caches. Needs the 0.8.1 native plugin. |
| Physics schemas | No | |

## 3. Install the package

1. Open **Window > Package Management > Package Manager**.
2. Select **+ > Add package from git URL**.
3. Enter `https://github.com/Unity-Technologies/com.unity.usd-toolkit.git`, then select **Add**.

To install from a local folder, copy the package to `<YourProject>/Packages/com.unity.usd-toolkit`, or select **+ > Add package from disk** and choose its `package.json`.

You can also add the package to `Packages/manifest.json`:

```json
"dependencies": {
  "com.unity.usd-toolkit": "https://github.com/Unity-Technologies/com.unity.usd-toolkit.git"
}
```

> [!NOTE]
> The native libraries are stored with Git LFS. If you clone the repository, run `git lfs install` and `git lfs pull` before you open the project. The plugin doesn't load while the libraries are LFS pointer files.

### Native files

Each platform's native files are in its own folder:

```text
Runtime/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_rt.dll
  tbb_usdrt.dll
  lib/usd/**/plugInfo.json
  plugin/usd/**/plugInfo.json

Runtime/Plugins/macOS/
  UnityUSDToolkitNative.dylib
  libusd_ms.dylib
  libtbb*.dylib
  lib/usd/…, plugin/usd/…

Runtime/Plugins/x86_64/Linux/
  libUnityUSDToolkitNative.so
  lib/libusd_ms.so, lib/libtbb.so.2
  lib/usd/…, plugin/usd/…
```

On Windows, the OpenUSD and oneTBB libraries are renamed so they don't conflict with other copies loaded in the same process, such as the Unity Editor's own `tbb.dll`.

### Native plugin versions

Every package release ships a native plugin, and the plugin reports an interface number through `RUsd_GetApiVersion`. The number goes up once per release that changes the native interface, not once per change, and the C# API names the release in its messages, not the number. It loads any plugin from 0.8.0 or newer (`UsdNative.MinimumApiVersion`) and uses each feature only when the loaded plugin has it:

| Release | Interface | Added | Constant in `UsdNative` |
| --- | --- | --- | --- |
| 0.8.0 | 12 | Composition on export, variant and payload control on import (the minimum accepted); lights, cameras and spatial audio on import; point instancers as prototype and matrix arrays; metallic and roughness map layouts; EXR decoding, block compression, whole textures and UDIM atlases in native code. | `CompositionApiVersion`, `SceneComponentsApiVersion`, `InstancedMeshApiVersion`, `MetallicLayoutApiVersion`, `TextureLayoutApiVersion`, `ImageCodecApiVersion`, `ImageBuildApiVersion` |
| 0.8.1 | 13 | Static values read at the stage's start time; animated tracks, skeletons, joint weights and blend shapes; the stage API (`UsdStage`) and live variant and payload switching; export to Draco (`.drc`); lights, cameras, extra UV sets, vertex colours and OmniPBR / MaterialX on export; albedo masks and Unreal-connector blends; texture colour spaces, normal-map flips and remote MDL modules. Without it, `UsdStage` throws `NotSupportedException`, a switch imports the whole stage again, and a `.drc` export is refused. | `AnimationApiVersion`, `StageApiVersion`, `DracoExportApiVersion`, `ExportExtrasApiVersion`, `AlbedoMaskApiVersion`, `UnrealBlendApiVersion`, `TextureColorSpaceApiVersion` |

A plugin built from the 0.8.1 sources before 2026-10-20 reports 14 to 17, numbers handed out during development; it loads as a 0.8.0 plugin until it is rebuilt.

## 4. Export at runtime

```csharp
using Unity.USDToolkit;
using UnityEngine;

public class ExportButton : MonoBehaviour
{
    [SerializeField] private GameObject exportRoot;

    public void Export()
    {
        string path = System.IO.Path.Combine(Application.persistentDataPath, "robot.usdc");

        UsdExportResult result = UsdExporter.ExportGameObjectWithResult(
            exportRoot,
            path,
            new UsdExportOptions
            {
                RootPrimName = "Robot",
                MetersPerUnit = 1.0f,
                TransformPolicy = UsdTransformPolicy.BakedMesh,
                ExportTextures = true,
                CaptureNativeDiagnostics = true,
            });

        Debug.Log(result.ToString());
    }
}
```

The file extension of the output path sets the format. For a `.usdz` path, the exporter writes the stage and its textures to a temporary folder and packages them, and leaves no other files next to the `.usdz`. An `.abc` path writes Alembic and a `.drc` path writes one Draco mesh; see [Alembic and Draco files](#alembic-and-draco-files).

### Choose a transform policy

| Policy | When to use it | Result |
| --- | --- | --- |
| `BakedMesh` | Most exports, when the downstream tool doesn't need the hierarchy. | Transforms are baked into mesh points, and the USD hierarchy is flat. |
| `PreserveHierarchy` | When the GameObject hierarchy and local transforms must be kept. | Each GameObject becomes an `Xform` prim, and mesh points stay in local space. |

With `BakedMesh`, each mesh is written in the export root's space. Positions, rotations and scale, including negative scale, are applied to the points, and triangle winding is corrected for the conversion from Unity's coordinate system to USD's.

With `PreserveHierarchy`, the export root becomes the USD root prim, and each child's local position, rotation and scale are written as an `xformOp:transform` matrix.

### Meshes without Read/Write Enabled

In Play mode, the exporter reads meshes without **Read/Write Enabled** back from the GPU, so you don't need to reimport shipped content. Set `RequireReadableMeshes = false` to allow this. GPU readback needs a graphics device; in batch mode or with `-nographics`, enable **Read/Write Enabled** on the meshes instead.

### Export skinned meshes and animation

These need the 0.8.1 native plugin.

- **Skinned meshes.** With `TransformPolicy = PreserveHierarchy`, each `SkinnedMeshRenderer` is written as UsdSkel. Renderers that share their bones become one character: the lowest object above all of them becomes a `SkelRoot`, with a `Skeleton` prim whose joints are the bones and every object between them and the SkelRoot. A skeleton this package imported keeps its own Skeleton prim and joint names. Each mesh keeps its bind-pose points, bone weights and blend shapes (inbetweens included), and its bind transforms are chosen so that OpenUSD skins it exactly as Unity draws it. The current pose becomes the skeleton's rest pose, and nonzero blend-shape weights go on a static `SkelAnimation`. Bones are also written as `Xform` prims, so objects attached to a bone keep their place. A mesh a `UsdDualQuaternionSkin` poses is written the same way, with `skinningMethod = "dualQuaternion"`.
- **Baked instead.** With `BakedMesh`, a skinned mesh is written as the static mesh it draws in its current pose. With `PreserveHierarchy` the same happens, with a warning, to a renderer whose mesh isn't readable, whose bones are outside the exported hierarchy, or whose bind poses don't match the other meshes on the same bones.
- **Override exports.** `UsdCompositionMode.Preserve` writes the skinned meshes added to an imported stage into the override layer; those that came from the source are kept by composition. A new character, with its own bones, becomes its own `SkelRoot` and `Skeleton`, as above. A mesh added to an imported character -- a garment weighted to its joints -- is bound to that character's existing `Skeleton` prim, so it follows the source's animation; it has to be inside the character's SkelRoot (anywhere below the Skeleton prim's parent) and bound in the skeleton's bind pose, and its blend-shape weights can't be animated, because they would go on the source's `SkelAnimation`. A renderer whose mesh and bones meet only at a prim of the source stage is written in its current pose, with a warning: making that prim a SkelRoot would change its type. Prefab assets written as their own layers get their skinned meshes the same way.
- **Point caches.** A flattened export writes a point-cached mesh in the frame on screen, as it writes a skinned mesh's current pose.
- **Record animation.** `UsdAnimationRecorder` records a hierarchy over time and saves it as an animated stage:

  ```csharp
  var recorder = new UsdAnimationRecorder(character, framesPerSecond: 30);

  // Once per frame, in Play mode or a player, with the time since recording started:
  recorder.Capture(Time.time - startTime);

  // When done:
  recorder.Save(Path.Combine(Application.persistentDataPath, "take.usdc"));
  ```

  `CaptureClip(clip)` samples an `AnimationClip` on the root, and `CapturePlayer(player)` plays a `UsdAnimationPlayer` through, one frame at the recorder's rate each, and then put the pose back. `Save` writes a flattened stage with `PreserveHierarchy`. The first frame is the stage's static pose. Transforms and active states that change become time samples on their prims; only what changes is kept. Visibility follows each object's own active state, which USD inherits the way Unity's active hierarchy does, and each renderer's `enabled` state, on its mesh prim. A mesh drawn through a vertex stream (a point cache a `UsdAnimationPlayer` plays) has its points, and normals, sampled. Each skinned character's bone transforms and blend-shape weights become a `SkelAnimation`. Time codes are frames at the recorder's rate, starting at 0.
- **Record as overrides.** Recording an imported stage, say an Isaac Sim robot, writes the whole model by default. With `recorder.Save(path, new UsdExportOptions { Composition = UsdCompositionMode.Preserve })` the recording is a layer over the source file instead, written the way a `Preserve` export writes the hierarchy: it sublayers the source when the recorded object is the import root, and references it when the import sits below the recorded object. The samples go on the source's own prims: a moved prim gets its transform as time samples, a shown or hidden one its visibility, and an imported character's Skeleton prim a new `SkelAnimation` (bones and blend-shape weights) as its `skel:animationSource`. Nothing of the source is copied, so its geometry, materials and physics stay as they are; Franka FR3 (17 MB) records 61 frames of seven moving links into 101 KB, against 19.8 MB flattened. The layer carries the time range and rate. An imported mesh's points are left to the source (they are the source's topology, not Unity's split vertices), with a warning when they moved. Objects added in Unity are written as new content, with their samples. Alembic cannot hold overrides, so `.abc` is refused.
- **Unity Recorder.** With com.unity.recorder 5.0 or newer in the project, the `Unity.USDToolkit.Recorder.Editor` assembly adds a **USD Animation** recorder to the Recorder window and to Timeline Recorder clips. Pick the GameObject to record, a format (`.usdc`, `.usda` or `.usdz`), whether to export textures and whether to write **Overrides Only** (the layer above, over the imported file the GameObject came from), then record in Play mode. At a constant frame rate, each recorded frame is one time code. A recording saved under `Assets` is imported as an asset. The Recorder captures at the end of each rendered Game view frame, so it records nothing in batch mode.
- **Precision.** UsdSkel stores joint scales as half floats, so an animated joint scale comes back to about three significant digits. Positions and rotations keep single precision.

### Keep USD composition

By default the exporter writes every object out in full, into one layer. Set `Composition = UsdCompositionMode.Preserve` to keep USD composition instead. Three kinds of content are then written as composition rather than as copies.

**An imported stage is written back as overrides on its source file.** The importer adds a `UsdStageSource` component to the root it creates and a `UsdPrimSource` component to every object, recording the prim each object stands for and the state it arrived in. When the export root is that import root, the output sublayers the source file and holds only what changed in Unity:

| Change in Unity | Written as |
| --- | --- |
| Moved, rotated or scaled an object | `over` with an `xformOp:transform` |
| Deactivated an object, or disabled a renderer | `visibility = "invisible"` |
| Deleted an object | `active = false` on the prim |
| Added an object | A new prim under its parent's prim |
| Imported with a variant selection | That variant selection |
| Changed an object below an instance | `instanceable = false` on the instance, then the change |
| Replaced a mesh or its materials | `active = false` on the source prim and a new prim beside it |

```csharp
UsdImportResult imported = UsdImporter.Import(sourcePath);
// ... the user edits imported.RootObject ...
UsdExporter.ExportGameObjectWithResult(imported.RootObject, editedPath, new UsdExportOptions
{
    Composition = UsdCompositionMode.Preserve,
});
```

The output looks like this, and every reference, payload, variant set, inherit and specialize in the source survives untouched:

```usda
#usda 1.0
(
    defaultPrim = "World"
    subLayers = [@./scene.usda@]
)

over "World"
{
    over "Robot" { matrix4d xformOp:transform = ( ... ) ... }
    over "Heavy" ( active = false ) { }
}
```

The output depends on the source file, so keep them together, or export to `.usdz`, which packages the source with it. The output can't replace its own source file. Objects are identified by their prim path, not their name, so renaming an object changes nothing. Moving an imported object under another parent is written as a relocate when the output references the source -- the import sits below the export root -- so the source prim, with everything its composition brings, is composed at the new place and the object's edits are written there; a new parent made in Unity is written too. A copy of an imported object, with the original still in place, is new content. When the export root is the import root, the output sublayers the source, whose prims are then the output's own and can't be relocated (OpenUSD only relocates prims that come in through a reference or payload), so the original prim is deactivated and the object is written as new content where it now is. `UsdExportResult.RelocateCount` reports the relocates. Needs the 0.8.1 native plugin.

When an imported stage sits deeper in the exported hierarchy, or `ImportedContent = UsdImportedContentMode.ReferenceSource`, each object that stands for one of the stage's root prims references that prim of the source file (the default prim by the file's default reference, any other root prim by its path), and the changes are written as overrides through those references. `UsdImportedContentMode.Inline` writes imported objects like any others.

**Prefab instances become references to one asset per prefab.** The prefab is written once, and each instance references it. What an instance changes (a moved child, a hidden child, an added or removed child, a different mesh or material) is written as overrides on that instance. An instance with no changes is also marked `instanceable`. `Layout` controls where the prefab assets go:

| Layout | Result |
| --- | --- |
| `SingleFile` (default) | One file. Prefab assets are `class` prims under `/UnityPrototypes`, referenced internally. |
| `FolderWithAssets` | The scene file, plus one file per prefab in `<usd-name>_assets/`, each referenced by a relative path. |

An output path ending in `.usdz` packages either layout, asset files included.

Unity only keeps the link between a prefab and its instances in the Editor. To carry it into Play mode and players, the package adds a `UsdPrefabMarker` component to every prefab instance root of a scene when the scene is built or enters Play mode. Only that copy of the scene changes; scene files and prefabs on disk don't. For a prefab instantiated at runtime, use `UsdPrefabMarker.Instantiate(prefab, parent)` instead of `Object.Instantiate`, or call `UsdPrefabMarker.Attach(instance, prefab)`. A plain `Object.Instantiate` copy has no link and is exported as ordinary objects. In the Editor outside Play mode, the exporter reads the prefab link directly. To turn off the automatic markers, define the scripting symbol `USD_TOOLKIT_DISABLE_PREFAB_MARKERS`.

**Repeated meshes become instances of one prototype** when `ShareMeshes = true`. Objects that share one `Mesh` and the same materials reference a single prototype mesh and are marked `instanceable`.

`UsdExportResult` reports `ReferenceCount`, `OverrideCount`, `PrototypeCount`, the `SourceLayer` an override export depends on, and the `AssetFiles` it wrote.

`Preserve` always keeps the hierarchy, whatever `TransformPolicy` says. It needs the 0.8.0 native plugin; with an older native plugin, the export throws an exception that says so.

### Export options

| Option | Default | Description |
| --- | --- | --- |
| `RootPrimName` | `null` | The root prim name. Uses the export root's name when empty. |
| `MetersPerUnit` | `1.0f` | The stage's `metersPerUnit`. Must be greater than zero. |
| `TransformPolicy` | `BakedMesh` | `BakedMesh` or `PreserveHierarchy`. |
| `IncludeInactive` | `false` | Includes inactive child GameObjects. |
| `RequireReadableMeshes` | `true` | Throws an exception for meshes without **Read/Write Enabled**. Set to `false` to read them from the GPU. |
| `ExportNormals` | `true` | Writes normals when the mesh has them. |
| `ExportUv0` | `true` | Writes UV0 when the mesh has it. |
| `ExportBounds` | `true` | Writes the mesh extent. |
| `ExportDisabledRenderers` | `true` | Includes disabled renderers. |
| `PreserveInactiveAndDisabledVisibility` | `true` | Marks included inactive objects and disabled renderers as `visibility = "invisible"`. |
| `ExportTextures` | `false` | Writes PNG textures to `<usd-name>_textures/`. |
| `IgnoreAlbedoInMetallicSlot` | `true` | Ignores a metallic map that is the same texture as the albedo map. See [Albedo texture in the metallic slot](#albedo-texture-in-the-metallic-slot). |
| `UsdzArkitCompatible` | `false` | For `.usdz` output, packages the file for AR Quick Look. This can remove features such as variant sets. |
| `ValidateNativeRuntime` | `true` | Checks that the native files are present. |
| `VerifyNativeRuntimeIntegrity` | `true` | Checks the SHA-256 digest of each native file before loading it. Can be disabled only in the Editor and development builds. |
| `ValidateOpenUsdPluginPath` | `true` | Checks the OpenUSD plugin paths. |
| `PluginSearchPath` | `null` | Overrides where OpenUSD looks for plugins. See [Plugin search path](#plugin-search-path). |
| `CaptureNativeDiagnostics` | `false` | Captures OpenUSD warnings and errors. |
| `NativeDiagnosticsLogPath` | `null` | Writes captured diagnostics to this file. |
| `LogExportSummary` | `false` | Logs a summary after a successful export. |
| `Composition` | `Flatten` | `Flatten` writes every object in full. `Preserve` keeps USD composition. See [Keep USD composition](#keep-usd-composition). |
| `Layout` | `SingleFile` | With `Preserve`: `SingleFile` or `FolderWithAssets`. |
| `ImportedContent` | `OverrideSource` | With `Preserve`: how imported stages are written. `OverrideSource`, `ReferenceSource` or `Inline`. |
| `ExportPrefabsAsReferences` | `true` | With `Preserve`: writes each prefab once and references it from its instances. |
| `ShareMeshes` | `false` | With `Preserve`: objects that share a mesh and materials reference one prototype. |

## 5. Import at runtime

Preview a stage, then import it:

```csharp
UsdImportPreviewInfo preview = UsdImporter.GetPreviewInfo(path);
if (preview.TriangleCount > 5_000_000)
{
    // Warn the user, or let them cancel, before a large import.
}

UsdImportResult result = await UsdImporter.ImportAsync(path, new UsdImportOptions
{
    Parent = transform,
    ImportMaterials = true,
    ImportTextures = true,
    GenerateColliders = false,
    ProgressCallback = (fraction, phase) => Debug.Log($"{phase} {fraction:P0}")
});
```

`ImportAsync` reads the stage on a worker thread and creates GameObjects on the main thread, spread across frames so the application stays responsive. Call it from the main thread. To import synchronously, use `UsdImporter.Import`.

By default (`StreamTextures = true`), `ImportAsync` returns as soon as the hierarchy, meshes and untextured materials exist, and attaches the textures as they finish decoding on a worker thread. Until then a material shows its authored constant values. Before you export, capture, save a prefab or read material textures, await `UsdImportResult.TexturesReady`:

```csharp
UsdImportResult result = await UsdImporter.ImportAsync(path);
await result.TexturesReady; // every texture is on its material
UsdExporter.ExportGameObject(result.RootObject, outputPath);
```

`TexturesReady` never faults. A texture that fails to load is logged as a warning, and its material keeps its constant values. If the import root is destroyed before the textures arrive, the remaining textures are dropped and their memory is freed. To have `ImportAsync` return only after everything, textures included, is loaded, set `StreamTextures = false`.

To list the USD files in a folder, with their thumbnails:

```csharp
IReadOnlyList<UsdLibraryItem> items = UsdLibraryScanner.ScanFolder(folderPath);
```

### Up axis and units

The importer converts from USD's coordinate system to Unity's and, with `ApplyStageUpAxisAndUnits = true` (the default), applies the stage's `upAxis` and `metersPerUnit` to the import root, so the content arrives Y-up and in meters. A Z-up stage, such as one from Isaac Sim, gets a -90 degree rotation about X on the root, and the root is scaled by `metersPerUnit` (0.01 for a centimeter stage).

The correction sits only on the root's local rotation and scale. Vertices and every prim's local transform stay exactly as authored, so a `Preserve` export writes no override for it. `UsdImportResult.StageCorrectionRotation` and `UsdImportResult.StageCorrectionScale` report what was applied, and `UpAxis` and `MetersPerUnit` are still returned in `UsdImportPreviewInfo` and `UsdImportResult`.

To get the stage's raw coordinates under an identity root, as imports did before, turn the correction off:

```csharp
UsdImportResult result = await UsdImporter.ImportAsync(path, new UsdImportOptions
{
    ApplyStageUpAxisAndUnits = false
});
```

### Alembic and Draco files

OpenUSD reads Alembic (`.abc`) and Draco (`.drc`) files through its usdAbc and usdDraco file format plugins. They are in a payload whose OpenUSD was built with them (`Native~/build_openusd.py`): macOS, Windows and Linux. On Windows, Draco needs a pinned patch, since OpenUSD's own build refuses it in a monolithic Windows library. Without the plugin, opening such a file fails with an error.

- **Alembic import.** An `.abc` opens like a stage: meshes with normals and texture coordinates, transforms, and their animation (transform tracks, and animated points as point caches), played by a `UsdAnimationPlayer`. Materials and their bindings aren't read.
- **Alembic units.** Alembic has no units. OpenUSD reads it as centimeters (`metersPerUnit` 0.01), as Unity's Alembic package does by default, so the import root gets a 0.01 scale. A file written in meters, which is Blender's default, arrives at 1/100 size: turn `ApplyStageUpAxisAndUnits` off, or scale the root.
- **Draco.** A `.drc` file holds one compressed mesh. A stage references or payloads it (`references = @./mesh.drc@`), and OpenUSD decodes it into a `Mesh` prim. Only files written by OpenUSD's own Draco writer name their attributes as usdDraco needs; a file from Draco's `draco_encoder` tool fails with "Draco mesh has no valid positions".
- **Alembic export.** An output path ending in `.abc` writes Alembic through usdAbc, from `UsdExporter` and from `UsdAnimationRecorder.Save`. Alembic has no skeletons, so skinned meshes are written in their current pose, and a recording bakes them every frame into sampled points. It has no composition either, so a `UsdCompositionMode.Preserve` export is refused. The root prim scales the scene from Unity's meters into centimeters, so OpenUSD, Unity's Alembic package and Maya read it at its size. Alembic keeps no material bindings and no `metersPerUnit`, and `ExportTextures` writes nothing for it.
- **Draco export.** An output path ending in `.drc` writes one Draco-compressed mesh through usdDraco's writer (the 0.8.1 native plugin). A Draco file holds a single mesh with no hierarchy, so every visible mesh is merged into one in the export root's space, with normals and the primary texture coordinates when every mesh has them. Skinned meshes are written in their current pose, and the scene in centimeters, as for Alembic. Positions are quantized to 14 bits, texture coordinates to 12 and normals to 10. Materials, animation and composition aren't kept: a recording or a `UsdCompositionMode.Preserve` export to `.drc` is refused.
- **Project assets.** `UsdStageImporter` imports `.abc` and `.drc` files. When Unity's Alembic package (com.unity.formats.alembic) is installed, it leaves `.abc` to that package and is offered as an alternative importer.

### Variant sets and payloads

Choose a variant before the stage is composed, and choose which payloads load. The source file isn't changed.

```csharp
var options = new UsdImportOptions { LoadPayloads = false };
options.VariantSelections.Add(new UsdVariantSelection("/World/Robot", "color", "red"));
options.PayloadsToLoad.Add("/World/Robot/Arm");

UsdImportResult result = UsdImporter.Import(path, options);
```

Selections apply in list order, so a variant set nested inside another set's variant can follow the outer selection. A selection that doesn't exist is skipped with a warning in the diagnostics. With `LoadPayloads = false`, only the prims in `PayloadsToLoad` load their payloads. An unloaded payload's own prim still becomes an empty GameObject, so it keeps its place in the hierarchy.

`result.Source` (a `UsdStageSource`) lists what the stage offers: `VariantSets` with their variants and current selection, `Payloads` and whether each was loaded, and the direct references, payloads, inherits, specializes and relocates in `Arcs`. To switch a variant or the loaded payloads at runtime, ask the source to switch:

```csharp
// Selects the variant and rebuilds only what it recomposes, in place.
UsdSwitchResult switched = result.Source.SwitchVariant("/World/Robot", "color", "blue");

// Off the main thread, without a hitch; the old parts stay until the new ones are built.
switched = await result.Source.SwitchVariantAsync("/World/Robot", "color", "blue");

// All payloads, none, only some, or one at a time.
result.Source.SwitchPayloads(false, new[] { "/World/Robot/Arm" });
result.Source.LoadPayload("/World/Robot/Leg");
```

A switch is made on the stage the import keeps open (`UsdStageSource.Stage`), in its session layer, so no file changes. OpenUSD reports which prims the change recomposed, and only their subtrees are imported again and put where the old ones were: under the same parent, at the same sibling index. A subtree inside a SkelRoot or a point instancer's prototype is widened to the whole SkelRoot or instancer, so skinning and instancing stay whole. Everything else in the import stays as it is, with its edits:

- **The rebuilt prim's own object.** A hand move, rename or deactivation carries over to its new object.
- **Inside the rebuilt subtree.** The objects are the stage's again; edits made to them are gone. Objects you added below them move to the new object of the prim they were under, or to the subtree's root when that prim is gone.
- **Meshes and materials.** The old subtree's meshes, materials and textures that nothing else uses are destroyed, when the import was made in this session; an asset's are left alone.
- **Animation.** The subtree's tracks are replaced in the import's `UsdAnimationPlayer`, which keeps playing at its time.

`UsdSwitchResult` says what happened: `RebuiltPrimPaths`, or `Reimported` with a `ReimportReason` when the switch had to import the whole stage again in the import's place, as `Reimport` does. That happens when the change reaches past one subtree (a material or a skeleton animation, which prims elsewhere bind to), when a file the import read changed on disk since, for a prefab instance in Edit Mode (which can't lose part of itself), and on a plugin older than 0.8.1. A reimport destroys the old root; use `switched.RootObject` afterwards. Settings come from the options the import ran with (`UsdStageSource.ImportOptions`, known for an import made in this session), from a template passed as the last argument, or from the defaults. A prim, set or variant the stage doesn't have throws `ArgumentException`, and nothing changes.

The source file has to be where `SourcePath` says, so switching works for runtime imports from disk and for anything in the Editor; in a player, an asset has no USD file behind it. The stage is opened on first use and closed with the component; `UsdImportOptions.KeepStageOpen` keeps the import's own stage instead, so the first switch doesn't open it again, at the cost of its memory while the import lives. A rebuilt subtree is held to the import's limits less what the rest of the import already holds.

To build the options without importing, use `CreateOptionsWithVariant(primPath, set, variant, options)` or `CreateOptionsWithPayloads(loadPayloads, payloadsToLoad, options)` and pass them to `Reimport` or `UsdImporter.Import`. Without the last argument, the rest are the defaults.

A `Preserve` export writes the import's variant selections back, so the output shows the variants that were chosen in Unity.

### Read and edit stages from C#

`UsdStage` opens a stage to read and edit without importing it, and `UsdStageSource.Stage` gives the stage an import keeps, read-only:

```csharp
using (UsdStage stage = UsdStage.Open(path))
{
    foreach (UsdPrim prim in stage.Traverse())
    {
        if (prim.TypeName == "Mesh")
        {
            Vector3[] points = prim.GetAttribute("points").Get<Vector3[]>(new UsdTimeCode(24));
        }
    }

    UsdPrim robot = stage.GetPrimAtPath("/World/Robot");
    robot.GetAttribute("primvars:displayColor").Set(new[] { Color.red });
    robot.SetVariantSelection("color", "blue");
    stage.Save();
}
```

- **Stages.** `UsdStage.Open(path, loadPayloads)`, `CreateNew(path)` (written by `Save`, in the format the extension names) and `CreateInMemory()` (written by `Export`). `Export(path, flatten)` writes the root layer, or the composed stage as one layer. `Reload()` rereads the files. Stage metadata: `StartTimeCode`, `EndTimeCode`, `TimeCodesPerSecond`, `FramesPerSecond`, `MetersPerUnit`, `UpAxis`, `DefaultPrim`, `LayerStack`, `HasUnsavedChanges`.
- **Prims.** `GetPrimAtPath`, `Traverse`, `DefinePrim`, `OverridePrim`, `CreateClassPrim`, `RemovePrim`. A `UsdPrim` is a path on a stage and keeps nothing alive; `IsValid` says whether the stage composes it now. It has its type, specifier, flags (`IsActive`, `IsLoaded`, `IsInstance`, ...), kind and metadata, `Children`, `Attributes`, `Relationships` and `VariantSets`, and edits: `SetActive`, `SetInstanceable`, `AddVariant`, `SetVariantSelection`, `AddReference`, `AddPayload`, `AddInherit`, `AddSpecialize`, `Load`, `Unload`, `SetLocalTransform`.
- **Values.** `UsdAttribute.Get<T>(time)` and `Set<T>(value, time, typeName)` read and write a value as C# and Unity types: `bool`, integers, `float`, `double`, `string` (string, token and asset), `Vector2`/`3`/`4` (any role: point, normal, color, texCoord), `Color` (color3, with alpha 1, or color4), `Quaternion`, `Matrix4x4`, `Vector2Int`, `Vector3Int`, and arrays of each. Precision converts (a double3 reads as a `Vector3`, a half writes from a float); shape and kind never do: an int isn't read as a float, a float3 never as a float4, and asking throws `UsdStageException`. `GetComponents` reads any real-valued attribute at full precision. `GetTimeSamples`, `Block`, `ClearAt` and `Clear` handle time samples; a blocked attribute reads its schema fallback, or no value without one. `GetResolvedPath` resolves an asset path.
- **Edit targets.** Edits go to `UsdStage.EditTarget`: `UsdEditTarget.RootLayer` (the default), `SessionLayer` (changes what this stage composes without touching a file), `Layer(identifier)` for a sublayer, and `.InVariant(prim, set, variant)` to author inside a variant, which must be the selected one, as OpenUSD requires.
- **Coordinates.** Values are USD's own: right-handed and in stage units. `UsdCoordinates.ToUnity` and `FromUnity` convert points, rotations and matrices to Unity's basis the way the importer does (X mirrored); units and the up axis are on the import root.
- **Lifetime and threads.** Dispose a stage when done; the native stage is also released when the Editor reloads scripts or the player quits. One stage may be used from any thread, one call at a time.
- **Shared layers.** OpenUSD shares a file's layers between every stage in the process that uses them, imports included: an unsaved edit is seen by any other stage, or import, of the same file until it is saved or reloaded.

`UsdStageSource.Stage` is read-only (`IsReadOnly`): it changes only through the source's switches, so the scene and a `Preserve` export of it keep agreeing with it. Open the file with `UsdStage.Open` to edit it. Needs the 0.8.1 native plugin.

### Point instancers

A `UsdGeomPointInstancer` is one prototype scattered to many placements, and the importer keeps it that way. Each prototype is imported once as a GameObject with its renderers off, and the instancer's prim becomes a GameObject with a `UsdPointInstancer` component that draws the prototype's `Mesh` with its `Materials` at every placement, with `Graphics.RenderMeshInstanced`. The placements are sorted into a grid of cells, each with its own bounds, so the cells that are off screen aren't drawn. Instances can't be selected individually and have no collider. A stage whose parts are selected one by one is better served by prims than by an instancer.

A skinned prototype keeps its skeleton below the prototype's object, where the stage's animation poses it. Its `UsdPointInstancer` has the prototype's renderer as `SkinnedSource`: each frame, after animation, it bakes the posed prototype and draws that mesh at every placement, so every instance plays the prototype's animation together, as OpenUSD renders them.

`UsdPointInstancer` exposes `Mesh`, `Materials`, `SkinnedSource`, `InstanceCount`, `LodDistance`, `CellCount` and `Draw`. The component calls `Draw` every frame, and it is public so a custom render loop or a benchmark can call it. An import accepts at most 4,000,000 placements (instances times prototype meshes) across its instancers, and is refused past that.

Every placement is drawn at full detail however far away it is. To use Mesh LOD, select the import root and choose **GameObject > USD > Generate Mesh LODs for Point Instancers** (`Unity.USDToolkit.Editor.UsdMeshLodGenerator`). It gives each prototype mesh up to four extra levels and sets `LodDistance` from the extent the placements cover. The level is chosen per cell from its distance to the main camera, reaching the least detailed level at `LodDistance`; zero always draws at full detail. Generating levels is Editor-only, because mesh decimation is an Editor API, but selecting a level works in players when the mesh already has levels. Decimation thins foliage visibly, so check the result on each stage.

`UsdImportResult.DefaultMaterial` holds the shared grey material given to meshes without a material binding, or `null` when no mesh needed it, and `UniqueMeshCount` the number of distinct `Mesh` objects the import created.

### Import options

| Option | Default | Description |
| --- | --- | --- |
| `Parent` | `null` | The parent `Transform` of the imported root. |
| `RootObjectName` | `null` | The root GameObject name. Uses the file name when empty. |
| `ImportMaterials` | `true` | Creates Unity materials from `UsdPreviewSurface`, MaterialX and MDL (`OmniPBR`, `OmniSurface`, `OmniGlass`) values. See [Materials and textures](#7-materials-and-textures). |
| `ImportTextures` | `true` | Loads the PBR textures that materials reference. |
| `VerifyNativeRuntimeIntegrity` | `true` | Checks the SHA-256 digest of each native file before the first call into the plugin. Honoured in the Editor and development builds only; a release player always verifies. |
| `CompressTextures` | `true` | Block-compresses imported textures on the worker thread, with mipmaps: BC1 for opaque images and BC3 for images with alpha. Turn it off for textures that carry data rather than appearance, such as identifiers or lookup tables, where compression gives a wrong answer. |
| `IncludeInvisible` | `true` | Also creates prims marked `visibility = "invisible"`. |
| `GenerateColliders` | `false` | Adds a `MeshCollider` to each mesh. |
| `RecalculateNormalsIfMissing` | `true` | Computes smooth normals when the file has none. |
| `AllowExternalAssetPaths` | `false` | Allows textures outside the stage folder. See [Untrusted USD files](#8-untrusted-usd-files). |
| `Limits` | the fixed budgets | Nodes, meshes, point-instancer placements, points and triangle indices, animation samples, values and joints an import may build, and whether a stage over one fails (`Fail`, the default) or imports what fits (`SkipWithWarning`). See [Import limits](#import-limits). |
| `MaxMillisecondsPerFrame` | `10f` | The time `ImportAsync` can spend on the main thread each frame. |
| `StreamTextures` | `true` | `ImportAsync` returns before textures are attached. Await `UsdImportResult.TexturesReady` for the final materials. Ignored by `UsdImporter.Import`. |
| `ProgressCallback` | `null` | Receives progress from `0` to `1` and the current phase, on the main thread. With `StreamTextures`, `ImportAsync` returns at `0.9` ("Streaming textures"), and `1` ("Done") is reported when the textures are attached. |
| `CaptureNativeDiagnostics` | `false` | Captures OpenUSD warnings and errors. |
| `NativeDiagnosticsLogPath` | `null` | Writes captured diagnostics to this file. |
| `VariantSelections` | empty | Variant selections to compose with, in order. See [Variant sets and payloads](#variant-sets-and-payloads). |
| `LoadPayloads` | `true` | When `false`, only the prims in `PayloadsToLoad` load their payloads. |
| `PayloadsToLoad` | empty | Payload prims to load when `LoadPayloads` is `false`. |
| `RecordSource` | `true` | Adds `UsdStageSource` and `UsdPrimSource` components, which a `Preserve` export needs to write only the changes. |
| `ApplyStageUpAxisAndUnits` | `true` | Applies the stage's `upAxis` and `metersPerUnit` to the import root. See [Up axis and units](#up-axis-and-units). |
| `AssignDefaultMaterial` | `true` | Gives a mesh or submesh with no material binding a shared grey lit material, `UsdDefaultMaterial`, instead of none, which renders magenta under URP and HDRP. Only used while `ImportMaterials` is on. |
| `ShareIdenticalMeshes` | `true` | Builds one Unity `Mesh` per distinct geometry and shares it among every imported mesh with the same points, indices, normals, UV sets and submesh ranges, as instanceable prims and `PointInstancer` instances produce. Editing `MeshFilter.sharedMesh` changes every object that uses it; `MeshFilter.mesh` gives an object its own copy. Turn it off to get one `Mesh` per prim. |
| `ImportLights` | `true` | Adds a Unity `Light` for each UsdLux distant, sphere, disk, cylinder and rect light. See [Lights, cameras and spatial audio](#lights-cameras-and-spatial-audio). |
| `ImportCameras` | `true` | Adds a disabled Unity `Camera` for each `UsdGeomCamera`, so the scene's existing camera keeps rendering until you enable one. |
| `ImportSpatialAudio` | `true` | Adds an `AudioSource` and a `UsdSpatialAudio` component for each `UsdMediaSpatialAudio`. The clip isn't loaded. |
| `ApplyDomeLightToEnvironment` | `false` | Applies a `UsdLuxDomeLight` to the scene: its color times intensity becomes the flat ambient color, and a PNG or JPG texture becomes a panoramic skybox. This changes `RenderSettings` for the whole scene. The dome is always recorded on a `UsdDomeLight` component. |
| `ReadAtStageStartTime` | `true` | Reads static values at the stage's `startTimeCode`, or at each attribute's first time sample when the stage has no time range. `false` reads at USD's Default time, which ignores samples. See [Skinning and animation](#skinning-and-animation). |
| `ImportSkinning` | `true` | Builds joint hierarchies, `SkinnedMeshRenderer`s and blend shapes for UsdSkel. `false` imports skinned meshes as static meshes in their authored points. |
| `DualQuaternionSkinning` | `true` | Skins meshes that ask for dual-quaternion skinning with a `UsdDualQuaternionSkin` (CPU, as UsdSkel does). `false` gives them a `SkinnedMeshRenderer` (GPU, linear), with a warning. |
| `KeepTexturesReadable` | `false` | Keeps each texture's CPU copy, so its pixels can be read back and it can be saved as an asset. Doubles texture memory. The asset importer turns it on. |
| `KeepStageOpen` | `false` | Keeps the stage open after the import as `UsdStageSource.Stage`, so the first live switch doesn't open it again; the stage's layers stay in memory while the import lives. Needs `RecordSource` and the 0.8.1 native plugin. |
| `ImportAnimation` | `true` | Adds a `UsdAnimationPlayer` to the root for the stage's animation, point caches included. `false` imports the stage frozen at the read time. Skeleton animation also needs `ImportSkinning`. |
| `ImportPhysics` | `false` | Builds the stage's UsdPhysics: articulations as `ArticulationBody` trees with their joint limits and drives, other rigid bodies, joints, colliders, physics materials and collision filtering. See [Physics](#physics). Needs the 0.8.1 native plugin. |

### Lights, cameras and spatial audio

These need the 0.8.0 native plugin, which every shipped payload includes. With a native plugin older than 0.8.0, the import still works and these prims arrive as empty transforms, without the components.

- **Lights and cameras.** USD lights emit, and cameras look, along their local -Z axis, and Unity's along +Z. The `Light` or `Camera` therefore sits on a child GameObject turned 180 degrees about Y and marked with `UsdImportHelperObject`. That child is not a prim, so exporters skip it and everything below it. USD has no single unit convention for light intensity, so the mapping to Unity is heuristic. Check the result against the source renderer.
- **Dome lights.** Unity has no dome light. The authored color, intensity and texture path are recorded on a `UsdDomeLight` component. Set `ApplyDomeLightToEnvironment = true` to also apply them to `RenderSettings`.
- **Spatial audio.** The importer sets up an `AudioSource` but doesn't load the clip, because audio files load asynchronously. Load `UsdSpatialAudio.FilePath`, for example with `UnityWebRequestMultimedia`, and assign the clip.

### Skinning and animation

These need the 0.8.1 native plugin. With an older plugin the import still works: skinned meshes arrive as static meshes in their authored points, nothing is animated, and the console says so once per session.

- **Read time.** Static values (transforms, points, primvars, visibility, implicit surfaces, lights and cameras) are read at the stage's `startTimeCode` when the stage authors a time range, and otherwise at each attribute's first time sample, which is how a viewer opens the stage. An attribute without samples reads its default either way. Before the 0.8.1 native plugin everything was read at USD's Default time, which ignores samples: Blender's animated exports author every transform only as samples, so their objects all arrived at their parent's origin. Set `ReadAtStageStartTime = false` to read at Default time.
- **Skeletons and skinned meshes.** Each Skeleton that a SkelRoot binds becomes a hierarchy of joint GameObjects below the Skeleton prim's object, posed as the skeleton is at the read time, with a `UsdSkeleton` component that lists the joints in the skeleton's order. Each skinned mesh gets a `SkinnedMeshRenderer` whose bones are those joints, with bone weights (a mesh's own `skel:joints` order and rigid bindings included) and bind poses (`inverse(bindTransform) * geomBindTransform`). Joints are not prims: the root joints carry `UsdImportHelperObject`, so `UsdPrimSource` and both exporters skip them. Skinned meshes are never shared by `ShareIdenticalMeshes` and get no `MeshCollider`.
- **Dual-quaternion skinning.** `SkinnedMeshRenderer` only blends linearly, which collapses the skin around a twisting joint. A mesh that asks for `primvars:skel:skinningMethod = "dualQuaternion"` keeps a `MeshFilter` and `MeshRenderer` instead and gets a `UsdDualQuaternionSkin` (`Bones`, `RootBone`, `GeomBindTransform`, `Get/SetBlendShapeWeight`, `Deform`, `BakeMesh`). It poses the mesh on the CPU as UsdSkel does: each joint's rotation and translation blended as dual quaternions on the hemisphere of the vertex's heaviest joint, scale and shear blended linearly first, blend shapes before skinning. The result is drawn through the renderer's `additionalVertexStreams`, so the Mesh asset and the scene keep the bind pose, and it is recomputed only when a bone or weight changes. Set `DualQuaternionSkinning = false` for a `SkinnedMeshRenderer` (GPU, linear) instead, with a warning; worth it for crowds.
- **Blend shapes.** A mesh's UsdSkel blend shapes become Unity blend shapes with the same names. Inbetweens with a weight between 0 and 1 become extra frames; others can't be placed by Unity and are skipped with a warning. The weights at the read time are applied, on Unity's 0 to 100 scale.
- **Animation.** When the stage has time-sampled prim transforms or visibility, skeleton animation, blend-shape weights or mesh points, the import root gets a `UsdAnimationPlayer` (`UsdImportResult.Animation`) that plays a `UsdAnimationData`. The data keeps the samples at the times the stage authors them and is interpolated as USD does: positions, scales, weights and points linearly, rotations along the shorter arc, and visibility held until the next sample. No Animator, AnimationClip or Timeline is involved, so it behaves the same in players and the Editor. `Evaluate(seconds)` and `EvaluateAtTimeCode(timeCode)` pose everything; `Play`, `Pause`, `Stop`, `Loop`, `Speed` and `PlayOnStart` drive it in Play Mode and players. Set `ImportAnimation = false` to import the stage frozen at the read time. `UsdAnimationClipBuilder.Build(player)` (Editor) turns the animation into a generic `AnimationClip` for an Animator, the Animation window or a Timeline Animation track; see [Import USD files as project assets](#import-usd-files-as-project-assets).
- **Point caches.** A mesh whose points are time-sampled imports its read-time frame as its Mesh, and every sample as a point-cache track on the imported vertices. Normals are recomputed for each sample when the mesh authors none, read for each sample when authored ones animate, and kept when authored ones hold still. The player draws the current frame through the renderer's `additionalVertexStreams`, so the imported Mesh is never written and instances that share it play at different times; `RestoreMeshes()` shows the Mesh again.
- **Visibility animation.** A prim hidden at the read time and shown later still has its geometry. Below a prim whose visibility is animated, each prim's own visibility opinion sets its GameObject's active state, and Unity's active hierarchy does the inheriting, which is USD's rule.
- **Timeline.** With com.unity.timeline 1.7 or newer in the project, the `Unity.USDToolkit.Timeline` assembly adds a **USD Animation** track (`UsdAnimationTrack`). Bind the track to an imported stage's `UsdAnimationPlayer` and add a `UsdAnimationClip`: the stage is posed at the clip's local time, with clip-in, speed, looping and extrapolation, in the Timeline preview and at runtime. A clip given the `UsdAnimationData` takes its length from it. While a clip is active, the timeline owns the time and the player's own playback is paused.
- **Preserve export.** An animated prim's transform and visibility come from the source's time samples. An override written over them holds a single value at Default time, which hides every sample, so the export leaves out what playback moved. A pose that the animation doesn't explain, such as an animated object you moved yourself, is reported in the console and not written.
- **Not imported.** A point cache whose topology or point count changes over time, one on a skinned mesh, and one inside a point-instancer prototype import as the frame at the read time, with a warning. A point-instancer prototype bound to a skeleton outside it (or reused by a second instancer) is drawn unskinned, with a warning.

### Physics

With `ImportPhysics = true` (or **Import Physics** on a USD asset), the import reads the stage's physics with OpenUSD's own UsdPhysics parser, PhysX's attributes included, and builds it as Unity physics. Values are converted from the stage's units and right-handed space to Unity's; the table shows how what Isaac Sim lists for a prim maps.

| Isaac Sim (USD) | Unity |
|---|---|
| `PhysicsArticulationRootAPI` and its links | An `ArticulationBody` tree. The root is `immovable` when a joint ties the articulation to the world. Unity needs each link under its parent link, so links an Isaac Sim robot keeps as siblings are moved under their parent link, world pose kept. `UsdPrimSource` keeps each object's prim path. |
| Revolute / prismatic joint, `physics:lowerLimit` / `upperLimit` | `RevoluteJoint` / `PrismaticJoint` with the limits in degrees (meters for prismatic), as authored. The anchor's X axis is chosen so a positive USD angle is a positive Unity angle. |
| `drive:<axis>:physics:stiffness` / `damping` | Stiffness and damping are per degree in USD and per radian in PhysX, so they are multiplied by 180/π (57.3). `maxForce`, `targetPosition`, `targetVelocity` and the drive type map directly. |
| Spherical, fixed and single-axis generic joints | `SphericalJoint` with the cone angles as swing limits, `FixedJoint`, or the one-axis joint the free degree of freedom amounts to. |
| Rigid body outside an articulation | `Rigidbody`, with its joints as `FixedJoint` or `ConfigurableJoint` (limits within ±177°; drives are not applied). |
| `PhysicsMassAPI` | Mass, center of mass and inertia tensor. An inertia over 1000 times below what the body's colliders give is kept, and a note says so (`UsdPhysicsBody.UseColliderInertia` swaps in the derived one). |
| Colliders and `approximation` | Box, sphere and capsule colliders; cylinders, cones and convex approximations as convex `MeshCollider`s; triangle meshes on moving bodies built convex. Guide-purpose collision meshes still collide. |
| `PhysicsMaterialAPI`, `physxArticulation:enabledSelfCollisions`, filtered pairs, collision groups | `PhysicsMaterial`s, and a `UsdPhysicsCollisionFilter` on the root that ignores the filtered collider pairs. |
| `PhysxMimicJointAPI` (gearing, offset, reference joint) | Unity has no joint coupling. Between single-axis joints of an articulation, the follower gets a drive (the reference joint's gains when both move the same way) and a `UsdPhysicsMimicJoint` that sets its target every physics step so `follower + gearing × reference + offset = 0`, as PhysX holds it (radians for a rotation). It lags a step and yields under load, where PhysX's constraint doesn't. |
| `PhysicsScene` | A `UsdPhysicsScene` component. Gravity and the step rate are project-wide in Unity, so call `Apply()` to set them. |

Next to each Unity body and joint, a `UsdPhysicsBody` or `UsdPhysicsJoint` component keeps the values as authored. `TryReadUnityValues` reads the Unity component back into the same units and sign, so the two can be compared. What could not be mapped (tendons, distance joints, inverted collision groups, and mimic joints outside an articulation) is listed in `UsdImportResult.PhysicsNotes` and in the console. The [Isaac Robot Physics sample](#12-the-isaac-robot-physics-sample) shows the comparison.

Physics import needs the 0.8.1 native plugin's physics reader (`RUsd_ReadPhysics`). On a plugin built without it, the stage imports without physics and the notes say why.

### Import USD files as project assets

In the Editor, a `.usd`, `.usda`, `.usdc` or `.usdz` file placed in the project imports as a model asset through `UsdStageImporter`, and so does an `.abc` or `.drc` file (see [Alembic and Draco files](#alembic-and-draco-files)). The main asset is the stage's root object: drag it into a scene or make prefab variants of it. Its meshes, materials, textures (with their pixels) and `UsdAnimationData` are sub-assets, so nothing is imported again at runtime and the content builds for every platform Unity targets, not only those the native plugin supports.

- **Settings.** The importer's Inspector holds the import options (geometry, materials, skinning and animation, scene components, composition). It also lists every variant set the stage offers, with a dropdown for each. Choosing a variant writes it into the variant selections, and **Apply** reimports the stage with it.
- **Same result as a runtime import.** The asset is what `UsdImporter.Import` builds with those settings. It always records its source (`RecordSource`) and keeps texture pixels (`KeepTexturesReadable`), and never changes the scene's environment (`ApplyDomeLightToEnvironment` is off).
- **Reimport.** The asset reimports when the stage or any project file it read changes: sublayers, references and payloads (the 0.8.1 native plugin) and textures. A file outside `Assets` and `Packages` isn't an asset, so after editing one, reimport by hand.
- **Animation preview.** `UsdAnimationPlayer`'s Inspector plays, pauses, stops and scrubs in Play Mode. In Edit Mode, **Preview** scrubs it with a time slider without changing the scene: the preview runs in Unity's animation mode, as the Animation window's does, and puts back every transform, active state, blend-shape weight and mesh when it ends (Preview again, another selection, saving the scene or entering Play Mode). A Timeline USD Animation track previews the same way. `UsdAnimationPreview.Start(player)`, `SetTime` and `Dispose` do it from a tool.
- **Animation clips.** Turn on **Generate Animation Clip** to add the animation as an `AnimationClip` sub-asset, or use **Create Animation Clip...** in the `UsdAnimationPlayer` context menu to save one as a `.anim`. The clip is generic: prim and joint transforms, active states and blend-shape weights as curves on the objects below the player, keyed at the stage's samples, linear where USD is linear and stepped for visibility, with extra rotation keys where samples are more than 10 degrees apart so Unity's quaternion curves follow USD's arc. Clip time 0 is the stage's start, and it lasts the stage's time range. Point caches and the blend shapes of dual-quaternion meshes have no curve type and stay with the player. With an Animator playing the clip, turn the player's **Play On Start** off.
- **Source path.** `UsdStageSource` records a file inside the project by its project path (`Assets/...` or `Packages/<name>/...`, `RecordedSourcePath`), so assets and scenes don't hold one machine's absolute path; `SourcePath` resolves it to the file on disk in the Editor. Files outside the project are recorded by their absolute path.
- **Other USD importers.** When com.unity.importer.usd or com.unity.formats.usd is installed, `UsdStageImporter` doesn't take the extensions over. It is offered as an alternative: pick it in the asset's Inspector. The same goes for `.abc` when com.unity.formats.alembic is installed.

## 6. Threading

Methods that create or read Unity objects must run on the main thread.

`ImportAsync` splits an import between threads. Call it from the main thread. Reading the stage, decoding PNG and EXR files, stitching UDIM tiles, packing metallic and roughness maps and albedo and opacity maps, and block compression run on worker threads in parallel, in native code with the 0.8.0 native plugin or newer and in managed code otherwise. GameObjects, meshes and materials are created on the main thread, within `MaxMillisecondsPerFrame` per frame. `ImportAsync` returns once the objects and the untextured materials exist, and keeps attaching textures on the main thread as they finish decoding, until `TexturesReady` completes. JPEG files and 16-bit PNGs are decoded on the main thread through `Texture2D.LoadImage`.

| Method | Thread |
| --- | --- |
| `UsdImporter.ImportAsync` | Call from the main thread. Stage reading and texture decoding run on worker threads; objects are created on the main thread. |
| `UsdImporter.Import` | Runs on the calling thread, apart from texture decoding, which uses worker threads. Call from the main thread. |
| `UsdImporter.GetPreviewInfo` | Any thread. It doesn't touch Unity objects. |
| `UsdImporter.LoadImageFile` | Main thread. It creates a `Texture2D`. |
| `UsdLibraryScanner.ScanFolder` | Any thread. It lists files and doesn't touch Unity objects. |
| `UsdExporter.ExportGameObject`, `ExportGameObjectWithResult` | Main thread. They read meshes, materials and textures. |
| `UsdPointInstancer.Draw` | Main thread, every frame. The component calls it from `Update`. |
| `UsdStage`, `UsdPrim`, `UsdAttribute` | Any thread. Calls on one stage are serialized. |
| `UsdStageSource.SwitchVariant`, `SwitchPayloads`, `LoadPayload`, `UnloadPayload` | Main thread; the async forms read the stage on a worker thread. |

Every public method sets up the OpenUSD runtime before its first use, so you don't need to initialize anything.

## 7. Materials and textures

### Export

With `ExportTextures = true`, each material's textures are written as PNGs to `<usd-name>_textures/` and referenced by relative path:

| Unity property | USD input |
| --- | --- |
| `_BaseMap`, `_MainTex`, `_BaseColorMap` | `diffuseColor` (sRGB) |
| `_BumpMap`, `_NormalMap` | `normal` |
| `_MetallicGlossMap`, `_MetallicMap` | `metallic` (red channel) and `roughness` (1 − alpha) |
| `_EmissionMap` | `emissiveColor` |
| Texture tiling and offset | `UsdTransform2d`, when not the default |

Source textures don't need **Read/Write Enabled**. Each texture is exported once, even when several materials use it.

### Import

Imported materials use the first shader available from `Universal Render Pipeline/Lit`, `HDRP/Lit` and `Standard`. Textures are assigned to `_BaseMap`, `_BumpMap`, `_MetallicGlossMap`, `_OcclusionMap` and `_EmissionMap`, with tiling and offset from `UsdTransform2d`. Each texture file is loaded once and shared between materials.

The importer reads these surface shaders:

| Source | What is read |
| --- | --- |
| `UsdPreviewSurface` | Base color, opacity, metallic, roughness, emission and their textures. |
| MaterialX (`standard_surface`, `open_pbr_surface`, `UsdPreviewSurface`) | The same values and textures, from a material authored in the stage or in a referenced `.mtlx` file. |
| MDL `OmniPBR`, `OmniSurface`, `OmniGlass` | Colors and textures. Other MDL materials aren't read. |
| `UsdPrimvarReader` on `diffuseColor` | The primvar color, such as `displayColor`, as the base color. |

Textures referenced from a material layer in a sub-folder resolve relative to that layer. A mesh or submesh with no material binding gets the shared grey `UsdDefaultMaterial` unless `AssignDefaultMaterial = false`.

### Texture formats and packing

The importer reads these image formats:

| Format | Notes |
| --- | --- |
| PNG | 8-bit, non-interlaced files are decoded off the main thread. Other PNGs go through `Texture2D.LoadImage` on the main thread. |
| JPEG | Through `Texture2D.LoadImage`, on the main thread. |
| OpenEXR | Scanline and tiled files with `NONE`, `RLE`, `ZIPS` or `ZIP` compression, in half, float or uint. Level 0 of a mipmapped file is read. Pixels are quantised to 8 bits, and sRGB-encoded for colour slots. |

A `<UDIM>` texture path stands for a grid of tiles. The tiles of a set are stitched into one atlas laid out as they sit in UV space, holding only the rows and columns the set has. The mesh's UVs are unchanged, and the material's base-map scale and offset place them on the atlas. An atlas larger than the graphics device accepts is halved until it fits. Tiles must be PNG or OpenEXR; a tile in another format is left out with a warning.

A mesh's or `GeomSubset`'s material is looked up through `material:binding:full`, then `material:binding:preview`, then the all-purpose `material:binding`.

Separate metallic, roughness and occlusion maps are packed into Unity's layout on the worker thread: metallic in the red channel and smoothness (1 − roughness) in the alpha channel of `_MetallicGlossMap`, and the occlusion map in `_OcclusionMap`. An OmniPBR ORM map (occlusion, roughness and metallic in red, green and blue) is repacked the same way, with occlusion in green, and assigned to both slots. An opacity map that isn't the albedo's alpha is folded into the albedo's alpha channel. Textures are block-compressed by default; see `CompressTextures`.

### Albedo texture in the metallic slot

A base color texture assigned to the metallic slot (`_MetallicGlossMap`) produces a mirror-like surface. In Unity it can look correct because a skybox or reflection probe gives it something to reflect. In viewers without environment reflections, such as the Isaac Sim viewport, it renders black.

When a material's metallic map is the same texture as its albedo map, the exporter ignores the metallic map, uses the material's **Metallic** value, and logs a warning. To use one texture for both slots on purpose, set `IgnoreAlbedoInMetallicSlot = false`.

## 8. Untrusted USD files

The importer treats every USD file as untrusted, such as a downloaded asset, a shared `.usdz`, or a file in a scanned folder. The following protections are on by default.

### Texture paths

A USD file sets its own texture paths, and a path can point anywhere on disk. The importer skips any texture that resolves outside the folder of the stage or its layers, and logs a warning that shows the path as written in the file. Textures inside a `.usdz` are not affected. A path that passes through a symbolic link or junction below the stage folder is refused too, because the link can point anywhere. The tiles of a `<UDIM>` set are found by listing the folder and are confined the same way, one by one.

For a trusted stage that references a shared texture library in another folder, or reaches its textures through links, set `UsdImportOptions.AllowExternalAssetPaths = true`.

### Mesh topology

The importer skips any mesh whose face data is inconsistent, or whose face-vertex indices name points it doesn't have, and logs a warning with the prim path.

### Alembic and Draco files

A USD file that references an `.abc` or `.drc` runs OpenUSD's Alembic and Draco readers, so they read untrusted data too. The payload builds them with Unity patches (`Native~/patches`) that fix every crash a fuzzing campaign of 24,000 damaged files found in them, including heap writes. A damaged file is an import error, or a mesh without the part that was damaged. These readers have been fuzzed, not audited: they have less hardening behind them than OpenUSD's own `.usda` and `.usdc` readers.

### Texture size

The importer refuses any image that declares more than 16,384 pixels on a side or more than 64 million pixels in total. The limits apply to PNG, JPEG and OpenEXR files and to UDIM atlases. An image file larger than 512 MB is refused before it is read. A PNG or EXR whose header claims more pixels than its compressed bytes can expand to is refused before any memory is allocated for it.

### Point instancers

An import whose point instancers come to more than 4,000,000 placements (instances times prototype meshes) is refused, with an error that names the limit. A prototype that contains its own instancer, an ancestor of it, or an instancer already being expanded would import itself again, so it is skipped with a warning.

### Import limits

An import counts what it builds against budgets, because a few hundred bytes of USD can describe unbounded work. By default they are 2,000,000 nodes, 1,000,000 meshes, 4,000,000 point-instancer placements, 268,435,456 points and triangle indices across every mesh (also the most one mesh may have), 1,000,000 samples per animation track, 67,108,864 animation values and 65,536 joints per skeleton. A stage over the node, mesh, placement or geometry budget fails the import with an error that names it; the animation budgets drop only the track, skeleton animation or blend shape that is over.

`UsdImportOptions.Limits` sets them per import. Raise them for a trusted stage that is larger, or lower them to keep a device's imports small; a value of 0 or less keeps the default. With `WhenExceeded = UsdImportLimitBehavior.SkipWithWarning`, a stage over a budget imports what fits instead: the walk stops at the node limit, a mesh over the mesh or geometry limit is left out (with its instance copies), and an instancer's placements over theirs are not drawn. A warning says what was left out. `GetPreviewInfo` uses the same limits. Values other than the defaults need the 0.8.1 native plugin. In the Editor, `UsdStageImporter` shows them under **Limits**.

```csharp
var options = new UsdImportOptions
{
    Limits = new UsdImportLimits
    {
        MaxMeshes = 20000,
        WhenExceeded = UsdImportLimitBehavior.SkipWithWarning,
    },
};
```

### Animation

A track, skeleton animation or blend shape that would pass the animation budgets is dropped with a warning, and the rest of the stage imports: more than 1,000,000 samples on one track, more than 64 million animation values in the whole stage (transform samples, joint poses and blend-shape frames), more than 65,536 joints in one skeleton, or more than 255 joint influences per point, which is Unity's limit.

### Plugin search path

OpenUSD runs the libraries that a `plugInfo.json` file names. For this reason, `UsdExportOptions.PluginSearchPath` must be inside the package's own native folders. To use a custom OpenUSD installation, set `UsdExporter.AllowExternalPluginSearchPath = true` from code. This setting is static and never serialized, so an options object saved in a scene or prefab can't enable it.

## 9. Build a standalone player

1. In **Build Profiles**, select Windows, macOS or Linux. On macOS, the player architecture must match the native libraries.
2. Build with Mono or IL2CPP.
3. Write output files under `Application.persistentDataPath`.

When you build, the package copies the OpenUSD files into the player:

```text
<Build>/<App>_Data/Plugins/x86_64/Windows/     (Windows)
<Build>/<App>.app/Contents/PlugIns/            (macOS)
<Build>/<App>_Data/Plugins/x86_64/Linux/       (Linux)
```

For distribution outside your own machine, sign the macOS app bundle after Unity builds it.

## 10. The USD Live Sync sample

`Samples~/Live Sync Example` synchronizes transforms in both directions between a running Unity scene and an external tool, such as NVIDIA Isaac Sim or a Python script. Unity exports the scene geometry once as `base_stage.usda`, then streams transform changes over TCP.

Every connection must authenticate with a token before it receives data. The server accepts connections from the local machine only, and the traffic is not encrypted.

For setup instructions, see the [Live Sync Example guide](../Samples~/Live%20Sync%20Example/README.md).

## 11. The AGV Fleet LiveSync Demo sample

`Samples~/AGVFleet_LiveSync Demo` shows a fleet of AGVs in a factory hall, with Unity as the operator front end and NVIDIA Isaac Sim 6.1.0 as the simulator. Both open one USD stage: Isaac Sim simulates the AGVs and streams their state to Unity; Unity writes the obstacles and goals the operator places to its own USD layer, which Isaac Sim reloads.

The hall is either NVIDIA's Factory scene from the USD Explorer Sample Asset Pack, which you download from NVIDIA, or a warehouse hall the sample generates as USD and imports with this package. Without Isaac Sim, Unity shows the hall and the AGVs at their charging docks.

The sample needs URP and the Input System. For setup instructions, see the [AGV Fleet LiveSync Demo guide](../Samples~/AGVFleet_LiveSync%20Demo/README.md).

## 12. The Isaac Robot Physics sample

`Samples~/Isaac Robot Physics` imports a robot with its physics and puts what Isaac Sim's Property panel shows for each joint and body next to what Unity built: joint limits, axes, drive stiffness, damping, maximum force and target, mass, center of mass and inertia. In Play Mode it drives the joints and runs a limit test that pushes every limited joint past both of its limits and records where the physics stops it.

It ships a small arm laid out like an Isaac Sim robot, and downloads NVIDIA's Franka Panda from the Isaac Sim 6.0 assets on request (about 29 MB, Apache 2.0), with every file checked against a pinned SHA-256, into a download location you can choose. Use **USD Toolkit > Robot Physics** in edit mode, or open `RobotPhysics.unity` and press Play: a panel on the left, which **H** hides, picks the robot (the arm, the Franka or any USD file) and holds the comparison, the drive sliders and the limit test. For details, see the [Isaac Robot Physics guide](../Samples~/Isaac%20Robot%20Physics/README.md).

## 13. Check exported USD files

- `usdchecker <file>` checks the structure and validity.
- `usdcat <file>` prints the contents as text.
- usdview, Isaac Sim, Omniverse or Blender display the geometry and materials.

## 14. Troubleshooting

| Symptom | Cause | Solution |
| --- | --- | --- |
| `DllNotFoundException` | The native files are missing or are still Git LFS pointer files. | Run `git lfs pull`, and check the platform folder under `Runtime/Plugins`. |
| Native plugin mismatch | The native plugin is older than 0.8.0, the minimum the package accepts. | Reinstall the package, or rebuild the plugin. See [Appendix C](#appendix-c-rebuild-the-native-plugin). |
| The native plugin is refused because its digest doesn't match | A native file differs from the one shipped with the package. | Reinstall the package. If you rebuilt the plugin, regenerate the digests. See [Appendix C](#appendix-c-rebuild-the-native-plugin). |
| OpenUSD plugin path error | The `plugin/usd` or `lib/usd` folder is missing. | Check that the `plugInfo.json` files are present. |
| `PluginSearchPath` is refused | The path is outside the package's native folders. | See [Plugin search path](#plugin-search-path). |
| A mesh can't be read | The mesh doesn't have **Read/Write Enabled**, and no graphics device is available. | Enable **Read/Write Enabled**, or export in Play mode. |
| The surface renders black in Isaac Sim | The albedo texture is also in the metallic slot. | See [Albedo texture in the metallic slot](#albedo-texture-in-the-metallic-slot). |
| Textures are missing after import | A texture path points outside the stage folder. | Read the warning in the Console. For a trusted file, set `AllowExternalAssetPaths = true`. |
| Some meshes are missing after import | A mesh has inconsistent face data. | The warning in the Console names the prim. Fix the source file. |
| The imported stage is rotated or scaled differently than expected | `ApplyStageUpAxisAndUnits` applies the stage's `upAxis` and `metersPerUnit` on the import root. | See [Up axis and units](#up-axis-and-units). Set `ApplyStageUpAxisAndUnits = false` to keep the stage's raw coordinates. |
| A smooth surface looks faceted after import | The mesh is a subdivision surface, and only its control cage is imported. | The warning names the prim. Export a subdivided mesh from the source tool. |
| No file is written | The output folder isn't writable, or the export threw an exception. | Write under `Application.persistentDataPath`, and check the Console. |
| The package works in the Editor but not in a player | The player targets an unsupported platform, or the native files are missing from the build. | Build for Windows x64, macOS or Linux x64, and check the player's `Plugins` folder. |
| Materials are flat right after `ImportAsync` | With `StreamTextures` (the default), `ImportAsync` returns before the textures are attached. | Await `UsdImportResult.TexturesReady`, or set `StreamTextures = false`. |
| UDIM tiles are missing, or the atlas is smaller than expected | A tile isn't a PNG or OpenEXR, or the atlas was halved to fit the graphics device's texture limit. | Read the warning in the Console; it names the tile, or the atlas size and the limit. |
| An EXR texture is refused | The file uses a compression other than `NONE`, `RLE`, `ZIPS` or `ZIP`, has more than 64 channels, or exceeds the size limits. | Re-export the texture with a supported compression and fewer channels. See [Untrusted USD files](#8-untrusted-usd-files). |
| Point instancer instances can't be selected and have no colliders | By design: a `UsdPointInstancer` draws its placements from arrays, not from GameObjects. | See [Point instancers](#point-instancers). Author the parts as prims when they must be selected individually. |
| A point instancer is refused as too dense | The stage exceeds the placement budget of 4,000,000 instances times prototype meshes. | Reduce the instance count or the number of prototype meshes in the source. |

To print the native plugin version, OpenUSD version and search paths:

```csharp
Debug.Log(UsdExporter.GetRuntimeInfo().ToString());
```

## Appendix A. API reference

All public types are in the `Unity.USDToolkit` namespace.

| API | Description |
| --- | --- |
| `UsdExporter.ExportGameObject` | Exports a GameObject hierarchy. |
| `UsdExporter.ExportGameObjectWithResult` | Exports a GameObject hierarchy and returns a `UsdExportResult`. |
| `UsdExporter.GetRuntimeInfo` | Returns the native plugin version, OpenUSD version and search paths. |
| `UsdExporter.AllowExternalPluginSearchPath` | Allows a `PluginSearchPath` outside the package. |
| `UsdImporter.GetPreviewInfo` | Returns a stage's statistics without importing it. |
| `UsdImporter.Import` | Imports a stage synchronously. |
| `UsdImporter.ImportAsync` | Imports a stage asynchronously, across several frames. |
| `UsdImporter.LoadImageFile` | Loads an image file as a `Texture2D`, with size limits. |
| `UsdLibraryScanner.ScanFolder` | Lists the USD files in a folder, with thumbnails. |
| `UsdExportOptions`, `UsdImportOptions`, `UsdLibraryScanOptions` | Options. |
| `UsdExportResult`, `UsdExportedMeshInfo` | Export results. |
| `UsdImportResult`, `UsdImportedMeshInfo`, `UsdImportPreviewInfo` | Import results and statistics. |
| `UsdImportResult.TexturesReady` | A `Task` that completes once every texture is attached to its material. Never faults. |
| `UsdImportResult.UniqueMeshCount` | The number of distinct `Mesh` objects the import created. |
| `UsdImportResult.DefaultMaterial` | The shared grey `UsdDefaultMaterial`, or `null` when no mesh needed it. |
| `UsdLibraryItem` | A USD file found by `ScanFolder`. |
| `UsdRuntimeInfo` | Native plugin version, OpenUSD version and search paths. |
| `UsdExportException`, `UsdImportException` | Errors, with diagnostics. |
| `UsdTransformPolicy` | `BakedMesh` or `PreserveHierarchy`. |
| `UsdCompositionMode`, `UsdExportLayout`, `UsdImportedContentMode` | Composition settings for export. |
| `UsdStageSource`, `UsdPrimSource` | Components that record where imported objects came from. `UsdStageSource.SourcePath` is the file on disk; `RecordedSourcePath` is what is stored, a project path for a file inside the project. |
| `UsdStageSource.CreateOptionsWithVariant`, `CreateOptionsWithPayloads` | Return import options that re-import the stage with another variant selected, or other payloads loaded; every other setting comes from the options given, or the defaults. |
| `UsdStageSource.SwitchVariant`, `SwitchPayloads`, `LoadPayload`, `UnloadPayload` (and `...Async`) | Switch the composition in place, rebuilding only what the change recomposes, or the whole stage when it must; return a `UsdSwitchResult`. |
| `UsdStageSource.Reimport` (and `ReimportAsync`) | Import the stage again with other options and replace this import with the result in place. `ImportOptions` is what the import ran with, when it was made in this session. |
| `UsdStageSource.Stage` | The stage the import keeps open, read-only (the 0.8.1 native plugin). |
| `UsdSwitchResult` | What a switch did: `RootObject`, `RebuiltPrimPaths`, `Reimported`, `ReimportReason`, `Import`. |
| `UsdStage`, `UsdPrim`, `UsdAttribute`, `UsdRelationship`, `UsdVariantSet` | The stage API: open, create, read, edit and save stages (the 0.8.1 native plugin). |
| `UsdTimeCode`, `UsdEditTarget`, `UsdValueType`, `UsdSpecifier`, `UsdUpAxis` | Time codes, where edits go, and the stage API's enums. |
| `UsdCoordinates` | Converts points, rotations and matrices between USD's basis and Unity's. |
| `UsdStageException` | A stage API call OpenUSD refused, with diagnostics. |
| `UsdImportResult.SourceFiles` | The files the import read: the stage's layers (the 0.8.1 native plugin) and its texture files. |
| `UsdStageImporter`, `UsdStageImporterEditor` | Editor-only, in the `Unity.USDToolkit.Editor` assembly. The importer for USD files in the project, its settings (`CreateOptions()`), and its Inspector with the variant dropdowns. |
| `UsdAnimationPlayerEditor` | Editor-only. The `UsdAnimationPlayer` Inspector, with Edit Mode preview and **Create Animation Clip...**. |
| `UsdAnimationPreview` | Editor-only. Previews a `UsdAnimationPlayer` in Edit Mode inside Unity's animation mode and puts the scene back when disposed. `CanStart`, `Start`, `SetTime`, `Time`, `IsActive`, `Dispose`. |
| `UsdAnimationClipBuilder` | Editor-only. `Build(player)` turns a player's animation into a generic `AnimationClip`. |
| `UsdPrefabMarker` | Links a runtime object to its prefab for export. |
| `UsdPrefabMarker.Instantiate`, `UsdPrefabMarker.Attach` | Instantiates a prefab with its link, or attaches the link to an existing instance. |
| `UsdVariantSelection`, `UsdVariantSetInfo`, `UsdPayloadInfo`, `UsdCompositionArcInfo`, `UsdPrimCompositionFlags` | Composition data of an imported stage. |
| `UsdPointInstancer` | Component that draws a point instancer's prototype at every placement. `Mesh`, `Materials`, `SkinnedSource`, `InstanceCount`, `LodDistance`, `CellCount`, `Draw`. |
| `UsdDomeLight` | Component that records a `UsdLuxDomeLight`'s color, intensity and texture path. |
| `UsdSpatialAudio` | Component that records a `UsdMediaSpatialAudio`'s file path, start time and end time. |
| `UsdImportHelperObject` | Marks an object the importer created that is not a prim: a child that turns a light or camera to Unity's convention, or a root joint. Exporters and `UsdPrimSource` skip it and everything below it. |
| `UsdSkeleton` | Component on a Skeleton prim's object. `JointPaths`, `Joints` and `BindTransforms`, in the skeleton's joint order, which bound `SkinnedMeshRenderer`s use as bones. |
| `UsdDualQuaternionSkin` | Component that skins a mesh with dual quaternions, as UsdSkel does, for meshes that ask for it. `Bones`, `RootBone`, `GeomBindTransform`, `SharedMesh`, `GetBlendShapeWeight`, `SetBlendShapeWeight`, `Deform`, `BakeMesh`. |
| `UsdAnimationPlayer` | Component on the import root that plays the stage's animation. `Data`, `Evaluate`, `EvaluateAtTimeCode`, `Play`, `Pause`, `Stop`, `CurrentTime`, `Duration`, `Loop`, `Speed`, `PlayOnStart`, `IsPlaying`, `RestoreMeshes`, `CollectAnimatedTargets`. |
| `UsdAnimationData` | The stage's animation as time samples: transform, visibility, skeleton, blend-shape and point-cache tracks, with the stage's time range. |
| `UsdImportResult.Animation` | The import's `UsdAnimationPlayer`, or `null` when the stage has nothing animated. |
| `UsdAnimationTrack`, `UsdAnimationClip` | In the `Unity.USDToolkit.Timeline` namespace and assembly, present with com.unity.timeline. A Timeline track bound to a `UsdAnimationPlayer`, and its clip. |
| `UsdAnimationRecorder` | Records a hierarchy over time and saves it as an animated stage. `Capture`, `CaptureClip`, `CapturePlayer`, `Save`, `SampleCount`, `FramesPerSecond`. Needs the 0.8.1 native plugin. |
| `UsdRecorderSettings`, `UsdRecorderInputSettings`, `UsdRecorderFormat` | Editor-only, in the `Unity.USDToolkit.Editor` namespace and the `Unity.USDToolkit.Recorder.Editor` assembly, present with com.unity.recorder 5.0 or newer. The Unity Recorder's USD Animation recorder: the GameObject to record, the format, `ExportTextures` and `OverridesOnly`. |
| `UsdMeshLodGenerator` | Editor-only, in the `Unity.USDToolkit.Editor` assembly. Generates Mesh LOD levels for point instancer prototypes. `Generate(root, levels)`. |

Types in `Unity.USDToolkit.Native` are internal and can change without notice.

While the package is in `0.x`, any release can include breaking API changes. From `1.0.0`, the public API follows semantic versioning.

## Appendix B. Verify the package's integrity

### Native file digests

Before it loads the native plugin, the package compares the SHA-256 digest of each native file with the digests in `Runtime/Native/NativeRuntimeHashes.g.cs`, and refuses to load files that don't match. This check runs once per process.

### Package signature

A package published by Unity includes a signature at `package/.attestation.p7m` that covers every file in the package, including the native libraries. Unity 6.3 and later verify it automatically and show the result in the Package Manager. To verify it yourself:

```bash
tar -xzf com.unity.usd-toolkit-<version>.tgz package/.attestation.p7m
openssl cms -verify -in package/.attestation.p7m -inform DER -noverify -out attestation.json
openssl pkcs7 -in package/.attestation.p7m -inform DER -print_certs -text | head -40
```

To also check the certificate chain, remove `-noverify` and pass Unity's root certificate with `-CAfile`.

### Code signatures

On Windows and macOS, the native libraries are signed with Unity's certificates:

```bash
# macOS: expect a Developer ID authority.
codesign --verify --strict --verbose=2 Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib
```

```powershell
# Windows: expect Status: Valid.
Get-ChildItem Runtime\Plugins\x86_64\Windows\*.dll | ForEach-Object {
    Get-AuthenticodeSignature $_.FullName | Select-Object Status, SignerCertificate
}
```

Linux shared libraries have no equivalent code signature. On Linux, use the package signature and the digests in the bill of materials.

### Bill of materials

`ThirdPartyNotices~/sbom.cdx.json` is a CycloneDX bill of materials. It lists every third-party component in the native files, with its version, source, OpenUSD commit and SHA-256 digest:

```bash
sha256sum Runtime/Plugins/x86_64/Linux/lib/libusd_ms.so
python3 -c "import json;[print(c['hashes'][0]['content'], [p['value'] for p in c['properties'] if p['name']=='unity:shippedPath'][0]) for c in json.load(open('ThirdPartyNotices~/sbom.cdx.json'))['components'][1:]]"
```

## Appendix C. Rebuild the native plugin

You only need to rebuild the native plugin to change the OpenUSD version or modify the plugin.

Build OpenUSD with `Native~/build_openusd.py`, which verifies the OpenUSD source and its dependencies before building. It builds OpenUSD with the Alembic and Draco plugins, so it needs CMake 3.x on `PATH`: Alembic 1.8.5 doesn't configure with CMake 4. For example, on macOS:

```bash
python3 Native~/build_openusd.py --platform macos --openusd-src <OpenUSD v26.05 clone> \
  --install /Users/Shared/usd-26.05/install --build-target universal --require-scan
bash Native~/build_macos.sh \
  --openusd-root /Users/Shared/usd-26.05/install --arch universal \
  --codesign-id "<Developer ID>"
```

After you rebuild, regenerate the digests:

```bash
python3 Native~/generate_native_hashes.py
```

For the full procedure on each platform, see `Native~/README.md` and `Native~/REBUILD_WINDOWS_LINUX.md`.
