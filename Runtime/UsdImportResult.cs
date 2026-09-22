using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Unity.USDToolkit
{
    public sealed class UsdImportResult
    {
        private readonly List<UsdImportedMeshInfo> meshes = new List<UsdImportedMeshInfo>();

        public string InputPath { get; internal set; }
        public GameObject RootObject { get; internal set; }
        public string DefaultPrimPath { get; internal set; }
        public double MetersPerUnit { get; internal set; }
        public string UpAxis { get; internal set; }
        public string NativeDiagnostics { get; internal set; }
        public int MeshCount { get; internal set; }
        public int VertexCount { get; internal set; }
        public int TriangleCount { get; internal set; }
        public int MaterialCount { get; internal set; }

        public IReadOnlyList<UsdImportedMeshInfo> Meshes => meshes;

        internal void AddMesh(string primPath, string gameObjectPath, int vertexCount, int triangleCount, int materialIndex)
        {
            meshes.Add(new UsdImportedMeshInfo(primPath, gameObjectPath, vertexCount, triangleCount, materialIndex));
            MeshCount++;
            VertexCount += vertexCount;
            TriangleCount += triangleCount;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append("USD import loaded ");
            builder.Append(MeshCount);
            builder.Append(" mesh(es), ");
            builder.Append(VertexCount);
            builder.Append(" vertices, ");
            builder.Append(TriangleCount);
            builder.Append(" triangles");

            if (!string.IsNullOrEmpty(InputPath))
            {
                builder.Append(" from ");
                builder.Append(InputPath);
            }

            return builder.ToString();
        }
    }

    public sealed class UsdImportedMeshInfo
    {
        internal UsdImportedMeshInfo(
            string primPath,
            string gameObjectPath,
            int vertexCount,
            int triangleCount,
            int materialIndex)
        {
            PrimPath = primPath;
            GameObjectPath = gameObjectPath;
            VertexCount = vertexCount;
            TriangleCount = triangleCount;
            MaterialIndex = materialIndex;
        }

        public string PrimPath { get; }
        public string GameObjectPath { get; }
        public int VertexCount { get; }
        public int TriangleCount { get; }
        public int MaterialIndex { get; }
    }
}
