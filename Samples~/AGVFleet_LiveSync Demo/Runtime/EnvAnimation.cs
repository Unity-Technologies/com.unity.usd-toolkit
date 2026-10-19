using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Plays the environment's own USD animation (Explorer Factory: the welding cell) on the imported
    /// hierarchy, at the time code Isaac's timeline is at.
    ///
    /// The usd-toolkit importer reads default values only, so Tools~/agv_fleet/anim_layer.py freezes the
    /// animation at its start frame for the import (root_unity.usda) and exports the keyframes to
    /// env_anim.json: per animated prim the USD local matrix at each authored key, per visibility
    /// attribute its (time, visible) steps. Keys are converted here exactly the way the importer
    /// converts a prim's local matrix (<see cref="UsdFrame.ImporterLocal"/>), so a key reproduces the
    /// imported pose; between keys translation/scale lerp and rotation slerp, as USD interpolates
    /// xformOps. Outside the key range the first/last key holds (USD semantics).
    /// </summary>
    public sealed class EnvAnimation
    {
        [Serializable]
        private class Doc
        {
            public string stamp;
            public string env;
            public double tcps;
            public double start;
            public double end;
            public double bake;
            public XfTrack[] xf;
            public VisTrack[] vis;
        }

        [Serializable]
        private class XfTrack
        {
            public string path;
            public float[] t;
            public float[] m;   // 16 per key, USD row-major local matrix
        }

        [Serializable]
        private class VisTrack
        {
            public string path;
            public float[] t;
            public int[] v;
        }

        private struct BoundXf
        {
            public Transform target;
            public float[] t;
            public Vector3[] pos;
            public Quaternion[] rot;
            public Vector3[] scale;
        }

        private struct BoundVis
        {
            public GameObject target;
            public float[] t;
            public int[] v;
        }

        private readonly List<BoundXf> xf = new List<BoundXf>();
        private readonly List<BoundVis> vis = new List<BoundVis>();

        public double TimeCodesPerSecond { get; private set; }
        public double Start { get; private set; }
        public double End { get; private set; }
        public double Bake { get; private set; }
        public int TrackCount => xf.Count + vis.Count;
        public int Missing { get; private set; }
        /// <summary>Every transform the animation moves or shows/hides (exclude from static batching).</summary>
        public readonly HashSet<Transform> Animated = new HashSet<Transform>();
        /// <summary>The animated assets: ancestor two levels below the environment (Factory:
        /// Samples/Welding_Assembly_Animated_Adjust). Their parts are not "overhead structure".</summary>
        public readonly HashSet<Transform> AssetRoots = new HashSet<Transform>();

        /// <summary>Loads env_anim.json and binds it to the imported environment (the default prim's object).</summary>
        public static EnvAnimation Load(string jsonPath, Transform env)
        {
            if (!env || !File.Exists(jsonPath)) return null;
            Doc doc = JsonUtility.FromJson<Doc>(File.ReadAllText(jsonPath));
            if (doc == null || ((doc.xf == null || doc.xf.Length == 0) && (doc.vis == null || doc.vis.Length == 0))) return null;
            var a = new EnvAnimation
            {
                TimeCodesPerSecond = doc.tcps > 0 ? doc.tcps : 24,
                Start = doc.start,
                End = doc.end,
                Bake = doc.bake,
            };
            foreach (XfTrack tr in doc.xf ?? Array.Empty<XfTrack>())
            {
                Transform target = env.Find(tr.path);
                int n = tr.t?.Length ?? 0;
                if (!target || n == 0 || tr.m == null || tr.m.Length != n * 16) { a.Missing++; continue; }
                var b = new BoundXf { target = target, t = tr.t, pos = new Vector3[n], rot = new Quaternion[n], scale = new Vector3[n] };
                for (int k = 0; k < n; k++)
                {
                    Matrix4x4 u = UsdFrame.ImporterLocal(tr.m, k * 16);
                    b.pos[k] = new Vector3(u.m03, u.m13, u.m23);
                    b.rot[k] = u.rotation;
                    b.scale[k] = u.lossyScale;
                }
                a.xf.Add(b);
                a.Animated.Add(target);
            }
            foreach (VisTrack tr in doc.vis ?? Array.Empty<VisTrack>())
            {
                Transform target = env.Find(tr.path);
                if (!target || tr.t == null || tr.t.Length == 0 || tr.v == null || tr.v.Length != tr.t.Length) { a.Missing++; continue; }
                a.vis.Add(new BoundVis { target = target.gameObject, t = tr.t, v = tr.v });
                a.Animated.Add(target);
            }
            foreach (Transform t in a.Animated)
            {
                Transform r = t;
                while (r.parent != null && r.parent != env && r.parent.parent != env) r = r.parent;
                if (r.parent != null && r.parent.parent == env) a.AssetRoots.Add(r);
            }
            return a;
        }

        /// <summary>Pose the environment at USD time code <paramref name="tc"/>.</summary>
        public void Evaluate(double tc)
        {
            float t = (float)tc;
            foreach (BoundXf b in xf)
            {
                if (!b.target) continue;
                int i = Segment(b.t, t, out float f);
                Transform tr = b.target;
                if (f <= 0f)
                {
                    tr.SetLocalPositionAndRotation(b.pos[i], b.rot[i]);
                    tr.localScale = b.scale[i];
                }
                else
                {
                    tr.SetLocalPositionAndRotation(Vector3.LerpUnclamped(b.pos[i], b.pos[i + 1], f),
                                                   Quaternion.Slerp(b.rot[i], b.rot[i + 1], f));
                    tr.localScale = Vector3.LerpUnclamped(b.scale[i], b.scale[i + 1], f);
                }
            }
            foreach (BoundVis b in vis)
            {
                if (!b.target) continue;
                int i = Segment(b.t, t, out _);   // visibility is held, not interpolated
                bool on = b.v[i] != 0;
                if (b.target.activeSelf != on) b.target.SetActive(on);
            }
        }

        /// <summary>Key index i with t[i] &lt;= time &lt; t[i+1] and the fraction f between them (0 when held).</summary>
        private static int Segment(float[] keys, float time, out float f)
        {
            f = 0f;
            int n = keys.Length;
            if (n == 1 || time <= keys[0]) return 0;
            if (time >= keys[n - 1]) return n - 1;
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid] <= time) lo = mid; else hi = mid;
            }
            f = (time - keys[lo]) / (keys[hi] - keys[lo]);
            return lo;
        }
    }
}
