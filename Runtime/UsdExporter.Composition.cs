using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    // UsdCompositionMode.Preserve: exports that keep USD composition instead of flattening it.
    //
    // Three things are written as composition rather than as copies:
    //   - an imported stage (UsdStageSource) becomes overrides on its source file, either as a
    //     sublayer under the whole output or as a reference where it sits in the hierarchy;
    //   - a prefab instance becomes a reference to one asset per prefab, with the instance's
    //     differences from the prefab as overrides on top;
    //   - with ShareMeshes, objects sharing one Mesh and material set reference one prototype.
    // Everything else is written as it is in the Flatten mode with the hierarchy kept.
    public static partial class UsdExporter
    {
        private const string PrototypeScopePath = "/UnityPrototypes";
        private const float PositionTolerance = 1e-5f;
        private const float ScaleTolerance = 1e-5f;
        private const float RotationDotTolerance = 1e-6f;

        private static void ExportComposed(
            GameObject root,
            string rootPrimName,
            string stagePath,
            bool staged,
            UsdExportOptions options,
            UsdExportResult result,
            TextureExportContext textures,
            Action<IntPtr, ExportSceneInfo> afterScene,
            ref IntPtr sceneContext)
        {
            if (!UsdNative.SupportsComposition)
            {
                throw new UsdExportException(
                    $"UsdCompositionMode.Preserve needs {UsdNative.NeedsPlugin(UsdNative.CompositionApiVersion)}; {UsdNative.LoadedPluginDescription}. Rebuild the native plugin for this platform (see Native~/REBUILD_WINDOWS_LINUX.md), or export with UsdCompositionMode.Flatten.");
            }

            var session = new CompositionSession(options, result, textures, stagePath, staged);
            session.Run(root, rootPrimName, afterScene, ref sceneContext);
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
            }

            string left = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string right = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(left, right,
                Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase);
        }

        // An asset path as a layer in `fromDirectory` should author it: "./x.usda" or "../a/x.usda".
        // The "./" matters: without it USD treats the path as a search path, not anchored to the layer.
        private static string MakeLayerRelativePath(string fromDirectory, string target)
        {
            string relative = Path.GetRelativePath(fromDirectory, target).Replace('\\', '/');
            if (Path.IsPathRooted(relative))
            {
                return relative;
            }

            return relative.StartsWith("../", StringComparison.Ordinal) ? relative : "./" + relative;
        }

        private static bool SameLocalTransform(Transform t, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            return (t.localPosition - position).sqrMagnitude <= PositionTolerance * PositionTolerance &&
                (t.localScale - scale).sqrMagnitude <= ScaleTolerance * ScaleTolerance &&
                1f - Mathf.Abs(Quaternion.Dot(t.localRotation, rotation)) <= RotationDotTolerance;
        }

        private static bool SameMaterials(Material[] a, Material[] b)
        {
            int lengthA = a?.Length ?? 0;
            int lengthB = b?.Length ?? 0;
            if (lengthA != lengthB)
            {
                return false;
            }

            for (int i = 0; i < lengthA; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string ParentPrimPath(string primPath)
        {
            int slash = primPath.LastIndexOf('/');
            return slash <= 0 ? string.Empty : primPath.Substring(0, slash);
        }

        private static bool IsRootPrimPath(string primPath) =>
            primPath.Length > 1 && primPath.LastIndexOf('/') == 0;

        private static readonly RUsdMatrix4x4 IdentityUsdMatrix = new RUsdMatrix4x4
        {
            M00 = 1.0,
            M11 = 1.0,
            M22 = 1.0,
            M33 = 1.0
        };

        // What identifies a prefab instance: an id shared by all instances of one prefab, a name
        // for the asset, and the prefab asset itself when it can be reached (null otherwise, in
        // which case the first instance exported stands in for it).
        private sealed class PrefabIdentity
        {
            public string Id;
            public string Name;
            public GameObject Asset;
        }

        private static PrefabIdentity GetPrefabIdentity(GameObject gameObject)
        {
            UsdPrefabMarker marker = gameObject.GetComponent<UsdPrefabMarker>();
            if (marker != null && !string.IsNullOrEmpty(marker.AssetId))
            {
                GameObject asset = marker.SourcePrefab;
                // A marker that lives on the prefab asset points at itself, and Object.Instantiate
                // remaps that to the copy: a scene object is no prefab asset.
                if (asset != null && (asset.scene.IsValid() || asset == gameObject))
                {
                    asset = null;
                }

                return new PrefabIdentity
                {
                    Id = marker.AssetId,
                    Name = string.IsNullOrEmpty(marker.AssetName) ? (asset != null ? asset.name : gameObject.name) : marker.AssetName,
                    Asset = asset
                };
            }

#if UNITY_EDITOR
            if (UnityEditor.PrefabUtility.IsAnyPrefabInstanceRoot(gameObject))
            {
                string assetPath = UnityEditor.PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
                GameObject asset = string.IsNullOrEmpty(assetPath)
                    ? null
                    : UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset != null && asset != gameObject)
                {
                    return new PrefabIdentity
                    {
                        Id = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath),
                        Name = asset.name,
                        Asset = asset
                    };
                }
            }
#endif

            return null;
        }

        // One USD layer being written, and what has been written into it so far.
        private sealed class LayerWriter
        {
            public IntPtr Context;
            public string FilePath;
            public string Directory;

            // Prim paths taken in this layer; new prims get a numeric suffix to stay unique.
            public readonly HashSet<string> UsedPaths = new HashSet<string>(StringComparer.Ordinal);

            // Set on a layer over another stage, whose prims new names must not land on.
            public bool CheckComposedPrims;

            // Object -> its prim in this layer, and its mesh prim. Prefab overrides use these to
            // find, inside an instance, the prim that stands for an object of the prefab.
            public readonly Dictionary<Transform, string> Paths = new Dictionary<Transform, string>();
            public readonly Dictionary<Transform, string> MeshPaths = new Dictionary<Transform, string>();

            // Objects whose prim already exists below this layer -- an imported prim the diff only
            // overrides, or a prefab object an instance's reference brings in -- as opposed to
            // ones this layer defines. Skinned renderers written into the layer, so none is twice.
            public readonly HashSet<Transform> Composed = new HashSet<Transform>();
            public readonly HashSet<Renderer> SkinsWritten = new HashSet<Renderer>();

            public readonly Dictionary<string, string> MeshPrototypes = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, PrefabAsset> InternalPrefabs = new Dictionary<string, PrefabAsset>(StringComparer.Ordinal);
            public bool PrototypeScopeDefined;

            public string UniqueChild(string parentPath, string name)
            {
                string basePath = parentPath + "/" + name;
                string path = basePath;
                int suffix = 1;
                while (UsedPaths.Contains(path) || (CheckComposedPrims && UsdNative.PrimExists(Context, path)))
                {
                    path = basePath + "_" + suffix++;
                }

                UsedPaths.Add(path);
                return path;
            }

            public void EnsurePrototypeScope()
            {
                if (PrototypeScopeDefined)
                {
                    return;
                }

                // A class prim: abstract, so neither it nor anything below it renders or is
                // traversed by default, but references to it compose normally.
                UsdNative.DefinePrim(Context, PrototypeScopePath, string.Empty, UsdNative.SpecifierClass);
                UsedPaths.Add(PrototypeScopePath);
                PrototypeScopeDefined = true;
            }
        }

        // A prefab written once. Its content lives either in its own file (AssetFile) or as a
        // class prim of the scene layer (PrototypePath); RootPath is the prim its objects' paths
        // are under, and Writer is the layer whose Paths/MeshPaths recorded them.
        private sealed class PrefabAsset
        {
            public string Id;
            public string AssetFile;
            public string PrototypePath;
            public string RootPath;
            public Transform Base;
            public LayerWriter Writer;

            public string RelativePath(Transform t)
            {
                if (t == Base)
                {
                    return string.Empty;
                }

                return Writer.Paths.TryGetValue(t, out string path) && path.StartsWith(RootPath + "/", StringComparison.Ordinal)
                    ? path.Substring(RootPath.Length)
                    : null;
            }

            public string RelativeMeshPath(Transform t)
            {
                return Writer.MeshPaths.TryGetValue(t, out string path) && path.StartsWith(RootPath + "/", StringComparison.Ordinal)
                    ? path.Substring(RootPath.Length)
                    : null;
            }
        }

        private sealed class CompositionSession
        {
            private readonly UsdExportOptions options;
            private readonly UsdExportResult result;
            private readonly TextureExportContext textures;
            private readonly string stagePath;
            private readonly bool staged;
            private readonly string assetsDirectory;
            private readonly string layerExtension;

            // Prefab id -> its asset file (FolderWithAssets); shared by every layer.
            private readonly Dictionary<string, PrefabAsset> assetFiles = new Dictionary<string, PrefabAsset>(StringComparer.Ordinal);
            private readonly HashSet<string> assetFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> prefabsInProgress = new HashSet<string>(StringComparer.Ordinal);

            public CompositionSession(
                UsdExportOptions options,
                UsdExportResult result,
                TextureExportContext textures,
                string stagePath,
                bool staged)
            {
                this.options = options;
                this.result = result;
                this.textures = textures;
                this.stagePath = stagePath;
                this.staged = staged;
                string directory = Path.GetDirectoryName(stagePath) ?? string.Empty;
                assetsDirectory = Path.Combine(directory, Path.GetFileNameWithoutExtension(stagePath) + "_assets");
                string extension = Path.GetExtension(stagePath);
                layerExtension = string.IsNullOrEmpty(extension) ? ".usda" : extension;
            }

            public void Run(GameObject root, string rootPrimName, Action<IntPtr, ExportSceneInfo> afterScene, ref IntPtr sceneContext)
            {
                result.Composition = UsdCompositionMode.Preserve;
                UsdStageSource source = options.ImportedContent == UsdImportedContentMode.OverrideSource
                    ? root.GetComponent<UsdStageSource>()
                    : null;

                LayerWriter scene;
                if (source != null)
                {
                    scene = BeginOverrideLayer(source);
                    sceneContext = scene.Context;
                    WriteImportedRoots(scene, root.transform, source, path => path, string.Empty);
                    // The root may hold the stage's up-axis and unit correction
                    // (UsdImportOptions.ApplyStageUpAxisAndUnits). That is how the stage's own
                    // frame is shown in Unity, not an edit: the layer keeps the source's upAxis
                    // and metersPerUnit, and the prims below are diffed on their local
                    // transforms, so the correction never reaches the layer. Only a difference
                    // from it is something the layer cannot carry.
                    if (!SameLocalTransform(
                            root.transform,
                            Vector3.zero,
                            source.StageCorrectionRotation,
                            Vector3.one * source.StageCorrectionScale))
                    {
                        Debug.LogWarning(
                            "Unity USD Toolkit: the import root '" + root.name + "' is moved, rotated or scaled beyond the " +
                            "stage's up-axis and unit correction. It stands for the stage itself, which has no transform, " +
                            "so an override export cannot carry that.");
                    }
                }
                else
                {
                    string rootPath = "/" + rootPrimName;
                    scene = BeginLayer(stagePath, rootPath, string.Empty, options.MetersPerUnit, 1, string.Empty, true);
                    sceneContext = scene.Context;
                    scene.Paths[root.transform] = rootPath;
                    UsdStageSource rootSource = root.GetComponent<UsdStageSource>();
                    if (rootSource != null && options.ImportedContent != UsdImportedContentMode.Inline)
                    {
                        WriteReferencedStage(scene, root.transform, rootSource, rootPath);
                    }
                    else
                    {
                        WriteContent(scene, root.transform, rootPath);
                    }
                }

                ExportSceneInfo skinned = WriteSkinnedMeshes(scene, root.transform, rootPrimName);
                afterScene?.Invoke(scene.Context, DescribeSceneLayer(scene, root.transform, skinned));
                UsdNative.EndExport(scene.Context);
            }

            // ---- Layers ----

            private LayerWriter BeginLayer(
                string filePath,
                string defaultPrimPath,
                string materialScopePath,
                double metersPerUnit,
                int upAxis,
                string subLayers,
                bool defineDefaultPrim)
            {
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                var writer = new LayerWriter
                {
                    FilePath = filePath,
                    Directory = directory ?? string.Empty,
                    Context = UsdNative.BeginLayerExport(
                        filePath,
                        defaultPrimPath,
                        materialScopePath,
                        metersPerUnit,
                        upAxis,
                        subLayers,
                        defineDefaultPrim,
                        options.CaptureNativeDiagnostics)
                };
                ApplyMaterialFlavors(writer.Context, options);

                if (!string.IsNullOrEmpty(defaultPrimPath))
                {
                    writer.UsedPaths.Add(defaultPrimPath);
                }

                return writer;
            }

            private LayerWriter BeginOverrideLayer(UsdStageSource source)
            {
                if (string.IsNullOrEmpty(source.SourcePath) || !File.Exists(source.SourcePath))
                {
                    throw new UsdExportException(
                        "The import's source file '" + source.SourcePath + "' is missing, so there is nothing to " +
                        "write overrides for. Restore it, or export with UsdImportedContentMode.Inline.");
                }

                if (PathsEqual(source.SourcePath, stagePath))
                {
                    throw new UsdExportException(
                        "An override export cannot replace its own source file '" + source.SourcePath + "': the " +
                        "output sublayers the source, so it has to be written somewhere else.");
                }

                // A staged (usdz) layer lives in a temporary folder, where a relative path would
                // climb back out of it; packaging localizes the source either way.
                string subLayer = staged
                    ? source.SourcePath.Replace('\\', '/')
                    : MakeLayerRelativePath(Path.GetDirectoryName(stagePath) ?? string.Empty, source.SourcePath);
                string defaultPrim = source.DefaultPrimPath;
                string looks = string.IsNullOrEmpty(defaultPrim) ? "/UnityLooks" : defaultPrim + "/UnityLooks";
                LayerWriter writer = BeginLayer(
                    stagePath,
                    defaultPrim,
                    looks,
                    source.MetersPerUnit,
                    source.UpAxisCode,
                    subLayer,
                    false);
                writer.CheckComposedPrims = true;
                result.SourceLayer = source.SourcePath;
                return writer;
            }

            // ---- Plain content ----

            // Writes `t`'s mesh and children under `primPath`, which already exists.
            private void WriteContent(LayerWriter writer, Transform t, string primPath)
            {
                WriteMeshFor(writer, t, primPath);
                WriteLightAndCameraOn(writer.Context, t, name => writer.UniqueChild(primPath, name), options, result);
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform child = t.GetChild(i);
                    if ((!options.IncludeInactive && !child.gameObject.activeSelf) ||
                        child.GetComponent<UsdImportHelperObject>() != null)
                    {
                        continue;
                    }

                    WriteObject(writer, child, primPath);
                }
            }

            // Writes `t` as a new prim under `parentPath`: a referenced stage, a prefab instance,
            // or an Xform with its mesh and children.
            private string WriteObject(LayerWriter writer, Transform t, string parentPath)
            {
                string path = writer.UniqueChild(parentPath, SanitizePrimName(t.name));
                writer.Paths[t] = path;
                UsdNative.AddXform(writer.Context, path, ToUsdMatrix(GetLocalMatrix(t)), IsVisible(t));

                UsdStageSource nested = t.GetComponent<UsdStageSource>();
                if (nested != null && options.ImportedContent != UsdImportedContentMode.Inline)
                {
                    WriteReferencedStage(writer, t, nested, path);
                    return path;
                }

                PrefabIdentity prefab = options.ExportPrefabsAsReferences ? GetPrefabIdentity(t.gameObject) : null;
                if (prefab != null && WritePrefabInstance(writer, t, prefab, path))
                {
                    return path;
                }

                WriteContent(writer, t, path);
                return path;
            }

            private bool IsVisible(Transform t)
            {
                return !options.PreserveInactiveAndDisabledVisibility || t.gameObject.activeSelf;
            }

            private bool TryGetExportableMesh(Transform t, out MeshFilter filter, out MeshRenderer renderer)
            {
                filter = t.GetComponent<MeshFilter>();
                renderer = t.GetComponent<MeshRenderer>();
                // A mesh a UsdDualQuaternionSkin poses is skinned; WriteSkinnedMeshes writes it.
                if (filter == null || filter.sharedMesh == null || t.GetComponent<UsdDualQuaternionSkin>() != null)
                {
                    return false;
                }

                return renderer == null || renderer.enabled || options.ExportDisabledRenderers;
            }

            // Writes `t`'s mesh, if it has one, as "<name>_Mesh" under `xformPath`.
            private void WriteMeshFor(LayerWriter writer, Transform t, string xformPath)
            {
                if (!TryGetExportableMesh(t, out MeshFilter filter, out MeshRenderer renderer))
                {
                    return;
                }

                string meshPath = writer.UniqueChild(xformPath, SanitizePrimName(t.name) + "_Mesh");
                writer.MeshPaths[t] = meshPath;
                bool visible = !options.PreserveInactiveAndDisabledVisibility || renderer == null || renderer.enabled;
                if (options.ShareMeshes)
                {
                    string prototype = GetMeshPrototype(writer, filter, renderer);
                    if (prototype != null)
                    {
                        UsdNative.AddXform(writer.Context, meshPath, IdentityUsdMatrix, visible);
                        UsdNative.AddReference(writer.Context, meshPath, string.Empty, prototype, UsdNative.ArcReference);
                        UsdNative.SetInstanceable(writer.Context, meshPath, true);
                        result.ReferenceCount++;
                        return;
                    }
                }

                WriteMeshPrim(writer, meshPath, filter, renderer, visible);
            }

            // Writes one Mesh prim with its materials. False when the mesh was skipped.
            private bool WriteMeshPrim(LayerWriter writer, string meshPath, MeshFilter filter, MeshRenderer renderer, bool visible)
            {
                Mesh mesh = filter.sharedMesh;
                Mesh workMesh = AcquireReadableMesh(filter, mesh, options, out bool temporary);
                if (workMesh == null)
                {
                    return false;
                }

                try
                {
                    textures.LayerDirectory = writer.Directory;
                    RUsdMaterial[] materials = CreateMaterials(renderer, textures);
                    BuildMeshPayload(filter.transform, filter.transform, workMesh, options, true,
                        out RUsdVec3[] points, out int[] indices, out RUsdSubmesh[] submeshes,
                        out RUsdVec3[] normals, out RUsdVec2[] uv0);
                    UsdNative.AddMesh(writer.Context, meshPath, points, indices, submeshes, normals, uv0, materials,
                        visible, options.ExportBounds);
                    WriteMeshExtras(writer.Context, meshPath, workMesh, options);
                    result.AddMesh(
                        GetTransformPath(filter.transform.root, filter.transform),
                        mesh.name,
                        meshPath,
                        points.Length,
                        indices.Length / 3,
                        submeshes.Length,
                        materials.Length);
                    return true;
                }
                finally
                {
                    if (temporary && workMesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(workMesh);
                    }
                }
            }

            // The prototype for a mesh + material set in this layer, written on first use.
            private string GetMeshPrototype(LayerWriter writer, MeshFilter filter, MeshRenderer renderer)
            {
                var key = new System.Text.StringBuilder();
                key.Append(filter.sharedMesh.GetEntityId().ToString());
                foreach (Material material in renderer != null ? renderer.sharedMaterials : Array.Empty<Material>())
                {
                    key.Append(',').Append(material != null ? material.GetEntityId().ToString() : "0");
                }

                string keyText = key.ToString();
                if (writer.MeshPrototypes.TryGetValue(keyText, out string existing))
                {
                    return existing;
                }

                writer.EnsurePrototypeScope();
                string prototype = writer.UniqueChild(PrototypeScopePath, SanitizePrimName(filter.sharedMesh.name));
                UsdNative.AddXform(writer.Context, prototype, IdentityUsdMatrix, true);
                if (!WriteMeshPrim(writer, prototype + "/Geom", filter, renderer, true))
                {
                    // Unreadable: no prototype; this and later users are skipped like any other.
                    UsdNative.SetActive(writer.Context, prototype, false);
                    writer.MeshPrototypes[keyText] = null;
                    return null;
                }

                writer.MeshPrototypes[keyText] = prototype;
                result.PrototypeCount++;
                return prototype;
            }

            // ---- Skinned meshes ----

            // The skinned renderers below `content` that this layer defines, written as UsdSkel the
            // way a flattened export writes them: a SkelRoot and Skeleton over new bones, or a binding
            // to the Skeleton prim an imported character already has. One that fits neither -- its
            // mesh and bones meet only at a prim of the overridden stage, or its bind poses disagree
            // with the imported skeleton's -- is written in its current pose, with a warning.
            // Renderers on prims the layer only overrides are left to composition.
            private ExportSceneInfo WriteSkinnedMeshes(LayerWriter writer, Transform content, string rootPrimName)
            {
                var renderers = new List<SkinnedRenderer>();
                foreach (SkinnedRenderer renderer in SkinnedRenderer.In(content.gameObject, options.IncludeInactive))
                {
                    Transform t = renderer.transform;
                    if (renderer.sharedMesh == null || (!renderer.enabled && !options.ExportDisabledRenderers) ||
                        writer.SkinsWritten.Contains(renderer.Renderer) || !writer.Paths.ContainsKey(t) ||
                        writer.Composed.Contains(t) || t.GetComponentInParent<UsdImportHelperObject>(true) != null)
                    {
                        continue;
                    }

                    writer.SkinsWritten.Add(renderer.Renderer);
                    renderers.Add(renderer);
                }

                var scene = new ExportSceneInfo { XformPaths = writer.Paths };
                if (renderers.Count == 0)
                {
                    return scene;
                }

                textures.LayerDirectory = writer.Directory;
                var rules = new SkinningRules
                {
                    Skeletons = UsdNative.SupportsAnimation,
                    Hierarchy = true,
                    // A SkelRoot changes its prim's type, so it has to be a prim this layer defines.
                    SkelRootAllowed = t => writer.Paths.ContainsKey(t) && !writer.Composed.Contains(t),
                    ExistingSkeletonPath = skeleton =>
                        writer.Composed.Contains(skeleton.transform) && skeleton.GetComponent<UsdPrimSource>() != null &&
                        writer.Paths.TryGetValue(skeleton.transform, out string path) ? path : null
                };

                WriteSkinnedRenderers(content.gameObject, rootPrimName, writer.Context, options, result, textures, scene,
                                      writer.UsedPaths, renderers, rules);
                foreach (KeyValuePair<Renderer, string> mesh in scene.MeshPaths)
                {
                    writer.MeshPaths[mesh.Key.transform] = mesh.Value;
                }

                return scene;
            }

            // What the scene layer holds for each object below `root`, for UsdAnimationRecorder: the
            // prim of every object written or overridden, the mesh prim of every renderer, and the
            // skeletons -- the new ones written here and the imported ones this layer overrides,
            // whose Skeleton prims take an animation like a new one's.
            private static ExportSceneInfo DescribeSceneLayer(LayerWriter writer, Transform root, ExportSceneInfo skinned)
            {
                var scene = new ExportSceneInfo { XformPaths = writer.Paths };
                foreach (KeyValuePair<Renderer, string> mesh in skinned.MeshPaths)
                {
                    scene.MeshPaths[mesh.Key] = mesh.Value;
                }

                scene.Skeletons.AddRange(skinned.Skeletons);
                scene.BakedSkinned.AddRange(skinned.BakedSkinned);
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    Transform t = renderer.transform;
                    if (scene.MeshPaths.ContainsKey(renderer))
                    {
                        continue;
                    }

                    if (writer.MeshPaths.TryGetValue(t, out string meshPath))
                    {
                        scene.MeshPaths[renderer] = meshPath;
                    }
                    else if (writer.Composed.Contains(t) && writer.Paths.TryGetValue(t, out string primPath))
                    {
                        UsdPrimSource prim = t.GetComponent<UsdPrimSource>();
                        if (prim != null && prim.ImportedMesh != null)
                        {
                            scene.MeshPaths[renderer] = primPath; // an imported mesh is its own prim
                            scene.SourceMeshes.Add(renderer);
                        }
                    }
                }

                SkinnedMeshRenderer[] skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (UsdSkeleton skeleton in root.GetComponentsInChildren<UsdSkeleton>(true))
                {
                    IReadOnlyList<Transform> joints = skeleton.Joints;
                    if (!writer.Composed.Contains(skeleton.transform) ||
                        !writer.Paths.TryGetValue(skeleton.transform, out string skeletonPath) ||
                        joints.Count == 0 || joints.Any(joint => joint == null))
                    {
                        continue;
                    }

                    var exported = new ExportedSkeleton { SkeletonPath = skeletonPath, Owner = skeleton.transform, Joints = joints.ToArray() };
                    var channels = new HashSet<string>(StringComparer.Ordinal);
                    foreach (SkinnedMeshRenderer skin in skins)
                    {
                        Mesh mesh = skin.sharedMesh;
                        if (mesh == null || skin.bones == null || !skin.bones.Any(bone => bone != null && joints.Contains(bone)))
                        {
                            continue;
                        }

                        // The importer names each blend shape after its skel:blendShapes token and
                        // keys its frames at the USD weight times 100.
                        for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                        {
                            string name = mesh.GetBlendShapeName(shape);
                            if (channels.Add(name))
                            {
                                exported.ChannelNames.Add(name);
                                exported.ChannelRenderers.Add(skin);
                                exported.ChannelShapes.Add(shape);
                                exported.ChannelFullWeights.Add(100f);
                            }
                        }
                    }

                    scene.Skeletons.Add(exported);
                }

                return scene;
            }

            // ---- Prefabs ----

            // `path` (already an Xform with t's transform) becomes a reference to the prefab's
            // asset, then t's differences from the prefab are written over it. False when the
            // prefab cannot be referenced (it contains itself), so t is written in full.
            private bool WritePrefabInstance(LayerWriter writer, Transform t, PrefabIdentity prefab, string path)
            {
                PrefabAsset asset = GetOrWritePrefabAsset(writer, prefab, t);
                if (asset == null)
                {
                    return false;
                }

                string assetPath = asset.AssetFile != null
                    ? MakeLayerRelativePath(writer.Directory, asset.AssetFile)
                    : string.Empty;
                UsdNative.AddReference(writer.Context, path, assetPath, asset.PrototypePath, UsdNative.ArcReference);
                result.ReferenceCount++;

                int overrides = asset.Base == t ? 0 : WriteInstanceOverrides(writer, t, asset.Base, path, asset);
                if (overrides == 0)
                {
                    // Nothing differs, so the instance can share the prototype's composition.
                    UsdNative.SetInstanceable(writer.Context, path, true);
                }

                result.OverrideCount += overrides;
                return true;
            }

            private PrefabAsset GetOrWritePrefabAsset(LayerWriter writer, PrefabIdentity prefab, Transform instance)
            {
                bool folder = options.Layout == UsdExportLayout.FolderWithAssets;
                if (folder ? assetFiles.TryGetValue(prefab.Id, out PrefabAsset existing)
                           : writer.InternalPrefabs.TryGetValue(prefab.Id, out existing))
                {
                    return existing;
                }

                if (!prefabsInProgress.Add(prefab.Id))
                {
                    return null; // a prefab that contains itself: write this level in full
                }

                try
                {
                    // Without the prefab asset (a runtime marker that only names the prefab), the
                    // first instance is the content every later instance is compared against.
                    Transform content = prefab.Asset != null ? prefab.Asset.transform : instance;
                    string primName = SanitizePrimName(prefab.Name);
                    var asset = new PrefabAsset { Id = prefab.Id, Base = content };
                    if (folder)
                    {
                        string fileName = SanitizeAssetFileName(primName);
                        string unique = fileName;
                        for (int n = 1; !assetFileNames.Add(unique); n++)
                        {
                            unique = fileName + "_" + n;
                        }

                        string file = Path.Combine(assetsDirectory, unique + layerExtension);
                        LayerWriter assetWriter = BeginLayer(file, "/" + primName, string.Empty, options.MetersPerUnit, 1,
                            string.Empty, true);
                        try
                        {
                            asset.AssetFile = file;
                            asset.RootPath = "/" + primName;
                            asset.Writer = assetWriter;
                            assetFiles[prefab.Id] = asset;
                            assetWriter.Paths[content] = asset.RootPath;
                            WriteContent(assetWriter, content, asset.RootPath);
                            WriteSkinnedMeshes(assetWriter, content, primName);
                            UsdNative.EndExport(assetWriter.Context);
                            result.AddAssetFile(file);
                        }
                        finally
                        {
                            UsdNative.Destroy(assetWriter.Context);
                            assetWriter.Context = IntPtr.Zero;
                        }
                    }
                    else
                    {
                        writer.EnsurePrototypeScope();
                        string prototype = writer.UniqueChild(PrototypeScopePath, primName);
                        asset.PrototypePath = prototype;
                        asset.RootPath = prototype;
                        asset.Writer = writer;
                        writer.InternalPrefabs[prefab.Id] = asset;
                        writer.Paths[content] = prototype;
                        UsdNative.AddXform(writer.Context, prototype, IdentityUsdMatrix, true);
                        WriteContent(writer, content, prototype);
                        WriteSkinnedMeshes(writer, content, primName);
                    }

                    result.PrototypeCount++;
                    return asset;
                }
                finally
                {
                    prefabsInProgress.Remove(prefab.Id);
                }
            }

            private static string SanitizeAssetFileName(string name)
            {
                foreach (char invalid in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(invalid, '_');
                }

                return string.IsNullOrEmpty(name) ? "Asset" : name;
            }

            // Writes how instance object `inst` differs from prefab object `baseObject`, below
            // instance prim `instancePath`. Returns the number of opinions written.
            private int WriteInstanceOverrides(LayerWriter writer, Transform inst, Transform baseObject, string instancePath, PrefabAsset asset)
            {
                // Names the prefab's prims take inside the instance, so new objects avoid them.
                foreach (KeyValuePair<Transform, string> entry in asset.Writer.Paths)
                {
                    string relative = asset.RelativePath(entry.Key);
                    if (!string.IsNullOrEmpty(relative))
                    {
                        writer.UsedPaths.Add(instancePath + relative);
                    }
                }

                foreach (KeyValuePair<Transform, string> entry in asset.Writer.MeshPaths)
                {
                    string relative = asset.RelativeMeshPath(entry.Key);
                    if (!string.IsNullOrEmpty(relative))
                    {
                        writer.UsedPaths.Add(instancePath + relative);
                    }
                }

                return WriteObjectOverrides(writer, inst, baseObject, instancePath, asset, true);
            }

            private int WriteObjectOverrides(
                LayerWriter writer,
                Transform inst,
                Transform baseObject,
                string instancePath,
                PrefabAsset asset,
                bool isRoot)
            {
                int count = 0;
                string path = instancePath + asset.RelativePath(baseObject);
                writer.Paths[inst] = path;
                writer.Composed.Add(inst);
                if (!isRoot)
                {
                    if (!SameLocalTransform(inst, baseObject.localPosition, baseObject.localRotation, baseObject.localScale))
                    {
                        UsdNative.SetLocalTransform(writer.Context, path, ToUsdMatrix(GetLocalMatrix(inst)));
                        count++;
                    }

                    if (options.PreserveInactiveAndDisabledVisibility &&
                        inst.gameObject.activeSelf != baseObject.gameObject.activeSelf)
                    {
                        UsdNative.SetVisibility(writer.Context, path, inst.gameObject.activeSelf);
                        count++;
                    }
                }

                count += WriteMeshOverride(writer, inst, baseObject, instancePath, path, asset);

                var matchedBase = new HashSet<Transform>();
                foreach (Transform child in EnumerateChildren(inst))
                {
                    Transform counterpart = FindCounterpart(child, inst, baseObject, matchedBase);
                    if (counterpart != null && asset.RelativePath(counterpart) != null)
                    {
                        matchedBase.Add(counterpart);
                        count += WriteObjectOverrides(writer, child, counterpart, instancePath, asset, false);
                    }
                    else if (options.IncludeInactive || child.gameObject.activeSelf)
                    {
                        WriteObject(writer, child, path);
                        count++;
                    }
                }

                // Objects of the prefab the instance no longer has.
                foreach (Transform baseChild in EnumerateChildren(baseObject))
                {
                    string relative = asset.RelativePath(baseChild);
                    if (!matchedBase.Contains(baseChild) && relative != null)
                    {
                        UsdNative.SetActive(writer.Context, instancePath + relative, false);
                        count++;
                    }
                }

                return count;
            }

            private int WriteMeshOverride(
                LayerWriter writer,
                Transform inst,
                Transform baseObject,
                string instancePath,
                string path,
                PrefabAsset asset)
            {
                string baseMesh = asset.RelativeMeshPath(baseObject);
                bool hasMesh = TryGetExportableMesh(inst, out MeshFilter filter, out MeshRenderer renderer);
                if (baseMesh == null && !hasMesh)
                {
                    return 0;
                }

                if (baseMesh != null && hasMesh)
                {
                    MeshFilter baseFilter = baseObject.GetComponent<MeshFilter>();
                    MeshRenderer baseRenderer = baseObject.GetComponent<MeshRenderer>();
                    bool sameGeometry = baseFilter != null && baseFilter.sharedMesh == filter.sharedMesh &&
                        SameMaterials(baseRenderer != null ? baseRenderer.sharedMaterials : null,
                            renderer != null ? renderer.sharedMaterials : null);
                    if (sameGeometry)
                    {
                        writer.MeshPaths[inst] = instancePath + baseMesh;
                        bool enabled = renderer == null || renderer.enabled;
                        bool baseEnabled = baseRenderer == null || baseRenderer.enabled;
                        if (options.PreserveInactiveAndDisabledVisibility && enabled != baseEnabled)
                        {
                            UsdNative.SetVisibility(writer.Context, instancePath + baseMesh, enabled);
                            return 1;
                        }

                        return 0;
                    }
                }

                int count = 0;
                if (baseMesh != null)
                {
                    UsdNative.SetActive(writer.Context, instancePath + baseMesh, false);
                    count++;
                }

                if (hasMesh)
                {
                    WriteMeshFor(writer, inst, path);
                    count++;
                }

                return count;
            }

            private static IEnumerable<Transform> EnumerateChildren(Transform t)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform child = t.GetChild(i);
                    // The importer's light/camera helpers are not prims, so they are neither
                    // compared nor written as added objects.
                    if (child.GetComponent<UsdImportHelperObject>() == null)
                    {
                        yield return child;
                    }
                }
            }

            // The prefab object an instance's child stands for: the k-th child of the same name
            // for the k-th such instance child, and of the same prefab when either is a nested
            // prefab instance.
            private static Transform FindCounterpart(Transform child, Transform inst, Transform baseObject, HashSet<Transform> taken)
            {
                int occurrence = 0;
                for (int i = 0; i < inst.childCount; i++)
                {
                    Transform sibling = inst.GetChild(i);
                    if (sibling == child)
                    {
                        break;
                    }

                    if (sibling.name == child.name)
                    {
                        occurrence++;
                    }
                }

                for (int i = 0; i < baseObject.childCount; i++)
                {
                    Transform candidate = baseObject.GetChild(i);
                    if (candidate.name != child.name)
                    {
                        continue;
                    }

                    if (occurrence-- > 0)
                    {
                        continue;
                    }

                    if (taken.Contains(candidate))
                    {
                        return null;
                    }

                    // Two different prefabs under one name are different objects. When only one side
                    // can say (in a player, objects inside a prefab asset carry no marker), the
                    // name decides.
                    PrefabIdentity childPrefab = GetPrefabIdentity(child.gameObject);
                    PrefabIdentity candidatePrefab = GetPrefabIdentity(candidate.gameObject);
                    if (childPrefab != null && candidatePrefab != null && childPrefab.Id != candidatePrefab.Id)
                    {
                        return null;
                    }

                    return candidate;
                }

                return null;
            }

            // ---- Imported stages ----

            // A stage imported somewhere below the export root: each object that stands for one of
            // its root prims references that prim of the source file (the default prim by the
            // default reference, the others by path), and everything below is diffed as overrides
            // through that reference. Objects that stand for no root prim are written as content.
            private void WriteReferencedStage(LayerWriter writer, Transform wrapper, UsdStageSource source, string wrapperPath)
            {
                string defaultPrim = source.DefaultPrimPath;
                if (string.IsNullOrEmpty(source.SourcePath) || !File.Exists(source.SourcePath) || string.IsNullOrEmpty(defaultPrim))
                {
                    Debug.LogWarning(
                        "Unity USD Toolkit: '" + wrapper.name + "' cannot reference its source " +
                        (string.IsNullOrEmpty(defaultPrim) ? "(the stage has no defaultPrim)" : "('" + source.SourcePath + "' is missing)") +
                        "; it is written as content instead.");
                    WriteContent(writer, wrapper, wrapperPath);
                    return;
                }

                string assetPath = staged
                    ? source.SourcePath.Replace('\\', '/')
                    : MakeLayerRelativePath(writer.Directory, source.SourcePath);

                WriteMeshFor(writer, wrapper, wrapperPath);
                for (int i = 0; i < wrapper.childCount; i++)
                {
                    Transform child = wrapper.GetChild(i);
                    UsdPrimSource prim = child.GetComponent<UsdPrimSource>();
                    if (prim == null || prim.Stage != source || !IsRootPrimPath(prim.PrimPath))
                    {
                        if (options.IncludeInactive || child.gameObject.activeSelf)
                        {
                            WriteObject(writer, child, wrapperPath);
                        }

                        continue;
                    }

                    // A root prim other than the default prim (a light, an OmniGraph next to the
                    // scene) is referenced by its path; written as content it became an empty Xform,
                    // since what the importer made for it is not a prim.
                    string rootPrim = prim.PrimPath;
                    string referencePath = writer.UniqueChild(wrapperPath, SanitizePrimName(rootPrim.Substring(1)));
                    UsdNative.DefinePrim(writer.Context, referencePath, string.Empty, UsdNative.SpecifierDef);
                    UsdNative.AddReference(writer.Context, referencePath, assetPath,
                                           rootPrim == defaultPrim ? string.Empty : rootPrim, UsdNative.ArcReference);
                    result.ReferenceCount++;

                    string Map(string sourcePath) =>
                        sourcePath == rootPrim ? referencePath
                        : sourcePath.StartsWith(rootPrim + "/", StringComparison.Ordinal)
                            ? referencePath + sourcePath.Substring(rootPrim.Length)
                            : null;

                    // Through a reference, a moved object can be a relocate: its prims come in across
                    // that arc, which is what Pcp relocates (native API 13).
                    var diff = new ImportDiff(this, writer, source, Map, UsdNative.SupportsAnimation ? rootPrim : null);
                    diff.Survey(child, string.Empty);
                    diff.WriteSelections();
                    diff.Visit(child, wrapperPath, string.Empty);
                    diff.WriteMovedSelections();
                    diff.WriteDeletions();
                }
            }

            // The override export: the output sublayers the source, so the stage's own paths
            // are the output's paths.
            private void WriteImportedRoots(LayerWriter writer, Transform root, UsdStageSource source, Func<string, string> map, string parentPath)
            {
                // A sublayer's prims are the output's own layer stack, which relocates cannot move:
                // a moved object stays new content here, its original deactivated.
                var diff = new ImportDiff(this, writer, source, map, null);
                diff.WriteSelections();
                for (int i = 0; i < root.childCount; i++)
                {
                    diff.Visit(root.GetChild(i), parentPath, string.Empty);
                }

                diff.WriteDeletions();
            }

            // Compares an imported subtree with the snapshot its UsdPrimSource components hold
            // and writes the differences on the mapped prims.
            private sealed class ImportDiff
            {
                private readonly CompositionSession session;
                private readonly LayerWriter writer;
                private readonly UsdStageSource source;
                private readonly Func<string, string> map;
                private readonly HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);

                // Relocates (reference mode only). `arcRoot` is the source prim the reference brings
                // in; prims below it can be relocated, it cannot (it is the referencing prim itself).
                // `moved` is, for each prim with no object at its own place, the one object that
                // moved it; `holdsMoves` the new objects with such a move somewhere below them;
                // `relocated` where each relocated prim is composed now.
                private readonly string arcRoot;
                private readonly HashSet<string> inPlace = new HashSet<string>(StringComparer.Ordinal);
                private readonly Dictionary<string, Transform> moved = new Dictionary<string, Transform>(StringComparer.Ordinal);
                private readonly HashSet<Transform> holdsMoves = new HashSet<Transform>();
                private readonly Dictionary<string, string> relocated = new Dictionary<string, string>(StringComparer.Ordinal);
                private readonly HashSet<string> deactivated = new HashSet<string>(StringComparer.Ordinal);
                private readonly HashSet<string> uninstanced = new HashSet<string>(StringComparer.Ordinal);

                // The import's own animation, when it has one. An animated prim's transform or
                // visibility is the source's time samples, and an override here holds one value at
                // Default time, which hides every sample below it: the animation would stop.
                private readonly UsdAnimationPlayer animation;

                public ImportDiff(CompositionSession session, LayerWriter writer, UsdStageSource source, Func<string, string> map,
                                  string arcRoot)
                {
                    this.session = session;
                    this.writer = writer;
                    this.source = source;
                    this.map = map;
                    this.arcRoot = arcRoot;
                    animation = source.GetComponent<UsdAnimationPlayer>();
                    if (animation != null && animation.Data == null)
                    {
                        animation = null;
                    }
                }

                private UsdExportResult Result => session.result;
                private UsdExportOptions Options => session.options;

                // Where a source prim is composed in the output: below a relocated prim, under that
                // prim's new place, else where the reference or sublayer puts it. An opinion left at
                // a relocated prim's old path is an error, so every write goes through this.
                private string Map(string sourcePath)
                {
                    for (string current = sourcePath; !string.IsNullOrEmpty(current); current = ParentPrimPath(current))
                    {
                        if (relocated.TryGetValue(current, out string target))
                        {
                            return target + sourcePath.Substring(current.Length);
                        }
                    }

                    return map(sourcePath);
                }

                // Before Visit, with the same arguments: finds the imported objects that moved. A
                // prim that still has an object at its own place was not moved; another object
                // standing for it is a copy, written as new content. Otherwise the first object
                // found for it moved it.
                public void Survey(Transform t, string parentSourcePath)
                {
                    if (arcRoot == null || t.GetComponent<UsdImportHelperObject>() != null)
                    {
                        return;
                    }

                    UsdPrimSource prim = t.GetComponent<UsdPrimSource>();
                    string childSourcePath = null;
                    if (prim != null && prim.Stage == source)
                    {
                        if (ParentPrimPath(prim.PrimPath) == parentSourcePath && map(prim.PrimPath) != null)
                        {
                            inPlace.Add(prim.PrimPath);
                            childSourcePath = prim.PrimPath;
                        }
                        else if (CanRelocate(prim))
                        {
                            if (!moved.ContainsKey(prim.PrimPath))
                            {
                                moved.Add(prim.PrimPath, t);
                            }

                            childSourcePath = prim.PrimPath;
                        }
                    }

                    for (int i = 0; i < t.childCount; i++)
                    {
                        Survey(t.GetChild(i), childSourcePath);
                    }

                    if (parentSourcePath == string.Empty)
                    {
                        // The whole subtree is surveyed: settle copies, then mark the new objects
                        // a move lies below, which are written here rather than as plain content.
                        foreach (string path in inPlace)
                        {
                            moved.Remove(path);
                        }

                        foreach (Transform mover in moved.Values)
                        {
                            for (Transform up = mover.parent; up != null && up != t.parent; up = up.parent)
                            {
                                holdsMoves.Add(up);
                            }
                        }
                    }
                }

                private bool CanRelocate(UsdPrimSource prim) =>
                    arcRoot != null && string.IsNullOrEmpty(prim.InstanceRootPath) &&
                    prim.PrimPath.StartsWith(arcRoot + "/", StringComparison.Ordinal);

                // Writes the relocate for a moved object and returns where its prim is composed now,
                // or null when it cannot be relocated (it is then written as new content).
                private string Relocate(UsdPrimSource prim, string parentOutputPath)
                {
                    string from = Map(prim.PrimPath);
                    if (from == null || parentOutputPath == null)
                    {
                        return null;
                    }

                    string name = prim.PrimPath.Substring(prim.PrimPath.LastIndexOf('/') + 1);
                    string to = writer.UniqueChild(parentOutputPath, name);
                    try
                    {
                        UsdNative.AddRelocate(writer.Context, from, to);
                    }
                    catch (UsdExportException exception)
                    {
                        Debug.LogWarning("Unity USD Toolkit: '" + prim.PrimPath + "' could not be relocated (" +
                                         exception.Message + "); it is written as new content.");
                        writer.UsedPaths.Remove(to);
                        return null;
                    }

                    relocated[prim.PrimPath] = to;
                    Result.RelocateCount++;
                    return to;
                }

                // A new object: plain content, unless a moved imported object is somewhere below
                // it, in which case it is written here so that object can be relocated under it.
                private void WriteNewObject(Transform t, string parentOutputPath)
                {
                    if (!holdsMoves.Contains(t))
                    {
                        session.WriteObject(writer, t, parentOutputPath);
                        return;
                    }

                    string path = writer.UniqueChild(parentOutputPath, SanitizePrimName(t.name));
                    writer.Paths[t] = path;
                    UsdNative.AddXform(writer.Context, path, ToUsdMatrix(GetLocalMatrix(t)), session.IsVisible(t));
                    session.WriteMeshFor(writer, t, path);
                    for (int i = 0; i < t.childCount; i++)
                    {
                        Visit(t.GetChild(i), path, null);
                    }
                }

                // Whether a source prim is a moved one or below it: its opinions wait for the relocate.
                private bool IsMoved(string sourcePath)
                {
                    for (string current = sourcePath; !string.IsNullOrEmpty(current); current = ParentPrimPath(current))
                    {
                        if (moved.ContainsKey(current))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                // A selection on a moved prim, written at its new place once Visit relocated it (an
                // opinion at the old place would be ignored, with an error). One whose move fell back
                // to new content has no prim of the source left to select on.
                public void WriteMovedSelections()
                {
                    foreach (UsdVariantSelection selection in source.VariantSelections)
                    {
                        if (!IsMoved(selection.PrimPath) || Map(selection.PrimPath) == map(selection.PrimPath))
                        {
                            continue;
                        }

                        UsdNative.SetVariantSelection(writer.Context, Map(selection.PrimPath), selection.VariantSet, selection.Variant);
                        Result.OverrideCount++;
                    }
                }

                // The import's variant choices are what Unity shows, so the output selects them too.
                // Written before Visit, which looks at the composed stage; those on moved prims wait.
                public void WriteSelections()
                {
                    foreach (UsdVariantSelection selection in source.VariantSelections)
                    {
                        if (IsMoved(selection.PrimPath))
                        {
                            continue;
                        }

                        string path = Map(selection.PrimPath);
                        if (path == null)
                        {
                            continue;
                        }

                        UsdNative.SetVariantSelection(writer.Context, path, selection.VariantSet, selection.Variant);
                        Result.OverrideCount++;
                    }
                }

                // `parentOutputPath` is the output prim of t's parent; `parentSourcePath` the
                // source prim that parent stands for ("" for the stage root).
                public void Visit(Transform t, string parentOutputPath, string parentSourcePath)
                {
                    // What the importer made that is not a prim -- the joints under a Skeleton, the
                    // turn under a light or camera -- is the source's already; written as new
                    // content it came back as a duplicate Xform hierarchy.
                    if (t.GetComponent<UsdImportHelperObject>() != null)
                    {
                        return;
                    }

                    UsdPrimSource prim = t.GetComponent<UsdPrimSource>();
                    string outputPath = prim != null && prim.Stage == source && ParentPrimPath(prim.PrimPath) == parentSourcePath
                        ? Map(prim.PrimPath)
                        : null;
                    if (outputPath == null && prim != null && moved.TryGetValue(prim.PrimPath, out Transform mover) && mover == t)
                    {
                        // An imported object moved under another parent: its prim, relocated.
                        outputPath = Relocate(prim, parentOutputPath);
                    }

                    if (outputPath == null)
                    {
                        // Not this stage's prim at its own place: new content (an added object, a
                        // copy of an imported one, or one moved where it cannot be relocated, whose
                        // original is then reported missing and deactivated).
                        if (Options.IncludeInactive || t.gameObject.activeSelf)
                        {
                            PrepareChildEdit(parentSourcePath);
                            WriteNewObject(t, parentOutputPath);
                            Result.OverrideCount++;
                        }

                        return;
                    }

                    present.Add(prim.PrimPath);
                    writer.Paths[t] = outputPath;
                    writer.Composed.Add(t);
                    writer.UsedPaths.Add(outputPath);

                    if (prim.HasTransform &&
                        !SameLocalTransform(t, prim.ImportedLocalPosition, prim.ImportedLocalRotation, prim.ImportedLocalScale))
                    {
                        // What playback moved stays out: the source animates it. A pose the
                        // animation does not explain is an edit this layer cannot carry without
                        // stopping that animation, so it is reported rather than written.
                        if (animation != null && animation.TryGetAnimatedPose(t, out Vector3 p, out Quaternion r, out Vector3 s))
                        {
                            if (!SameLocalTransform(t, p, r, s) && !Options.SamplesFollow)
                            {
                                Debug.LogWarning(
                                    "Unity USD Toolkit: '" + prim.PrimPath + "' is animated by its source stage, so its " +
                                    "moved pose is not exported: a transform override would stop the animation.");
                            }
                        }
                        else
                        {
                            PrepareEdit(prim);
                            UsdNative.SetLocalTransform(writer.Context, outputPath, ToUsdMatrix(GetLocalMatrix(t)));
                            Result.OverrideCount++;
                        }
                    }

                    if (t.gameObject.activeSelf != prim.ImportedActiveSelf)
                    {
                        if (animation != null && animation.TryGetAnimatedVisibility(t.gameObject, out bool shown))
                        {
                            if (t.gameObject.activeSelf != shown && !Options.SamplesFollow)
                            {
                                Debug.LogWarning(
                                    "Unity USD Toolkit: the visibility of '" + prim.PrimPath + "' is animated by its source " +
                                    "stage, so showing or hiding it is not exported: an override would stop the animation.");
                            }
                        }
                        else
                        {
                            PrepareEdit(prim);
                            UsdNative.SetVisibility(writer.Context, outputPath, t.gameObject.activeSelf);
                            Result.OverrideCount++;
                        }
                    }

                    string childOutputPath = outputPath;
                    string childSourcePath = prim.PrimPath;
                    MeshFilter filter = t.GetComponent<MeshFilter>();
                    MeshRenderer renderer = t.GetComponent<MeshRenderer>();
                    Mesh mesh = filter != null ? filter.sharedMesh : null;
                    if (prim.ImportedMesh != null)
                    {
                        bool sameGeometry = mesh == prim.ImportedMesh &&
                            SameMaterials(renderer != null ? renderer.sharedMaterials : null, prim.ImportedMaterials);
                        if (sameGeometry)
                        {
                            bool enabled = renderer == null || renderer.enabled;
                            if (enabled != prim.ImportedRendererEnabled)
                            {
                                PrepareEdit(prim);
                                UsdNative.SetVisibility(writer.Context, outputPath, enabled);
                                Result.OverrideCount++;
                            }
                        }
                        else
                        {
                            // The source mesh carries attributes this export does not write
                            // (indexed primvars, subsets, ...), so overwriting them in place could
                            // mix the two. Retire the source prim and write a fresh one beside it;
                            // the object's children follow it as new content.
                            PrepareEdit(prim);
                            UsdNative.SetActive(writer.Context, outputPath, false);
                            deactivated.Add(prim.PrimPath);
                            Result.OverrideCount++;
                            string replacement = writer.UniqueChild(ParentPrimPath(outputPath), SanitizePrimName(t.name));
                            writer.Paths[t] = replacement;
                            writer.Composed.Remove(t);
                            UsdNative.AddXform(writer.Context, replacement, ToUsdMatrix(GetLocalMatrix(t)), session.IsVisible(t));
                            session.WriteContent(writer, t, replacement);
                            return;
                        }
                    }
                    else if (mesh != null)
                    {
                        PrepareChildEdit(prim.PrimPath);
                        session.WriteMeshFor(writer, t, outputPath);
                        Result.OverrideCount++;
                    }

                    for (int i = 0; i < t.childCount; i++)
                    {
                        Visit(t.GetChild(i), childOutputPath, childSourcePath);
                    }
                }

                // Imported prims with no object left: deactivate the topmost of each missing subtree.
                public void WriteDeletions()
                {
                    foreach (string primPath in source.PrimPaths)
                    {
                        if (present.Contains(primPath) || HasDeactivatedAncestor(primPath))
                        {
                            continue;
                        }

                        string path = Map(primPath);
                        if (path == null)
                        {
                            continue;
                        }

                        PrepareEditAt(primPath);
                        UsdNative.SetActive(writer.Context, path, false);
                        deactivated.Add(primPath);
                        Result.OverrideCount++;
                    }
                }

                private bool HasDeactivatedAncestor(string primPath)
                {
                    for (string current = ParentPrimPath(primPath); current.Length > 0; current = ParentPrimPath(current))
                    {
                        if (deactivated.Contains(current))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                // A prim below an instance is read-only until the instance stops being one.
                private void PrepareEdit(UsdPrimSource prim)
                {
                    if (!string.IsNullOrEmpty(prim.InstanceRootPath))
                    {
                        Uninstance(prim.InstanceRootPath);
                    }
                }

                private void PrepareEditAt(string primPath)
                {
                    for (string current = ParentPrimPath(primPath); current.Length > 0; current = ParentPrimPath(current))
                    {
                        if (IsInstance(current))
                        {
                            Uninstance(current);
                        }
                    }
                }

                // Adding a child under an instance (or under a prim inside one) needs the same.
                private void PrepareChildEdit(string parentSourcePath)
                {
                    if (string.IsNullOrEmpty(parentSourcePath))
                    {
                        return;
                    }

                    if (IsInstance(parentSourcePath))
                    {
                        Uninstance(parentSourcePath);
                    }

                    PrepareEditAt(parentSourcePath);
                }

                private HashSet<string> instanceRoots;

                private bool IsInstance(string sourcePath)
                {
                    instanceRoots ??= CollectInstanceRoots();
                    return instanceRoots.Contains(sourcePath);
                }

                // Instance prims of this import: flagged ones, plus the roots proxies point at.
                private HashSet<string> CollectInstanceRoots()
                {
                    var roots = new HashSet<string>(StringComparer.Ordinal);
                    foreach (UsdPrimSource prim in source.GetComponentsInChildren<UsdPrimSource>(true))
                    {
                        if (prim.Stage == source && (prim.CompositionFlags & UsdPrimCompositionFlags.Instance) != 0)
                        {
                            roots.Add(prim.PrimPath);
                        }

                        if (!string.IsNullOrEmpty(prim.InstanceRootPath))
                        {
                            roots.Add(prim.InstanceRootPath);
                        }
                    }

                    return roots;
                }

                private void Uninstance(string sourcePath)
                {
                    if (!uninstanced.Add(sourcePath))
                    {
                        return;
                    }

                    string path = Map(sourcePath);
                    if (path != null)
                    {
                        UsdNative.SetInstanceable(writer.Context, path, false);
                        Result.OverrideCount++;
                    }
                }
            }
        }
    }
}
