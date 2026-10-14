using System;
using System.Collections.Generic;
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
        // API 6 added composition (layers, references, overrides, variant/payload control). A 5
        // was accepted while the Windows and Linux payloads caught up; since 2026-10-10 every
        // desktop payload is at least 6, so a 5 is refused. Newer plugins load, and the features
        // below switch on by the version the plugin reports. LatestApiVersion is the version the
        // source is written against (kApiVersion in UsdExporter.cpp) and is only quoted in the
        // mismatch message.
        internal const int MinimumApiVersion = 6;
        internal const int LatestApiVersion = 12;
        // usdz packaging and resolver-backed asset reads (textures inside a .usdz).
        internal const int UsdzApiVersion = 5;
        // Sublayers, references, payloads, instancing, variant selections and sparse overrides
        // on export; variant/payload control and composition flags on import.
        internal const int CompositionApiVersion = 6;
        // Lights, cameras and spatial audio on import. Additive: an API 6 plugin still loads and
        // imports everything else, without those components.
        internal const int SceneComponentsApiVersion = 7;

        /// Point instancers delivered as prototype + matrix arrays rather than expanded prims.
        internal const int InstancedMeshApiVersion = 8;

        // RUsd_GetImportMaterialMetallicLayout: how a material's metallic map is packed.
        internal const int MetallicLayoutApiVersion = 9;

        // RUsd_GetImportMaterialTextureLayout: a plain metallic map and a separate roughness map,
        // which the host packs into the one metallic+smoothness texture Unity reads.
        internal const int TextureLayoutApiVersion = 10;

        // RUsd_ExrDecode / RUsd_BlockCompress: the texture codecs in native code.
        internal const int ImageCodecApiVersion = 11;

        // RUsd_ImageBuild / RUsd_UdimAtlasBuild: a whole texture, or a whole UDIM atlas, in one call.
        internal const int ImageBuildApiVersion = 12;

        // Mirrors kMaxImportInstancePlacements in UsdExporter.cpp.
        internal const int MaxImportInstancePlacements = 4000000;

        // RUsd_BeginLayerExport flags.
        internal const int LayerDefineDefaultPrim = 1;
        internal const int LayerCaptureDiagnostics = 2;

        // RUsd_DefinePrim specifiers.
        internal const int SpecifierDef = 0;
        internal const int SpecifierOver = 1;
        internal const int SpecifierClass = 2;

        // RUsd_AddReference arc types.
        internal const int ArcReference = 0;
        internal const int ArcPayload = 1;

        // RUsd_OpenStageEx flags.
        internal const int OpenCaptureDiagnostics = 1;
        internal const int OpenLoadNoPayloads = 2;
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

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_BeginLayerExport(
            [MarshalAs(UnmanagedType.LPStr)] string outputPath,
            [MarshalAs(UnmanagedType.LPStr)] string defaultPrimPath,
            [MarshalAs(UnmanagedType.LPStr)] string materialScopePath,
            double metersPerUnit,
            int upAxis,
            [MarshalAs(UnmanagedType.LPStr)] string subLayers,
            int flags,
            out IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_DefinePrim(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            [MarshalAs(UnmanagedType.LPStr)] string typeName,
            int specifier);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddReference(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            [MarshalAs(UnmanagedType.LPStr)] string assetPath,
            [MarshalAs(UnmanagedType.LPStr)] string targetPrimPath,
            int arcType);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetInstanceable(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            int instanceable);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetActive(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            int active);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetVariantSelection(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            [MarshalAs(UnmanagedType.LPStr)] string variantSet,
            [MarshalAs(UnmanagedType.LPStr)] string variant);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetLocalTransform(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            ref RUsdMatrix4x4 matrix);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetVisibility(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            int visible);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_PrimExists(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            out int exists);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_OpenStageEx(
            [MarshalAs(UnmanagedType.LPStr)] string inputPath,
            int flags,
            [MarshalAs(UnmanagedType.LPStr)] string variantSelections,
            [MarshalAs(UnmanagedType.LPStr)] string loadPaths,
            out IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportNodeFlags(
            IntPtr context,
            int nodeIndex,
            out int flags);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportComposition(
            IntPtr context,
            [Out] byte[] buffer,
            int bufferCapacity,
            out int requiredLength);

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

        // ---- Composition (API 6+). Callers check SupportsComposition first. ----

        internal static bool SupportsComposition => IsSupportedPlatform && LoadedApiVersion >= CompositionApiVersion;

        internal static bool SupportsSceneComponents => IsSupportedPlatform && LoadedApiVersion >= SceneComponentsApiVersion;

        // ---- Point instancers (API 8+). Callers check SupportsInstancedMeshes first. ----

        internal static bool SupportsInstancedMeshes =>
            IsSupportedPlatform && LoadedApiVersion >= InstancedMeshApiVersion;

        /// Whether the loaded plugin reports how metallic maps are packed.
        internal static bool SupportsMetallicLayout =>
            IsSupportedPlatform && LoadedApiVersion >= MetallicLayoutApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_GetImportMaterialMetallicLayout(
            IntPtr context,
            int materialIndex,
            out int ormPacked,
            out int occlusionIsSeparate,
            StringBuilder occlusionTexturePathBuffer,
            int occlusionTexturePathBufferCapacity);

        /// <summary>
        /// How <c>RUsdMaterial.metallicTexturePath</c> is packed, and any separate occlusion map.
        /// </summary>
        /// <remarks>
        /// Omniverse's OmniPBR writes occlusion, roughness and metallic into one linear map;
        /// Unity reads metallic from red and smoothness from alpha. The file is the same three
        /// facts in different channels, so the layout travels separately and the caller moves
        /// them while it still has the decoded pixels.
        /// </remarks>
        internal static bool GetImportMaterialMetallicLayout(
            IntPtr context, int materialIndex, out bool ormPacked, out string occlusionPath)
        {
            ormPacked = false;
            occlusionPath = null;
            if (!SupportsMetallicLayout)
            {
                return false;
            }

            var buffer = new StringBuilder(ErrorBufferSize);
            Check(context, RUsd_GetImportMaterialMetallicLayout(
                context, materialIndex, out int orm, out int separate, buffer, buffer.Capacity));
            ormPacked = orm != 0;
            occlusionPath = separate != 0 ? buffer.ToString() : null;
            return true;
        }

        /// Whether the loaded plugin reports plain metallic maps and separate roughness maps.
        internal static bool SupportsTextureLayout =>
            IsSupportedPlatform && LoadedApiVersion >= TextureLayoutApiVersion;

        // ---- Image codecs (API 11+). Pure functions; callable from worker threads. ----

        /// Whether the loaded plugin decodes EXR and block-compresses in native code.
        internal static bool SupportsImageCodecs =>
            IsSupportedPlatform && LoadedApiVersion >= ImageCodecApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_ExrProbe(byte[] data, int length, out int width, out int height,
                                                StringBuilder reasonBuffer, int reasonCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_ExrDecode(byte[] data, int length, int toSrgb, byte[] rgba, long rgbaCapacity,
                                                 StringBuilder reasonBuffer, int reasonCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long RUsd_BlockCompressedSize(int width, int height, int alpha, out int mipCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_BlockCompress(byte[] rgba, int width, int height, int alpha, int srgb,
                                                     byte[] output, long outputCapacity);

        /// <summary>
        /// Decodes an OpenEXR image to RGBA8 (rows bottom-up) in native code. Returns false with
        /// <paramref name="fallback"/> true when this platform's payload has no zlib to open, in
        /// which case the caller decodes in managed code; false with it false when the file is
        /// one neither reader takes.
        /// </summary>
        internal static bool TryDecodeExr(byte[] data, bool srgb, out int width, out int height, out byte[] rgba,
                                          out string reason, out bool fallback)
        {
            rgba = null;
            reason = null;
            fallback = false;
            var buffer = new StringBuilder(512);
            if (RUsd_ExrProbe(data, data.Length, out width, out height, buffer, buffer.Capacity) != 0)
            {
                reason = buffer.ToString();
                return false;
            }

            rgba = new byte[(long)width * height * 4];
            int result = RUsd_ExrDecode(data, data.Length, srgb ? 1 : 0, rgba, rgba.LongLength, buffer, buffer.Capacity);
            if (result == 0)
            {
                return true;
            }

            rgba = null;
            reason = buffer.ToString();
            fallback = result == 2;
            return false;
        }

        // ---- Whole images (API 12+). ----

        internal static bool SupportsImageBuild =>
            IsSupportedPlatform && LoadedApiVersion >= ImageBuildApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr RUsd_ImageBuild(byte[] data, int length, int srgb, int mode, int ormRepack,
                                                     StringBuilder reasonBuffer, int reasonCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr RUsd_UdimAtlasBuild(byte[] pathsUtf8, int[] tiles, int count, int srgb, int mode,
                                                         int ormRepack, int maxSide, StringBuilder reasonBuffer,
                                                         int reasonCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ImageInfo(IntPtr handle, out int width, out int height, out int mode,
                                                 out long byteCount, out int mips, out int hasAlpha, out int columns,
                                                 out int rows, out int minU, out int minV, out int tilesUsed);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr RUsd_ImageData(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_ImageNotes(IntPtr handle, StringBuilder buffer, int capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RUsd_ImageRelease(IntPtr handle);

        /// <summary>A texture the native side built: pixels or a compressed chain it still owns.</summary>
        internal sealed class NativeImage
        {
            public IntPtr Handle;
            public int Width, Height, Mode, MipCount, Columns, Rows, MinU, MinV, TilesUsed;
            public long ByteCount;
            public bool HasAlpha;
            public string Notes;

            public IntPtr Data => RUsd_ImageData(Handle);

            public byte[] CopyBytes()
            {
                var bytes = new byte[ByteCount];
                Marshal.Copy(Data, bytes, 0, (int)ByteCount);
                return bytes;
            }

            public void Release()
            {
                if (Handle != IntPtr.Zero)
                {
                    RUsd_ImageRelease(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }

        private static NativeImage Describe(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var image = new NativeImage { Handle = handle };
            RUsd_ImageInfo(handle, out image.Width, out image.Height, out image.Mode, out image.ByteCount,
                           out image.MipCount, out int hasAlpha, out image.Columns, out image.Rows,
                           out image.MinU, out image.MinV, out image.TilesUsed);
            image.HasAlpha = hasAlpha != 0;
            var notes = new StringBuilder(4096);
            RUsd_ImageNotes(handle, notes, notes.Capacity);
            image.Notes = notes.ToString();
            return image;
        }

        /// <summary>
        /// Decodes one PNG or EXR and finishes it in the requested form (0 RGBA, 1 BC1/BC3 chain,
        /// 2 red channel only). Null, with a reason, when the bytes are not an image this reads.
        /// </summary>
        internal static NativeImage BuildImage(byte[] data, bool srgb, int mode, bool ormRepack, out string reason)
        {
            var buffer = new StringBuilder(512);
            IntPtr handle = RUsd_ImageBuild(data, data.Length, srgb ? 1 : 0, mode, ormRepack ? 1 : 0, buffer, buffer.Capacity);
            reason = handle == IntPtr.Zero ? buffer.ToString() : null;
            return Describe(handle);
        }

        /// <summary>
        /// Reads, decodes and stitches the tiles of a UDIM set into one atlas, finished like
        /// <see cref="BuildImage"/>. Null, with a reason, when no tile could be used.
        /// </summary>
        internal static NativeImage BuildUdimAtlas(IList<string> paths, IList<int> tiles, bool srgb, int mode,
                                                   bool ormRepack, int maxSide, out string reason)
        {
            byte[] joined = Encoding.UTF8.GetBytes(string.Join("\n", paths) + "\0");
            var tileArray = new int[tiles.Count];
            tiles.CopyTo(tileArray, 0);
            var buffer = new StringBuilder(4096);
            IntPtr handle = RUsd_UdimAtlasBuild(joined, tileArray, tileArray.Length, srgb ? 1 : 0, mode,
                                                ormRepack ? 1 : 0, maxSide, buffer, buffer.Capacity);
            reason = handle == IntPtr.Zero ? buffer.ToString() : null;
            return Describe(handle);
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr RUsd_PackMetallicRoughness(byte[] metallic, int metallicWidth, int metallicHeight,
                                                                int metallicStride, byte[] roughness, int roughnessWidth,
                                                                int roughnessHeight, int roughnessStride, int metallicConstant,
                                                                int smoothnessConstant, int mode);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr RUsd_PackAlbedoOpacity(byte[] albedo, int albedoWidth, int albedoHeight,
                                                            byte[] opacity, int opacityWidth, int opacityHeight,
                                                            int opacityStride, int srgb, int mode);

        /// <summary>Unity's packed metallic map, compressed, from a metallic and/or roughness map.</summary>
        internal static NativeImage PackMetallicRoughness(byte[] metallic, int metallicWidth, int metallicHeight, int metallicStride,
                                                          byte[] roughness, int roughnessWidth, int roughnessHeight, int roughnessStride,
                                                          int metallicConstant, int smoothnessConstant)
        {
            return Describe(RUsd_PackMetallicRoughness(metallic, metallicWidth, metallicHeight, metallicStride,
                                                       roughness, roughnessWidth, roughnessHeight, roughnessStride,
                                                       metallicConstant, smoothnessConstant, 1));
        }

        /// <summary>An albedo with an opacity map in its alpha, compressed.</summary>
        internal static NativeImage PackAlbedoOpacity(byte[] albedo, int albedoWidth, int albedoHeight,
                                                      byte[] opacity, int opacityWidth, int opacityHeight, int opacityStride, bool srgb)
        {
            return Describe(RUsd_PackAlbedoOpacity(albedo, albedoWidth, albedoHeight, opacity, opacityWidth, opacityHeight,
                                                   opacityStride, srgb ? 1 : 0, 1));
        }

        /// <summary>
        /// The BC1/BC3 chain of an RGBA8 image, built in native code; null when the size is not a
        /// positive multiple of 4, as the managed encoder returns.
        /// </summary>
        internal static byte[] BlockCompress(byte[] rgba, int width, int height, bool alpha, bool srgb, out int mipCount)
        {
            long size = RUsd_BlockCompressedSize(width, height, alpha ? 1 : 0, out mipCount);
            if (size <= 0)
            {
                mipCount = 0;
                return null;
            }

            var output = new byte[size];
            if (RUsd_BlockCompress(rgba, width, height, alpha ? 1 : 0, srgb ? 1 : 0, output, size) != 0)
            {
                mipCount = 0;
                return null;
            }

            return output;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_GetImportMaterialTextureLayout(
            IntPtr context,
            int materialIndex,
            out int metallicIsPlain,
            StringBuilder roughnessTexturePathBuffer,
            int roughnessTexturePathBufferCapacity);

        /// <summary>
        /// Whether <c>RUsdMaterial.metallicTexturePath</c> is a plain metallic map (metallic in
        /// red, nothing in alpha), and the roughness map the material carries in a file of its own.
        /// </summary>
        /// <remarks>
        /// Every DCC writes metallic and roughness as two greyscale images; Unity reads one map
        /// with metallic in red and smoothness in alpha. The two are reported as authored and the
        /// importer folds them together while it still holds the decoded pixels.
        /// </remarks>
        internal static bool GetImportMaterialTextureLayout(
            IntPtr context, int materialIndex, out bool metallicIsPlain, out string roughnessPath)
        {
            metallicIsPlain = false;
            roughnessPath = null;
            if (!SupportsTextureLayout)
            {
                return false;
            }

            var buffer = new StringBuilder(ErrorBufferSize);
            Check(context, RUsd_GetImportMaterialTextureLayout(
                context, materialIndex, out int plain, buffer, buffer.Capacity));
            metallicIsPlain = plain != 0;
            roughnessPath = buffer.Length > 0 ? buffer.ToString() : null;
            return true;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportInstancedMeshCount(IntPtr context, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportInstancedMesh(
            IntPtr context, int index, out RUsdImportInstancedMesh instanced);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportInstanceMatrices(
            IntPtr context, int index, float[] matrices, int capacity);

        internal static List<KeyValuePair<RUsdImportInstancedMesh, float[]>> GetImportInstancedMeshes(
            IntPtr context)
        {
            var instanced = new List<KeyValuePair<RUsdImportInstancedMesh, float[]>>();
            if (!SupportsInstancedMeshes)
            {
                return instanced;
            }

            Check(context, RUsd_GetImportInstancedMeshCount(context, out int count));
            for (int i = 0; i < count; i++)
            {
                Check(context, RUsd_GetImportInstancedMesh(context, i, out RUsdImportInstancedMesh record));
                // The native walk caps all placements at kMaxImportInstancePlacements; a count past
                // it, or negative, is not a payload this source built. Refused rather than sized
                // from: record.InstanceCount * 16 in int arithmetic wraps for counts over 2^27.
                if (record.InstanceCount < 0 || record.InstanceCount > MaxImportInstancePlacements)
                {
                    throw new InvalidOperationException(
                        $"USD import: point instancer record {i} reports {record.InstanceCount} instances, " +
                        $"outside 0-{MaxImportInstancePlacements}.");
                }

                var matrices = new float[record.InstanceCount * 16];
                if (matrices.Length > 0)
                {
                    Check(context, RUsd_CopyImportInstanceMatrices(context, i, matrices, matrices.Length));
                }

                instanced.Add(new KeyValuePair<RUsdImportInstancedMesh, float[]>(record, matrices));
            }

            return instanced;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportLightCount(IntPtr context, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportLight(
            IntPtr context, int index, out RUsdImportLight light, byte[] texturePath, int capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportCameraCount(IntPtr context, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportCamera(IntPtr context, int index, out RUsdImportCamera camera);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportAudioCount(IntPtr context, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportAudio(
            IntPtr context, int index, out RUsdImportAudio audio, byte[] filePath, int capacity);

        // Paths come back as UTF-8, so non-ASCII folder names survive.
        private static string Utf8(byte[] buffer)
        {
            int length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }

        internal static List<KeyValuePair<RUsdImportLight, string>> GetImportLights(IntPtr context)
        {
            var lights = new List<KeyValuePair<RUsdImportLight, string>>();
            Check(context, RUsd_GetImportLightCount(context, out int count));
            var buffer = new byte[ErrorBufferSize];
            for (int i = 0; i < count; i++)
            {
                Array.Clear(buffer, 0, buffer.Length);
                Check(context, RUsd_GetImportLight(context, i, out RUsdImportLight light, buffer, buffer.Length));
                lights.Add(new KeyValuePair<RUsdImportLight, string>(light, Utf8(buffer)));
            }

            return lights;
        }

        internal static List<RUsdImportCamera> GetImportCameras(IntPtr context)
        {
            var cameras = new List<RUsdImportCamera>();
            Check(context, RUsd_GetImportCameraCount(context, out int count));
            for (int i = 0; i < count; i++)
            {
                Check(context, RUsd_GetImportCamera(context, i, out RUsdImportCamera camera));
                cameras.Add(camera);
            }

            return cameras;
        }

        internal static List<KeyValuePair<RUsdImportAudio, string>> GetImportAudio(IntPtr context)
        {
            var sources = new List<KeyValuePair<RUsdImportAudio, string>>();
            Check(context, RUsd_GetImportAudioCount(context, out int count));
            var buffer = new byte[ErrorBufferSize];
            for (int i = 0; i < count; i++)
            {
                Array.Clear(buffer, 0, buffer.Length);
                Check(context, RUsd_GetImportAudio(context, i, out RUsdImportAudio audio, buffer, buffer.Length));
                sources.Add(new KeyValuePair<RUsdImportAudio, string>(audio, Utf8(buffer)));
            }

            return sources;
        }

        internal static IntPtr BeginLayerExport(
            string outputPath,
            string defaultPrimPath,
            string materialScopePath,
            double metersPerUnit,
            int upAxis,
            string subLayers,
            bool defineDefaultPrim,
            bool captureDiagnostics)
        {
            ThrowIfUnsupported();
            UsdExporter.EnsureRuntimeConfigured();
            int flags = (defineDefaultPrim ? LayerDefineDefaultPrim : 0) |
                (captureDiagnostics ? LayerCaptureDiagnostics : 0);
            int result = RUsd_BeginLayerExport(
                outputPath,
                defaultPrimPath ?? string.Empty,
                materialScopePath ?? string.Empty,
                metersPerUnit,
                upAxis,
                subLayers ?? string.Empty,
                flags,
                out IntPtr context);

            if (result != 0)
            {
                string error = GetLastError(context);
                string diagnostics = GetDiagnostics(context);
                Destroy(context);
                throw new UsdExportException(error, diagnostics);
            }

            return context;
        }

        internal static void DefinePrim(IntPtr context, string primPath, string typeName, int specifier)
        {
            Check(context, RUsd_DefinePrim(context, primPath, typeName ?? string.Empty, specifier));
        }

        internal static void AddReference(IntPtr context, string primPath, string assetPath, string targetPrimPath, int arcType)
        {
            Check(context, RUsd_AddReference(context, primPath, assetPath ?? string.Empty, targetPrimPath ?? string.Empty, arcType));
        }

        internal static void SetInstanceable(IntPtr context, string primPath, bool instanceable)
        {
            Check(context, RUsd_SetInstanceable(context, primPath, instanceable ? 1 : 0));
        }

        internal static void SetActive(IntPtr context, string primPath, bool active)
        {
            Check(context, RUsd_SetActive(context, primPath, active ? 1 : 0));
        }

        internal static void SetVariantSelection(IntPtr context, string primPath, string variantSet, string variant)
        {
            Check(context, RUsd_SetVariantSelection(context, primPath, variantSet, variant ?? string.Empty));
        }

        internal static void SetLocalTransform(IntPtr context, string primPath, RUsdMatrix4x4 matrix)
        {
            Check(context, RUsd_SetLocalTransform(context, primPath, ref matrix));
        }

        internal static void SetVisibility(IntPtr context, string primPath, bool visible)
        {
            Check(context, RUsd_SetVisibility(context, primPath, visible ? 1 : 0));
        }

        internal static bool PrimExists(IntPtr context, string primPath)
        {
            Check(context, RUsd_PrimExists(context, primPath, out int exists));
            return exists != 0;
        }

        internal static IntPtr OpenStageEx(
            string inputPath,
            bool captureDiagnostics,
            bool loadNoPayloads,
            string variantSelections,
            string loadPaths)
        {
            ThrowIfUnsupported();
            UsdExporter.EnsureRuntimeConfigured();
            int flags = (captureDiagnostics ? OpenCaptureDiagnostics : 0) | (loadNoPayloads ? OpenLoadNoPayloads : 0);
            int result = RUsd_OpenStageEx(
                inputPath,
                flags,
                variantSelections ?? string.Empty,
                loadPaths ?? string.Empty,
                out IntPtr context);

            if (result != 0)
            {
                string error = GetLastError(context);
                string diagnostics = GetDiagnostics(context);
                Destroy(context);
                throw new UsdExportException(error, diagnostics);
            }

            return context;
        }

        internal static int GetImportNodeFlags(IntPtr context, int nodeIndex)
        {
            Check(context, RUsd_GetImportNodeFlags(context, nodeIndex, out int flags));
            return flags;
        }

        // The stage's composition report (see RUsd_GetImportComposition). UTF-8, so prim and
        // layer names outside ASCII come through intact.
        internal static string GetImportComposition(IntPtr context)
        {
            Check(context, RUsd_GetImportComposition(context, null, 0, out int required));
            if (required <= 1)
            {
                return string.Empty;
            }

            var buffer = new byte[required];
            Check(context, RUsd_GetImportComposition(context, buffer, buffer.Length, out required));
            return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Math.Min(required, buffer.Length) - 1));
        }

        private static void Check(IntPtr context, int result)
        {
            if (result != 0)
            {
                throw new UsdExportException(GetLastError(context), GetDiagnostics(context));
            }
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
