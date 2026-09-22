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

        // 비어 있으면 단색, 채워지면 native가 UsdUVTexture로 연결한다 (모두 USD 기준 상대경로).
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string AlbedoTexturePath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string NormalTexturePath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string MetallicTexturePath;

        // 텍스처 UV 타일링/오프셋. (1,1,0,0)이 아니면 native가 UsdTransform2d로 적용한다.
        public float UvScaleX;
        public float UvScaleY;
        public float UvOffsetX;
        public float UvOffsetY;

        // emissiveColor (기본 0). 0이 아니거나 텍스처가 있으면 native가 emissiveColor를 출력한다.
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
