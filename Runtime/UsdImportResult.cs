using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
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

        /// <summary>
        /// The number of distinct Unity Mesh objects the import created. Equal to
        /// <see cref="MeshCount"/> unless UsdImportOptions.ShareIdenticalMeshes let objects with
        /// identical geometry share one Mesh, in which case it is smaller.
        /// </summary>
        public int UniqueMeshCount { get; internal set; }

        public int VertexCount { get; internal set; }
        public int TriangleCount { get; internal set; }
        public int MaterialCount { get; internal set; }

        /// <summary>
        /// The rotation put on the root's local rotation for the stage's upAxis
        /// (UsdImportOptions.ApplyStageUpAxisAndUnits): -90 degrees about X for a Z-up stage,
        /// identity for a Y-up stage or when the option is off.
        /// </summary>
        public Quaternion StageCorrectionRotation { get; internal set; } = Quaternion.identity;

        /// <summary>
        /// The uniform scale put on the root's local scale for the stage's metersPerUnit. 1 for a
        /// meter stage or when the option is off.
        /// </summary>
        public float StageCorrectionScale { get; internal set; } = 1f;

        /// <summary>
        /// The shared neutral grey material given to meshes with no material binding
        /// (UsdImportOptions.AssignDefaultMaterial), or null when no mesh needed it. It is not
        /// counted in <see cref="MaterialCount"/>, which counts the stage's own materials.
        /// </summary>
        public Material DefaultMaterial { get; internal set; }

        internal bool DefaultMaterialResolved { get; set; }

        /// <summary>
        /// Where the import came from and what the stage offers (variant sets, payloads, arcs).
        /// Null when UsdImportOptions.RecordSource is off.
        /// </summary>
        public UsdStageSource Source { get; internal set; }

        /// <summary>
        /// Completes once every texture is attached to its material. Already complete unless
        /// UsdImportOptions.StreamTextures let <c>ImportAsync</c> return before the textures had
        /// finished decoding. Never faults: a texture that fails to load is logged and its
        /// material keeps its authored constants, as in a non-streamed import.
        /// </summary>
        public Task TexturesReady { get; internal set; } = Task.CompletedTask;

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
            builder.Append(" mesh(es)");
            if (UniqueMeshCount != MeshCount)
            {
                builder.Append(" (");
                builder.Append(UniqueMeshCount);
                builder.Append(" unique)");
            }

            builder.Append(", ");
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
