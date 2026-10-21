using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.USDToolkit.Native;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit
{
    // Live switching: rereads the parts of an import's kept stage that a switch recomposed and puts
    // them where the old parts were, leaving everything else -- and every edit made to it -- alone.
    public static partial class UsdImporter
    {
        internal static UsdSwitchResult RebuildSubtrees(UsdStageSource source, UsdStage stage, IReadOnlyList<string> roots,
                                                        UsdImportOptions options, UsdImportLimits limits, float lightReference)
        {
            try
            {
                StageData data = ReadLive(stage, roots, source.SourcePath, options, limits, lightReference);
                try
                {
                    UsdImportResult built = BuildResult(data, StagingOptions(options));
                    return Graft(source, data, built, roots);
                }
                finally
                {
                    ReleaseNativeImages(data);
                }
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
        }

        // The stage is read off the main thread and the parts built across frames; the old parts
        // stay in place, and playing, until the new ones are ready. Null when the import was
        // destroyed meanwhile.
        internal static async Task<UsdSwitchResult> RebuildSubtreesAsync(UsdStageSource source, UsdStage stage,
                                                                         IReadOnlyList<string> roots, UsdImportOptions options,
                                                                         UsdImportLimits limits, float lightReference)
        {
            string path = source.SourcePath;
            StageData data;
            try
            {
                data = await Task.Run(() => ReadLive(stage, roots, path, options, limits, lightReference));
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }

            try
            {
                UsdImportResult built = await BuildResultAsync(data, StagingOptions(options));
                if (source == null)
                {
                    ReleaseBuilt(built);
                    return null;
                }

                return Graft(source, data, built, roots);
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
            finally
            {
                ReleaseNativeImages(data);
            }
        }

        // Rereads `roots` into the stage's import tables and reads them out, under the stage's lock:
        // nothing else reaches the stage between the reread and the copy.
        private static StageData ReadLive(UsdStage stage, IReadOnlyList<string> roots, string fullPath, UsdImportOptions options,
                                          UsdImportLimits limits, float lightReference)
        {
            lock (stage.Gate)
            {
                IntPtr context = stage.Context;
                UsdNative.ReadSubtrees(context, string.Join("\n", roots), limits);
                try
                {
                    // The composition report comes along: it is the whole stage's, and the source's
                    // variant and payload lists are refreshed from it.
                    UsdImportOptions read = options.Clone();
                    read.RecordSource = true;
                    StageData data = ReadStageTables(context, fullPath, read, false);
                    data.LightReference = lightReference;
                    return data;
                }
                finally
                {
                    UsdNative.ReleaseImportData(context);
                }
            }
        }

        // The parts are built under a root of their own, unparented and unrecorded; Graft moves them.
        private static UsdImportOptions StagingOptions(UsdImportOptions options)
        {
            UsdImportOptions staging = options.Clone();
            staging.Parent = null;
            staging.RecordSource = false;
            staging.ProgressCallback = null;
            return staging;
        }

        private static UsdSwitchResult Graft(UsdStageSource source, StageData data, UsdImportResult built, IReadOnlyList<string> roots)
        {
            Transform importRoot = source.transform;
            Transform staging = built.RootObject.transform;
            var objects = PrimObjects(source);
            var replaced = new List<GameObject>();
            var rebuilt = new List<string>();
            var nodeFlags = new Dictionary<string, int>(data.Nodes.Count, StringComparer.Ordinal);
            foreach (NodeData node in data.Nodes)
            {
                nodeFlags[node.Path] = node.Flags;
            }

            try
            {
                foreach (string root in roots)
                {
                    objects.TryGetValue(root, out Transform old);
                    Transform parent;
                    int sibling = -1;
                    if (old != null)
                    {
                        parent = old.parent;
                        sibling = old.GetSiblingIndex();
                    }
                    else
                    {
                        string parentPath = ParentPrimPath(root);
                        if (parentPath == "/")
                        {
                            parent = importRoot;
                        }
                        else if (!objects.TryGetValue(parentPath, out parent))
                        {
                            // The parent's object was deleted by hand: what the switch brought below
                            // it stays out, as the parent itself does.
                            continue;
                        }
                    }

                    Transform fresh = FindBuilt(staging, root);
                    var foreign = new List<KeyValuePair<Transform, string>>();
                    bool renamed = false;
                    bool moved = false;
                    bool toggled = false;
                    string oldName = null;
                    Vector3 position = default;
                    Quaternion rotation = Quaternion.identity;
                    Vector3 scale = Vector3.one;
                    bool active = true;
                    if (old != null)
                    {
                        CollectForeignChildren(source, old, foreign);
                        UsdPrimSource record = old.GetComponent<UsdPrimSource>();
                        oldName = old.name;
                        renamed = fresh != null && old.name != fresh.name;
                        if (record != null)
                        {
                            moved = old.localPosition != record.ImportedLocalPosition ||
                                    old.localRotation != record.ImportedLocalRotation ||
                                    old.localScale != record.ImportedLocalScale;
                            toggled = old.gameObject.activeSelf != record.ImportedActiveSelf;
                        }

                        position = old.localPosition;
                        rotation = old.localRotation;
                        scale = old.localScale;
                        active = old.gameObject.activeSelf;

                        // Off to the side until the end, so the paths below it stop resolving to it.
                        old.SetParent(null, false);
                        replaced.Add(old.gameObject);
                        RemoveRecorded(objects, root);
                    }

                    source.RemovePrimPathsUnder(root);
                    if (fresh == null)
                    {
                        continue; // gone from the stage (deactivated, unloaded below, removed)
                    }

                    fresh.SetParent(parent, false);
                    if (sibling >= 0)
                    {
                        fresh.SetSiblingIndex(sibling);
                    }

                    // What was done by hand to the part's own object carries over; inside it, the
                    // rebuilt part is the stage's.
                    if (renamed)
                    {
                        fresh.name = oldName;
                    }

                    if (moved)
                    {
                        fresh.localPosition = position;
                        fresh.localRotation = rotation;
                        fresh.localScale = scale;
                    }

                    if (toggled)
                    {
                        fresh.gameObject.SetActive(active);
                    }

                    RecordBelow(source, fresh, root, nodeFlags, objects);
                    rebuilt.Add(root);

                    // Objects added by hand inside the old part go to the new object of the prim they
                    // were under, or to the part itself when that prim is gone.
                    foreach (KeyValuePair<Transform, string> entry in foreign)
                    {
                        Transform home = objects.TryGetValue(entry.Value, out Transform found) ? found : fresh;
                        entry.Key.SetParent(home, false);
                    }
                }

                MergeAnimation(source, built.Animation, roots);
                source.RefreshComposition(data.CompositionReport);
                ReleaseReplaced(source, replaced);
            }
            finally
            {
                // What the staging build made for parts that were not grafted (a part whose parent
                // was deleted) and nothing grafted shares.
                var leftover = new HashSet<Object>();
                CollectMade(staging.gameObject, leftover);
                var used = new HashSet<Object>();
                CollectMade(source.gameObject, used);
                leftover.ExceptWith(used);
                if (built.Animation != null && built.Animation.Data != null)
                {
                    leftover.Add(built.Animation.Data);
                }

                DestroyMade(staging.gameObject);
                foreach (Object item in leftover)
                {
                    DestroyMade(item);
                }
            }

            MarkSceneDirty(source.gameObject);
            return new UsdSwitchResult(source.gameObject, rebuilt, null, null);
        }

        // Every object of the import, by the prim it stands for.
        private static Dictionary<string, Transform> PrimObjects(UsdStageSource source)
        {
            var objects = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (UsdPrimSource prim in source.GetComponentsInChildren<UsdPrimSource>(true))
            {
                if (prim.Stage == source && !string.IsNullOrEmpty(prim.PrimPath))
                {
                    objects[prim.PrimPath] = prim.transform;
                }
            }

            return objects;
        }

        private static void RemoveRecorded(Dictionary<string, Transform> objects, string root)
        {
            var gone = new List<string>();
            foreach (string path in objects.Keys)
            {
                if (IsAtOrBelow(path, root))
                {
                    gone.Add(path);
                }
            }

            foreach (string path in gone)
            {
                objects.Remove(path);
            }
        }

        internal static bool IsAtOrBelow(string path, string root) =>
            path == root || (path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal) && path[root.Length] == '/');

        private static string ParentPrimPath(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path.Substring(0, slash);
        }

        // The object the staging build made for `primPath`, found by the names CreatePrimPathObject gave it.
        private static Transform FindBuilt(Transform staging, string primPath)
        {
            Transform current = staging;
            foreach (string segment in primPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Find(SanitizeObjectName(segment));
                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        // The topmost objects inside `old` that are not the import's: no UsdPrimSource of this
        // stage and not an importer helper (joints, light and camera helpers). With the prim each
        // was under.
        private static void CollectForeignChildren(UsdStageSource source, Transform old, List<KeyValuePair<Transform, string>> foreign)
        {
            var pending = new Stack<Transform>();
            pending.Push(old);
            while (pending.Count > 0)
            {
                Transform current = pending.Pop();
                UsdPrimSource owner = current.GetComponent<UsdPrimSource>();
                for (int i = 0; i < current.childCount; i++)
                {
                    Transform child = current.GetChild(i);
                    UsdPrimSource record = child.GetComponent<UsdPrimSource>();
                    if (record != null && record.Stage == source)
                    {
                        pending.Push(child);
                    }
                    else if (owner != null && child.GetComponent<UsdImportHelperObject>() == null)
                    {
                        foreign.Add(new KeyValuePair<Transform, string>(child, owner.PrimPath));
                    }
                }
            }

            foreach (KeyValuePair<Transform, string> entry in foreign)
            {
                entry.Key.SetParent(null, false);
            }
        }

        // RecordSource for a rebuilt part: a UsdPrimSource on every object of it, for this import.
        private static void RecordBelow(UsdStageSource source, Transform top, string topPath, Dictionary<string, int> nodeFlags,
                                        Dictionary<string, Transform> objects)
        {
            var pending = new Stack<KeyValuePair<Transform, string>>();
            pending.Push(new KeyValuePair<Transform, string>(top, topPath));
            while (pending.Count > 0)
            {
                KeyValuePair<Transform, string> entry = pending.Pop();
                Transform current = entry.Key;
                string primPath = entry.Value;
                bool xformable = nodeFlags.TryGetValue(primPath, out int flags);
                string instanceRoot = (flags & (int)UsdPrimCompositionFlags.InstanceProxy) != 0
                    ? FindInstanceRoot(primPath, nodeFlags) ?? source.FindRecordedInstanceRoot(primPath)
                    : null;
                current.gameObject.AddComponent<UsdPrimSource>().Initialize(source, primPath, flags, instanceRoot, xformable);
                source.AddPrimPath(primPath);
                objects[primPath] = current;
                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    Transform child = current.GetChild(i);
                    if (child.GetComponent<UsdImportHelperObject>() != null)
                    {
                        continue;
                    }

                    pending.Push(new KeyValuePair<Transform, string>(child, primPath + "/" + child.name));
                }
            }
        }

        // The rebuilt parts' animation replaces theirs in the import's; the rest plays on.
        private static void MergeAnimation(UsdStageSource source, UsdAnimationPlayer built, IReadOnlyList<string> roots)
        {
            UsdAnimationPlayer player = source.GetComponent<UsdAnimationPlayer>();
            UsdAnimationData incoming = built != null ? built.Data : null;
            if (player == null && incoming == null)
            {
                return;
            }

            var paths = new List<string>(roots.Count);
            foreach (string root in roots)
            {
                paths.Add(RelativePrimPath(root));
            }

            if (player == null)
            {
                // The staging player lets go of the data; the import's new player takes it.
                player = source.gameObject.AddComponent<UsdAnimationPlayer>();
                player.Data = incoming;
                built.Data = null;
                player.Evaluate(0.0);
                player.RestoreMeshes();
                return;
            }

            UsdAnimationData data = player.Data;
            if (data == null)
            {
                return;
            }

#if UNITY_EDITOR
            // An asset's animation (an import of a project file) is left as it is; the switched
            // instance plays a copy.
            if (UnityEditor.EditorUtility.IsPersistent(data))
            {
                data = Object.Instantiate(data);
                data.name = player.Data.name;
            }
#endif
            data.RemoveTracksUnder(paths);
            if (incoming != null)
            {
                data.AddTracksFrom(incoming);
            }

            double time = player.CurrentTime;
            player.Data = data; // rebinds
            player.Evaluate(Math.Min(time, data.Duration));
        }

        // A prim path as a track path: relative to the import root, by object names.
        private static string RelativePrimPath(string primPath)
        {
            var names = new List<string>();
            foreach (string segment in primPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(SanitizeObjectName(segment));
            }

            return string.Join("/", names);
        }

        // Destroys the replaced parts, and what this session's import made for them that nothing
        // left in the import -- the rebuilt parts included -- still uses.
        private static void ReleaseReplaced(UsdStageSource source, List<GameObject> replaced)
        {
            var made = new HashSet<Object>();
            if (source.ImportedInSession)
            {
                foreach (GameObject part in replaced)
                {
                    CollectMade(part, made);
                }

                var used = new HashSet<Object>();
                CollectMade(source.gameObject, used);
                made.ExceptWith(used);
            }

            foreach (GameObject part in replaced)
            {
                DestroyMade(part);
            }

            foreach (Object item in made)
            {
                if (item == null)
                {
                    continue;
                }

#if UNITY_EDITOR
                if (UnityEditor.EditorUtility.IsPersistent(item))
                {
                    continue;
                }
#endif
                DestroyMade(item);
            }
        }

        // The meshes, materials and textures objects under `root` draw with.
        private static void CollectMade(GameObject root, HashSet<Object> made)
        {
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                made.Add(filter.sharedMesh);
            }

            foreach (SkinnedMeshRenderer skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                made.Add(skinned.sharedMesh);
            }

            foreach (UsdPointInstancer instancer in root.GetComponentsInChildren<UsdPointInstancer>(true))
            {
                made.Add(instancer.Mesh);
            }

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    made.Add(material);
                    foreach (int property in material.GetTexturePropertyNameIDs())
                    {
                        made.Add(material.GetTexture(property));
                    }
                }
            }

            made.Remove(null);
        }

        // A staging build whose import went away before it was grafted.
        private static void ReleaseBuilt(UsdImportResult built)
        {
            var made = new HashSet<Object>();
            CollectMade(built.RootObject, made);
            if (built.Animation != null)
            {
                made.Add(built.Animation.Data);
            }

            DestroyMade(built.RootObject);
            foreach (Object item in made)
            {
                if (item != null)
                {
                    DestroyMade(item);
                }
            }
        }

        private static void DestroyMade(Object item)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(item);
            }
            else
            {
                Object.DestroyImmediate(item);
            }
        }

        private static void MarkSceneDirty(GameObject target)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && target.scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(target.scene);
            }
#endif
        }
    }
}
