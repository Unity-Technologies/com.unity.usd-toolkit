using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Procedural handle geometry, built in code so the demo carries no prefab or primitive
    /// dependencies that could go missing in a build.
    /// </summary>
    public static class GizmoMeshes
    {
        public static Mesh CreateCylinder(float radius, float height, int segments = 16)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            float half = height * 0.5f;

            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2.0f * i / segments;
                float a1 = Mathf.PI * 2.0f * (i + 1) / segments;
                Vector3 b0 = new Vector3(Mathf.Cos(a0) * radius, -half, Mathf.Sin(a0) * radius);
                Vector3 b1 = new Vector3(Mathf.Cos(a1) * radius, -half, Mathf.Sin(a1) * radius);
                Vector3 t0 = new Vector3(b0.x, half, b0.z);
                Vector3 t1 = new Vector3(b1.x, half, b1.z);

                int baseIndex = vertices.Count;
                vertices.Add(b0);
                vertices.Add(b1);
                vertices.Add(t1);
                vertices.Add(t0);
                triangles.AddRange(new[] { baseIndex, baseIndex + 2, baseIndex + 1, baseIndex, baseIndex + 3, baseIndex + 2 });
            }

            return Finalise(vertices, triangles, "GizmoCylinder");
        }

        public static Mesh CreateCone(float radius, float height, int segments = 16)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var apex = new Vector3(0.0f, height, 0.0f);

            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2.0f * i / segments;
                float a1 = Mathf.PI * 2.0f * (i + 1) / segments;
                Vector3 p0 = new Vector3(Mathf.Cos(a0) * radius, 0.0f, Mathf.Sin(a0) * radius);
                Vector3 p1 = new Vector3(Mathf.Cos(a1) * radius, 0.0f, Mathf.Sin(a1) * radius);

                int baseIndex = vertices.Count;
                vertices.Add(p0);
                vertices.Add(p1);
                vertices.Add(apex);
                triangles.AddRange(new[] { baseIndex, baseIndex + 2, baseIndex + 1 });

                // Cap, so the cone does not read as hollow when seen from below.
                baseIndex = vertices.Count;
                vertices.Add(p0);
                vertices.Add(p1);
                vertices.Add(Vector3.zero);
                triangles.AddRange(new[] { baseIndex, baseIndex + 1, baseIndex + 2 });
            }

            return Finalise(vertices, triangles, "GizmoCone");
        }

        /// <summary>Ring lying in the XZ plane, so it reads as rotation about Y.</summary>
        public static Mesh CreateTorus(float ringRadius, float tubeRadius, int ringSegments = 48, int tubeSegments = 8)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            for (int i = 0; i < ringSegments; i++)
            {
                float u0 = Mathf.PI * 2.0f * i / ringSegments;
                float u1 = Mathf.PI * 2.0f * (i + 1) / ringSegments;

                for (int j = 0; j < tubeSegments; j++)
                {
                    float v0 = Mathf.PI * 2.0f * j / tubeSegments;
                    float v1 = Mathf.PI * 2.0f * (j + 1) / tubeSegments;

                    int baseIndex = vertices.Count;
                    vertices.Add(TorusPoint(u0, v0, ringRadius, tubeRadius));
                    vertices.Add(TorusPoint(u1, v0, ringRadius, tubeRadius));
                    vertices.Add(TorusPoint(u1, v1, ringRadius, tubeRadius));
                    vertices.Add(TorusPoint(u0, v1, ringRadius, tubeRadius));
                    triangles.AddRange(new[] { baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3 });
                }
            }

            return Finalise(vertices, triangles, "GizmoTorus");
        }

        /// <summary>
        /// Flat ring in the XZ plane, double sided so it reads from above and below. Writes into
        /// an existing mesh because the ground handle resizes with the selected part's footprint.
        /// </summary>
        public static void FillAnnulus(Mesh mesh, float innerRadius, float outerRadius, int segments = 64)
        {
            var vertices = new List<Vector3>(segments * 2);
            var triangles = new List<int>(segments * 12);

            for (int i = 0; i < segments; i++)
            {
                float a = Mathf.PI * 2.0f * i / segments;
                var direction = new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a));
                vertices.Add(direction * innerRadius);
                vertices.Add(direction * outerRadius);
            }

            for (int i = 0; i < segments; i++)
            {
                int i0 = i * 2;
                int o0 = i0 + 1;
                int i1 = (i + 1) % segments * 2;
                int o1 = i1 + 1;
                triangles.AddRange(new[] { i0, i1, o1, i0, o1, o0 });
                triangles.AddRange(new[] { i0, o1, i1, i0, o0, o1 });
            }

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        private static Vector3 TorusPoint(float u, float v, float ringRadius, float tubeRadius)
        {
            float r = ringRadius + tubeRadius * Mathf.Cos(v);
            return new Vector3(Mathf.Cos(u) * r, tubeRadius * Mathf.Sin(v), Mathf.Sin(u) * r);
        }

        private static Mesh Finalise(List<Vector3> vertices, List<int> triangles, string name)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
