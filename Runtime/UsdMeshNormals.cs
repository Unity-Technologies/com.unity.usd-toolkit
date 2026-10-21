using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    // Smooth normals the way Isaac Sim's RTX renderer shades a mesh. Measured on NVIDIA's Factory
    // (2026-10-19): blocking `primvars:normals`, replacing the schema `normals` with flat
    // face-varying ones and then with a constant vertex normal all left the Isaac render
    // pixel-identical, while scaling `points` changed it, so authored normals are not what it
    // draws. Its result matches an angle-limited smoothing: faces that meet below the crease angle
    // share one normal at a shared position, sharper edges keep theirs. The car body
    // (sledge/Default__24) carries exporter normals with hard breaks across 19% of its adjacent
    // corners, which rendered as wedge-shaped facets in Unity and smooth in Isaac Sim.
    internal static class UsdMeshNormals
    {
        // Faces meeting at or below this angle (degrees) are smoothed across. 60 keeps a cube's
        // edges and a 12-sided cylinder's smoothness; 30 was indistinguishable on the Factory.
        public const float CreaseAngle = 60f;

        // Normals per vertex of a split-vertex mesh: vertices at the same position (to 0.5 mm)
        // are treated as one point, and each vertex takes the area-weighted average of the
        // neighbouring faces that lie within the crease angle of its own face; one whose faces are
        // all degenerate takes every face at its position. Vertices that no triangle uses keep a
        // zero normal.
        public static Vector3[] ComputeCreaseSmoothed(IList<Vector3> vertices, int[] triangles, float creaseAngle = CreaseAngle)
        {
            int vertexCount = vertices.Count;
            int faceCount = triangles.Length / 3;
            var result = new Vector3[vertexCount];
            if (vertexCount == 0 || faceCount == 0)
            {
                return result;
            }

            // Face normals, unnormalised: the magnitude is twice the area, which is the weight.
            var faceNormals = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                Vector3 a = vertices[triangles[f * 3]];
                Vector3 b = vertices[triangles[f * 3 + 1]];
                Vector3 c = vertices[triangles[f * 3 + 2]];
                faceNormals[f] = Vector3.Cross(b - a, c - a);
            }

            // Position -> faces touching it. Quantised to 0.5 mm in the mesh's own units (metres
            // after the importer's stage correction), which merges a split vertex with its twins
            // but not neighbouring points.
            var facesAtPosition = new Dictionary<Vector3, List<int>>(vertexCount);
            var keys = new Vector3[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 p = vertices[v];
                keys[v] = new Vector3(Mathf.Round(p.x * 2000f), Mathf.Round(p.y * 2000f), Mathf.Round(p.z * 2000f));
            }

            for (int f = 0; f < faceCount; f++)
            {
                for (int k = 0; k < 3; k++)
                {
                    Vector3 key = keys[triangles[f * 3 + k]];
                    if (!facesAtPosition.TryGetValue(key, out List<int> list))
                    {
                        list = new List<int>(6);
                        facesAtPosition.Add(key, list);
                    }

                    list.Add(f);
                }
            }

            float cosCrease = Mathf.Cos(creaseAngle * Mathf.Deg2Rad);
            var unitFaceNormals = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                unitFaceNormals[f] = Unit(faceNormals[f]);
            }

            // A vertex can be referenced by several faces (an unsplit vertex); it takes the last
            // face's result, which for an unsplit vertex is the same average however it is reached
            // when every face lies within the crease angle. Where it does not, the faces disagree
            // anyway and either answer is as defensible.
            for (int f = 0; f < faceCount; f++)
            {
                Vector3 own = unitFaceNormals[f];
                for (int k = 0; k < 3; k++)
                {
                    int v = triangles[f * 3 + k];
                    List<int> neighbours = facesAtPosition[keys[v]];
                    Vector3 sum = Vector3.zero;
                    for (int n = 0; n < neighbours.Count; n++)
                    {
                        int other = neighbours[n];
                        if (Vector3.Dot(unitFaceNormals[other], own) >= cosCrease)
                        {
                            sum += faceNormals[other];
                        }
                    }

                    Vector3 smoothed = Unit(sum);
                    result[v] = smoothed != Vector3.zero ? smoothed : own;
                }
            }

            // A vertex whose own faces are all degenerate (zero area: a repeated index or three
            // points on a line) still has none. It takes the average of every face at its position,
            // so it shades with its neighbours instead of black (openchessset kept 688 such vertices
            // of 418,071 after the normalisation fix).
            for (int f = 0; f < faceCount; f++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = triangles[f * 3 + k];
                    if (result[v] != Vector3.zero)
                    {
                        continue;
                    }

                    Vector3 sum = Vector3.zero;
                    foreach (int other in facesAtPosition[keys[v]])
                    {
                        sum += unitFaceNormals[other];
                    }

                    result[v] = Unit(sum);
                }
            }

            return result;
        }

        // Vector3.normalized returns zero below a length of 1e-5. A face normal here is the
        // cross product of two edges, so its length is twice the triangle's area: a 1 mm triangle
        // gives 1e-6 and lost its normal. A 15 cm chess piece of 40,000 triangles came out with
        // 99.6% zero normals (openchessset, 2026-10-20). Normalising in double keeps any non-zero
        // length.
        internal static Vector3 Unit(Vector3 v)
        {
            double x = v.x, y = v.y, z = v.z;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (!(length > 0.0))
            {
                return Vector3.zero;
            }

            return new Vector3((float)(x / length), (float)(y / length), (float)(z / length));
        }
    }
}
