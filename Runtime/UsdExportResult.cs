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

        /// <summary>UsdLux lights written (the 0.8.1 native plugin, <see cref="UsdExportOptions.ExportLights"/>).</summary>
        public int LightCount { get; internal set; }

        /// <summary>UsdGeomCameras written (the 0.8.1 native plugin, <see cref="UsdExportOptions.ExportCameras"/>).</summary>
        public int CameraCount { get; internal set; }

        public IReadOnlyList<UsdExportedMeshInfo> Meshes => meshes;

        /// <summary>The composition mode the export ran in.</summary>
        public UsdCompositionMode Composition { get; internal set; }

        /// <summary>
        /// Override exports only: the source file the output sublayers. The output depends on it,
        /// so ship the two together (or export to .usdz, which packages it).
        /// </summary>
        public string SourceLayer { get; internal set; }

        /// <summary>Asset files written beside the output (UsdExportLayout.FolderWithAssets).</summary>
        public IReadOnlyList<string> AssetFiles => assetFiles;

        /// <summary>References written: prefab instances, mesh instances and referenced stages.</summary>
        public int ReferenceCount { get; internal set; }

        /// <summary>Opinions written over referenced or sublayered content (moves, visibility, deactivations, additions).</summary>
        public int OverrideCount { get; internal set; }

        /// <summary>
        /// Imported objects moved under another parent and written as relocates: the source prim,
        /// with everything its composition brings, composed at the new place.
        /// </summary>
        public int RelocateCount { get; internal set; }

        /// <summary>Prefab assets and mesh prototypes written once and shared.</summary>
        public int PrototypeCount { get; internal set; }

        private readonly List<string> assetFiles = new List<string>();

        internal void AddAssetFile(string path)
        {
            assetFiles.Add(path);
        }

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
            if (LightCount > 0 || CameraCount > 0)
            {
                builder.Append(", ");
                builder.Append(LightCount);
                builder.Append(" light(s), ");
                builder.Append(CameraCount);
                builder.Append(" camera(s)");
            }

            if (Composition == UsdCompositionMode.Preserve)
            {
                builder.Append(", ");
                builder.Append(ReferenceCount);
                builder.Append(" reference(s), ");
                builder.Append(OverrideCount);
                builder.Append(" override(s), ");
                builder.Append(RelocateCount);
                builder.Append(" relocate(s), ");
                builder.Append(PrototypeCount);
                builder.Append(" shared prototype(s)");
            }

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
