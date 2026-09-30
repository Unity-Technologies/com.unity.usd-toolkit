# Unity USD Toolkit User Manual

Runtime USD export and import plugin for Windows / macOS / Linux Standalone

Package version: 0.7.2-exp.1
Native ABI: API 5
Minimum Unity version: 2023.1
Document revised: 2026-09-23
Audience: developers and technical users adding this package to a Unity project for runtime USD export and import

> Korean edition: `Unity USD Toolkit User Manual KR.md`

## Contents

1. Overview
2. Scope and limitations
3. Adding the package to a Unity project
4. Checking the native runtime payload
5. Exporting at runtime
6. Export options
7. Importing at runtime
8. Import options
9. How untrusted USD files are handled
10. The Live Sync sample
11. Using the package in a Standalone build
12. Inspecting the resulting USD
13. Troubleshooting
14. Pre-release checklist
- Appendix A. Public API summary
- Appendix B. Rebuilding the native plugin

## 1. Overview

Unity USD Toolkit writes and reads USD files **inside** a built Unity player. It is not an Editor-only tool: while the application runs, it hands GameObject mesh data to a Pixar OpenUSD-based native plugin to produce `.usd`, `.usda`, `.usdc` and `.usdz` files, and reads USD stages back into Unity GameObjects through the same path.

### How it is put together

- The Unity C# API lives under the `Unity.USDToolkit` namespace.
- C# calls only the C ABI functions of the `UnityUSDToolkitNative` plugin, through P/Invoke.
- The native plugin uses the Pixar OpenUSD C++ API to work with stages, meshes, materials and transforms.
- Nothing here depends on Unity's USD Editor packages, USD.NET, or `com.unity.exporter.usd`.

### Native ABI version

The managed layer checks the API version the loaded plugin reports and **refuses anything that does not match the package source exactly** (currently API 5). It previously accepted API 2 and upward, but a fix that is not gated on the ABI — a security fix, for instance — is simply absent from an older binary with nothing in the version number to say so. The check runs on **both** the export and the import paths.

## 2. Scope and limitations

### Platforms

| Platform | Supported | Minimum OS | Notes |
| --- | --- | --- | --- |
| Windows x64 (Editor / Standalone) | Yes | Windows 10 version 21H1 | `Runtime/Plugins/x86_64/Windows` |
| macOS (Editor / Standalone) | Yes | macOS 12.0 (Monterey) | Universal (x86_64 + arm64), `Runtime/Plugins/macOS` |
| Linux x64 (Editor / Standalone) | Yes | **Ubuntu 24.04** | Self-contained payload, `Runtime/Plugins/x86_64/Linux` |
| Mobile / WebGL / console | No | — | Out of scope for this version. |

The minimum OS is a property of the shipped native payload, not of the C# layer, and it applies to
players you build as well as to the Editor: the same libraries are copied into a standalone build.

- **macOS 12.0.** Every dylib is built with a deployment target of 12.0, which is Unity 6.3's
  minimum for a macOS player. dyld refuses to load them on anything older.
- **Ubuntu 24.04.** The Linux payload is built on Ubuntu 24.04 and needs **glibc ≥ 2.38** and
  **libstdc++ with `GLIBCXX_3.4.32`** (GCC 13). Unity 6.3 itself also supports Ubuntu 22.04, which
  ships glibc 2.35 and `GLIBCXX_3.4.30` — this package does not run there, and the toolkit throws
  a native-load error naming the requirement rather than failing silently. Nothing in the code
  needs 24.04; the dependency comes from the build machine, and a rebuild on 22.04 would lower it.

> All three platforms are built against the public **OpenUSD `v26.05`** tag (commit `2095faf`), and the native ABI is API 5 on all three. Standardising on a published tag is what lets a third party reproduce and check the shipped payload.

### Export

