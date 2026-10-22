using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    /// <summary>
    /// Turns the animation a <see cref="UsdAnimationPlayer"/> plays into a generic
    /// <see cref="AnimationClip"/>, for an Animator, the Animation window or a Timeline Animation
    /// track: prim and joint transforms, active states and blend-shape weights, as curves on the
    /// objects below the player, keyed at the stage's time samples. Clip time 0 is the stage's
    /// start time code and the clip is as long as the stage's time range.
    /// </summary>
    /// <remarks>
    /// The curves reproduce what the player poses. Positions, scales and weights are keyed with
    /// linear tangents, which is USD's linear interpolation exactly. Rotations are quaternion
    /// curves, which Unity blends component-wise and normalizes; where two samples are more than
    /// <see cref="MaxRotationStep"/> degrees apart, keys are added along the arc USD interpolates on,
    /// so the clip follows the same rotation between them. Active states are stepped, as USD holds
    /// visibility until the next sample. Mesh points that animate (point caches) have no curve
    /// type; they are left to the player.
    /// </remarks>
    public static class UsdAnimationClipBuilder
    {
        /// <summary>Degrees between rotation keys beyond which keys are added along the arc.</summary>
        public const float MaxRotationStep = 10f;

        /// <summary>
        /// A clip of <paramref name="player"/>'s animation, its curves bound to paths below the
        /// player's object. Null when the player has no data.
        /// </summary>
        public static AnimationClip Build(UsdAnimationPlayer player)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            UsdAnimationData data = player.Data;
            if (data == null)
            {
                return null;
            }

            var clip = new AnimationClip { name = data.name.Length > 0 ? data.name : player.name + " Animation" };
            clip.frameRate = (float)data.TimeCodesPerSecond;
            Transform root = player.transform;
            double start = data.StartTimeCode;
            double perSecond = data.TimeCodesPerSecond;
            float ToClip(double timeCode) => (float)((timeCode - start) / perSecond);

            var bindings = new List<EditorCurveBinding>();
            var curves = new List<AnimationCurve>();
            var ambiguous = new List<string>();
            string PathOf(Transform target)
            {
                string path = AnimationUtility.CalculateTransformPath(target, root);
                if (path.Length > 0 && root.Find(path) != target)
                {
                    ambiguous.Add(path);
                }

                return path;
            }

            void Add(string path, Type type, string property, AnimationCurve curve)
            {
                bindings.Add(EditorCurveBinding.FloatCurve(path, type, property));
                curves.Add(curve);
            }

            foreach (UsdAnimationData.TransformTrack track in data.TransformTracks)
            {
                Transform target = root.Find(track.Path);
                if (target != null && track.Times.Length > 0)
                {
                    AddTransformCurves(Add, PathOf(target), track.Times,
                                       track.Positions, track.Rotations, track.Scales, 0, 1, ToClip);
                }
            }

            foreach (UsdAnimationData.SkeletonTrack track in data.SkeletonTracks)
            {
                Transform node = root.Find(track.Path);
                UsdSkeleton skeleton = node != null ? node.GetComponent<UsdSkeleton>() : null;
                if (skeleton == null || track.Times.Length == 0)
                {
                    continue;
                }

                IReadOnlyList<Transform> joints = skeleton.Joints;
                for (int j = 0; j < track.JointCount && j < joints.Count; j++)
                {
                    if (joints[j] != null)
                    {
                        AddTransformCurves(Add, PathOf(joints[j]), track.Times,
                                           track.Positions, track.Rotations, track.Scales, j, track.JointCount, ToClip);
                    }
                }
            }

            foreach (UsdAnimationData.VisibilityTrack track in data.VisibilityTracks)
            {
                Transform target = root.Find(track.Path);
                if (target == null || track.Times.Length == 0)
                {
                    continue;
                }

                var keys = new Keyframe[track.Times.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = new Keyframe(ToClip(track.Times[i]), track.Visible[i] ? 1f : 0f,
                                           float.PositiveInfinity, float.PositiveInfinity);
                }

                Add(PathOf(target), typeof(GameObject), "m_IsActive", new AnimationCurve(keys));
            }

            foreach (UsdAnimationData.BlendShapeTrack track in data.BlendShapeTracks)
            {
                Transform target = root.Find(track.Path);
                SkinnedMeshRenderer renderer = target != null ? target.GetComponent<SkinnedMeshRenderer>() : null;
                Mesh mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null || track.Times.Length == 0)
                {
                    continue;
                }

                int shapes = track.ShapeIndices.Length;
                string path = PathOf(target);
                for (int s = 0; s < shapes; s++)
                {
                    int shape = track.ShapeIndices[s];
                    if (shape < 0 || shape >= mesh.blendShapeCount)
                    {
                        continue;
                    }

                    var values = new float[track.Times.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = track.Weights[i * shapes + s];
                    }

                    Add(path, typeof(SkinnedMeshRenderer), "blendShape." + mesh.GetBlendShapeName(shape),
                        LinearCurve(track.Times, values, ToClip));
                }
            }

            // Curves find their objects by name, so a sibling with the same name ahead of the
            // target takes its curves.
            if (ambiguous.Count > 0)
            {
                Debug.LogWarning("Unity USD Toolkit: " + ambiguous.Count + " object(s) below '" + player.name +
                                 "' share their path with an earlier sibling, so the clip animates that sibling instead (first: '" +
                                 ambiguous[0] + "'). Rename one of them, or play the animation with the UsdAnimationPlayer.");
            }

            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.startTime = 0f;
            settings.stopTime = (float)data.Duration;
            settings.loopTime = player.Loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        // Position, rotation and scale curves of one transform; element i of the value arrays is
        // sample i / stride's value when the arrays hold several transforms (`offset` picks which).
        private static void AddTransformCurves(
            Action<string, Type, string, AnimationCurve> add, string path, double[] times,
            Vector3[] positions, Quaternion[] rotations, Vector3[] scales, int offset, int stride, Func<double, float> toClip)
        {
            int count = times.Length;
            var position = new Vector3[count];
            var scale = new Vector3[count];
            var rotation = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                position[i] = positions[i * stride + offset];
                scale[i] = scales[i * stride + offset];
                rotation[i] = rotations[i * stride + offset];
            }

            for (int axis = 0; axis < 3; axis++)
            {
                add(path, typeof(Transform), "m_LocalPosition." + "xyz"[axis], LinearCurve(times, Component(position, axis), toClip));
                add(path, typeof(Transform), "m_LocalScale." + "xyz"[axis], LinearCurve(times, Component(scale, axis), toClip));
            }

            // Keys along the arc where samples are far apart: the curves blend between keys
            // component-wise, which only stays on USD's arc over short steps.
            var keyTimes = new List<float>(count);
            var keyRotations = new List<Quaternion>(count);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    Quaternion previous = rotation[i - 1];
                    float angle = Quaternion.Angle(previous, rotation[i]);
                    int steps = Mathf.CeilToInt(angle / MaxRotationStep);
                    for (int step = 1; step < steps; step++)
                    {
                        float t = (float)step / steps;
                        keyTimes.Add(Mathf.Lerp(toClip(times[i - 1]), toClip(times[i]), t));
                        keyRotations.Add(Quaternion.SlerpUnclamped(previous, rotation[i], t));
                    }
                }

                keyTimes.Add(toClip(times[i]));
                keyRotations.Add(rotation[i]);
            }

            for (int c = 0; c < 4; c++)
            {
                var values = new float[keyRotations.Count];
                for (int k = 0; k < values.Length; k++)
                {
                    values[k] = keyRotations[k][c];
                }

                add(path, typeof(Transform), "m_LocalRotation." + "xyzw"[c], LinearCurve(keyTimes, values));
            }
        }

        private static float[] Component(Vector3[] values, int axis)
        {
            var result = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                result[i] = values[i][axis];
            }

            return result;
        }

        private static AnimationCurve LinearCurve(double[] times, float[] values, Func<double, float> toClip)
        {
            var clipTimes = new List<float>(times.Length);
            foreach (double time in times)
            {
                clipTimes.Add(toClip(time));
            }

            return LinearCurve(clipTimes, values);
        }

        // Keys whose tangents are the slopes to their neighbours: Unity's Hermite segments between
        // them are then straight lines. A curve that never changes is one key.
        private static AnimationCurve LinearCurve(List<float> times, float[] values)
        {
            bool constant = true;
            for (int i = 1; i < values.Length && constant; i++)
            {
                constant = values[i] == values[0];
            }

            if (constant)
            {
                return new AnimationCurve(new Keyframe(times[0], values[0]));
            }

            var keys = new Keyframe[values.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                float into = i > 0 ? Slope(times[i - 1], values[i - 1], times[i], values[i]) : 0f;
                float outOf = i + 1 < keys.Length ? Slope(times[i], values[i], times[i + 1], values[i + 1]) : 0f;
                keys[i] = new Keyframe(times[i], values[i], i > 0 ? into : outOf, i + 1 < keys.Length ? outOf : into);
            }

            return new AnimationCurve(keys);
        }

        private static float Slope(float t0, float v0, float t1, float v1) => t1 > t0 ? (v1 - v0) / (t1 - t0) : 0f;
    }
}
