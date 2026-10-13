# Unity USD Toolkit User Manual

Read and write OpenUSD stages from Unity at runtime, on Windows, macOS and Linux.

| | |
| --- | --- |
| **Package version** | 0.7.2-exp.1 |
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
11. [Check exported USD files](#11-check-exported-usd-files)
12. [Troubleshooting](#12-troubleshooting)
- [Appendix A. API reference](#appendix-a-api-reference)
- [Appendix B. Verify the package's integrity](#appendix-b-verify-the-packages-integrity)
- [Appendix C. Rebuild the native plugin](#appendix-c-rebuild-the-native-plugin)

## 1. Overview

The Unity USD Toolkit writes and reads USD files inside a running Unity application, in the Editor and in built players. It converts GameObjects to `.usd`, `.usda`, `.usdc` and `.usdz` files, and loads USD stages back into GameObjects.

The package has three layers:

- **C# API.** The public API in the `Unity.USDToolkit` namespace.
- **Native plugin.** `UnityUSDToolkitNative`, a C++ library that wraps the Pixar OpenUSD API behind a C interface. The C# API calls it through P/Invoke.
- **OpenUSD.** Pixar's OpenUSD 26.05 libraries and oneTBB, built for each platform.

The package does not depend on Unity's other USD packages or on USD.NET.

The C# API checks the version of the native plugin when it loads, and refuses a plugin that doesn't match the package exactly. This ensures that every fix in the package is present in the binary that runs.

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
| Inactive objects and disabled renderers | Yes | Authored as `visibility = "invisible"`. |
| Keep USD composition | Yes | Sublayers, references, instancing and overrides, with `UsdCompositionMode.Preserve`. See [Keep USD composition](#keep-usd-composition). Needs native API 6. |
| Skinned meshes and animation | No | |

### Import

| Feature | Supported | Notes |
| --- | --- | --- |
| `.usd`, `.usda`, `.usdc`, `.usdz` | Yes | Textures inside a `.usdz` are supported. |
| Static `UsdGeomMesh` | Yes | Smooth normals are generated when none are authored. Face-varying normals and UVs that match are merged into shared vertices. |
| Implicit surfaces | Yes | `Cube`, `Sphere`, `Cylinder`, `Cone`, `Capsule` and `Plane` become meshes. |
| `PointInstancer` | Yes | Each instance becomes its own GameObjects. Prototypes aren't drawn in place. |
| Subdivision surfaces | Partly | The control cage is imported without subdividing, with a warning. |
| Transform hierarchy | Yes | Every transformable prim becomes a Unity `Transform`. Typeless and custom-schema prims become empty GameObjects. Prims with `proxy` or `guide` purpose are skipped. |
| Multiple materials per mesh | Yes | Each `materialBind` subset becomes a submesh. |
| `UsdPreviewSurface` materials and PBR textures | Yes | Albedo, normal, metallic-smoothness, emission. |
| MaterialX materials | Yes | `standard_surface`, `open_pbr_surface` and `UsdPreviewSurface` nodes, authored in the stage or in a referenced `.mtlx` file. See [Materials and textures](#7-materials-and-textures). |
| MDL materials | Partly | Colors and textures of `OmniPBR`, `OmniSurface` and `OmniGlass`. Other MDL materials aren't read. |
| Primvar colors | Yes | A `UsdPrimvarReader` on `diffuseColor`, such as `displayColor`, sets the base color. |
| Meshes without a material | Yes | Get a shared grey `UsdDefaultMaterial`. See `AssignDefaultMaterial`. |
| Shared meshes | Yes | Instances with identical geometry share one Unity `Mesh`. See `ShareIdenticalMeshes`. |
| Lights | Yes | UsdLux distant, sphere, disk, cylinder and rect lights become Unity `Light`s. Intensity mapping is heuristic. Needs native API 7. |
| Dome lights | Partly | Recorded on a `UsdDomeLight` component. With `ApplyDomeLightToEnvironment`, they set the ambient color and a panoramic skybox. Needs native API 7. |
| Cameras | Yes | `UsdGeomCamera` becomes a disabled Unity `Camera`. Needs native API 7. |
| Spatial audio | Partly | `UsdMediaSpatialAudio` becomes an `AudioSource` with a `UsdSpatialAudio` component. The clip isn't loaded. Needs native API 7. |
| Asynchronous import | Yes | Parses off the main thread and creates objects across several frames. |
| Statistics preview | Yes | Mesh, material, triangle and vertex counts before importing. |
| Folder scanning | Yes | `UsdLibraryScanner.ScanFolder`. |
| Up axis and unit conversion | Yes | Applied on the import root by default. See [Up axis and units](#up-axis-and-units). |
| Variant selection and payload loading | Yes | Choose variants and which payloads load. See [Variant sets and payloads](#variant-sets-and-payloads). Needs native API 6. |
| Composition, instancing | Yes | References, payloads, inherits, specializes and instances are composed; each instance becomes its own GameObjects. |
| Skinned meshes, animation | No | |
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

The file extension of the output path sets the format. For a `.usdz` path, the exporter writes the stage and its textures to a temporary folder and packages them, and leaves no other files next to the `.usdz`.

### Choose a transform policy

| Policy | When to use it | Result |
| --- | --- | --- |
| `BakedMesh` | Most exports, when the downstream tool doesn't need the hierarchy. | Transforms are baked into mesh points, and the USD hierarchy is flat. |
| `PreserveHierarchy` | When the GameObject hierarchy and local transforms must be kept. | Each GameObject becomes an `Xform` prim, and mesh points stay in local space. |

With `BakedMesh`, each mesh is written in the export root's space. Positions, rotations and scale, including negative scale, are applied to the points, and triangle winding is corrected for the conversion from Unity's coordinate system to USD's.

With `PreserveHierarchy`, the export root becomes the USD root prim, and each child's local position, rotation and scale are written as an `xformOp:transform` matrix.

### Meshes without Read/Write Enabled

In Play mode, the exporter reads meshes without **Read/Write Enabled** back from the GPU, so you don't need to reimport shipped content. Set `RequireReadableMeshes = false` to allow this. GPU readback needs a graphics device; in batch mode or with `-nographics`, enable **Read/Write Enabled** on the meshes instead.

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

The output depends on the source file, so keep them together, or export to `.usdz`, which packages the source with it. The output can't replace its own source file. Objects are identified by their prim path, not their name, so renaming an object changes nothing. Moving an object under another parent can't be expressed as an override: the original prim is deactivated, and the object is written as new content where it now is.

When an imported stage sits deeper in the exported hierarchy, or `ImportedContent = UsdImportedContentMode.ReferenceSource`, the object that stands for the stage's default prim references the source file, and the changes are written as overrides through that reference. Root prims other than the default prim can't come through a reference, so they're written as content. `UsdImportedContentMode.Inline` writes imported objects like any others.

**Prefab instances become references to one asset per prefab.** The prefab is written once, and each instance references it. What an instance changes (a moved child, a hidden child, an added or removed child, a different mesh or material) is written as overrides on that instance. An instance with no changes is also marked `instanceable`. `Layout` controls where the prefab assets go:

| Layout | Result |
| --- | --- |
| `SingleFile` (default) | One file. Prefab assets are `class` prims under `/UnityPrototypes`, referenced internally. |
| `FolderWithAssets` | The scene file, plus one file per prefab in `<usd-name>_assets/`, each referenced by a relative path. |

An output path ending in `.usdz` packages either layout, asset files included.

Unity only keeps the link between a prefab and its instances in the Editor. To carry it into Play mode and players, the package adds a `UsdPrefabMarker` component to every prefab instance root of a scene when the scene is built or enters Play mode. Only that copy of the scene changes; scene files and prefabs on disk don't. For a prefab instantiated at runtime, use `UsdPrefabMarker.Instantiate(prefab, parent)` instead of `Object.Instantiate`, or call `UsdPrefabMarker.Attach(instance, prefab)`. A plain `Object.Instantiate` copy has no link and is exported as ordinary objects. In the Editor outside Play mode, the exporter reads the prefab link directly. To turn off the automatic markers, define the scripting symbol `USD_TOOLKIT_DISABLE_PREFAB_MARKERS`.

**Repeated meshes become instances of one prototype** when `ShareMeshes = true`. Objects that share one `Mesh` and the same materials reference a single prototype mesh and are marked `instanceable`.

`UsdExportResult` reports `ReferenceCount`, `OverrideCount`, `PrototypeCount`, the `SourceLayer` an override export depends on, and the `AssetFiles` it wrote.

`Preserve` always keeps the hierarchy, whatever `TransformPolicy` says. It needs native API 6; with an older native plugin, the export throws an exception that says so.

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

### Variant sets and payloads

Choose a variant before the stage is composed, and choose which payloads load. The source file isn't changed.

```csharp
var options = new UsdImportOptions { LoadPayloads = false };
options.VariantSelections.Add(new UsdVariantSelection("/World/Robot", "color", "red"));
options.PayloadsToLoad.Add("/World/Robot/Arm");

UsdImportResult result = UsdImporter.Import(path, options);
```

Selections apply in list order, so a variant set nested inside another set's variant can follow the outer selection. A selection that doesn't exist is skipped with a warning in the diagnostics. With `LoadPayloads = false`, only the prims in `PayloadsToLoad` load their payloads. An unloaded payload's own prim still becomes an empty GameObject, so it keeps its place in the hierarchy.

`result.Source` (a `UsdStageSource`) lists what the stage offers: `VariantSets` with their variants and current selection, `Payloads` and whether each was loaded, and the direct references, payloads, inherits, specializes and relocates in `Arcs`. To switch a variant, import again with the options `CreateOptionsWithVariant` returns:

```csharp
UsdImportOptions next = result.Source.CreateOptionsWithVariant("/World/Robot", "color", "blue");
UsdImportResult switched = UsdImporter.Import(result.Source.SourcePath, next);
```

A `Preserve` export writes the import's variant selections back, so the output shows the variants that were chosen in Unity.

### Import options

| Option | Default | Description |
| --- | --- | --- |
| `Parent` | `null` | The parent `Transform` of the imported root. |
| `RootObjectName` | `null` | The root GameObject name. Uses the file name when empty. |
| `ImportMaterials` | `true` | Creates Unity materials from `UsdPreviewSurface`, MaterialX and MDL (`OmniPBR`, `OmniSurface`, `OmniGlass`) values. See [Materials and textures](#7-materials-and-textures). |
| `ImportTextures` | `true` | Loads the PBR textures that materials reference. |
| `IncludeInvisible` | `true` | Also creates prims marked `visibility = "invisible"`. |
| `GenerateColliders` | `false` | Adds a `MeshCollider` to each mesh. |
| `RecalculateNormalsIfMissing` | `true` | Computes smooth normals when the file has none. |
| `AllowExternalAssetPaths` | `false` | Allows textures outside the stage folder. See [Untrusted USD files](#8-untrusted-usd-files). |
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

### Lights, cameras and spatial audio

These need native API 7. The Windows payload has it today. With a native plugin at API 6 (currently Linux and macOS, until their plugin is rebuilt), the import still works and these components are simply not added.

- **Lights and cameras.** USD lights emit, and cameras look, along their local -Z axis, and Unity's along +Z. The `Light` or `Camera` therefore sits on a child GameObject turned 180 degrees about Y and marked with `UsdImportHelperObject`. That child is not a prim, so exporters skip it and everything below it. USD has no single unit convention for light intensity, so the mapping to Unity is heuristic. Check the result against the source renderer.
- **Dome lights.** Unity has no dome light. The authored color, intensity and texture path are recorded on a `UsdDomeLight` component. Set `ApplyDomeLightToEnvironment = true` to also apply them to `RenderSettings`.
- **Spatial audio.** The importer sets up an `AudioSource` but doesn't load the clip, because audio files load asynchronously. Load `UsdSpatialAudio.FilePath`, for example with `UnityWebRequestMultimedia`, and assign the clip.

## 6. Threading

Methods that create or read Unity objects must run on the main thread.

| Method | Thread |
| --- | --- |
| `UsdImporter.ImportAsync` | Call from the main thread. Reading the stage runs on a worker thread. |
| `UsdImporter.Import` | Runs on the calling thread. Call from the main thread. |
| `UsdImporter.GetPreviewInfo` | Any thread. It doesn't touch Unity objects. |
| `UsdImporter.LoadImageFile` | Main thread. It creates a `Texture2D`. |
| `UsdExporter.ExportGameObject`, `ExportGameObjectWithResult` | Main thread. They read meshes, materials and textures. |

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

Imported materials use the first shader available from `Universal Render Pipeline/Lit`, `HDRP/Lit` and `Standard`. Textures are assigned to `_BaseMap`, `_BumpMap`, `_MetallicGlossMap` and `_EmissionMap`, with tiling and offset from `UsdTransform2d`. Each texture file is loaded once and shared between materials.

The importer reads these surface shaders:

| Source | What is read |
| --- | --- |
| `UsdPreviewSurface` | Base color, opacity, metallic, roughness, emission and their textures. |
| MaterialX (`standard_surface`, `open_pbr_surface`, `UsdPreviewSurface`) | The same values and textures, from a material authored in the stage or in a referenced `.mtlx` file. |
| MDL `OmniPBR`, `OmniSurface`, `OmniGlass` | Colors and textures. Other MDL materials aren't read. |
| `UsdPrimvarReader` on `diffuseColor` | The primvar color, such as `displayColor`, as the base color. |

Textures referenced from a material layer in a sub-folder resolve relative to that layer. A mesh or submesh with no material binding gets the shared grey `UsdDefaultMaterial` unless `AssignDefaultMaterial = false`.

### Albedo texture in the metallic slot

A base color texture assigned to the metallic slot (`_MetallicGlossMap`) produces a mirror-like surface. In Unity it can look correct because a skybox or reflection probe gives it something to reflect. In viewers without environment reflections, such as the Isaac Sim viewport, it renders black.

When a material's metallic map is the same texture as its albedo map, the exporter ignores the metallic map, uses the material's **Metallic** value, and logs a warning. To use one texture for both slots on purpose, set `IgnoreAlbedoInMetallicSlot = false`.

## 8. Untrusted USD files

The importer treats every USD file as untrusted, such as a downloaded asset, a shared `.usdz`, or a file in a scanned folder. The following protections are on by default.

### Texture paths

A USD file sets its own texture paths, and a path can point anywhere on disk. The importer skips any texture that resolves outside the folder of the stage or its layers, and logs a warning that shows the path as written in the file. Textures inside a `.usdz` are not affected.

For a trusted stage that references a shared texture library in another folder, set `UsdImportOptions.AllowExternalAssetPaths = true`.

### Mesh topology

The importer skips any mesh whose face data is inconsistent, and logs a warning with the prim path.

### Texture size

The importer refuses PNG and JPEG textures that declare more than 16,384 pixels on a side or more than 64 million pixels in total.

### Plugin search path

OpenUSD runs the libraries that a `plugInfo.json` file names. For this reason, `UsdExportOptions.PluginSearchPath` must be inside the package's own native folders. To use a custom OpenUSD installation, set `UsdExporter.AllowExternalPluginSearchPath = true` from code. This setting is static and never serialized, so an options object saved in a scene or prefab can't enable it.

## 9. Build a standalone player

1. In **Build Profiles**, select Windows or macOS. On macOS, the player architecture must match the native libraries.
2. Build with Mono or IL2CPP.
3. Write output files under `Application.persistentDataPath`.

When you build, the package copies the OpenUSD files into the player:

```text
<Build>/<App>_Data/Plugins/x86_64/Windows/     (Windows)
<Build>/<App>.app/Contents/PlugIns/            (macOS)
```

For distribution outside your own machine, sign the macOS app bundle after Unity builds it.

## 10. The USD Live Sync sample

`Samples~/Runtime Examples/Live Sync Example` synchronizes transforms in both directions between a running Unity scene and an external tool, such as NVIDIA Isaac Sim or a Python script. Unity exports the scene geometry once as `base_stage.usda`, then streams transform changes over TCP.

Every connection must authenticate with a token before it receives data. The server accepts connections from the local machine only, and the traffic is not encrypted.

For setup instructions, see the [Live Sync Example guide](../Samples~/Runtime%20Examples/Live%20Sync%20Example/README.md).

## 11. Check exported USD files

- `usdchecker <file>` checks the structure and validity.
- `usdcat <file>` prints the contents as text.
- usdview, Isaac Sim, Omniverse or Blender display the geometry and materials.

## 12. Troubleshooting

| Symptom | Cause | Solution |
| --- | --- | --- |
| `DllNotFoundException` | The native files are missing or are still Git LFS pointer files. | Run `git lfs pull`, and check the platform folder under `Runtime/Plugins`. |
| Native API version mismatch | The native plugin is from a different version of the package. | Reinstall the package, or rebuild the plugin. See [Appendix C](#appendix-c-rebuild-the-native-plugin). |
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
| `UsdLibraryItem` | A USD file found by `ScanFolder`. |
| `UsdRuntimeInfo` | Native plugin version, OpenUSD version and search paths. |
| `UsdExportException`, `UsdImportException` | Errors, with diagnostics. |
| `UsdTransformPolicy` | `BakedMesh` or `PreserveHierarchy`. |
| `UsdCompositionMode`, `UsdExportLayout`, `UsdImportedContentMode` | Composition settings for export. |
| `UsdStageSource`, `UsdPrimSource` | Components that record where imported objects came from. |
| `UsdPrefabMarker` | Links a runtime object to its prefab for export. |
| `UsdVariantSelection`, `UsdVariantSetInfo`, `UsdPayloadInfo`, `UsdCompositionArcInfo`, `UsdPrimCompositionFlags` | Composition data of an imported stage. |

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

Build OpenUSD with `Native~/build_openusd.py`, which verifies the OpenUSD source and its dependencies before building. For example, on macOS:

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
