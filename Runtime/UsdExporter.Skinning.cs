using System;
using System.Collections.Generic;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    // SkinnedMeshRenderer and UsdDualQuaternionSkin export (native API 13).
    //
    // With TransformPolicy.PreserveHierarchy, renderers that share their bones become one UsdSkel
    // character: the lowest common ancestor of the renderers and bones becomes a SkelRoot, with a
    // Skeleton below it whose joints are the bones and every transform between them and the root.
    // Each mesh keeps its bind-pose points, joint weights and blend shapes. Bind transforms come
    // from the renderers' bind poses, so UsdSkel skinning reproduces Unity's exactly:
    //   geomBind   = inverse(R) * S for the first renderer   (skeleton space; R the skeleton's world,
    //   bind_i     = geomBind * inverse(bindpose_i)             S the renderer's world)
    //   geomBind   = bind_i * bindpose_i for the others, which share the skeleton's bind_i
    //   inverse(bind_i) * geomBind = bindpose_i, and joint_i's skeleton-space pose is inverse(R) *
    //   bone_i's world, so R * sum(w * joint_i * inverse(bind_i) * geomBind * p) is Unity's
    //   sum(w * bone_i.localToWorld * bindpose_i * p). A renderer bound in another pose, whose
    //   bind poses fit no single geomBind, is baked instead.
    // Bones stay Xform prims too, so objects attached to a bone keep their place. A mesh a
    // UsdDualQuaternionSkin poses is written the same way, with skel:skinningMethod dualQuaternion.
    //
    // With TransformPolicy.BakedMesh, a skinned mesh is written as the static mesh it currently
    // draws (SkinnedMeshRenderer.BakeMesh), and so is any renderer the skeleton path cannot carry,
    // and every one in an Alembic (.abc) export, which has no skeletons.
    public static partial class UsdExporter
    {
        // What the scene export wrote, for writing time samples onto it afterwards (UsdAnimationRecorder).
        internal sealed class ExportSceneInfo
        {
            public Dictionary<Transform, string> XformPaths;
            public readonly List<ExportedSkeleton> Skeletons = new List<ExportedSkeleton>();

            // The Mesh prim each renderer was written as; the Xform's own path for a mesh the
            // importer made, else a "<name>_Mesh" child of it.
            public readonly Dictionary<Renderer, string> MeshPaths = new Dictionary<Renderer, string>();

            // Renderers whose mesh prim is the source's (a Preserve export's imported meshes): their
            // points are the source's topology, which the Unity mesh (split at seams) is not.
            public readonly HashSet<Renderer> SourceMeshes = new HashSet<Renderer>();

            // Every skinned mesh is written in its current pose (Alembic, which has no skeletons).
            public bool BakeSkinning;

            // Skinned renderers written as the static mesh they draw now, and not as UsdSkel.
            public readonly List<SkinnedRenderer> BakedSkinned = new List<SkinnedRenderer>();
        }

        internal sealed class ExportedSkeleton
        {
            public string SkeletonPath;
            public Transform Owner;   // the object whose space is skeleton space
            public Transform[] Joints;
            public readonly List<string> ChannelNames = new List<string>();
            public readonly List<Renderer> ChannelRenderers = new List<Renderer>();
            public readonly List<int> ChannelShapes = new List<int>();
            public readonly List<float> ChannelFullWeights = new List<float>();

            // Joint-local transforms now, in USD's basis, 16 floats a joint.
            public float[] LocalTransforms()
            {
                var values = new float[Joints.Length * 16];
                for (int j = 0; j < Joints.Length; j++)
                {
                    // Top joints are children of the Skeleton's own object, so a Transform's local
                    // matrix is the joint-local transform for every joint.
                    WriteUsdMatrix(values, j * 16, GetLocalMatrix(Joints[j]));
                }

                return values;
            }

            // Blend-shape weights now, 0 to 1 against each shape's full frame.
            public float[] ChannelWeights()
            {
                var values = new float[ChannelNames.Count];
                for (int c = 0; c < values.Length; c++)
                {
                    values[c] = SkinnedRenderer.WeightOf(ChannelRenderers[c], ChannelShapes[c]) / ChannelFullWeights[c];
                }

                return values;
            }
        }

        private static void ExportSkinnedMeshes(
            GameObject root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options,
            UsdExportResult result,
            TextureExportContext textureContext,
            ExportSceneInfo scene,
            HashSet<string> usedPaths)
        {
            var renderers = new List<SkinnedRenderer>();
            foreach (SkinnedRenderer renderer in SkinnedRenderer.In(root, options.IncludeInactive))
            {
                if (renderer.sharedMesh != null && (renderer.enabled || options.ExportDisabledRenderers) &&
                    renderer.Renderer.GetComponentInParent<UsdImportHelperObject>(true) == null)
                {
                    renderers.Add(renderer);
                }
            }

            if (renderers.Count == 0)
            {
                return;
            }

            bool hierarchy = options.TransformPolicy == UsdTransformPolicy.PreserveHierarchy;
            var rules = new SkinningRules
            {
                Skeletons = hierarchy && scene.XformPaths != null && UsdNative.SupportsAnimation && !scene.BakeSkinning,
                Hierarchy = hierarchy
            };
            WriteSkinnedRenderers(root, rootPrimName, context, options, result, textureContext, scene, usedPaths, renderers, rules);
        }

        // How a set of skinned renderers is written: as UsdSkel at all, with the hierarchy kept,
        // where a SkelRoot may go, and the existing Skeleton prim an imported skeleton already is
        // (an override export binds new meshes to it instead of writing it again).
        private sealed class SkinningRules
        {
            public bool Skeletons;
            public bool Hierarchy;
            public Predicate<Transform> SkelRootAllowed;
            public Func<UsdSkeleton, string> ExistingSkeletonPath;
        }

        private static void WriteSkinnedRenderers(
            GameObject root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options,
            UsdExportResult result,
            TextureExportContext textureContext,
            ExportSceneInfo scene,
            HashSet<string> usedPaths,
            List<SkinnedRenderer> renderers,
            SkinningRules rules)
        {
            bool skeletons = rules.Skeletons;
            var groups = new Dictionary<string, List<SkinnedRenderer>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (SkinnedRenderer renderer in renderers)
            {
                string reason = skeletons ? SkinningProblem(root, renderer) : "the export bakes transforms into meshes";
                if (reason != null)
                {
                    if (skeletons)
                    {
                        Debug.LogWarning($"Unity USD Toolkit: '{renderer.name}' is exported as a static mesh in its current pose: {reason}.");
                    }

                    ExportBakedSkinnedMesh(root, rootPrimName, context, options, result, textureContext, scene, usedPaths, renderer,
                                           rules.Hierarchy);
                    continue;
                }

                string key = BonesKey(renderer.bones);
                if (!groups.TryGetValue(key, out List<SkinnedRenderer> group))
                {
                    group = new List<SkinnedRenderer>();
                    groups.Add(key, group);
                    order.Add(key);
                }

                group.Add(renderer);
            }

            foreach (string key in order)
            {
                List<SkinnedRenderer> group = groups[key];
                UsdSkeleton imported = FindOwningSkeleton(group);
                string existing = imported != null ? rules.ExistingSkeletonPath?.Invoke(imported) : null;
                if (existing != null)
                {
                    foreach (SkinnedRenderer renderer in group)
                    {
                        ExportOnExistingSkeleton(root, rootPrimName, context, options, result, textureContext, scene, usedPaths,
                                                 renderer, imported, existing);
                    }

                    continue;
                }

                ExportSkeleton(root, rootPrimName, context, options, result, textureContext, scene, usedPaths, group, rules);
            }
        }

        // A renderer on the joints of a skeleton that already is a prim of the stage the export
        // overrides: its mesh is bound to that Skeleton, whose bind transforms it has to agree with.
        private static void ExportOnExistingSkeleton(
            GameObject root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options,
            UsdExportResult result,
            TextureExportContext textureContext,
            ExportSceneInfo scene,
            HashSet<string> usedPaths,
            SkinnedRenderer renderer,
            UsdSkeleton skeleton,
            string skeletonPath)
        {
            string problem = null;
            Matrix4x4 geomBind = Matrix4x4.identity;
            Matrix4x4[] bind = skeleton.BindTransformArray;
            Transform[] joints = skeleton.JointArray;
            if (skeleton.transform.parent == null || !renderer.transform.IsChildOf(skeleton.transform.parent))
            {
                problem = "it is not inside the imported character's SkelRoot";
            }
            else if (bind == null || bind.Length != joints.Length)
            {
                problem = "the imported skeleton's bind pose is not recorded (import the stage again)";
            }
            else
            {
                var jointIndex = new Dictionary<Transform, int>();
                for (int j = 0; j < joints.Length; j++)
                {
                    jointIndex[joints[j]] = j;
                }

                var hasBind = new bool[joints.Length];
                var binds = (Matrix4x4[])bind.Clone();
                for (int j = 0; j < hasBind.Length; j++)
                {
                    hasBind[j] = true;
                }

                if (!FitBindTransforms(renderer, skeleton.transform.worldToLocalMatrix, jointIndex, binds, hasBind, out geomBind))
                {
                    problem = "its bind poses do not match the imported skeleton's bind pose";
                }
            }

            if (problem != null)
            {
                Debug.LogWarning($"Unity USD Toolkit: '{renderer.name}' is exported as a static mesh in its current pose: {problem}.");
                ExportBakedSkinnedMesh(root, rootPrimName, context, options, result, textureContext, scene, usedPaths, renderer, true);
                return;
            }

            Mesh mesh = renderer.sharedMesh;
            string meshPath = MakePreservedMeshPrimPath(root.transform, renderer.transform, rootPrimName, scene.XformPaths, usedPaths);
            RUsdMaterial[] materials = CreateMaterials(renderer.Renderer, textureContext);
            bool visible = IsShown(options, renderer.gameObject, renderer.Renderer, scene.XformPaths, meshPath);
            BuildMeshPayload(root.transform, renderer.transform, mesh, options, true, out RUsdVec3[] points,
                             out int[] indices, out RUsdSubmesh[] submeshes, out RUsdVec3[] normals, out RUsdVec2[] uv0);
            UsdNative.AddMesh(context, meshPath, points, indices, submeshes, normals, uv0, materials, visible, options.ExportBounds);
            scene.MeshPaths[renderer.Renderer] = meshPath;
            WriteJointWeights(context, renderer, mesh, points.Length, meshPath, skeletonPath, joints, geomBind);

            // The shapes are written; their weights would belong on the source's SkelAnimation, which
            // an override export does not rewrite, so they read as the animation's same-named channels.
            var channels = new ExportedSkeleton { SkeletonPath = skeletonPath, Owner = skeleton.transform, Joints = joints };
            ExportBlendShapes(context, mesh, renderer, meshPath, channels, new HashSet<string>(StringComparer.Ordinal));
            if (Array.Exists(channels.ChannelWeights(), w => w != 0f))
            {
                Debug.LogWarning($"Unity USD Toolkit: the blend-shape weights of '{renderer.name}' are not exported: they would go on " +
                                 "the imported skeleton's animation, which an override export leaves as the source has it.");
            }

            result.AddMesh(GetTransformPath(root.transform, renderer.transform), mesh.name, meshPath, points.Length,
                           indices.Length / 3, submeshes.Length, materials.Length);
        }

        // Unity's per-vertex weight runs, padded to the widest, as joint indices into `joints`.
        private static void WriteJointWeights(IntPtr context, SkinnedRenderer renderer, Mesh mesh, int vertexCount,
                                              string meshPath, string skeletonPath, IList<Transform> joints, Matrix4x4 geomBind)
        {
            var jointIndex = new Dictionary<Transform, int>();
            for (int j = 0; j < joints.Count; j++)
            {
                jointIndex[joints[j]] = j;
            }

            var bonesPerVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int influences = 1;
            foreach (byte count in bonesPerVertex)
            {
                influences = Math.Max(influences, count);
            }

            Transform[] bones = renderer.bones;
            var jointIndices = new int[vertexCount * influences];
            var jointWeights = new float[vertexCount * influences];
            int next = 0;
            for (int v = 0; v < vertexCount && v < bonesPerVertex.Length; v++)
            {
                for (int i = 0; i < bonesPerVertex[v]; i++, next++)
                {
                    BoneWeight1 weight = weights[next];
                    jointIndices[v * influences + i] = jointIndex[bones[weight.boneIndex]];
                    jointWeights[v * influences + i] = weight.weight;
                }
            }

            UsdNative.BindSkinnedMesh(context, meshPath, skeletonPath, jointIndices, jointWeights, vertexCount,
                                      influences, ToUsdMatrix(geomBind));
            if (renderer.DualQuaternion)
            {
                UsdNative.SetSkinningMethod(context, meshPath, true);
            }
        }

        // Why a renderer cannot be written as UsdSkel, or null when it can.
        private static string SkinningProblem(GameObject root, SkinnedRenderer renderer)
        {
            Mesh mesh = renderer.sharedMesh;
            Transform[] bones = renderer.bones;
            if (!mesh.isReadable)
            {
                return "its mesh is not readable (enable Read/Write on the asset)";
            }

            if (bones == null || bones.Length == 0)
            {
                return "it has no bones";
            }

            if (mesh.bindposes.Length != bones.Length)
            {
                return "its mesh has a different number of bind poses than the renderer has bones";
            }

            foreach (Transform bone in bones)
            {
                if (bone == null || !bone.IsChildOf(root.transform) || bone == root.transform)
                {
                    return "a bone is missing or outside the exported hierarchy";
                }
            }

            return null;
        }

        private static string BonesKey(Transform[] bones)
        {
            var key = new System.Text.StringBuilder(bones.Length * 8);
            foreach (Transform bone in bones)
            {
                key.Append(bone.GetEntityId().ToString()).Append(',');
            }

            return key.ToString();
        }

        private static void ExportSkeleton(
            GameObject root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options,
            UsdExportResult result,
            TextureExportContext textureContext,
            ExportSceneInfo scene,
            HashSet<string> usedPaths,
            List<SkinnedRenderer> renderers,
            SkinningRules rules)
        {
            // A skeleton this package imported keeps its Skeleton prim and joint names: its object
            // becomes the Skeleton again. Anything else gets a new Skeleton below the SkelRoot whose
            // joints are the bones and every object between them and the SkelRoot.
            UsdSkeleton imported = FindOwningSkeleton(renderers);
            var members = new List<Transform>();
            foreach (SkinnedRenderer renderer in renderers)
            {
                members.Add(renderer.transform);
                if (imported == null)
                {
                    members.AddRange(renderer.bones);
                }
            }

            if (imported != null)
            {
                members.Add(imported.transform);
            }

            // The SkelRoot: the deepest object above every renderer and the skeleton. A mesh, a joint
            // or the Skeleton itself cannot be its own SkelRoot.
            Transform skelRoot = LowestCommonAncestor(members, root.transform);
            if (members.Contains(skelRoot) && skelRoot != root.transform)
            {
                skelRoot = skelRoot.parent;
            }

            if (rules.SkelRootAllowed != null && !rules.SkelRootAllowed(skelRoot))
            {
                foreach (SkinnedRenderer renderer in renderers)
                {
                    Debug.LogWarning($"Unity USD Toolkit: '{renderer.name}' is exported as a static mesh in its current pose: its " +
                                     $"mesh and bones only meet at '{skelRoot.name}', which is a prim of the stage being overridden; " +
                                     "put them under a new object to write them as a skinned character.");
                    ExportBakedSkinnedMesh(root, rootPrimName, context, options, result, textureContext, scene, usedPaths, renderer,
                                           rules.Hierarchy);
                }

                return;
            }

            string skelRootPath = scene.XformPaths.TryGetValue(skelRoot, out string path) ? path : "/" + rootPrimName;
            UsdNative.DefineSkelRoot(context, skelRootPath);

            var joints = new List<Transform>();
            var jointPaths = new List<string>();
            Transform owner;
            string skeletonPath;
            if (imported != null)
            {
                owner = imported.transform;
                skeletonPath = scene.XformPaths[owner];
                joints.AddRange(imported.Joints);
                jointPaths.AddRange(imported.JointPaths);
            }
            else
            {
                var inSkeleton = new HashSet<Transform>();
                foreach (SkinnedRenderer renderer in renderers)
                {
                    foreach (Transform bone in renderer.bones)
                    {
                        for (Transform t = bone; t != null && t != skelRoot; t = t.parent)
                        {
                            inSkeleton.Add(t);
                        }
                    }
                }

                owner = skelRoot;
                skeletonPath = MakeUniquePath(skelRootPath + "/Skeleton", usedPaths);
                CollectJoints(skelRoot, string.Empty, inSkeleton, joints, jointPaths);
            }

            var jointIndex = new Dictionary<Transform, int>();
            for (int j = 0; j < joints.Count; j++)
            {
                jointIndex[joints[j]] = j;
            }

            var skeleton = new ExportedSkeleton
            {
                SkeletonPath = skeletonPath,
                Owner = owner,
                Joints = joints.ToArray()
            };

            // One set of bind transforms serves every mesh on the skeleton, so each renderer gets the
            // geomBindTransform that makes inverse(bind_i) * geomBind its own bind pose: the first
            // renderer's from its transform, the others' from the bind transforms already set. A
            // joint takes its bind from the first renderer that weights it; joints no renderer
            // weights take the current pose (they move no point, so any valid transform will do).
            Matrix4x4 rootInverse = owner.worldToLocalMatrix;
            var bindMatrices = new Matrix4x4[joints.Count];
            var hasBind = new bool[joints.Count];
            var geomBinds = new Dictionary<SkinnedRenderer, Matrix4x4>();
            var bound = new List<SkinnedRenderer>();
            foreach (SkinnedRenderer renderer in renderers)
            {
                if (FitBindTransforms(renderer, rootInverse, jointIndex, bindMatrices, hasBind, out Matrix4x4 geomBind))
                {
                    geomBinds.Add(renderer, geomBind);
                    bound.Add(renderer);
                    continue;
                }

                Debug.LogWarning($"Unity USD Toolkit: '{renderer.name}' is exported as a static mesh in its current pose: its bind poses do not match the other meshes on the same bones.");
                ExportBakedSkinnedMesh(root, rootPrimName, context, options, result, textureContext, scene, usedPaths, renderer,
                                       rules.Hierarchy);
            }

            var bind = new float[joints.Count * 16];
            for (int j = 0; j < joints.Count; j++)
            {
                WriteUsdMatrix(bind, j * 16, hasBind[j] ? bindMatrices[j] : rootInverse * joints[j].localToWorldMatrix);
            }

            UsdNative.AddSkeleton(context, skeleton.SkeletonPath, jointPaths.ToArray(), bind, skeleton.LocalTransforms());

            var channelNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (SkinnedRenderer renderer in bound)
            {
                Mesh mesh = renderer.sharedMesh;
                string meshPath = MakePreservedMeshPrimPath(root.transform, renderer.transform, rootPrimName, scene.XformPaths, usedPaths);
                RUsdMaterial[] materials = CreateMaterials(renderer.Renderer, textureContext);
                bool visible = IsShown(options, renderer.gameObject, renderer.Renderer, scene.XformPaths, meshPath);
                BuildMeshPayload(root.transform, renderer.transform, mesh, options, true, out RUsdVec3[] points,
                                 out int[] indices, out RUsdSubmesh[] submeshes, out RUsdVec3[] normals, out RUsdVec2[] uv0);
                UsdNative.AddMesh(context, meshPath, points, indices, submeshes, normals, uv0, materials, visible, options.ExportBounds);
                scene.MeshPaths[renderer.Renderer] = meshPath;

                WriteJointWeights(context, renderer, mesh, points.Length, meshPath, skeleton.SkeletonPath, joints, geomBinds[renderer]);
                ExportBlendShapes(context, mesh, renderer, meshPath, skeleton, channelNames);
                result.AddMesh(GetTransformPath(root.transform, renderer.transform), mesh.name, meshPath, points.Length,
                               indices.Length / 3, submeshes.Length, materials.Length);
            }

            // Blend-shape weights live on a SkelAnimation; without one they would all read as 0.
            float[] channelWeights = skeleton.ChannelWeights();
            if (Array.Exists(channelWeights, w => w != 0f))
            {
                UsdNative.SetSkeletonAnimation(context, skeleton.SkeletonPath, skeleton.ChannelNames.ToArray(), null,
                                               skeleton.LocalTransforms(), channelWeights);
            }

            scene.Skeletons.Add(skeleton);
        }

        // The renderer's geomBindTransform (skeleton space) against the bind transforms set so far,
        // setting those of the joints it is the first to weight. False, with nothing set, when its
        // bind poses disagree with the bind transforms already there.
        private static bool FitBindTransforms(SkinnedRenderer renderer, Matrix4x4 rootInverse,
                                              Dictionary<Transform, int> jointIndex, Matrix4x4[] bind, bool[] hasBind,
                                              out Matrix4x4 geomBind)
        {
            Transform[] bones = renderer.bones;
            Matrix4x4[] bindposes = renderer.sharedMesh.bindposes;
            var used = new bool[bones.Length];
            foreach (BoneWeight1 weight in renderer.sharedMesh.GetAllBoneWeights())
            {
                if (weight.weight > 0f && weight.boneIndex < used.Length)
                {
                    used[weight.boneIndex] = true;
                }
            }

            geomBind = rootInverse * renderer.transform.localToWorldMatrix;
            for (int b = 0; b < bones.Length; b++)
            {
                if (used[b] && hasBind[jointIndex[bones[b]]])
                {
                    geomBind = bind[jointIndex[bones[b]]] * bindposes[b];
                    break;
                }
            }

            for (int b = 0; b < bones.Length; b++)
            {
                int j = jointIndex[bones[b]];
                if (used[b] && hasBind[j] && !NearlyEqual(bind[j] * bindposes[b], geomBind))
                {
                    return false;
                }
            }

            for (int b = 0; b < bones.Length; b++)
            {
                int j = jointIndex[bones[b]];
                if (used[b] && !hasBind[j])
                {
                    bind[j] = geomBind * bindposes[b].inverse;
                    hasBind[j] = true;
                }
            }

            return true;
        }

        private static bool NearlyEqual(Matrix4x4 a, Matrix4x4 b)
        {
            float scale = 1f;
            for (int i = 0; i < 16; i++)
            {
                scale = Mathf.Max(scale, Mathf.Abs(b[i]));
            }

            for (int i = 0; i < 16; i++)
            {
                if (Mathf.Abs(a[i] - b[i]) > 1e-4f * scale)
                {
                    return false;
                }
            }

            return true;
        }

        // The UsdSkeleton every renderer's bones belong to, when they all share one.
        private static UsdSkeleton FindOwningSkeleton(List<SkinnedRenderer> renderers)
        {
            UsdSkeleton owner = null;
            foreach (SkinnedRenderer renderer in renderers)
            {
                foreach (Transform bone in renderer.bones)
                {
                    UsdSkeleton candidate = bone.GetComponentInParent<UsdSkeleton>(true);
                    if (candidate == null || (owner != null && candidate != owner) ||
                        Array.IndexOf(candidate.JointArray, bone) < 0)
                    {
                        return null;
                    }

                    owner = candidate;
                }
            }

            return owner;
        }

        private static void CollectJoints(Transform parent, string parentPath, HashSet<Transform> inSkeleton,
                                          List<Transform> joints, List<string> paths)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (!inSkeleton.Contains(child))
                {
                    continue;
                }

                string name = SanitizePrimName(child.name);
                string unique = name;
                for (int suffix = 1; !taken.Add(unique); suffix++)
                {
                    unique = name + "_" + suffix;
                }

                string path = parentPath.Length == 0 ? unique : parentPath + "/" + unique;
                joints.Add(child);
                paths.Add(path);
                CollectJoints(child, path, inSkeleton, joints, paths);
            }
        }

        private static void ExportBlendShapes(IntPtr context, Mesh mesh, SkinnedRenderer renderer, string meshPath,
                                              ExportedSkeleton skeleton, HashSet<string> channelNames)
        {
            int vertexCount = mesh.vertexCount;
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                int frames = mesh.GetBlendShapeFrameCount(shape);
                float full = frames > 0 ? mesh.GetBlendShapeFrameWeight(shape, frames - 1) : 0f;
                if (full <= 0f)
                {
                    continue;
                }

                // Every frame of a USD blend shape moves the same points, so collect them over all frames.
                var vertexDeltas = new Vector3[frames][];
                var normalDeltas = new Vector3[frames][];
                var moved = new SortedSet<int>();
                bool hasNormals = false;
                for (int frame = 0; frame < frames; frame++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals, deltaTangents);
                    vertexDeltas[frame] = (Vector3[])deltaVertices.Clone();
                    normalDeltas[frame] = (Vector3[])deltaNormals.Clone();
                    for (int v = 0; v < vertexCount; v++)
                    {
                        bool normal = deltaNormals[v].sqrMagnitude > 0f;
                        hasNormals |= normal;
                        if (normal || deltaVertices[v].sqrMagnitude > 0f)
                        {
                            moved.Add(v);
                        }
                    }
                }

                if (moved.Count == 0)
                {
                    continue; // nothing to write: an empty pointIndices would mean every point
                }

                string name = SanitizePrimName(mesh.GetBlendShapeName(shape));
                string channel = name;
                for (int suffix = 1; !channelNames.Add(channel); suffix++)
                {
                    channel = name + "_" + suffix;
                }

                var pointIndices = new int[moved.Count];
                moved.CopyTo(pointIndices);
                for (int frame = 0; frame < frames; frame++)
                {
                    float weight = mesh.GetBlendShapeFrameWeight(shape, frame) / full;
                    if (!(weight > 0f))
                    {
                        continue;
                    }

                    var offsets = new RUsdVec3[pointIndices.Length];
                    RUsdVec3[] normals = hasNormals ? new RUsdVec3[pointIndices.Length] : null;
                    for (int i = 0; i < pointIndices.Length; i++)
                    {
                        offsets[i] = ToUsdVector(vertexDeltas[frame][pointIndices[i]]);
                        if (normals != null)
                        {
                            normals[i] = ToUsdVector(normalDeltas[frame][pointIndices[i]]);
                        }
                    }

                    UsdNative.AddBlendShapeFrame(context, meshPath, channel, Math.Min(1f, weight), pointIndices, offsets, normals);
                }

                skeleton.ChannelNames.Add(channel);
                skeleton.ChannelRenderers.Add(renderer.Renderer);
                skeleton.ChannelShapes.Add(shape);
                skeleton.ChannelFullWeights.Add(full);
            }
        }

        // The renderer as it draws now, written like a MeshFilter's mesh. BakeMesh with useScale
        // gives vertices in the renderer's own space, scale included, as a MeshFilter mesh is.
        private static void ExportBakedSkinnedMesh(
            GameObject root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options,
            UsdExportResult result,
            TextureExportContext textureContext,
            ExportSceneInfo scene,
            HashSet<string> usedPaths,
            SkinnedRenderer renderer,
            bool preserve)
        {
            var baked = new Mesh { name = renderer.sharedMesh.name };
            try
            {
                renderer.BakeMesh(baked);
                string primPath = preserve
                    ? MakePreservedMeshPrimPath(root.transform, renderer.transform, rootPrimName, scene.XformPaths, usedPaths)
                    : MakeBakedMeshPrimPath(root.transform, renderer.transform, rootPrimName, usedPaths);
                RUsdMaterial[] materials = CreateMaterials(renderer.Renderer, textureContext);
                bool visible = IsShown(options, renderer.gameObject, renderer.Renderer, scene.XformPaths, primPath);
                BuildMeshPayload(root.transform, renderer.transform, baked, options, preserve, out RUsdVec3[] points,
                                 out int[] indices, out RUsdSubmesh[] submeshes, out RUsdVec3[] normals, out RUsdVec2[] uv0);
                UsdNative.AddMesh(context, primPath, points, indices, submeshes, normals, uv0, materials, visible, options.ExportBounds);
                scene.MeshPaths[renderer.Renderer] = primPath;
                scene.BakedSkinned.Add(renderer);
                result.AddMesh(GetTransformPath(root.transform, renderer.transform), renderer.sharedMesh.name, primPath,
                               points.Length, indices.Length / 3, submeshes.Length, materials.Length);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }

        private static Transform LowestCommonAncestor(List<Transform> members, Transform limit)
        {
            Transform candidate = members.Count > 0 ? members[0] : limit;
            while (candidate != null && candidate != limit)
            {
                bool all = true;
                foreach (Transform member in members)
                {
                    if (!member.IsChildOf(candidate))
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    return candidate;
                }

                candidate = candidate.parent;
            }

            return limit;
        }

        // A skinned renderer as the exporter writes it: a SkinnedMeshRenderer, or a MeshRenderer a
        // UsdDualQuaternionSkin poses. Named like SkinnedMeshRenderer's members, so the export code
        // reads the same for both.
        internal sealed class SkinnedRenderer
        {
            public readonly Renderer Renderer;
            private readonly SkinnedMeshRenderer skinned;
            private readonly UsdDualQuaternionSkin dualQuaternion;

            private SkinnedRenderer(Renderer renderer, SkinnedMeshRenderer skinned, UsdDualQuaternionSkin dualQuaternion)
            {
                Renderer = renderer;
                this.skinned = skinned;
                this.dualQuaternion = dualQuaternion;
            }

            public Transform transform => Renderer.transform;

            public GameObject gameObject => Renderer.gameObject;

            public string name => Renderer.name;

            public bool enabled => Renderer.enabled;

            public bool DualQuaternion => dualQuaternion != null;

            public Mesh sharedMesh => skinned != null ? skinned.sharedMesh : dualQuaternion.SharedMesh;

            public Transform[] bones => skinned != null ? skinned.bones : dualQuaternion.Bones;

            public float GetBlendShapeWeight(int shape) => WeightOf(Renderer, shape);

            // As SkinnedMeshRenderer.BakeMesh with useScale: what it draws, in its own space.
            public void BakeMesh(Mesh mesh)
            {
                if (skinned != null)
                {
                    skinned.BakeMesh(mesh, true);
                }
                else
                {
                    dualQuaternion.BakeMesh(mesh);
                }
            }

            public static SkinnedRenderer Of(Renderer renderer)
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    return new SkinnedRenderer(renderer, skinned, null);
                }

                return renderer is MeshRenderer && renderer.TryGetComponent(out UsdDualQuaternionSkin dualQuaternion)
                    ? new SkinnedRenderer(renderer, null, dualQuaternion)
                    : null;
            }

            public static List<SkinnedRenderer> In(GameObject root, bool includeInactive)
            {
                var found = new List<SkinnedRenderer>();
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(includeInactive))
                {
                    SkinnedRenderer skinnedRenderer = Of(renderer);
                    if (skinnedRenderer != null)
                    {
                        found.Add(skinnedRenderer);
                    }
                }

                return found;
            }

            public static float WeightOf(Renderer renderer, int shape)
            {
                if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
                {
                    return skinnedMeshRenderer.GetBlendShapeWeight(shape);
                }

                return renderer != null && renderer.TryGetComponent(out UsdDualQuaternionSkin skin) ? skin.GetBlendShapeWeight(shape) : 0f;
            }
        }

        // A Unity matrix in USD's basis, as 16 row-major floats (row-vector convention).
        internal static void WriteUsdMatrix(float[] destination, int offset, Matrix4x4 unity)
        {
            RUsdMatrix4x4 m = ToUsdMatrix(unity);
            destination[offset + 0] = (float)m.M00; destination[offset + 1] = (float)m.M01;
            destination[offset + 2] = (float)m.M02; destination[offset + 3] = (float)m.M03;
            destination[offset + 4] = (float)m.M10; destination[offset + 5] = (float)m.M11;
            destination[offset + 6] = (float)m.M12; destination[offset + 7] = (float)m.M13;
            destination[offset + 8] = (float)m.M20; destination[offset + 9] = (float)m.M21;
            destination[offset + 10] = (float)m.M22; destination[offset + 11] = (float)m.M23;
            destination[offset + 12] = (float)m.M30; destination[offset + 13] = (float)m.M31;
            destination[offset + 14] = (float)m.M32; destination[offset + 15] = (float)m.M33;
        }
    }
}
