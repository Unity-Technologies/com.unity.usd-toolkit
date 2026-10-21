using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Plays a <see cref="UsdAnimationData"/>: the importer adds one to the root of a stage that
    /// has animated transforms, visibility, skeletons, blend-shape weights or mesh points. It runs
    /// in players and the Editor alike, with no Animator, AnimationClip or Timeline involved.
    /// <see cref="Evaluate"/> poses everything at a time; <see cref="Play"/> advances that time
    /// every frame (Play Mode and players), and a Timeline track can drive it instead.
    /// Animated mesh points are drawn through each renderer's
    /// <see cref="MeshRenderer.additionalVertexStreams"/>, so the imported Mesh itself is never
    /// changed and instances sharing it can play at different times.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class UsdAnimationPlayer : MonoBehaviour
    {
        [SerializeField] private UsdAnimationData data;
        [Tooltip("Start playing when the scene starts (Play Mode and players only).")]
        [SerializeField] private bool playOnStart = true;
        [SerializeField] private bool loop = true;
        [SerializeField] private float speed = 1f;
        [Tooltip("Seconds from the animation's start.")]
        [SerializeField] private double time;

        private bool playing;
        private Transform[] transformTargets;
        private GameObject[] visibilityTargets;
        private UsdSkeleton[] skeletonTargets;
        private Component[] blendShapeTargets;
        private MeshRenderer[] pointCacheTargets;
        private Mesh[] pointCacheStreams;
        private Vector3[] pointScratch;
        private Vector3[] normalScratch;
        private UsdAnimationData boundData;

        /// <summary>The animation this player plays.</summary>
        public UsdAnimationData Data
        {
            get => data;
            set
            {
                data = value;
                boundData = null;
            }
        }

        /// <summary>Starts playback when the scene starts. Play Mode and players only.</summary>
        public bool PlayOnStart
        {
            get => playOnStart;
            set => playOnStart = value;
        }

        /// <summary>Wraps to the start at the end instead of stopping there.</summary>
        public bool Loop
        {
            get => loop;
            set => loop = value;
        }

        /// <summary>Playback rate; 1 is the stage's own timeCodesPerSecond.</summary>
        public float Speed
        {
            get => speed;
            set => speed = value;
        }

        /// <summary>Whether <see cref="Play"/> is advancing the time.</summary>
        public bool IsPlaying => playing;

        /// <summary>Seconds from the animation's start. Setting it poses everything at that time.</summary>
        public double CurrentTime
        {
            get => time;
            set => Evaluate(value);
        }

        /// <summary>Length in seconds; 0 without data.</summary>
        public double Duration => data != null ? data.Duration : 0.0;

        public void Play() => playing = data != null;

        public void Pause() => playing = false;

        /// <summary>Stops and returns to the start.</summary>
        public void Stop()
        {
            playing = false;
            Evaluate(0.0);
        }

        /// <summary>Poses every animated object at <paramref name="seconds"/> from the start.</summary>
        public void Evaluate(double seconds)
        {
            time = seconds;
            if (data != null)
            {
                EvaluateAtTimeCode(data.TimeCodeAt(seconds));
            }
        }

        /// <summary>Poses every animated object at a stage time code.</summary>
        public void EvaluateAtTimeCode(double timeCode)
        {
            if (data == null)
            {
                return;
            }

            Bind();
            for (int i = 0; i < transformTargets.Length; i++)
            {
                Transform target = transformTargets[i];
                UsdAnimationData.TransformTrack track = data.TransformTracks[i];
                int sample = UsdAnimationData.Segment(track.Times, timeCode, out float t);
                if (target == null || sample < 0)
                {
                    continue;
                }

                int next = Math.Min(sample + 1, track.Times.Length - 1);
                target.localPosition = Vector3.LerpUnclamped(track.Positions[sample], track.Positions[next], t);
                target.localRotation = Quaternion.SlerpUnclamped(track.Rotations[sample], track.Rotations[next], t);
                target.localScale = Vector3.LerpUnclamped(track.Scales[sample], track.Scales[next], t);
            }

            for (int i = 0; i < visibilityTargets.Length; i++)
            {
                UsdAnimationData.VisibilityTrack track = data.VisibilityTracks[i];
                int sample = UsdAnimationData.Segment(track.Times, timeCode, out _);
                if (visibilityTargets[i] != null && sample >= 0 && visibilityTargets[i].activeSelf != track.Visible[sample])
                {
                    visibilityTargets[i].SetActive(track.Visible[sample]);
                }
            }

            for (int i = 0; i < skeletonTargets.Length; i++)
            {
                UsdSkeleton skeleton = skeletonTargets[i];
                UsdAnimationData.SkeletonTrack track = data.SkeletonTracks[i];
                int sample = UsdAnimationData.Segment(track.Times, timeCode, out float t);
                if (skeleton == null || sample < 0)
                {
                    continue;
                }

                int next = Math.Min(sample + 1, track.Times.Length - 1);
                Transform[] joints = skeleton.JointArray;
                int count = Math.Min(track.JointCount, joints.Length);
                int a = sample * track.JointCount;
                int b = next * track.JointCount;
                for (int j = 0; j < count; j++)
                {
                    Transform joint = joints[j];
                    if (joint == null)
                    {
                        continue;
                    }

                    joint.localPosition = Vector3.LerpUnclamped(track.Positions[a + j], track.Positions[b + j], t);
                    joint.localRotation = Quaternion.SlerpUnclamped(track.Rotations[a + j], track.Rotations[b + j], t);
                    joint.localScale = Vector3.LerpUnclamped(track.Scales[a + j], track.Scales[b + j], t);
                }
            }

            for (int i = 0; i < blendShapeTargets.Length; i++)
            {
                Component target = blendShapeTargets[i];
                UsdAnimationData.BlendShapeTrack track = data.BlendShapeTracks[i];
                int sample = UsdAnimationData.Segment(track.Times, timeCode, out float t);
                if (target == null || sample < 0)
                {
                    continue;
                }

                var skinned = target as SkinnedMeshRenderer;
                var dualQuaternion = target as UsdDualQuaternionSkin;
                int next = Math.Min(sample + 1, track.Times.Length - 1);
                int shapes = track.ShapeIndices.Length;
                for (int s = 0; s < shapes; s++)
                {
                    if (track.ShapeIndices[s] < 0)
                    {
                        continue;
                    }

                    float weight = Mathf.LerpUnclamped(track.Weights[sample * shapes + s], track.Weights[next * shapes + s], t);
                    if (skinned != null)
                    {
                        skinned.SetBlendShapeWeight(track.ShapeIndices[s], weight);
                    }
                    else
                    {
                        dualQuaternion?.SetBlendShapeWeight(track.ShapeIndices[s], weight);
                    }
                }
            }

            for (int i = 0; i < pointCacheTargets.Length; i++)
            {
                EvaluatePointCache(i, timeCode);
            }
        }

        /// <summary>
        /// Stops drawing animated mesh points: every renderer the point caches drew into shows its
        /// imported Mesh again, as at the read time, until the next evaluation.
        /// </summary>
        public void RestoreMeshes()
        {
            if (pointCacheTargets == null || pointCacheStreams == null)
            {
                return;
            }

            for (int i = 0; i < pointCacheTargets.Length; i++)
            {
                MeshRenderer renderer = pointCacheTargets[i];
                if (renderer != null && pointCacheStreams[i] != null && renderer.additionalVertexStreams == pointCacheStreams[i])
                {
                    renderer.additionalVertexStreams = null;
                    renderer.ResetLocalBounds();
                }
            }
        }

        // The track's points at `timeCode`, linearly between samples, into a vertex stream of the
        // renderer's own: positions, and normals when the track has them.
        private void EvaluatePointCache(int index, double timeCode)
        {
            MeshRenderer renderer = pointCacheTargets[index];
            UsdAnimationData.PointCacheTrack track = data.PointCacheTracks[index];
            int sample = UsdAnimationData.Segment(track.Times, timeCode, out float t);
            MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            int count = track.VertexCount;
            if (filter == null || filter.sharedMesh == null || filter.sharedMesh.vertexCount != count || sample < 0 ||
                track.Positions.Length < track.Times.Length * count)
            {
                return;
            }

            int next = Math.Min(sample + 1, track.Times.Length - 1);
            if (pointScratch == null || pointScratch.Length != count)
            {
                pointScratch = new Vector3[count];
                normalScratch = new Vector3[count];
            }

            int a = sample * count;
            int b = next * count;
            for (int v = 0; v < count; v++)
            {
                pointScratch[v] = Vector3.LerpUnclamped(track.Positions[a + v], track.Positions[b + v], t);
            }

            Mesh stream = pointCacheStreams[index];
            if (stream == null)
            {
                stream = new Mesh { name = filter.sharedMesh.name + " (point cache)", hideFlags = HideFlags.DontSave };
                stream.MarkDynamic();
                pointCacheStreams[index] = stream;
            }

            stream.SetVertices(pointScratch);
            if (track.Normals != null && track.Normals.Length >= track.Times.Length * count)
            {
                for (int v = 0; v < count; v++)
                {
                    normalScratch[v] = Vector3.LerpUnclamped(track.Normals[a + v], track.Normals[b + v], t).normalized;
                }

                stream.SetNormals(normalScratch);
            }

            renderer.additionalVertexStreams = stream;
            renderer.localBounds = track.Bounds;
        }

        /// <summary>
        /// Adds every object the animation drives: transforms (prims and joints), objects whose
        /// active state it toggles, and renderers whose blend-shape weights it sets. A Timeline
        /// preview registers these so it can restore them afterwards.
        /// </summary>
        public void CollectAnimatedTargets(List<Transform> transforms, List<GameObject> toggled,
                                           List<SkinnedMeshRenderer> blendShapeRenderers)
        {
            List<Component> components = blendShapeRenderers != null ? new List<Component>() : null;
            CollectAnimatedTargets(transforms, toggled, components);
            if (components != null)
            {
                foreach (Component component in components)
                {
                    if (component is SkinnedMeshRenderer renderer)
                    {
                        blendShapeRenderers.Add(renderer);
                    }
                }
            }
        }

        /// <summary>
        /// Adds every object the animation drives: transforms (prims and joints), objects whose
        /// active state it toggles, and the components whose state it sets -- SkinnedMeshRenderers
        /// and <see cref="UsdDualQuaternionSkin"/>s whose blend-shape weights it animates, and the
        /// MeshRenderers point caches draw into.
        /// </summary>
        public void CollectAnimatedTargets(List<Transform> transforms, List<GameObject> toggled, List<Component> components)
        {
            if (data == null)
            {
                return;
            }

            Bind();
            foreach (Transform target in transformTargets)
            {
                if (target != null)
                {
                    transforms?.Add(target);
                }
            }

            foreach (UsdSkeleton skeleton in skeletonTargets)
            {
                if (skeleton == null)
                {
                    continue;
                }

                foreach (Transform joint in skeleton.JointArray)
                {
                    if (joint != null)
                    {
                        transforms?.Add(joint);
                    }
                }
            }

            foreach (GameObject target in visibilityTargets)
            {
                if (target != null)
                {
                    toggled?.Add(target);
                }
            }

            foreach (Component target in blendShapeTargets)
            {
                if (target != null)
                {
                    components?.Add(target);
                }
            }

            foreach (MeshRenderer renderer in pointCacheTargets)
            {
                if (renderer != null)
                {
                    components?.Add(renderer);
                }
            }
        }

        // The local pose the animation gives `target` at the current time, without applying it;
        // false when no transform track drives it. The Preserve export uses this to tell a pose
        // playback produced from one the user made.
        internal bool TryGetAnimatedPose(Transform target, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = default;
            rotation = Quaternion.identity;
            scale = Vector3.one;
            if (data == null || target == null)
            {
                return false;
            }

            Bind();
            double timeCode = data.TimeCodeAt(time);
            for (int i = 0; i < transformTargets.Length; i++)
            {
                if (transformTargets[i] != target)
                {
                    continue;
                }

                UsdAnimationData.TransformTrack track = data.TransformTracks[i];
                int sample = UsdAnimationData.Segment(track.Times, timeCode, out float t);
                if (sample < 0)
                {
                    return false;
                }

                int next = Math.Min(sample + 1, track.Times.Length - 1);
                position = Vector3.LerpUnclamped(track.Positions[sample], track.Positions[next], t);
                rotation = Quaternion.SlerpUnclamped(track.Rotations[sample], track.Rotations[next], t);
                scale = Vector3.LerpUnclamped(track.Scales[sample], track.Scales[next], t);
                return true;
            }

            return false;
        }

        // Whether the animation shows `target` at the current time; false when no visibility track
        // drives it.
        internal bool TryGetAnimatedVisibility(GameObject target, out bool visible)
        {
            visible = true;
            if (data == null || target == null)
            {
                return false;
            }

            Bind();
            double timeCode = data.TimeCodeAt(time);
            for (int i = 0; i < visibilityTargets.Length; i++)
            {
                if (visibilityTargets[i] != target)
                {
                    continue;
                }

                UsdAnimationData.VisibilityTrack track = data.VisibilityTracks[i];
                int sample = UsdAnimationData.Segment(track.Times, timeCode, out _);
                if (sample < 0)
                {
                    return false;
                }

                visible = track.Visible[sample];
                return true;
            }

            return false;
        }

        private void Start()
        {
            if (Application.isPlaying && playOnStart)
            {
                Play();
            }
        }

        private void Update()
        {
            if (!playing || data == null || !Application.isPlaying)
            {
                return;
            }

            double next = time + UnityEngine.Time.deltaTime * speed;
            double duration = data.Duration;
            if (duration <= 0.0)
            {
                next = 0.0;
            }
            else if (next > duration || next < 0.0)
            {
                if (loop)
                {
                    next -= Math.Floor(next / duration) * duration;
                }
                else
                {
                    next = Math.Max(0.0, Math.Min(duration, next));
                    playing = false;
                }
            }

            Evaluate(next);
        }

        private void OnValidate()
        {
            boundData = null;
        }

        private void OnDisable()
        {
            RestoreMeshes();
        }

        private void OnDestroy()
        {
            ReleaseStreams();
        }

        private void ReleaseStreams()
        {
            RestoreMeshes();
            if (pointCacheStreams == null)
            {
                return;
            }

            foreach (Mesh stream in pointCacheStreams)
            {
                if (stream == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Destroy(stream);
                }
                else
                {
                    DestroyImmediate(stream);
                }
            }

            pointCacheStreams = null;
        }

        // Resolves track paths to objects once per data; they are relative to this object.
        private void Bind()
        {
            if (boundData == data && transformTargets != null)
            {
                return;
            }

            boundData = data;
            transformTargets = new Transform[data.TransformTracks.Count];
            for (int i = 0; i < transformTargets.Length; i++)
            {
                transformTargets[i] = transform.Find(data.TransformTracks[i].Path);
            }

            visibilityTargets = new GameObject[data.VisibilityTracks.Count];
            for (int i = 0; i < visibilityTargets.Length; i++)
            {
                Transform target = transform.Find(data.VisibilityTracks[i].Path);
                visibilityTargets[i] = target != null ? target.gameObject : null;
            }

            skeletonTargets = new UsdSkeleton[data.SkeletonTracks.Count];
            for (int i = 0; i < skeletonTargets.Length; i++)
            {
                Transform target = transform.Find(data.SkeletonTracks[i].Path);
                skeletonTargets[i] = target != null ? target.GetComponent<UsdSkeleton>() : null;
            }

            blendShapeTargets = new Component[data.BlendShapeTracks.Count];
            for (int i = 0; i < blendShapeTargets.Length; i++)
            {
                Transform target = transform.Find(data.BlendShapeTracks[i].Path);
                if (target == null)
                {
                    continue;
                }

                blendShapeTargets[i] = target.GetComponent<SkinnedMeshRenderer>();
                if (blendShapeTargets[i] == null)
                {
                    blendShapeTargets[i] = target.GetComponent<UsdDualQuaternionSkin>();
                }
            }

            ReleaseStreams();
            pointCacheTargets = new MeshRenderer[data.PointCacheTracks.Count];
            pointCacheStreams = new Mesh[pointCacheTargets.Length];
            for (int i = 0; i < pointCacheTargets.Length; i++)
            {
                Transform target = transform.Find(data.PointCacheTracks[i].Path);
                pointCacheTargets[i] = target != null ? target.GetComponent<MeshRenderer>() : null;
            }
        }
    }
}
