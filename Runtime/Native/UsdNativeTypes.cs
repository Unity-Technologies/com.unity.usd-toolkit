using System.Runtime.InteropServices;

namespace Unity.USDToolkit.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdVec2
    {
        public float X;
        public float Y;

        public RUsdVec2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdVec3
    {
        public float X;
        public float Y;
        public float Z;

        public RUsdVec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdMaterial
    {
        public float R;
        public float G;
        public float B;
        public float A;
        public float Metallic;
        public float Roughness;

        // Empty means a flat colour; when set, the native side wires it up as a UsdUVTexture
        // (all paths are relative to the USD file).
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string AlbedoTexturePath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string NormalTexturePath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string MetallicTexturePath;

        // Texture UV tiling/offset. Anything other than (1,1,0,0) is applied by the native
        // side as a UsdTransform2d.
        public float UvScaleX;
        public float UvScaleY;
        public float UvOffsetX;
        public float UvOffsetY;

        // emissiveColor (0 by default). The native side authors emissiveColor when this is
        // non-zero or an emission texture is present.
        public float EmissiveR;
        public float EmissiveG;
        public float EmissiveB;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string EmissiveTexturePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdSubmesh
    {
        public int IndexStart;
        public int IndexCount;
        public int MaterialIndex;

        public RUsdSubmesh(int indexStart, int indexCount, int materialIndex)
        {
            IndexStart = indexStart;
            IndexCount = indexCount;
            MaterialIndex = materialIndex;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdMatrix4x4
    {
        public double M00;
        public double M01;
        public double M02;
        public double M03;
        public double M10;
        public double M11;
        public double M12;
        public double M13;
        public double M20;
        public double M21;
        public double M22;
        public double M23;
        public double M30;
        public double M31;
        public double M32;
        public double M33;
    }

    internal readonly struct RUsdImportInfo
    {
        public RUsdImportInfo(
            string defaultPrimPath,
            double metersPerUnit,
            int upAxis,
            int meshCount,
            int materialCount)
        {
            DefaultPrimPath = defaultPrimPath;
            MetersPerUnit = metersPerUnit;
            UpAxis = upAxis;
            MeshCount = meshCount;
            MaterialCount = materialCount;
        }

        public string DefaultPrimPath { get; }
        public double MetersPerUnit { get; }
        public int UpAxis { get; }
        public int MeshCount { get; }
        public int MaterialCount { get; }
    }

    // Authored UsdPreviewSurface opacity. TexturePath is empty when `opacity` is not connected
    // to a texture; Threshold > 0 means alpha cutout at that value, 0 means alpha blending.
    internal readonly struct RUsdImportOpacity
    {
        public RUsdImportOpacity(string texturePath, float threshold)
        {
            TexturePath = texturePath;
            Threshold = threshold;
        }

        public string TexturePath { get; }
        public float Threshold { get; }

        public bool IsTextured => !string.IsNullOrEmpty(TexturePath);
    }

    internal readonly struct RUsdImportMeshInfo
    {
        public RUsdImportMeshInfo(
            string primPath,
            string name,
            int pointCount,
            int indexCount,
            int normalCount,
            int uv0Count,
            int materialIndex,
            bool visible)
        {
            PrimPath = primPath;
            Name = name;
            PointCount = pointCount;
            IndexCount = indexCount;
            NormalCount = normalCount;
            Uv0Count = uv0Count;
            MaterialIndex = materialIndex;
            Visible = visible;
        }

        public string PrimPath { get; }
        public string Name { get; }
        public int PointCount { get; }
        public int IndexCount { get; }
        public int NormalCount { get; }
        public int Uv0Count { get; }
        public int MaterialIndex { get; }
        public bool Visible { get; }
    }

    internal readonly struct RUsdImportNode
    {
        public RUsdImportNode(string path, RUsdMatrix4x4 localMatrix, bool visible)
        {
            Path = path;
            LocalMatrix = localMatrix;
            Visible = visible;
        }

        public string Path { get; }
        public RUsdMatrix4x4 LocalMatrix { get; }
        public bool Visible { get; }
    }

    internal readonly struct RUsdImportMaterial
    {
        public RUsdImportMaterial(string name, RUsdMaterial material)
        {
            Name = name;
            Material = material;
        }

        public string Name { get; }
        public RUsdMaterial Material { get; }
    }
}