| Feature | Supported | Notes |
| --- | --- | --- |
| Static mesh | Yes | GameObjects with `MeshFilter` + `MeshRenderer` |
| GPU readback for non-readable meshes | Yes | Exports in Play mode even with `Read/Write Enabled` off |
| Normals / UV0 | Yes | Each can be turned off. |
| Per-submesh material binding | Yes | Authored as `UsdGeomSubset` |
| Hierarchy transform preservation | Yes | `UsdTransformPolicy.PreserveHierarchy` |
| Baked mesh transform | Yes | The default |
| `UsdPreviewSurface` | Yes | Base color, opacity, metallic, roughness, emission |
| **PBR texture export** | **Yes** | PNGs written to `<usd-name>_textures/` and referenced relatively |
| **`.usdz` packaging** | **Yes** | A `.usdz` output path writes a package. An ARKit-compatible mode is available. |
| Mesh extent | Yes | |
| Inactive / disabled visibility | Yes | Authored as `visibility = "invisible"` |
| Skinned mesh / animation | No | A later milestone |

### Import (MVP)

| Feature | Supported | Notes |
| --- | --- | --- |
| `.usd` / `.usda` / `.usdc` / `.usdz` | Yes | Textures inside a usdz are read through the stage resolver. |
| Static `UsdGeomMesh` | Yes | |
| Xform hierarchy reconstruction | Yes | Every transformable prim becomes a Unity `Transform`. |
| Multiple materials per mesh | Yes | `materialBind` `UsdGeomSubset` becomes a Unity submesh. |
| `UsdPreviewSurface` and PBR textures | Yes | Albedo / normal / metallic-smoothness / emission |
| Asynchronous import | Yes | Parsing off the main thread, object creation sliced across frames |
| Stage preview metadata | Yes | Mesh, material, triangle and vertex counts before importing |
| Folder scanning | Yes | `UsdLibraryScanner.ScanFolder` |
| Skinned mesh / animation / variants / payload streaming | No | A later milestone |

## 3. Adding the package to a Unity project

### Option A: drop it into the Packages folder

1. Copy the `com.unity.usd-toolkit` folder into your project's `Packages` folder.
2. Confirm the final path is `<UnityProject>/Packages/com.unity.usd-toolkit/package.json`.
3. Open the Editor and check the Console for compile errors.

### Option B: add it from Package Manager

1. Open `Window > Package Manager`.
2. Choose `+` → `Add package from disk`.
3. Select the package's `package.json`.

### Registering it in manifest.json

```json
"dependencies": {
  "com.unity.usd-toolkit": "file:Packages/com.unity.usd-toolkit"
}
```

> The native binaries are stored with Git LFS. If you obtained the package by cloning the repository, run `git lfs install` followed by `git lfs pull`. The plugin will not load while the binaries are still LFS pointer files.

## 4. Checking the native runtime payload

Each platform needs these files:

```text
Runtime/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_rt.dll          (OpenUSD monolithic, renamed from usd_ms to avoid a name collision
                       with another package's OpenUSD)
  tbb_usdrt.dll       (Intel's TBB, renamed from tbb.dll so the Windows loader cannot
                       hand the plugin the Unity Editor's own copy instead)
  lib/usd/**/plugInfo.json
  plugin/usd/**/plugInfo.json

Runtime/Plugins/macOS/
  UnityUSDToolkitNative.dylib
  libusd_ms.dylib
  libtbb*.dylib
  lib/usd/… , plugin/usd/…

Runtime/Plugins/x86_64/Linux/
  libUnityUSDToolkitNative.so
  lib/libusd_ms.so , lib/libtbb.so.2
  lib/usd/… , plugin/usd/…
```

The macOS dylibs resolve their dependencies through `@loader_path`; confirm this with
`otool -L Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib`.

### Payload integrity verification

Before the first P/Invoke, the package compares the SHA-256 of each native binary against the digest recorded in `Runtime/Native/NativeRuntimeHashes.g.cs` and refuses to load a payload that does not match. Unlike the older presence check, this reads the file contents, so it catches a binary that was substituted or corrupted in distribution. The check runs once per process.

If you rebuild the native plugin yourself you must regenerate the manifest:

