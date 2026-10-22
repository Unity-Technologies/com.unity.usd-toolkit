using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// The animation of an imported stage, as its time samples: animated transforms and visibility
    /// of prims, skeleton poses, blend-shape weights and animated mesh points (point caches). A
    /// <see cref="UsdAnimationPlayer"/> on the import root plays it. Samples are kept at the times
    /// the stage authors them, not resampled, and are interpolated the way USD interpolates them:
    /// positions, scales, weights and points linearly, rotations along the shorter arc, visibility
    /// held.
    /// Paths are relative to the player's object, as Transform.Find takes them.
    /// </summary>
    public sealed class UsdAnimationData : ScriptableObject
    {
        /// <summary>An animated prim transform, as local position, rotation and scale.</summary>
        [Serializable]
        public sealed class TransformTrack
        {
            public string Path;
            public double[] Times;
            public Vector3[] Positions;
            public Quaternion[] Rotations;
            public Vector3[] Scales;
        }

        /// <summary>Animated visibility of a prim; a sample holds until the next one.</summary>
        [Serializable]
        public sealed class VisibilityTrack
        {
            public string Path;
            public double[] Times;
            public bool[] Visible;
        }

        /// <summary>
        /// A skeleton's animation: every joint's local transform at every sample, the joints of the
        /// first sample first. The joints are those of the <see cref="UsdSkeleton"/> at Path.
        /// </summary>
        [Serializable]
        public sealed class SkeletonTrack
        {
            public string Path;
            public int JointCount;
            public double[] Times;
            public Vector3[] Positions;
            public Quaternion[] Rotations;
            public Vector3[] Scales;
        }

        /// <summary>
        /// Blend-shape weights of a <see cref="SkinnedMeshRenderer"/> at Path, in Unity's 0-100 range:
        /// the shapes listed in ShapeIndices at every sample, the first sample first.
        /// </summary>
        [Serializable]
        public sealed class BlendShapeTrack
        {
            public string Path;
            public int[] ShapeIndices;
            public double[] Times;
            public float[] Weights;
        }

        /// <summary>
        /// Animated points of the mesh on the <see cref="MeshFilter"/> at Path: every vertex of the
        /// imported mesh at every sample, the first sample first, in the mesh's own space and vertex
        /// order. Normals are laid out the same way, or empty when the mesh keeps the normals it was
        /// imported with (authored ones that do not animate). Bounds encloses every sample.
        /// </summary>
        [Serializable]
        public sealed class PointCacheTrack
        {
            public string Path;
            public int VertexCount;
            public double[] Times;
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Bounds Bounds;
        }

        [SerializeField] private double startTimeCode;
        [SerializeField] private double endTimeCode;
        [SerializeField] private double timeCodesPerSecond = 24.0;
        [SerializeField] private List<TransformTrack> transformTracks = new List<TransformTrack>();
        [SerializeField] private List<VisibilityTrack> visibilityTracks = new List<VisibilityTrack>();
        [SerializeField] private List<SkeletonTrack> skeletonTracks = new List<SkeletonTrack>();
        [SerializeField] private List<BlendShapeTrack> blendShapeTracks = new List<BlendShapeTrack>();
        [SerializeField] private List<PointCacheTrack> pointCacheTracks = new List<PointCacheTrack>();

        /// <summary>The stage time code playback starts at (its startTimeCode, or the first sample).</summary>
        public double StartTimeCode => startTimeCode;

        /// <summary>The stage time code playback ends at.</summary>
        public double EndTimeCode => endTimeCode;

        /// <summary>The stage's timeCodesPerSecond.</summary>
        public double TimeCodesPerSecond => timeCodesPerSecond;

        /// <summary>Length in seconds.</summary>
        public double Duration => Math.Max(0.0, (endTimeCode - startTimeCode) / timeCodesPerSecond);

        public IReadOnlyList<TransformTrack> TransformTracks => transformTracks;

        public IReadOnlyList<VisibilityTrack> VisibilityTracks => visibilityTracks;

        public IReadOnlyList<SkeletonTrack> SkeletonTracks => skeletonTracks;

        public IReadOnlyList<BlendShapeTrack> BlendShapeTracks => blendShapeTracks;

        public IReadOnlyList<PointCacheTrack> PointCacheTracks => pointCacheTracks;

        /// <summary>Whether there is anything to play.</summary>
        public bool IsEmpty => transformTracks.Count == 0 && visibilityTracks.Count == 0 &&
                               skeletonTracks.Count == 0 && blendShapeTracks.Count == 0 && pointCacheTracks.Count == 0;

        /// <summary>The time code at <paramref name="seconds"/> after the start.</summary>
        public double TimeCodeAt(double seconds) => startTimeCode + seconds * timeCodesPerSecond;

        internal void SetTimeRange(double start, double end, double codesPerSecond)
        {
            startTimeCode = start;
            endTimeCode = Math.Max(start, end);
            timeCodesPerSecond = codesPerSecond > 0.0 && !double.IsInfinity(codesPerSecond) ? codesPerSecond : 24.0;
        }

        internal void Add(TransformTrack track) => transformTracks.Add(track);

        internal void Add(VisibilityTrack track) => visibilityTracks.Add(track);

        internal void Add(SkeletonTrack track) => skeletonTracks.Add(track);

        internal void Add(BlendShapeTrack track) => blendShapeTracks.Add(track);

        internal void Add(PointCacheTrack track) => pointCacheTracks.Add(track);

        // A live switch rebuilt the parts of the import at `paths` (relative to the root, as track
        // paths are): their tracks go, and the rebuilt parts' come in from `other`.
        internal void RemoveTracksUnder(IReadOnlyList<string> paths)
        {
            bool Under(string path)
            {
                foreach (string prefix in paths)
                {
                    if (path == prefix || (path != null && path.StartsWith(prefix + "/", StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }

                return false;
            }

            transformTracks.RemoveAll(track => Under(track.Path));
            visibilityTracks.RemoveAll(track => Under(track.Path));
            skeletonTracks.RemoveAll(track => Under(track.Path));
            blendShapeTracks.RemoveAll(track => Under(track.Path));
            pointCacheTracks.RemoveAll(track => Under(track.Path));
        }

        internal void AddTracksFrom(UsdAnimationData other)
        {
            transformTracks.AddRange(other.transformTracks);
            visibilityTracks.AddRange(other.visibilityTracks);
            skeletonTracks.AddRange(other.skeletonTracks);
            blendShapeTracks.AddRange(other.blendShapeTracks);
            pointCacheTracks.AddRange(other.pointCacheTracks);
        }

        // The sample at or before `time` and how far `time` is towards the next one (0 to 1).
        // Before the first sample the first holds, after the last the last.
        internal static int Segment(double[] times, double time, out float fraction)
        {
            fraction = 0f;
            if (times == null || times.Length == 0)
            {
                return -1;
            }

            if (time <= times[0])
            {
                return 0;
            }

            int last = times.Length - 1;
            if (time >= times[last])
            {
                return last;
            }

            int index = Array.BinarySearch(times, time);
            if (index >= 0)
            {
                return index;
            }

            int before = ~index - 1;
            double span = times[before + 1] - times[before];
            fraction = span > 0.0 ? (float)((time - times[before]) / span) : 0f;
            return before;
        }
    }
}
