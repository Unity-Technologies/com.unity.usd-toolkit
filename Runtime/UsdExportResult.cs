using System.Collections.Generic;
using System.Text;

namespace Unity.USDToolkit
{
    public sealed class UsdExportResult
    {
        private readonly List<UsdExportedMeshInfo> meshes = new List<UsdExportedMeshInfo>();

        public string OutputPath { get; internal set; }
        public string RootPrimName { get; internal set; }
        public string PluginSearchPath { get; internal set; }
        public string NativeDllSearchPath { get; internal set; }
        public string NativeDiagnostics { get; internal set; }
        public int MeshCount { get; internal set; }
        public int VertexCount { get; internal set; }
        public int TriangleCount { get; internal set; }

        public IReadOnlyList<UsdExportedMeshInfo> Meshes => meshes;

        internal void AddMesh(
            string gameObjectPath,
            string meshName,
            string primPath,
            int vertexCount,
            int triangleCount,
            int submeshCount,
            int materialCount)
        {
            meshes.Add(new UsdExportedMeshInfo(
                gameObjectPath,
                meshName,
                primPath,
                vertexCount,
                triangleCount,
                submeshCount,
                materialCount));
            MeshCount++;
            VertexCount += vertexCount;
            TriangleCount += triangleCount;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append("USD export wrote ");
            builder.Append(MeshCount);
            builder.Append(" mesh(es), ");
            builder.Append(VertexCount);
            builder.Append(" vertices, ");
            builder.Append(TriangleCount);
            builder.Append(" triangles");

            if (!string.IsNullOrEmpty(OutputPath))
            {
                builder.Append(" to ");
                builder.Append(OutputPath);
            }

            return builder.ToString();
        }
    }

    public sealed class UsdExportedMeshInfo
    {
        internal UsdExportedMeshInfo(
            string gameObjectPath,
            string meshName,
            string primPath,
            int vertexCount,
            int triangleCount,
            int submeshCount,
            int materialCount)
        {
            GameObjectPath = gameObjectPath;
            MeshName = meshName;
            PrimPath = primPath;
            VertexCount = vertexCount;
            TriangleCount = triangleCount;
            SubmeshCount = submeshCount;
            MaterialCount = materialCount;
        }

        public string GameObjectPath { get; }
        public string MeshName { get; }
        public string PrimPath { get; }
        public int VertexCount { get; }
        public int TriangleCount { get; }
        public int SubmeshCount { get; }
        public int MaterialCount { get; }
    }
}
