using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// The lanes AGVs may drive: nodes (USD x, y in meters) joined by straight, two-way edges.
    /// </summary>
    [Serializable]
    public class RouteGraph
    {
        public List<Vector2> nodes = new List<Vector2>();
        public List<Vector2Int> edges = new List<Vector2Int>();

        private const float MergeDistance = 0.01f;

        /// <summary>Index of the node at <paramref name="p"/>, added when there is none yet.</summary>
        public int AddNode(Vector2 p)
        {
            int i = FindNode(p);
            if (i >= 0) return i;
            nodes.Add(p);
            return nodes.Count - 1;
        }

        public int FindNode(Vector2 p)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if ((nodes[i] - p).sqrMagnitude < MergeDistance * MergeDistance) return i;
            }
            return -1;
        }

        public void AddEdge(int a, int b)
        {
            if (a == b) return;
            var e = new Vector2Int(Mathf.Min(a, b), Mathf.Max(a, b));
            if (!edges.Contains(e)) edges.Add(e);
        }

        public void AddEdge(Vector2 a, Vector2 b) => AddEdge(AddNode(a), AddNode(b));

        public int NearestNode(Vector2 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                float d = (nodes[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public List<int>[] Adjacency()
        {
            var adj = new List<int>[nodes.Count];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
            foreach (Vector2Int e in edges)
            {
                adj[e.x].Add(e.y);
                adj[e.y].Add(e.x);
            }
            return adj;
        }

        /// <summary>Number of nodes reachable from <paramref name="start"/> (all of them when connected).</summary>
        public int ReachableCount(int start)
        {
            if (start < 0 || start >= nodes.Count) return 0;
            List<int>[] adj = Adjacency();
            var seen = new bool[nodes.Count];
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            int n = 0;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                n++;
                foreach (int j in adj[i])
                {
                    if (!seen[j]) { seen[j] = true; stack.Push(j); }
                }
            }
            return n;
        }

        public bool IsConnected => nodes.Count > 0 && ReachableCount(0) == nodes.Count;

        /// <summary>Shortest node sequence from <paramref name="from"/> to <paramref name="to"/> (Dijkstra); empty when unreachable.</summary>
        public List<int> ShortestPath(int from, int to)
        {
            var path = new List<int>();
            if (from < 0 || to < 0) return path;
            List<int>[] adj = Adjacency();
            var dist = new float[nodes.Count];
            var prev = new int[nodes.Count];
            var done = new bool[nodes.Count];
            for (int i = 0; i < dist.Length; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
            dist[from] = 0f;
            // the graphs here have a few hundred nodes: a linear scan is fast and keeps ties deterministic
            for (int k = 0; k < nodes.Count; k++)
            {
                int u = -1;
                for (int i = 0; i < dist.Length; i++)
                {
                    if (!done[i] && dist[i] < float.MaxValue && (u < 0 || dist[i] < dist[u])) u = i;
                }
                if (u < 0 || u == to) break;
                done[u] = true;
                foreach (int v in adj[u])
                {
                    float d = dist[u] + Vector2.Distance(nodes[u], nodes[v]);
                    if (d < dist[v] - 1e-5f) { dist[v] = d; prev[v] = u; }
                }
            }
            if (dist[to] == float.MaxValue) return path;
            for (int i = to; i >= 0; i = prev[i]) path.Add(i);
            path.Reverse();
            return path;
        }

        /// <summary>Distance from <paramref name="p"/> to the segment a-b.</summary>
        public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector2.Distance(p, a + t * ab);
        }

        /// <summary>Shortest distance between edge a-b and the (rotated) box, 0 when they touch.</summary>
        public static float SegmentBoxDistance(Vector2 a, Vector2 b, StaticBox box)
        {
            // into the box frame, where the box is axis aligned
            float rad = -box.yawDeg * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            Vector2 ToBox(Vector2 p)
            {
                Vector2 d = p - box.center;
                return new Vector2(c * d.x - s * d.y, s * d.x + c * d.y);
            }
            Vector2 la = ToBox(a), lb = ToBox(b), h = box.size * 0.5f;
            // sample-free: distance of a segment to an AABB is the min over the segment of the point distance,
            // a convex function -- ternary search is exact enough for clearance checks
            float PointDist(float t)
            {
                Vector2 p = Vector2.Lerp(la, lb, t);
                float dx = Mathf.Max(Mathf.Abs(p.x) - h.x, 0f), dy = Mathf.Max(Mathf.Abs(p.y) - h.y, 0f);
                return Mathf.Sqrt(dx * dx + dy * dy);
            }
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 60; i++)
            {
                float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
                if (PointDist(m1) <= PointDist(m2)) hi = m2; else lo = m1;
            }
            return Mathf.Min(PointDist((lo + hi) * 0.5f), Mathf.Min(PointDist(0f), PointDist(1f)));
        }
    }
}
