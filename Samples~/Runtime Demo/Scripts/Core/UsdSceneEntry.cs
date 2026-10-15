using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// One imported USD file as it exists in the running scene.
    /// </summary>
    /// <remarks>
    /// Each entry owns a two level transform chain:
    /// <code>
    /// Root          identity at import, this is what the gizmo moves and what the user sees
    ///  +- Correction  holds the up-axis rotation and unit scale for this stage
    ///      +- ...     the hierarchy the toolkit built
    /// </code>
    /// Keeping the correction on its own transform means user manipulation stays readable
    /// (Root's local transform is exactly what the user did, nothing else) while
    /// <c>Correction.localToWorldMatrix</c> is the full source-to-world matrix the referencing
    /// exporter needs.
    /// </remarks>
    public sealed class UsdSceneEntry
    {
        public string SourcePath { get; }
        public string DisplayName { get; }
        public GameObject Root { get; }
        public Transform Correction { get; }
        public int MeshCount { get; }
        public int VertexCount { get; }
        public int TriangleCount { get; }
        public UsdStageConversion Conversion { get; }
        public string DefaultPrimPath { get; }
        public IReadOnlyList<Renderer> Renderers { get; }

        public UsdSceneEntry(
            string sourcePath,
            GameObject root,
            Transform correction,
            UsdImportResult result,
            UsdStageConversion conversion)
        {
            SourcePath = sourcePath;
            DisplayName = Path.GetFileName(sourcePath);
            Root = root;
            Correction = correction;
            MeshCount = result.MeshCount;
            VertexCount = result.VertexCount;
            TriangleCount = result.TriangleCount;
            Conversion = conversion;
            DefaultPrimPath = result.DefaultPrimPath;
            Renderers = root.GetComponentsInChildren<Renderer>(true);
        }

        /// <summary>
        /// Source-space to world matrix: user manipulation composed with the stage correction.
        /// This is the transform a referencing layer must author so that re-composing the
        /// original (still Z-up, still centimetre) source lands in the right place.
        /// </summary>
        public Matrix4x4 SourceToWorld => Correction.localToWorldMatrix;

        public string SummaryLine =>
            $"{MeshCount} mesh{(MeshCount == 1 ? string.Empty : "es")} · {TriangleCount:N0} tris";
    }
}