```bash
python3 Native~/generate_native_hashes.py
```

> The script hashes **every** platform's binaries, not just the one you rebuilt. It refuses to run while any payload file is still an unfetched Git LFS pointer, so run `git lfs pull` first.

### Verifying that the package is the one Unity published

Three separate things can be checked, and they establish different properties. The digest
manifest above proves the payload has not changed since it was packed; the two below prove where
it came from.

**1. The package signature (all platforms).** A package published through Unity's pipeline carries
a CMS/PKCS#7 attestation at `package/.attestation.p7m`, signed by Unity's PKI and covering the
digest of every file in the tarball — the native binaries under `Runtime/Plugins/**` included. Any
file added, removed or altered after packing invalidates it. Unity 6.3 and later verify it
automatically and show the result in the Package Manager window; to check it yourself:

```bash
tar -xzf com.unity.usd-toolkit-<version>.tgz package/.attestation.p7m
openssl cms -verify -in package/.attestation.p7m -inform DER -noverify -out attestation.json
openssl pkcs7 -in package/.attestation.p7m -inform DER -print_certs -text | head -40
```

Drop `-noverify` and pass `-CAfile` with Unity's root to check the chain as well as the structure.
Internally the same two properties are asserted by PVP-28-3 (the signature is present) and
PVP-29-3 (it is valid and matches the archive contents).

**2. Platform code signatures (Windows and macOS).** The native binaries carry a publisher
signature from Unity's certificates:

```bash
# macOS — expect a Developer ID authority, not "Signature=adhoc"
codesign --verify --strict --verbose=2 Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib
codesign -dv --verbose=4 Runtime/Plugins/macOS/libusd_ms.dylib
```

```powershell
# Windows — expect Status: Valid
Get-ChildItem Runtime\Plugins\x86_64\Windows\*.dll | ForEach-Object {
    Get-AuthenticodeSignature $_.FullName | Select-Object Status, SignerCertificate
}
```

**Linux is deliberately not code-signed.** Neither the ELF format, the dynamic linker, nor Unity's
signing infrastructure has an equivalent of Authenticode or Developer ID for a `.so`; Unity's code
signing service covers Windows PE and macOS Mach-O only, and other Unity packages that ship native
Linux libraries are signed the same way — that is, at the package level. For Linux the attestation
in (1) and the digests in (3) are the integrity evidence.

**3. Per-file digests (all platforms).** `ThirdPartyNotices~/sbom.cdx.json` is a CycloneDX 1.6
bill of materials listing every third-party component in the payload with its version, source and
SHA-256. It is the same digest the runtime check uses, in a form you can verify from outside the
Editor:

```bash
sha256sum Runtime/Plugins/x86_64/Linux/lib/libusd_ms.so
python3 -c "import json;[print(c['hashes'][0]['content'], [p['value'] for p in c['properties'] if p['name']=='unity:shippedPath'][0]) for c in json.load(open('ThirdPartyNotices~/sbom.cdx.json'))['components'][1:]]"
```

The SBOM also records which OpenUSD tag and commit each binary was built from, so a third party can
rebuild from the same public source and compare what the binary contains.

## 5. Exporting at runtime

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

The output extension decides the format. Because usdz is a read-only zip, a `.usdz` path makes the exporter write a `.usdc` plus its textures into a temporary staging folder and package that; nothing is left beside the `.usdz`.

### Readable meshes

A player can export a mesh whose `Read/Write Enabled` is off. In Play mode the exporter reads the buffers back from GPU memory, and it uses the CPU path when `Read/Write Enabled` is on. With `RequireReadableMeshes` left at `true`, a mesh that neither path can read raises an exception.

## 6. Export options

