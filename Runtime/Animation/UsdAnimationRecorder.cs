using System;
using System.Collections.Generic;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Records a GameObject hierarchy over time and writes it as an animated USD stage: transforms
    /// and active states that change become time samples on their prims, and every skinned
    /// character's bones and blend-shape weights a UsdSkel animation. A renderer switched on and off
    /// (Renderer.enabled) hides and shows its mesh prim the same way, and a mesh drawn through a
    /// vertex stream (a point cache a <see cref="UsdAnimationPlayer"/> plays) has its points
    /// sampled. Written as Alembic (.abc), which has no skeletons, a skinned mesh is baked every
    /// frame into sampled points instead. Call <see cref="Capture"/>
    /// once per frame (in Play Mode or a player), or sample an <see cref="AnimationClip"/> or a
    /// <see cref="UsdAnimationPlayer"/> with <see cref="CaptureClip"/> and
    /// <see cref="CapturePlayer"/>, then <see cref="Save"/>. The stage is written with
    /// <see cref="UsdTransformPolicy.PreserveHierarchy"/>; its static values are the first
    /// captured frame, and its time codes are frames at <see cref="FramesPerSecond"/>. With
    /// <see cref="UsdCompositionMode.Preserve"/>, a recording of an imported stage is written as
    /// overrides on it instead (see <see cref="Save"/>).
    /// Only what changes is kept: a transform costs memory from the first frame it moves.
    /// Needs the 0.8.1 native plugin.
    /// </summary>
    public sealed class UsdAnimationRecorder
    {
        private readonly GameObject root;
        private readonly Transform[] transforms;
        // Renderers with blend shapes to record: SkinnedMeshRenderers, and MeshRenderers a
        // UsdDualQuaternionSkin poses.
        private readonly Renderer[] renderers;
        private readonly Renderer[] meshRenderers;
        private readonly List<double> times = new List<double>();

        private readonly Pose[] firstPoses;
        private readonly List<Pose>[] poses;
        private readonly bool[] firstActive;
        private readonly List<bool>[] actives;
        private readonly bool[] firstEnabled;
        private readonly List<bool>[] enables;
        private readonly List<float[]>[] weights;

        // Meshes drawn through MeshRenderer.additionalVertexStreams: their points, once they move.
        private readonly MeshRenderer[] streamed;
        private readonly List<Vector3[]>[] streamPoints;
        private readonly List<Vector3[]>[] streamNormals;

        private struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;

            public static Pose Of(Transform t) => new Pose { Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale };

            public bool Same(Pose other) => Position == other.Position && Rotation == other.Rotation && Scale == other.Scale;

            public void Apply(Transform t)
            {
                t.localPosition = Position;
                t.localRotation = Rotation;
                t.localScale = Scale;
            }

            public Matrix4x4 Matrix => Matrix4x4.TRS(Position, Rotation, Scale);
        }

        /// <summary>Records <paramref name="root"/> and everything below it, as it is now.</summary>
        public UsdAnimationRecorder(GameObject root, double framesPerSecond = 24.0)
        {
            this.root = root != null ? root : throw new ArgumentNullException(nameof(root));
            if (!(framesPerSecond > 0.0) || double.IsInfinity(framesPerSecond))
            {
                throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frames per second must be positive.");
            }

            FramesPerSecond = framesPerSecond;
            transforms = root.GetComponentsInChildren<Transform>(true);
            renderers = Array.FindAll(root.GetComponentsInChildren<Renderer>(true),
                                      r => r is SkinnedMeshRenderer || r.GetComponent<UsdDualQuaternionSkin>() != null);
            meshRenderers = Array.FindAll(root.GetComponentsInChildren<Renderer>(true),
                                          r => r is MeshRenderer || r is SkinnedMeshRenderer);
            firstEnabled = new bool[meshRenderers.Length];
            enables = new List<bool>[meshRenderers.Length];
            streamed = Array.FindAll(root.GetComponentsInChildren<MeshRenderer>(true),
                                     r => r.GetComponent<MeshFilter>() != null && r.GetComponent<UsdDualQuaternionSkin>() == null);
            streamPoints = new List<Vector3[]>[streamed.Length];
            streamNormals = new List<Vector3[]>[streamed.Length];
            firstPoses = new Pose[transforms.Length];
            poses = new List<Pose>[transforms.Length];
            firstActive = new bool[transforms.Length];
            actives = new List<bool>[transforms.Length];
            weights = new List<float[]>[renderers.Length];
            for (int r = 0; r < renderers.Length; r++)
            {
                weights[r] = new List<float[]>();
            }
        }

        /// <summary>The time-code rate of the stage <see cref="Save"/> writes.</summary>
        public double FramesPerSecond { get; }

        /// <summary>How many frames have been captured.</summary>
        public int SampleCount => times.Count;

        /// <summary>
        /// Captures the hierarchy's current state at <paramref name="seconds"/>, which must be later
        /// than the previous capture.
        /// </summary>
        public void Capture(double seconds)
        {
            if (times.Count > 0 && !(seconds > times[times.Count - 1]))
            {
                throw new ArgumentException("Each capture must be later than the one before.", nameof(seconds));
            }

            int frame = times.Count;
            times.Add(seconds);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                Pose pose = t != null ? Pose.Of(t) : (frame == 0 ? default : Last(i));
                bool active = t != null && t.gameObject.activeSelf;
                if (frame == 0)
                {
                    firstPoses[i] = pose;
                    firstActive[i] = active;
                    continue;
                }

                if (poses[i] == null && !pose.Same(firstPoses[i]))
                {
                    poses[i] = new List<Pose>(frame + 1);
                    for (int f = 0; f < frame; f++)
                    {
                        poses[i].Add(firstPoses[i]);
                    }
                }

                poses[i]?.Add(pose);

                if (actives[i] == null && active != firstActive[i])
                {
                    actives[i] = new List<bool>(frame + 1);
                    for (int f = 0; f < frame; f++)
                    {
                        actives[i].Add(firstActive[i]);
                    }
                }

                actives[i]?.Add(active);
            }

            for (int r = 0; r < meshRenderers.Length; r++)
            {
                bool enabled = meshRenderers[r] != null && meshRenderers[r].enabled;
                if (frame == 0)
                {
                    firstEnabled[r] = enabled;
                    continue;
                }

                if (enables[r] == null && enabled != firstEnabled[r])
                {
                    enables[r] = new List<bool>(frame + 1);
                    for (int f = 0; f < frame; f++)
                    {
                        enables[r].Add(firstEnabled[r]);
                    }
                }

                enables[r]?.Add(enabled);
            }

            for (int r = 0; r < renderers.Length; r++)
            {
                weights[r].Add(WeightsOf(renderers[r]));
            }

            for (int r = 0; r < streamed.Length; r++)
            {
                CaptureStream(r, frame);
            }
        }

        // A renderer's points this frame: its vertex stream's when it draws through one that has
        // every vertex, else its mesh's. Nothing is kept until a stream first shows up.
        private void CaptureStream(int r, int frame)
        {
            MeshRenderer renderer = streamed[r];
            Mesh mesh = renderer != null && renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable)
            {
                if (streamPoints[r] != null)
                {
                    streamPoints[r].Add(streamPoints[r][streamPoints[r].Count - 1]);
                    streamNormals[r].Add(streamNormals[r][streamNormals[r].Count - 1]);
                }

                return;
            }

            // A stream of colours alone (vertex painting) moves nothing; only positions count.
            Mesh stream = renderer.additionalVertexStreams;
            bool drawn = stream != null && stream.vertexCount == mesh.vertexCount && stream.isReadable &&
                         stream.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Position);
            if (!drawn && streamPoints[r] == null)
            {
                return;
            }

            if (streamPoints[r] == null)
            {
                streamPoints[r] = new List<Vector3[]>(frame + 1);
                streamNormals[r] = new List<Vector3[]>(frame + 1);
                Vector3[] restPoints = mesh.vertices;
                Vector3[] restNormals = mesh.normals;
                for (int f = 0; f < frame; f++)
                {
                    streamPoints[r].Add(restPoints);
                    streamNormals[r].Add(restNormals);
                }
            }

            Vector3[] normals = drawn ? stream.normals : null;
            streamPoints[r].Add(drawn ? stream.vertices : mesh.vertices);
            streamNormals[r].Add(normals != null && normals.Length == mesh.vertexCount ? normals : mesh.normals);
        }

        /// <summary>
        /// Samples <paramref name="clip"/> on the root from its start to its end at
        /// <see cref="FramesPerSecond"/>, capturing each frame after the ones already captured, then
        /// puts the hierarchy back as it was. A legacy clip samples anywhere; a Mecanim clip in the
        /// Editor.
        /// </summary>
        public void CaptureClip(AnimationClip clip)
        {
            if (clip == null)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            Pose[] before = Snapshot();
            double offset = NextStart();
            try
            {
                foreach (double t in FrameTimes(clip.length))
                {
                    clip.SampleAnimation(root, (float)t);
                    Capture(offset + t);
                }
            }
            finally
            {
                Restore(before);
            }
        }

        /// <summary>
        /// Plays <paramref name="player"/> through from start to end at <see cref="FramesPerSecond"/>,
        /// capturing each frame, then returns it to the time it was at.
        /// </summary>
        public void CapturePlayer(UsdAnimationPlayer player)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            double time = player.CurrentTime;
            double offset = NextStart();
            try
            {
                foreach (double t in FrameTimes(player.Duration))
                {
                    player.Evaluate(t);
                    Capture(offset + t);
                }
            }
            finally
            {
                player.Evaluate(time);
            }
        }

        /// <summary>
        /// Writes the recording to <paramref name="outputPath"/> (.usd, .usda, .usdc, .usdz, or .abc
        /// where the payload has OpenUSD's usdAbc plugin). By default a recording is a flattened
        /// stage with its hierarchy kept: the whole model, with the samples on it (the options'
        /// transform policy is overridden). With <see cref="UsdExportOptions.Composition"/> set to
        /// <see cref="UsdCompositionMode.Preserve"/> it is written as that export writes the
        /// hierarchy -- an imported stage as a layer over its source file (sublayered, or
        /// referenced when the stage sits below the recorded object), objects added in Unity as new
        /// content -- and the samples go on those prims: what moved gets its transform as time
        /// samples, an imported character's Skeleton prim a new SkelAnimation, and nothing of the
        /// source is copied. Not for .abc, which cannot hold overrides.
        /// </summary>
        public UsdExportResult Save(string outputPath, UsdExportOptions options = null)
        {
            if (times.Count == 0)
            {
                throw new InvalidOperationException("Nothing was captured.");
            }

            UsdExportOptions settings = options != null
                ? JsonUtility.FromJson<UsdExportOptions>(JsonUtility.ToJson(options))
                : new UsdExportOptions();
            settings.TransformPolicy = UsdTransformPolicy.PreserveHierarchy;
            if (settings.Composition == UsdCompositionMode.Preserve && UsdExporter.IsAlembicPath(outputPath))
            {
                throw new ArgumentException(
                    "An Alembic file cannot hold overrides on another stage; record to .usd, .usda, .usdc or " +
                    ".usdz, or with UsdCompositionMode.Flatten.", nameof(options));
            }

            settings.VisibilityPerObject = true;
            settings.SamplesFollow = settings.Composition == UsdCompositionMode.Preserve;
            if (Array.Exists(actives, track => track != null))
            {
                settings.IncludeInactive = true; // an object hidden in the first frame still needs its prim
            }

            if (Array.Exists(enables, track => track != null))
            {
                settings.ExportDisabledRenderers = true; // and so does a renderer switched off in it
            }

            // The stage's static values are the first frame; the scene is put back afterwards.
            Pose[] now = Snapshot();
            bool[] activeNow = ActiveSnapshot();
            bool[] enabledNow = EnabledSnapshot();
            List<float[]> weightsNow = WeightSnapshot();
            try
            {
                ApplyFrame(0);
                // ExportCore checks the plugin for animation support once it has configured the
                // native runtime: asking it here could load the library before OpenUSD's plugin
                // path is set, which OpenUSD reads only once, when the library loads.
                return UsdExporter.ExportCore(root, outputPath, settings,
                                              (context, scene) => WriteSamples(context, scene, settings),
                                              requiresAnimation: true);
            }
            finally
            {
                Restore(now);
                RestoreActive(activeNow);
                RestoreEnabled(enabledNow);
                RestoreWeights(weightsNow);
            }
        }

        private void WriteSamples(IntPtr context, UsdExporter.ExportSceneInfo scene, UsdExportOptions settings)
        {
            var timeCodes = new double[times.Count];
            for (int f = 0; f < timeCodes.Length; f++)
            {
                timeCodes[f] = Math.Round(times[f] * FramesPerSecond, 6);
            }

            UsdNative.SetExportTimeRange(context, timeCodes[0], timeCodes[timeCodes.Length - 1], FramesPerSecond, FramesPerSecond);
            var index = new Dictionary<Transform, int>(transforms.Length);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    index[transforms[i]] = i;
                }
            }

            // Visibility per prim: an object's active state on its Xform, a renderer's enabled
            // state on its Mesh prim, and both together where the two are one prim (a mesh the
            // importer made). Without PreserveInactiveAndDisabledVisibility everything shows.
            var visibility = new Dictionary<string, int[]>(StringComparer.Ordinal);
            bool preserve = settings.PreserveInactiveAndDisabledVisibility;
            if (scene.XformPaths != null)
            {
                foreach (KeyValuePair<Transform, string> prim in scene.XformPaths)
                {
                    // The root is the stage's own prim: its motion relative to the scene is not the stage's.
                    if (prim.Key == root.transform || !index.TryGetValue(prim.Key, out int i))
                    {
                        continue;
                    }

                    if (poses[i] != null)
                    {
                        var matrices = new float[timeCodes.Length * 16];
                        for (int f = 0; f < timeCodes.Length; f++)
                        {
                            UsdExporter.WriteUsdMatrix(matrices, f * 16, poses[i][f].Matrix);
                        }

                        UsdNative.SetXformSamples(context, prim.Value, timeCodes, matrices);
                    }

                    if (preserve && actives[i] != null)
                    {
                        var visible = new int[timeCodes.Length];
                        for (int f = 0; f < timeCodes.Length; f++)
                        {
                            visible[f] = actives[i][f] ? 1 : 0;
                        }

                        visibility[prim.Value] = visible;
                    }
                }
            }

            for (int r = 0; preserve && r < meshRenderers.Length; r++)
            {
                Renderer renderer = meshRenderers[r];
                if (renderer == null || !scene.MeshPaths.TryGetValue(renderer, out string meshPath))
                {
                    continue;
                }

                bool ownPrim = scene.XformPaths != null && renderer.transform != root.transform &&
                               scene.XformPaths.TryGetValue(renderer.transform, out string xformPath) && xformPath == meshPath;
                int i = index.TryGetValue(renderer.transform, out int found) ? found : -1;
                bool activeAnimates = ownPrim && i >= 0 && actives[i] != null;
                if (enables[r] == null && !activeAnimates)
                {
                    continue;
                }

                var visible = new int[timeCodes.Length];
                for (int f = 0; f < timeCodes.Length; f++)
                {
                    bool enabled = enables[r] != null ? enables[r][f] : firstEnabled[r];
                    bool active = !ownPrim || i < 0 || (actives[i] != null ? actives[i][f] : firstActive[i]);
                    visible[f] = enabled && active ? 1 : 0;
                }

                visibility[meshPath] = visible;
            }

            foreach (KeyValuePair<string, int[]> prim in visibility)
            {
                UsdNative.SetVisibilitySamples(context, prim.Key, timeCodes, prim.Value);
            }

            int sourcePointCaches = 0;
            for (int r = 0; r < streamed.Length; r++)
            {
                if (streamPoints[r] != null && streamed[r] != null && scene.MeshPaths.TryGetValue(streamed[r], out string meshPath))
                {
                    // An override recording leaves an imported mesh's points to its source: they are
                    // the source's points, not the vertices Unity split them into.
                    if (scene.SourceMeshes.Contains(streamed[r]))
                    {
                        sourcePointCaches++;
                        continue;
                    }

                    WritePoints(context, meshPath, timeCodes, streamPoints[r], streamNormals[r], settings.ExportNormals);
                }
            }

            if (sourcePointCaches > 0)
            {
                Debug.LogWarning("Unity USD Toolkit: " + sourcePointCaches + " imported mesh(es) whose points move were left to " +
                                 "their source stage: an override recording does not write an imported mesh's points.");
            }

            foreach (UsdExporter.ExportedSkeleton skeleton in scene.Skeletons)
            {
                WriteSkeleton(context, skeleton, timeCodes, index);
            }

            if (scene.BakedSkinned.Count > 0)
            {
                WriteBakedSkinning(context, scene, timeCodes, settings.ExportNormals);
            }
        }

        // Skinned meshes the export wrote as static meshes (all of them in an .abc, which has no
        // skeletons; elsewhere the ones UsdSkel cannot carry) are posed and baked again every
        // frame, and get their points sampled like a point cache. The caller puts the scene back.
        private void WriteBakedSkinning(IntPtr context, UsdExporter.ExportSceneInfo scene, double[] timeCodes, bool withNormals)
        {
            int count = scene.BakedSkinned.Count;
            var points = new List<Vector3[]>[count];
            var normals = new List<Vector3[]>[count];
            for (int b = 0; b < count; b++)
            {
                points[b] = new List<Vector3[]>(timeCodes.Length);
                normals[b] = new List<Vector3[]>(timeCodes.Length);
            }

            var baked = new Mesh();
            try
            {
                for (int f = 0; f < timeCodes.Length; f++)
                {
                    ApplyFrame(f);
                    for (int b = 0; b < count; b++)
                    {
                        scene.BakedSkinned[b].BakeMesh(baked);
                        points[b].Add(baked.vertices);
                        normals[b].Add(baked.normals);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }

            for (int b = 0; b < count; b++)
            {
                if (Moves(points[b]) && scene.MeshPaths.TryGetValue(scene.BakedSkinned[b].Renderer, out string meshPath))
                {
                    WritePoints(context, meshPath, timeCodes, points[b], normals[b], withNormals);
                }
            }
        }

        private static bool Moves(List<Vector3[]> frames)
        {
            for (int f = 1; f < frames.Count; f++)
            {
                if (frames[f].Length != frames[0].Length)
                {
                    return true;
                }

                for (int v = 0; v < frames[f].Length; v++)
                {
                    if (frames[f][v] != frames[0][v])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Points in USD's basis (X mirrored), the mesh's own space as PreserveHierarchy writes it;
        // normals too when every frame has one per point.
        private static void WritePoints(IntPtr context, string meshPath, double[] timeCodes, List<Vector3[]> frames,
                                        List<Vector3[]> normalFrames, bool withNormals)
        {
            int count = frames[0].Length;
            var points = new RUsdVec3[timeCodes.Length * count];
            bool normals = withNormals;
            for (int f = 0; f < timeCodes.Length; f++)
            {
                normals &= normalFrames[f] != null && normalFrames[f].Length == count;
                if (frames[f].Length != count)
                {
                    return; // the mesh changed under the recording; its points stay static
                }

                for (int v = 0; v < count; v++)
                {
                    Vector3 p = frames[f][v];
                    points[f * count + v] = new RUsdVec3(-p.x, p.y, p.z);
                }
            }

            RUsdVec3[] normalSamples = null;
            if (normals)
            {
                normalSamples = new RUsdVec3[points.Length];
                for (int f = 0; f < timeCodes.Length; f++)
                {
                    for (int v = 0; v < count; v++)
                    {
                        Vector3 n = normalFrames[f][v].normalized;
                        normalSamples[f * count + v] = new RUsdVec3(-n.x, n.y, n.z);
                    }
                }
            }

            UsdNative.SetPointsSamples(context, meshPath, timeCodes, points, normalSamples, count);
        }

        private void WriteSkeleton(IntPtr context, UsdExporter.ExportedSkeleton skeleton, double[] timeCodes,
                                   Dictionary<Transform, int> index)
        {
            Transform[] joints = skeleton.Joints;
            bool moves = false;
            foreach (Transform joint in joints)
            {
                moves |= index.TryGetValue(joint, out int i) && poses[i] != null;
            }

            var channelRenderer = new int[skeleton.ChannelNames.Count];
            for (int c = 0; c < channelRenderer.Length; c++)
            {
                channelRenderer[c] = Array.IndexOf(renderers, skeleton.ChannelRenderers[c]);
                List<float[]> track = channelRenderer[c] >= 0 ? weights[channelRenderer[c]] : null;
                for (int f = 1; track != null && f < track.Count && !moves; f++)
                {
                    moves |= track[f][skeleton.ChannelShapes[c]] != track[0][skeleton.ChannelShapes[c]];
                }
            }

            if (!moves)
            {
                return; // nothing animates: the rest pose and the static weights already say it all
            }

            var local = new float[timeCodes.Length * joints.Length * 16];
            for (int f = 0; f < timeCodes.Length; f++)
            {
                for (int j = 0; j < joints.Length; j++)
                {
                    Pose pose = index.TryGetValue(joints[j], out int i)
                        ? (poses[i] != null ? poses[i][f] : firstPoses[i])
                        : Pose.Of(joints[j]);
                    UsdExporter.WriteUsdMatrix(local, (f * joints.Length + j) * 16, pose.Matrix);
                }
            }

            int channels = skeleton.ChannelNames.Count;
            var channelWeights = new float[timeCodes.Length * channels];
            for (int f = 0; f < timeCodes.Length; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int r = channelRenderer[c];
                    float weight = r >= 0 && f < weights[r].Count && skeleton.ChannelShapes[c] < weights[r][f].Length
                        ? weights[r][f][skeleton.ChannelShapes[c]]
                        : 0f;
                    channelWeights[f * channels + c] = weight / skeleton.ChannelFullWeights[c];
                }
            }

            UsdNative.SetSkeletonAnimation(context, skeleton.SkeletonPath, skeleton.ChannelNames.ToArray(), timeCodes,
                                           local, channelWeights);
        }

        private Pose Last(int i) => poses[i] != null ? poses[i][poses[i].Count - 1] : firstPoses[i];

        private double NextStart() => times.Count == 0 ? 0.0 : times[times.Count - 1] + 1.0 / FramesPerSecond;

        private IEnumerable<double> FrameTimes(double length)
        {
            int frames = Math.Max(1, (int)Math.Floor(length * FramesPerSecond + 1e-6) + 1);
            for (int f = 0; f < frames; f++)
            {
                yield return f / FramesPerSecond;
            }
        }

        private void ApplyFrame(int frame)
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null)
                {
                    continue;
                }

                (poses[i] != null ? poses[i][frame] : firstPoses[i]).Apply(t);
                bool active = actives[i] != null ? actives[i][frame] : firstActive[i];
                if (t.gameObject.activeSelf != active)
                {
                    t.gameObject.SetActive(active);
                }
            }

            for (int r = 0; r < meshRenderers.Length; r++)
            {
                if (meshRenderers[r] != null)
                {
                    meshRenderers[r].enabled = enables[r] != null ? enables[r][frame] : firstEnabled[r];
                }
            }

            for (int r = 0; r < renderers.Length; r++)
            {
                if (renderers[r] != null && frame < weights[r].Count)
                {
                    float[] values = weights[r][frame];
                    for (int s = 0; s < values.Length; s++)
                    {
                        SetWeight(renderers[r], s, values[s]);
                    }
                }
            }
        }

        private Pose[] Snapshot()
        {
            var snapshot = new Pose[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    snapshot[i] = Pose.Of(transforms[i]);
                }
            }

            return snapshot;
        }

        private void Restore(Pose[] snapshot)
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    snapshot[i].Apply(transforms[i]);
                }
            }
        }

        private bool[] ActiveSnapshot()
        {
            var snapshot = new bool[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                snapshot[i] = transforms[i] != null && transforms[i].gameObject.activeSelf;
            }

            return snapshot;
        }

        private void RestoreActive(bool[] snapshot)
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].gameObject.activeSelf != snapshot[i])
                {
                    transforms[i].gameObject.SetActive(snapshot[i]);
                }
            }
        }

        private bool[] EnabledSnapshot()
        {
            var snapshot = new bool[meshRenderers.Length];
            for (int r = 0; r < meshRenderers.Length; r++)
            {
                snapshot[r] = meshRenderers[r] != null && meshRenderers[r].enabled;
            }

            return snapshot;
        }

        private void RestoreEnabled(bool[] snapshot)
        {
            for (int r = 0; r < meshRenderers.Length; r++)
            {
                if (meshRenderers[r] != null && meshRenderers[r].enabled != snapshot[r])
                {
                    meshRenderers[r].enabled = snapshot[r];
                }
            }
        }

        private List<float[]> WeightSnapshot()
        {
            var snapshot = new List<float[]>(renderers.Length);
            foreach (Renderer renderer in renderers)
            {
                snapshot.Add(WeightsOf(renderer));
            }

            return snapshot;
        }

        private void RestoreWeights(List<float[]> snapshot)
        {
            for (int r = 0; r < renderers.Length; r++)
            {
                if (renderers[r] == null)
                {
                    continue;
                }

                for (int s = 0; s < snapshot[r].Length; s++)
                {
                    SetWeight(renderers[r], s, snapshot[r][s]);
                }
            }
        }

        private static float[] WeightsOf(Renderer renderer)
        {
            Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                : renderer != null && renderer.TryGetComponent(out UsdDualQuaternionSkin skin) ? skin.SharedMesh : null;
            var values = new float[mesh != null ? mesh.blendShapeCount : 0];
            for (int s = 0; s < values.Length; s++)
            {
                values[s] = UsdExporter.SkinnedRenderer.WeightOf(renderer, s);
            }

            return values;
        }

        private static void SetWeight(Renderer renderer, int shape, float weight)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                skinned.SetBlendShapeWeight(shape, weight);
            }
            else if (renderer != null && renderer.TryGetComponent(out UsdDualQuaternionSkin skin))
            {
                skin.SetBlendShapeWeight(shape, weight);
            }
        }
    }
}
