namespace Unity.USDToolkit
{
    public sealed class UsdImportPreviewInfo
    {
        internal UsdImportPreviewInfo(
            string path,
            string defaultPrimPath,
            double metersPerUnit,
            string upAxis,
            int meshCount,
            int materialCount,
            int triangleCount,
            int vertexCount,
            string nativeDiagnostics)
        {
            Path = path;
            DefaultPrimPath = defaultPrimPath;
            MetersPerUnit = metersPerUnit;
            UpAxis = upAxis;
            MeshCount = meshCount;
            MaterialCount = materialCount;
            TriangleCount = triangleCount;
            VertexCount = vertexCount;
            NativeDiagnostics = nativeDiagnostics;
        }

        public string Path { get; }
        public string DefaultPrimPath { get; }
        public double MetersPerUnit { get; }
        public string UpAxis { get; }
        public int MeshCount { get; }
        public int MaterialCount { get; }

        /// <summary>Total triangles across every mesh on the stage. Use this to gate heavy imports.</summary>
        public int TriangleCount { get; }

        /// <summary>Total mesh points across every mesh on the stage (pre-split, as authored in USD).</summary>
        public int VertexCount { get; }

        public string NativeDiagnostics { get; }
    }
}