| Option | Default | Notes |
| --- | --- | --- |
| `RootPrimName` | `null` | Falls back to the export root GameObject's name. |
| `MetersPerUnit` | `1.0f` | Must be greater than zero. |
| `IncludeInactive` | `false` | Whether inactive children are included. |
| `RequireReadableMeshes` | `true` | Throws on a mesh that cannot be read. |
| `ExportNormals` | `true` | |
| `ExportUv0` | `true` | |
| `ExportBounds` | `true` | Authors mesh extent. |
| `ExportDisabledRenderers` | `true` | Includes disabled renderers as invisible. |
| `PreserveInactiveAndDisabledVisibility` | `true` | Authors `visibility = "invisible"`. |
| `TransformPolicy` | `BakedMesh` | `BakedMesh` or `PreserveHierarchy` |
| `ExportTextures` | `false` | Writes PNGs to `<usd-name>_textures/` and references them. |
| `IgnoreAlbedoInMetallicSlot` | `true` | Treats an albedo texture in the metallic slot as a misassignment, exports the scalar value instead, and warns. |
| `UsdzArkitCompatible` | `false` | `.usdz` output only: packages under ARKit constraints. |
| `ValidateNativeRuntime` | `true` | Checks that the native payload is **present**. |
| `VerifyNativeRuntimeIntegrity` | `true` | Checks the payload's **contents** (SHA-256). |
| `ValidateOpenUsdPluginPath` | `true` | Checks plugin/resource discovery paths. |
| `PluginSearchPath` | `null` | Overrides OpenUSD plugin discovery. **See section 9.** |
| `CaptureNativeDiagnostics` | `false` | Captures OpenUSD diagnostics. |
| `NativeDiagnosticsLogPath` | `null` | File path for the captured diagnostics. |
| `LogExportSummary` | `false` | Logs a summary after a successful export. |

### Choosing a TransformPolicy

| Policy | When to use it | Result |
| --- | --- | --- |
| `BakedMesh` | Most runtime exports, and whenever the transform structure does not matter downstream | Transforms are baked into mesh points and the USD hierarchy stays flat. |
| `PreserveHierarchy` | When the Unity hierarchy and local transforms must survive into USD | The GameObject hierarchy becomes Xform prims and mesh points stay in local space. |

## 7. Importing at runtime

### Preview, then import

