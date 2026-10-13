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

The C# API checks the API version of the native plugin when it loads. It accepts any plugin at API 6 or newer (`UsdNative.MinimumApiVersion`) and refuses an older one, because fixes that aren't gated on the API version are missing from an older binary. Features that need API 7 to 12 switch on when the loaded plugin has them, and are skipped otherwise while the rest of the import works. See [Native API levels](#native-api-levels).

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
| `PointInstancer` | Yes | Each prototype is imported once and hidden. The instancer becomes one GameObject with a `UsdPointInstancer` component that draws every placement from the instance arrays with `Graphics.RenderMeshInstanced`, culled by cell. Instances can't be selected individually and get no collider. `invisibleIds` is honoured. See [Point instancers](#point-instancers). Needs native API 8. |
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
| Asynchronous import | Yes | Parses off the main thread and creates objects across several frames. `ImportAsync` returns before the textures are attached, and `UsdImportResult.TexturesReady` completes when they are (`StreamTextures`, on by default). |
| Statistics preview | Yes | Mesh, material, triangle and vertex counts before importing. |
| Folder scanning | Yes | `UsdLibraryScanner.ScanFolder`. |
| Up axis and unit conversion | Yes | Applied on the import root by default. See [Up axis and units](#up-axis-and-units). |
| Variant selection and payload loading | Yes | Choose variants and which payloads load. See [Variant sets and payloads](#variant-sets-and-payloads). Needs native API 6. |
| Composition, instancing | Yes | References, payloads, inherits, specializes and instances are composed; each instance becomes its own GameObjects. Instances with identical geometry share one `Mesh` (`ShareIdenticalMeshes`; `UsdImportResult.UniqueMeshCount` reports the distinct count). |
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

### Native API levels

The native plugin reports its API version through `RUsd_GetApiVersion`. The shipped Windows, macOS and Linux payloads are API 12. The C# API accepts API 6 and newer, and uses each later feature only when the loaded plugin reaches its level:

| API | Added | Constant in `UsdNative` |
| --- | --- | --- |
| 6 | Composition: sublayers, references, payloads, instancing, variant selections and overrides on export; variant and payload control on import. The minimum accepted. | `CompositionApiVersion` |
| 7 | Lights, cameras and spatial audio on import. | `SceneComponentsApiVersion` |
| 8 | Point instancers delivered as prototype and matrix arrays instead of expanded prims. | `InstancedMeshApiVersion` |
| 9 | How a material's metallic map is packed. | `MetallicLayoutApiVersion` |
| 10 | A plain metallic map and a separate roughness map, which the C# side packs into one texture. | `TextureLayoutApiVersion` |
| 11 | EXR decoding and block compression in native code. | `ImageCodecApiVersion` |
| 12 | Whole textures and UDIM atlases built in native code in one call. | `ImageBuildApiVersion` |

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

### Point instancers

A `UsdGeomPointInstancer` is one prototype scattered to many placements, and the importer keeps it that way. Each prototype is imported once as a hidden GameObject, and the instancer's prim becomes a GameObject with a `UsdPointInstancer` component that draws the prototype's `Mesh` with its `Materials` at every placement, with `Graphics.RenderMeshInstanced`. The placements are sorted into a grid of cells, each with its own bounds, so the cells that are off screen aren't drawn. Instances can't be selected individually and have no collider. A stage whose parts are selected one by one is better served by prims than by an instancer.

`UsdPointInstancer` exposes `Mesh`, `Materials`, `InstanceCount`, `LodDistance`, `CellCount` and `Draw`. The component calls `Draw` every frame, and it is public so a custom render loop or a benchmark can call it. An import accepts at most 4,000,000 placements (instances times prototype meshes) across its instancers, and is refused past that.

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

These need native API 7, which every shipped payload includes. With a native plugin older than API 7, the import still works and these prims arrive as empty transforms, without the components.

- **Lights and cameras.** USD lights emit, and cameras look, along their local -Z axis, and Unity's along +Z. The `Light` or `Camera` therefore sits on a child GameObject turned 180 degrees about Y and marked with `UsdImportHelperObject`. That child is not a prim, so exporters skip it and everything below it. USD has no single unit convention for light intensity, so the mapping to Unity is heuristic. Check the result against the source renderer.
- **Dome lights.** Unity has no dome light. The authored color, intensity and texture path are recorded on a `UsdDomeLight` component. Set `ApplyDomeLightToEnvironment = true` to also apply them to `RenderSettings`.
- **Spatial audio.** The importer sets up an `AudioSource` but doesn't load the clip, because audio files load asynchronously. Load `UsdSpatialAudio.FilePath`, for example with `UnityWebRequestMultimedia`, and assign the clip.

## 6. Threading

Methods that create or read Unity objects must run on the main thread.

`ImportAsync` splits an import between threads. Call it from the main thread. Reading the stage, decoding PNG and EXR files, stitching UDIM tiles, packing metallic and roughness maps and albedo and opacity maps, and block compression run on worker threads in parallel, in native code when the plugin is API 11 or 12 and in managed code otherwise. GameObjects, meshes and materials are created on the main thread, within `MaxMillisecondsPerFrame` per frame. `ImportAsync` returns once the objects and the untextured materials exist, and keeps attaching textures on the main thread as they finish decoding, until `TexturesReady` completes. JPEG files and 16-bit PNGs are decoded on the main thread through `Texture2D.LoadImage`.

| Method | Thread |
| --- | --- |
| `UsdImporter.ImportAsync` | Call from the main thread. Stage reading and texture decoding run on worker threads; objects are created on the main thread. |
| `UsdImporter.Import` | Runs on the calling thread, apart from texture decoding, which uses worker threads. Call from the main thread. |
| `UsdImporter.GetPreviewInfo` | Any thread. It doesn't touch Unity objects. |
| `UsdImporter.LoadImageFile` | Main thread. It creates a `Texture2D`. |
| `UsdLibraryScanner.ScanFolder` | Any thread. It lists files and doesn't touch Unity objects. |
| `UsdExporter.ExportGameObject`, `ExportGameObjectWithResult` | Main thread. They read meshes, materials and textures. |
| `UsdPointInstancer.Draw` | Main thread, every frame. The component calls it from `Update`. |

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

The importer skips any mesh whose face data is inconsistent, and logs a warning with the prim path.

### Texture size

The importer refuses any image that declares more than 16,384 pixels on a side or more than 64 million pixels in total. The limits apply to PNG, JPEG and OpenEXR files and to UDIM atlases. An image file larger than 512 MB is refused before it is read. A PNG or EXR whose header claims more pixels than its compressed bytes can expand to is refused before any memory is allocated for it.

### Point instancers

An import whose point instancers come to more than 4,000,000 placements (instances times prototype meshes) is refused, with an error that names the limit. A prototype that contains its own instancer, an ancestor of it, or an instancer already being expanded would import itself again, so it is skipped with a warning.

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
| Native API version mismatch | The native plugin is older than API 6, the minimum the package accepts. | Reinstall the package, or rebuild the plugin. See [Appendix C](#appendix-c-rebuild-the-native-plugin). |
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
| `UsdStageSource`, `UsdPrimSource` | Components that record where imported objects came from. |
| `UsdStageSource.CreateOptionsWithVariant` | Returns import options that re-import the stage with another variant selected. |
| `UsdPrefabMarker` | Links a runtime object to its prefab for export. |
| `UsdPrefabMarker.Instantiate`, `UsdPrefabMarker.Attach` | Instantiates a prefab with its link, or attaches the link to an existing instance. |
| `UsdVariantSelection`, `UsdVariantSetInfo`, `UsdPayloadInfo`, `UsdCompositionArcInfo`, `UsdPrimCompositionFlags` | Composition data of an imported stage. |
| `UsdPointInstancer` | Component that draws a point instancer's prototype at every placement. `Mesh`, `Materials`, `InstanceCount`, `LodDistance`, `CellCount`, `Draw`. |
| `UsdDomeLight` | Component that records a `UsdLuxDomeLight`'s color, intensity and texture path. |
| `UsdSpatialAudio` | Component that records a `UsdMediaSpatialAudio`'s file path, start time and end time. |
| `UsdImportHelperObject` | Marks a child object the importer created to turn a light or camera to Unity's convention. Exporters skip it. |
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
