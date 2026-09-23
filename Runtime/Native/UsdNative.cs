using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Unity.USDToolkit.Native
{
    internal static class UsdNative
    {
        private const string DllName = "UnityUSDToolkitNative";
        private const int ErrorBufferSize = 4096;
        // Exact match with the source, deliberately. This used to accept anything >= 2 so that
        // payloads lagging the source (uv0 only, no usdz) kept working while Windows and Linux
        // were rebuilt. All three desktop payloads are API 5 as of 2026-09-22, and the tolerance
        // had become a hole: fixes that are not gated on the ABI version — the SECURITY-282834
        // topology and asset-path fixes among them — are simply absent from an older binary, and
        // nothing in the version number says so. An older plugin is now refused outright.
        internal const int MinimumApiVersion = 5;
        internal const int LatestApiVersion = 5;
        // usdz packaging and resolver-backed asset reads (textures inside a .usdz).
        internal const int UsdzApiVersion = 5;
        internal const int MaxUvSets = 3;
        internal const string NativeDllName = DllName;

        private static int loadedApiVersion;

        // Version the loaded plugin reports, queried once on first use. Self-resolving on
        // purpose: the import path never runs the export path's validation, so a gate that
        // waited to be told the version would silently stay at 0 and skip every v3 feature.
        internal static int LoadedApiVersion
        {
            get
            {
                int cached = loadedApiVersion;
                if (cached == 0)
                {
                    cached = GetApiVersion();
                    loadedApiVersion = cached;
                }

                return cached;
            }
        }

#if ((UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN) && UNITY_64) || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX || ((UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX) && UNITY_64)
        internal static bool IsSupportedPlatform => true;
#else
        internal static bool IsSupportedPlatform => false;
#endif

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetApiVersion();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetOpenUsdVersion(
            StringBuilder buffer,
            int bufferCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_BeginExport(
            [MarshalAs(UnmanagedType.LPStr)] string outputPath,
            [MarshalAs(UnmanagedType.LPStr)] string rootPrimName,
            float metersPerUnit,
            out IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_BeginExportEx(
            [MarshalAs(UnmanagedType.LPStr)] string outputPath,
            [MarshalAs(UnmanagedType.LPStr)] string rootPrimName,
            float metersPerUnit,
            int captureDiagnostics,
            out IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddMesh(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            [In] RUsdVec3[] points,
            int pointCount,
            [In] int[] indices,
            int indexCount,
            [In] RUsdVec3[] normals,
            int normalCount,
            [In] RUsdVec2[] uv0,
            int uv0Count,
            ref RUsdMaterial material);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddMeshEx(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            [In] RUsdVec3[] points,
            int pointCount,
            [In] int[] indices,
            int indexCount,
            [In] RUsdSubmesh[] submeshes,
            int submeshCount,
            [In] RUsdVec3[] normals,
            int normalCount,
            [In] RUsdVec2[] uv0,
            int uv0Count,
            [In] RUsdMaterial[] materials,
            int materialCount,
            int visible,
            int writeExtent);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddXform(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            ref RUsdMatrix4x4 matrix,
            int visible);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_EndExport(IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RUsd_Destroy(IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetLastError(
            IntPtr context,
            StringBuilder buffer,
            int bufferCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetDiagnostics(
            IntPtr context,
            StringBuilder buffer,
            int bufferCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_OpenStage(
            [MarshalAs(UnmanagedType.LPStr)] string inputPath,
            int captureDiagnostics,
            out IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportInfo(
            IntPtr context,
            StringBuilder defaultPrimPathBuffer,
            int defaultPrimPathBufferCapacity,
            out double metersPerUnit,
            out int upAxis,
            out int meshCount,
            out int materialCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMeshInfo(
            IntPtr context,
            int meshIndex,
            StringBuilder primPathBuffer,
            int primPathBufferCapacity,
            StringBuilder nameBuffer,
            int nameBufferCapacity,
            out int pointCount,
            out int indexCount,
            out int normalCount,
            out int uv0Count,
            out int materialIndex,
            out int visible);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportNodeCount(
            IntPtr context,
            out int nodeCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportNodeInfo(
            IntPtr context,
            int nodeIndex,
            StringBuilder pathBuffer,
            int pathBufferCapacity,
            out RUsdMatrix4x4 localMatrix,
            out int visible);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportSubmeshCount(
            IntPtr context,
            int meshIndex,
            out int submeshCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportSubmeshes(
            IntPtr context,
            int meshIndex,
            [Out] RUsdSubmesh[] submeshes,
            int submeshCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportMesh(
            IntPtr context,
            int meshIndex,
            [Out] RUsdVec3[] points,
            int pointCapacity,
            [Out] int[] indices,
            int indexCapacity,
            [Out] RUsdVec3[] normals,
            int normalCapacity,
            [Out] RUsdVec2[] uv0,
            int uv0Capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMaterialOpacity(
            IntPtr context,
            int materialIndex,
            out float opacityThreshold,
            StringBuilder texturePathBuffer,
            int texturePathBufferCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMeshUvSetInfo(
            IntPtr context,
            int meshIndex,
            int uvSet,
            out int uvCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportMeshUvSet(
            IntPtr context,
            int meshIndex,
            int uvSet,
            [Out] RUsdVec2[] uv,
            int uvCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMaterial(
            IntPtr context,
            int materialIndex,
            out RUsdMaterial material,
            StringBuilder nameBuffer,
            int nameBufferCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CreateUsdzPackage(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string sourceUsdPath,
            [MarshalAs(UnmanagedType.LPStr)] string usdzPath,
            [MarshalAs(UnmanagedType.LPStr)] string firstLayerName,
            int arkitCompatible);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ReadImportAsset(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string assetPath,
            StringBuilder resolvedPathBuffer,
            int resolvedPathBufferCapacity,
            [Out] byte[] buffer,
            long bufferCapacity,
            out long byteCount);

        internal static IntPtr BeginExport(
            string outputPath,
            string rootPrimName,
            float metersPerUnit,
            bool captureDiagnostics)
        {
            ThrowIfUnsupported();
            UsdExporter.EnsureRuntimeConfigured();
            IntPtr context;
            int result = captureDiagnostics
                ? RUsd_BeginExportEx(outputPath, rootPrimName, metersPerUnit, 1, out context)
                : RUsd_BeginExport(outputPath, rootPrimName, metersPerUnit, out context);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return context;
        }

        internal static int GetApiVersion()
        {
            ThrowIfUnsupported();
            return RUsd_GetApiVersion();
        }

        internal static string GetOpenUsdVersion()
        {
            ThrowIfUnsupported();
            var builder = new StringBuilder(ErrorBufferSize);
            int result = RUsd_GetOpenUsdVersion(builder, builder.Capacity);
            return result == 0 && builder.Length > 0
                ? builder.ToString()
                : "unknown";
        }

        internal static void AddMesh(
            IntPtr context,
            string primPath,
            RUsdVec3[] points,
            int[] indices,
            RUsdVec3[] normals,
            RUsdVec2[] uv0,
            RUsdMaterial material)
        {
            int result = RUsd_AddMesh(
                context,
                primPath,
                points,
                points?.Length ?? 0,
                indices,
                indices?.Length ?? 0,
                normals,
                normals?.Length ?? 0,
                uv0,
                uv0?.Length ?? 0,
                ref material);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static void AddXform(IntPtr context, string primPath, RUsdMatrix4x4 matrix, bool visible)
        {
            int result = RUsd_AddXform(context, primPath, ref matrix, visible ? 1 : 0);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static void AddMesh(
            IntPtr context,
            string primPath,
            RUsdVec3[] points,
            int[] indices,
            RUsdSubmesh[] submeshes,
            RUsdVec3[] normals,
            RUsdVec2[] uv0,
            RUsdMaterial[] materials,
            bool visible,
            bool writeExtent)
        {
            int result = RUsd_AddMeshEx(
                context,
                primPath,
                points,
                points?.Length ?? 0,
                indices,
                indices?.Length ?? 0,
                submeshes,
                submeshes?.Length ?? 0,
                normals,
                normals?.Length ?? 0,
                uv0,
                uv0?.Length ?? 0,
                materials,
                materials?.Length ?? 0,
                visible ? 1 : 0,
                writeExtent ? 1 : 0);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static void EndExport(IntPtr context)
        {
            int result = RUsd_EndExport(context);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static void Destroy(IntPtr context)
        {
            if (context != IntPtr.Zero)
            {
                RUsd_Destroy(context);
            }
        }

        internal static IntPtr OpenStage(string inputPath, bool captureDiagnostics)
        {
            ThrowIfUnsupported();
            UsdExporter.EnsureRuntimeConfigured();
            int result = RUsd_OpenStage(inputPath, captureDiagnostics ? 1 : 0, out IntPtr context);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return context;
        }

        internal static RUsdImportInfo GetImportInfo(IntPtr context)
        {
            var defaultPrimPath = new StringBuilder(ErrorBufferSize);
            int result = RUsd_GetImportInfo(
                context,
                defaultPrimPath,
                defaultPrimPath.Capacity,
                out double metersPerUnit,
                out int upAxis,
                out int meshCount,
                out int materialCount);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return new RUsdImportInfo(
                defaultPrimPath.ToString(),
                metersPerUnit,
                upAxis,
                meshCount,
                materialCount);
        }

        internal static RUsdImportMeshInfo GetImportMeshInfo(IntPtr context, int meshIndex)
        {
            var primPath = new StringBuilder(ErrorBufferSize);
            var name = new StringBuilder(ErrorBufferSize);
            int result = RUsd_GetImportMeshInfo(
                context,
                meshIndex,
                primPath,
                primPath.Capacity,
                name,
                name.Capacity,
                out int pointCount,
                out int indexCount,
                out int normalCount,
                out int uv0Count,
                out int materialIndex,
                out int visible);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return new RUsdImportMeshInfo(
                primPath.ToString(),
                name.ToString(),
                pointCount,
                indexCount,
                normalCount,
                uv0Count,
                materialIndex,
                visible != 0);
        }

        internal static int GetImportNodeCount(IntPtr context)
        {
            int result = RUsd_GetImportNodeCount(context, out int nodeCount);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return nodeCount;
        }

        internal static RUsdImportNode GetImportNodeInfo(IntPtr context, int nodeIndex)
        {
            var path = new StringBuilder(ErrorBufferSize);
            int result = RUsd_GetImportNodeInfo(
                context,
                nodeIndex,
                path,
                path.Capacity,
                out RUsdMatrix4x4 localMatrix,
                out int visible);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return new RUsdImportNode(path.ToString(), localMatrix, visible != 0);
        }

        internal static int GetImportSubmeshCount(IntPtr context, int meshIndex)
        {
            int result = RUsd_GetImportSubmeshCount(context, meshIndex, out int submeshCount);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return submeshCount;
        }

        internal static void CopyImportSubmeshes(IntPtr context, int meshIndex, RUsdSubmesh[] submeshes)
        {
            int result = RUsd_CopyImportSubmeshes(context, meshIndex, submeshes, submeshes?.Length ?? 0);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static void CopyImportMesh(
            IntPtr context,
            int meshIndex,
            RUsdVec3[] points,
            int[] indices,
            RUsdVec3[] normals,
            RUsdVec2[] uv0)
        {
            int result = RUsd_CopyImportMesh(
                context,
                meshIndex,
                points,
                points?.Length ?? 0,
                indices,
                indices?.Length ?? 0,
                normals,
                normals?.Length ?? 0,
                uv0,
                uv0?.Length ?? 0);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        // Authored opacity (API 4+). Same rule as the UV sets: gate the call on LoadedApiVersion.
        internal static RUsdImportOpacity GetImportMaterialOpacity(IntPtr context, int materialIndex)
        {
            var texturePath = new StringBuilder(ErrorBufferSize);
            int result = RUsd_GetImportMaterialOpacity(
                context, materialIndex, out float threshold, texturePath, texturePath.Capacity);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return new RUsdImportOpacity(texturePath.ToString(), threshold);
        }

        // Extra UV sets (API 3+). Callers must check LoadedApiVersion first: the entry points do
        // not exist in older plugins, where the call would throw EntryPointNotFoundException.
        internal static int GetImportMeshUvSetCount(IntPtr context, int meshIndex, int uvSet)
        {
            int result = RUsd_GetImportMeshUvSetInfo(context, meshIndex, uvSet, out int uvCount);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return uvCount;
        }

        internal static void CopyImportMeshUvSet(IntPtr context, int meshIndex, int uvSet, RUsdVec2[] uv)
        {
            int result = RUsd_CopyImportMeshUvSet(context, meshIndex, uvSet, uv, uv?.Length ?? 0);
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static RUsdImportMaterial GetImportMaterial(IntPtr context, int materialIndex)
        {
            var name = new StringBuilder(ErrorBufferSize);
            int result = RUsd_GetImportMaterial(
                context,
                materialIndex,
                out RUsdMaterial material,
                name,
                name.Capacity);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }

            return new RUsdImportMaterial(name.ToString(), material);
        }

        // True when the loaded plugin can package usdz and read assets through the resolver.
        internal static bool SupportsUsdz => IsSupportedPlatform && LoadedApiVersion >= UsdzApiVersion;

        // Packages an already-written stage (and its dependencies) into usdzPath.
        internal static void CreateUsdzPackage(
            IntPtr context,
            string sourceUsdPath,
            string usdzPath,
            string firstLayerName,
            bool arkitCompatible)
        {
            ThrowIfUnsupported();
            int result = RUsd_CreateUsdzPackage(
                context,
                sourceUsdPath,
                usdzPath,
                firstLayerName ?? string.Empty,
                arkitCompatible ? 1 : 0);

            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        // Resolves an authored asset path through the open stage and reports its size. Returns
        // false when the asset does not resolve - a missing texture is not an error here, the
        // importer reports it and carries on - so this never throws.
        internal static bool TryResolveImportAsset(
            IntPtr context,
            string assetPath,
            out string resolvedPath,
            out long byteCount)
        {
            resolvedPath = null;
            byteCount = 0;
            if (!SupportsUsdz || context == IntPtr.Zero || string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            var resolved = new StringBuilder(ErrorBufferSize);
            if (RUsd_ReadImportAsset(context, assetPath, resolved, resolved.Capacity, null, 0, out long size) != 0)
            {
                return false;
            }

            resolvedPath = resolved.Length > 0 ? resolved.ToString() : assetPath;
            byteCount = size;
            return true;
        }

        // Reads an authored asset's bytes through the stage's resolver (the only way to reach a
        // file packaged inside a .usdz). Returns null when it cannot be read.
        internal static byte[] ReadImportAsset(IntPtr context, string assetPath)
        {
            if (!TryResolveImportAsset(context, assetPath, out _, out long size))
            {
                return null;
            }

            if (size <= 0 || size > int.MaxValue)
            {
                return size == 0 ? Array.Empty<byte>() : null;
            }

            var buffer = new byte[size];
            if (RUsd_ReadImportAsset(context, assetPath, null, 0, buffer, buffer.LongLength, out _) != 0)
            {
                return null;
            }

            return buffer;
        }

        private static string GetLastError(IntPtr context)
        {
            var builder = new StringBuilder(ErrorBufferSize);
            int result = RUsd_GetLastError(context, builder, builder.Capacity);
            return result == 0 && builder.Length > 0
                ? builder.ToString()
                : "Native USD export failed.";
        }

        internal static string GetDiagnostics(IntPtr context)
        {
            var builder = new StringBuilder(ErrorBufferSize * 4);
            int result = RUsd_GetDiagnostics(context, builder, builder.Capacity);
            return result == 0 && builder.Length > 0
                ? builder.ToString()
                : string.Empty;
        }

        private static void ThrowIfUnsupported()
        {
            if (!IsSupportedPlatform)
            {
                throw new PlatformNotSupportedException("Runtime USD export is only configured for Windows x64, macOS, and Linux x64 desktop targets.");
            }
        }
    }
}
