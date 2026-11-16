using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    // physics:approximation = "convexDecomposition" without a decomposer: Unity cooks one convex
    // hull per MeshCollider, so the collision mesh is cut into pieces, each its own convex
    // collider. The cut is by connected parts first (separate shells the author modelled as one
    // mesh, which is most of what a decomposition separates on a CAD asset), then, for a single
    // part that fills less than half its bounds, by halving along the longest axis. It is not
    // V-HACD: a piece's hull can still cover a concavity the part has inside it.
    internal static class UsdConvexPieces
    {
        internal const int DefaultMaxPieces = 8;

        // A piece needs this many distinct points for PhysX to cook a hull with volume.
        private const int MinPiecePoints = 4;

        // A part filling less of its bounds than this is split further.
        private const float SolidRatio = 0.5f;

        // Below this many triangles a part is kept whole.
        private const int MinSplitTriangles = 64;

        internal static List<Mesh> Split(Mesh mesh, int maxPieces = DefaultMaxPieces)
        {
            var pieces = new List<Mesh>();
            if (mesh == null)
            {
                return pieces;
            }

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            if (triangles.Length < 3 || maxPieces <= 1)
            {
                pieces.Add(mesh);
                return pieces;
            }

            List<List<int>> parts = ConnectedParts(vertices, triangles, mesh.bounds);
            parts.Sort((a, b) => b.Count.CompareTo(a.Count));

            // More parts than pieces: the smallest are merged into one piece.
            if (parts.Count > maxPieces)
            {
                var rest = new List<int>();
                for (int i = maxPieces - 1; i < parts.Count; i++)
                {
                    rest.AddRange(parts[i]);
                }

                parts.RemoveRange(maxPieces - 1, parts.Count - (maxPieces - 1));
                parts.Add(rest);
            }
            else if (parts.Count == 1 && triangles.Length / 3 >= MinSplitTriangles &&
                     FillRatio(vertices, triangles, parts[0]) < SolidRatio)
            {
                parts = Bisect(vertices, triangles, parts[0], Mathf.Min(maxPieces, 4));
            }

            foreach (List<int> part in parts)
            {
                Mesh piece = PieceMesh(mesh.name, vertices, triangles, part, pieces.Count);
                if (piece != null)
                {
                    pieces.Add(piece);
                }
            }

            if (pieces.Count == 0)
            {
                pieces.Add(mesh);
            }

            return pieces;
        }

        // The mesh's enclosed volume in its own units (0 for an open or empty mesh).
        internal static float Volume(Mesh mesh)
        {
            if (mesh == null)
            {
                return 0f;
            }

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            return Mathf.Abs(SignedVolume(vertices, triangles, null));
        }

        private static float SignedVolume(Vector3[] vertices, int[] triangles, List<int> subset)
        {
            double sum = 0.0;
            int count = subset?.Count ?? triangles.Length / 3;
            for (int i = 0; i < count; i++)
            {
                int t = subset != null ? subset[i] : i;
                Vector3 a = vertices[triangles[t * 3]];
                Vector3 b = vertices[triangles[(t * 3) + 1]];
                Vector3 c = vertices[triangles[(t * 3) + 2]];
                sum += Vector3.Dot(a, Vector3.Cross(b, c));
            }

            return (float)(sum / 6.0);
        }

        private static float FillRatio(Vector3[] vertices, int[] triangles, List<int> part)
        {
            Bounds bounds = PartBounds(vertices, triangles, part);
            float box = bounds.size.x * bounds.size.y * bounds.size.z;
            return box > 0f ? Mathf.Abs(SignedVolume(vertices, triangles, part)) / box : 1f;
        }

        // Triangles grouped by shared (welded) points.
        private static List<List<int>> ConnectedParts(Vector3[] vertices, int[] triangles, Bounds bounds)
        {
            float cell = Mathf.Max(bounds.size.magnitude * 1e-6f, 1e-9f);
            var weld = new Dictionary<Vector3Int, int>();
            var welded = new int[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 p = vertices[v] / cell;
                var key = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
                if (!weld.TryGetValue(key, out int id))
                {
                    id = weld.Count;
                    weld[key] = id;
                }

                welded[v] = id;
            }

            var parent = new int[weld.Count];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            int triangleCount = triangles.Length / 3;
            for (int t = 0; t < triangleCount; t++)
            {
                int a = welded[triangles[t * 3]];
                Union(parent, a, welded[triangles[(t * 3) + 1]]);
                Union(parent, a, welded[triangles[(t * 3) + 2]]);
            }

            var byRoot = new Dictionary<int, List<int>>();
            var parts = new List<List<int>>();
            for (int t = 0; t < triangleCount; t++)
            {
                int root = Find(parent, welded[triangles[t * 3]]);
                if (!byRoot.TryGetValue(root, out List<int> part))
                {
                    part = new List<int>();
                    byRoot[root] = part;
                    parts.Add(part);
                }

                part.Add(t);
            }

            return parts;
        }

        // Halves a part by triangle centroid along its longest axis until `count` pieces.
        private static List<List<int>> Bisect(Vector3[] vertices, int[] triangles, List<int> part, int count)
        {
            var pieces = new List<List<int>> { part };
            while (pieces.Count < count)
            {
                pieces.Sort((a, b) => b.Count.CompareTo(a.Count));
                List<int> largest = pieces[0];
                if (largest.Count < MinSplitTriangles / 2)
                {
                    break;
                }

                Bounds bounds = PartBounds(vertices, triangles, largest);
                int axis = bounds.size.x >= bounds.size.y && bounds.size.x >= bounds.size.z ? 0 : bounds.size.y >= bounds.size.z ? 1 : 2;
                var keyed = new List<KeyValuePair<float, int>>(largest.Count);
                foreach (int t in largest)
                {
                    Vector3 centroid = (vertices[triangles[t * 3]] + vertices[triangles[(t * 3) + 1]] + vertices[triangles[(t * 3) + 2]]) / 3f;
                    keyed.Add(new KeyValuePair<float, int>(centroid[axis], t));
                }

                keyed.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
                int half = keyed.Count / 2;
                var low = new List<int>(half);
                var high = new List<int>(keyed.Count - half);
                for (int i = 0; i < keyed.Count; i++)
                {
                    (i < half ? low : high).Add(keyed[i].Value);
                }

                pieces.RemoveAt(0);
                pieces.Add(low);
                pieces.Add(high);
            }

            return pieces;
        }

        private static Bounds PartBounds(Vector3[] vertices, int[] triangles, List<int> part)
        {
            var bounds = new Bounds(vertices[triangles[part[0] * 3]], Vector3.zero);
            foreach (int t in part)
            {
                bounds.Encapsulate(vertices[triangles[t * 3]]);
                bounds.Encapsulate(vertices[triangles[(t * 3) + 1]]);
                bounds.Encapsulate(vertices[triangles[(t * 3) + 2]]);
            }

            return bounds;
        }

        private static Mesh PieceMesh(string name, Vector3[] vertices, int[] triangles, List<int> part, int index)
        {
            var remap = new Dictionary<int, int>();
            var points = new List<Vector3>();
            var indices = new List<int>(part.Count * 3);
            foreach (int t in part)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int v = triangles[(t * 3) + corner];
                    if (!remap.TryGetValue(v, out int mapped))
                    {
                        mapped = points.Count;
                        remap[v] = mapped;
                        points.Add(vertices[v]);
                    }

                    indices.Add(mapped);
                }
            }

            if (points.Count < MinPiecePoints)
            {
                return null;
            }

            var piece = new Mesh { name = $"{name} piece {index}" };
            if (points.Count > 65535)
            {
                piece.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            piece.SetVertices(points);
            piece.SetTriangles(indices, 0);
            piece.RecalculateBounds();
            return piece;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb)
            {
                parent[ra] = rb;
            }
        }
    }
}