```csharp
UsdImportPreviewInfo preview = UsdImporter.GetPreviewInfo(path);
if (preview.TriangleCount > 5_000_000)
{
    // Warn, or offer a cancel, before committing to a heavy import.
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

`ImportAsync` runs the USD parse and file reads on a worker thread and creates Unity objects on the main thread, sliced across frames (`MaxMillisecondsPerFrame`). **Call it from the main thread.** Use `UsdImporter.Import` when you need the synchronous form.

### Scanning a folder

```csharp
IReadOnlyList<UsdLibraryItem> items = UsdLibraryScanner.ScanFolder(folderPath);
```

## 8. Import options

| Option | Default | Notes |
| --- | --- | --- |
| `Parent` | `null` | Parent Transform for the created root. |
| `RootObjectName` | `null` | Falls back to the file name. |
| `ImportMaterials` | `true` | Turns `UsdPreviewSurface` values into Unity materials. |
| `ImportTextures` | `true` | Loads referenced PBR textures. |
| `IncludeInvisible` | `true` | Creates prims marked `visibility = "invisible"` too. |
| `GenerateColliders` | `false` | Adds a `MeshCollider`. |
| `RecalculateNormalsIfMissing` | `true` | Computes normals when the file has none. |
| `AllowExternalAssetPaths` | `false` | **See section 9.** Permits reads outside the stage folder. |
| `MaxMillisecondsPerFrame` | `10f` | Main-thread budget per frame for `ImportAsync`. |
| `ProgressCallback` | `null` | `(0..1, phase)`, invoked on the main thread. |
| `CaptureNativeDiagnostics` | `false` | |
| `NativeDiagnosticsLogPath` | `null` | |

## 9. How untrusted USD files are handled

A USD file being imported is treated as **untrusted input**: a marketplace asset, a shared `.usdz`, and anything dropped into a scanned folder all qualify. The defaults are as follows.

### Asset paths are confined to the stage folder (on by default)

A USD file authors its own texture paths, so an `inputs:file` can name an absolute path or climb out with `../` and make the importer read an unrelated file. Any path that resolves outside the stage's own folder — or outside the folder of any layer composing the stage — is therefore skipped. Textures packaged inside a `.usdz` live within the package and are unaffected.

For a stage you trust that deliberately references a shared texture library elsewhere, set `UsdImportOptions.AllowExternalAssetPaths = true`.

A rejected path is reported by its authored spelling only; the resolved absolute path is never printed, so a blocked attempt does not disclose local filesystem layout.

### Mesh topology is checked (on by default)

A mesh whose `faceVertexCounts` do not sum to exactly the `faceVertexIndices` length is reported and skipped. Such a file could previously make the importer read outside its buffer and take down the process.

### Texture dimensions are capped (on by default)

If a PNG header declares more than 16384 pixels per side, or more than 64M pixels in total, the built-in decoder refuses it and falls back to Unity's decoder. This stops a small file from driving a multi-gigabyte allocation from its header alone.

### The plugin search path is confined (on by default)

OpenUSD loads and executes whatever library a `plugInfo.json` names, inside your process. `UsdExportOptions.PluginSearchPath` must therefore resolve inside the package's own native folders, and anything outside them is refused. To run deliberately against a custom OpenUSD install, set `UsdExporter.AllowExternalPluginSearchPath = true` from code. That switch is a static that is never serialized, so an options object stored in a scene or prefab cannot grant itself the permission.

## 10. The Live Sync sample

`Samples/Live Sync Example` keeps transforms in sync, in both directions, between a running Unity scene and an external tool (Python, NVIDIA Isaac Sim, or a DCC). Unity is the host: it exports the geometry once as `base_stage.usda`, then streams newline-delimited JSON over loopback TCP.

**The channel requires authentication.** Every connection must first send `{"cmd":"auth","token":"..."}`; until that succeeds no command is answered and no broadcast is delivered. The token comes from the inspector's `Auth Token`, else the `USD_LIVE_SYNC_TOKEN` environment variable, else a per-session value written to `live_sync_token.txt` beside `base_stage.usda`.

The default bind is loopback. Binding to an address reachable from other machines requires both `allowNonLoopbackBind` and an **explicit** `authToken` — a generated token is shared only through a local file, so it is not a credential a remote client can obtain. In that configuration the traffic is not encrypted and the token travels in clear text, so use it only on a trusted network.

See `Samples/Live Sync Example/README.md` for the full walkthrough.

## 11. Using the package in a Standalone build

1. Pick the target platform in Build Settings. On macOS the build architecture must match the payload.
2. Both Mono and IL2CPP work.
3. Writing output under `Application.persistentDataPath` is recommended.

### Where the payload lands in a built player

```text
<Build>/<App>_Data/Plugins/x86_64/Windows/     (Windows)
<Build>/<App>.app/Contents/PlugIns/            (macOS)
<Build>/<App>_Data/Plugins/x86_64/Linux/       (Linux)
```

An Editor build postprocessor in the package copies the OpenUSD resource tree into the player, because `plugInfo.json` and `share/usd/plugins` are data files rather than native libraries and Unity's native plugin importer does not move them.

## 12. Inspecting the resulting USD

- `usdchecker <file>` — structure and basic validity
- `usdcat <file>` — dump the contents as text
- Blender USD import — eyeball geometry and materials
- usdview / Omniverse — inspect stage structure and material bindings

## 13. Troubleshooting

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| `DllNotFoundException` | Native files missing, or still LFS pointers | Check the platform payload folder and run `git lfs pull`. |
| Native API version mismatch exception | The plugin reports a lower API than the package source | Rebuild that platform's plugin from `Native~` (Appendix B). |
| Load refused on a digest mismatch | The payload does not match the manifest | If you rebuilt it yourself, run `python3 Native~/generate_native_hashes.py`. Otherwise re-acquire the package. |
| OpenUSD plugin path error | `plugin/usd` or `lib/usd` missing | Confirm the `plugInfo.json` files are present. |
| `PluginSearchPath` rejected | The path is outside the package's native folders | See section 9; set `UsdExporter.AllowExternalPluginSearchPath` if this is intended. |
| Mesh is not readable | Neither path could read the mesh | Turn on `Read/Write Enabled`, or export in Play mode. |
| Textures empty after import | The texture path resolved outside the stage folder | Check the Console warning; for a trusted file, enable `AllowExternalAssetPaths`. |
| Some meshes missing after import | Rejected for inconsistent topology | The Console warning names the prim path. The source file needs fixing. |
| No file produced | Output path permissions, or an exception | Write under `Application.persistentDataPath` and check the log. |
| Works in the Editor, fails in Standalone | Payload missing from the build | Check the player's Plugins folder. |

### Printing diagnostics

```csharp
UsdRuntimeInfo info = UsdExporter.GetRuntimeInfo();
Debug.Log(info.ToString());   // API version, OpenUSD version, native paths
```

## 14. Pre-release checklist

- Package id is `com.unity.usd-toolkit` and the version is what you intend
- Each platform's native payload is a real binary, not an LFS pointer
- `python3 Native~/generate_native_hashes.py` produces no diff (a diff means the manifest is stale)
- `plugin/usd/plugInfo.json` and `lib/usd/plugInfo.json` are included
- The package compiles in the Unity Editor
- A simple mesh exports and passes `usdchecker`
- The exported file imports back (round trip)
- Export and import both work in a Standalone player on the target platform
- If you ship IL2CPP, verify an IL2CPP player separately
- Tell users that skinned meshes and animation are not supported yet

## Appendix A. Public API summary

| API | Purpose |
| --- | --- |
| `UsdExporter.ExportGameObject` | Simple export with no result object |
| `UsdExporter.ExportGameObjectWithResult` | Export that returns a summary |
| `UsdExporter.GetRuntimeInfo` | Native API / OpenUSD version and runtime paths |
| `UsdExporter.AllowExternalPluginSearchPath` | Permit a plugin path outside the package (static, never serialized) |
| `UsdImporter.GetPreviewInfo` | Stage statistics before importing |
| `UsdImporter.Import` | Synchronous import |
| `UsdImporter.ImportAsync` | Asynchronous, frame-sliced import |
| `UsdLibraryScanner.ScanFolder` | List the USD files in a folder |
| `UsdExportOptions` / `UsdImportOptions` | Behaviour options |
| `UsdExportResult` / `UsdImportResult` / `UsdImportPreviewInfo` | Results and statistics |
| `UsdExportException` / `UsdImportException` | Failures, with diagnostics |
| `UsdTransformPolicy` | `BakedMesh` or `PreserveHierarchy` |

## Appendix B. Rebuilding the native plugin

Most users should keep the payload that ships with the package. Rebuilding is only needed when changing the OpenUSD version or modifying the native wrapper.

- Windows / Linux: `Native~/REBUILD_WINDOWS_LINUX.md` (step-by-step, with gates)
- macOS:

```bash
python3 Native~/build_openusd.py --platform macos --openusd-src <OpenUSD v26.05 clone> \
  --install /Users/Shared/usd-26.05/install --build-target universal --require-scan
bash Native~/build_macos.sh \
  --openusd-root /Users/Shared/usd-26.05/install --arch universal \
  --codesign-id "<Developer ID>"
```

Build OpenUSD with `Native~/build_openusd.py` on every platform, never with `build_usd.py` directly: it checks the pinned TBB archive before `build_usd.py` compiles it and stamps the install, and every wrapper build refuses an install without that stamp.

After any rebuild you **must**:

1. Run `python3 Native~/generate_native_hashes.py` to regenerate the digest manifest.
2. Run `Native~/Tests~/security_test.cpp` to confirm the security fixes are in the binary.
3. Check that no `.meta` file was deleted — the install step can wipe them, and Unity would regenerate them with new GUIDs, breaking references.

`Native~/REBUILD_WINDOWS_LINUX.md` and `BUILD_NOTES.md` have the full procedure and its verification gates.
