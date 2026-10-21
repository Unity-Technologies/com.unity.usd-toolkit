using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Unity.USDToolkit.Native
{
    internal static partial class UsdNative
    {
        private const string DllName = "UnityUSDToolkitNative";
        private const int ErrorBufferSize = 4096;
        // Exact match with the source, deliberately. This used to accept anything >= 2 so that
        // payloads lagging the source (uv0 only, no usdz) kept working while Windows and Linux
        // were rebuilt. All three desktop payloads are API 5 as of 2026-09-22, and the tolerance
        // had become a hole: fixes that are not gated on the ABI version — the security review
        // topology and asset-path fixes among them — are simply absent from an older binary, and
        // nothing in the version number says so. An older plugin is now refused outright.
        // API 6 added composition (layers, references, overrides, variant/payload control). A 5
        // was accepted while the Windows and Linux payloads caught up; since 2026-10-10 every
        // desktop payload is at least 6, so a 5 is refused. Newer plugins load, and the features
        // below switch on by the version the plugin reports. LatestApiVersion is the version the
        // source is written against (kApiVersion in UsdExporter.cpp) and is only quoted in the
        // mismatch message.
        //
        // The number goes up once per package release that changes the native interface, not once
        // per change: 0.8.0 shipped 12 and 0.8.1 ships 13. Messages name the release, not the
        // number (PluginRelease); the table of both is in BUILD_NOTES.md.
        internal const int MinimumApiVersion = 6;
        internal const int LatestApiVersion = 13;
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
        // RUsd_GetImportMaterialAlbedoMask: an albedo blended with a colour by a mask texture.
        internal const int AlbedoMaskApiVersion = 13;
        // RUsd_GetImportMaterialUnrealBlend: per-channel mask colours and a roughness range.
        internal const int UnrealBlendApiVersion = 13;
        // RUsd_GetImportTextureColorSpace: the colour space a texture's file input authors.
        internal const int TextureColorSpaceApiVersion = 13;
        // RUsd_SetMeshUvSet / RUsd_SetMeshVertexColors / RUsd_SetExportMaterialFlavors /
        // RUsd_AddLight / RUsd_AddCamera: uv1-uv2, vertex colours, OmniPBR and MaterialX, lights
        // and cameras on export. Additive: an older plugin exports without them.
        internal const int ExportExtrasApiVersion = 13;

        // RUsd_SetExportMaterialFlavors flags.
        internal const int MaterialFlavorOmniPbr = 1;
        internal const int MaterialFlavorMaterialX = 2;

        // RUsdExportLight.Type / RUsdExportCamera.Projection.
        internal const int LightDistant = 1;
        internal const int LightSphere = 2;
        internal const int LightRect = 3;
        internal const int LightDisk = 4;
        internal const int CameraPerspective = 0;
        internal const int CameraOrthographic = 1;

        // Static values read at the stage's start time, animated node tracks, skeletons, joint
        // weights and blend shapes (RUsd_GetImportTimeInfo and the skeleton/skin entry points).
        internal const int AnimationApiVersion = 13;

        // The stage API: a stage kept open to read and edit (RUsd_CreateStage, RUsd_QueryText,
        // RUsd_Get/SetAttributeValue, ...), the change record, and rereading part of a stage
        // (RUsd_ReadSubtrees) that live variant and payload switching is built on.
        internal const int StageApiVersion = 13;

        // A Draco (.drc) output path: RUsd_EndExport encodes the stage's meshes through usdDraco's
        // writer, which the OpenUSD library exports from openusd-26.05-windows-draco.patch on.
        internal const int DracoExportApiVersion = 13;

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
        internal const int OpenDefaultTime = 4; // API 13: read static values at UsdTimeCode::Default()
        internal const int OpenNoRead = 8;      // API 13: open without walking the stage
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
                    // 14-17 were development numbers before the 0.8.1 interface was renumbered 13
                    // (2026-10-20). A plugin reporting one predates parts of that interface, so it
                    // is taken for what it surely has, the 0.8.0 interface, until it is rebuilt.
                    if (cached > LatestApiVersion && cached <= LegacyDevelopmentApiVersion)
                    {
                        cached = ImageBuildApiVersion;
                    }

                    loadedApiVersion = cached;
                }

                return cached;
            }
        }

        // The highest number handed out before the renumbering; see LoadedApiVersion.
        private const int LegacyDevelopmentApiVersion = 17;

        // The package release whose native plugin first had a given interface version, for messages:
        // people read "the 0.8.1 native plugin", not an interface number.
        internal static string PluginRelease(int apiVersion)
        {
            if (apiVersion >= 13) return "0.8.1";
            if (apiVersion >= 6) return "0.8.0";
            if (apiVersion >= 4) return "0.7.1";
            if (apiVersion == 3) return "0.7.0";
            if (apiVersion == 2) return "0.6.0";
            return "0.3.0";
        }

        // "the 0.8.1 native plugin", for "needs ..." messages.
        internal static string NeedsPlugin(int apiVersion) => "the " + PluginRelease(apiVersion) + " native plugin or newer";

        // What the loaded plugin is, for the same messages.
        internal static string LoadedPluginDescription =>
            "the loaded " + NativeDllName + " is from " + PluginRelease(LoadedApiVersion);

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
        private static extern int RUsd_SetMeshUvSet(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            int uvSet,
            [In] RUsdVec2[] uv,
            int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetMeshVertexColors(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            [In] RUsdVec4[] colors,
            int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetExportMaterialFlavors(IntPtr context, int flags);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddLight(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            ref RUsdMatrix4x4 matrix,
            ref RUsdExportLight light,
            int visible);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddCamera(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string primPath,
            ref RUsdMatrix4x4 matrix,
            ref RUsdExportCamera camera,
            int visible);

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
        private static extern int RUsd_AddRelocate(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStr)] string sourcePath,
            [MarshalAs(UnmanagedType.LPStr)] string targetPath);

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
        private static extern int RUsd_OpenStageWithLimits(
            [MarshalAs(UnmanagedType.LPStr)] string inputPath,
            int flags,
            [MarshalAs(UnmanagedType.LPStr)] string variantSelections,
            [MarshalAs(UnmanagedType.LPStr)] string loadPaths,
            ref RUsdImportLimits limits,
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

        internal static bool SupportsExportExtras =>
            IsSupportedPlatform && LoadedApiVersion >= ExportExtrasApiVersion;

        internal static void SetMeshUvSet(IntPtr context, string primPath, int uvSet, RUsdVec2[] uv)
        {
            Check(context, RUsd_SetMeshUvSet(context, primPath, uvSet, uv, uv?.Length ?? 0));
        }

        internal static void SetMeshVertexColors(IntPtr context, string primPath, RUsdVec4[] colors)
        {
            Check(context, RUsd_SetMeshVertexColors(context, primPath, colors, colors?.Length ?? 0));
        }

        internal static void SetExportMaterialFlavors(IntPtr context, int flags)
        {
            Check(context, RUsd_SetExportMaterialFlavors(context, flags));
        }

        internal static void AddLight(IntPtr context, string primPath, RUsdMatrix4x4 matrix, RUsdExportLight light, bool visible)
        {
            Check(context, RUsd_AddLight(context, primPath, ref matrix, ref light, visible ? 1 : 0));
        }

        internal static void AddCamera(IntPtr context, string primPath, RUsdMatrix4x4 matrix, RUsdExportCamera camera, bool visible)
        {
            Check(context, RUsd_AddCamera(context, primPath, ref matrix, ref camera, visible ? 1 : 0));
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

        // ---- Time, animation and skinning (API v13) ----

        // Mirror the native animation budgets in UsdExporter.cpp. A record past them is not one this
        // source built, so it is refused before anything is sized from it.
        internal const int MaxImportTrackSamples = 1000000;
        internal const int MaxImportJoints = 65536;
        internal const int MaxImportInfluences = 255;

        internal static bool SupportsAnimation =>
            IsSupportedPlatform && LoadedApiVersion >= AnimationApiVersion;

        internal static bool SupportsDracoExport =>
            IsSupportedPlatform && LoadedApiVersion >= DracoExportApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportTimeInfo(IntPtr context, out RUsdImportTimeInfo info);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportNodeTrackCount(IntPtr context, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportNodeTrack(IntPtr context, int index, out RUsdImportNodeTrack track);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportNodeTrack(
            IntPtr context, int index, double[] times, int timeCapacity, float[] values, int valueCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportSkeletonCount(IntPtr context, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportSkeleton(IntPtr context, int index, out RUsdImportSkeleton skeleton);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportSkeletonJoints(
            IntPtr context, int index, byte[] jointNames, int jointNamesCapacity, int[] parents,
            float[] bindTransforms, float[] restTransforms, float[] poseTransforms, int jointCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportSkeletonAnimation(
            IntPtr context, int index, double[] times, int timeCapacity, float[] transforms, int transformCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMeshSkin(IntPtr context, int meshIndex, out RUsdImportMeshSkin skin);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportMeshSkinWeights(
            IntPtr context, int meshIndex, int[] jointIndices, float[] jointWeights, int capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportBlendShape(
            IntPtr context, int meshIndex, int shapeIndex, byte[] nameBuffer, int nameBufferCapacity,
            out int frameCount, out int hasNormals);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportBlendShapeFrame(
            IntPtr context, int meshIndex, int shapeIndex, int frame, out float weight,
            [Out] RUsdVec3[] deltaVertices, [Out] RUsdVec3[] deltaNormals, int vertexCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportMeshBlendShapeWeights(
            IntPtr context, int meshIndex, float[] weights, int weightCapacity, double[] times, int timeCapacity,
            float[] samples, int sampleCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMeshPointCache(IntPtr context, int meshIndex, out RUsdImportPointCache pointCache);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CopyImportMeshPointCache(
            IntPtr context, int meshIndex, double[] times, int timeCapacity, [Out] RUsdVec3[] points,
            [Out] RUsdVec3[] normals, int valueCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportLayers(
            IntPtr context, [Out] byte[] buffer, int bufferCapacity, out int requiredLength);

        // The layer files the stage composed from (RUsd_GetImportLayers), UTF-8.
        internal static string[] GetImportLayers(IntPtr context)
        {
            Check(context, RUsd_GetImportLayers(context, null, 0, out int required));
            if (required <= 1)
            {
                return Array.Empty<string>();
            }

            var buffer = new byte[required];
            Check(context, RUsd_GetImportLayers(context, buffer, buffer.Length, out required));
            string text = Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Math.Min(required, buffer.Length) - 1));
            return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // ---- Animation and skinning on export (API v13) ----

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetExportTimeRange(
            IntPtr context, double startTimeCode, double endTimeCode, double timeCodesPerSecond, double framesPerSecond);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetXformSamples(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string primPath, double[] times, float[] matrices, int sampleCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetVisibilitySamples(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string primPath, double[] times, int[] visible, int sampleCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetPointsSamples(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string meshPath, double[] times, RUsdVec3[] points,
            RUsdVec3[] normals, int pointCount, int sampleCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetSkinningMethod(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string meshPath, int dualQuaternion);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_DefineSkelRoot(IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string primPath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddSkeleton(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string skeletonPath, byte[] jointPaths, int jointCount,
            float[] bindTransforms, float[] restTransforms);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_BindSkinnedMesh(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string meshPath, [MarshalAs(UnmanagedType.LPStr)] string skeletonPath,
            int[] jointIndices, float[] jointWeights, int pointCount, int influences, ref RUsdMatrix4x4 geomBindTransform);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_AddBlendShapeFrame(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string meshPath, byte[] shapeName, float weight,
            int[] pointIndices, int pointCount, RUsdVec3[] offsets, RUsdVec3[] normalOffsets);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetSkeletonAnimation(
            IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string skeletonPath, byte[] blendShapeNames, int shapeCount,
            double[] times, int sampleCount, float[] jointTransforms, float[] blendShapeWeights);

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes((text ?? string.Empty) + "\0");

        internal static void SetExportTimeRange(IntPtr context, double start, double end, double timeCodesPerSecond, double framesPerSecond) =>
            Check(context, RUsd_SetExportTimeRange(context, start, end, timeCodesPerSecond, framesPerSecond));

        internal static void SetXformSamples(IntPtr context, string primPath, double[] times, float[] matrices) =>
            Check(context, RUsd_SetXformSamples(context, primPath, times, matrices, times.Length));

        internal static void SetVisibilitySamples(IntPtr context, string primPath, double[] times, int[] visible) =>
            Check(context, RUsd_SetVisibilitySamples(context, primPath, times, visible, times.Length));

        // `points` (and `normals`, which may be null) are times.Length x pointCount, USD basis.
        internal static void SetPointsSamples(IntPtr context, string meshPath, double[] times, RUsdVec3[] points,
                                              RUsdVec3[] normals, int pointCount) =>
            Check(context, RUsd_SetPointsSamples(context, meshPath, times, points, normals, pointCount, times.Length));

        internal static void SetSkinningMethod(IntPtr context, string meshPath, bool dualQuaternion) =>
            Check(context, RUsd_SetSkinningMethod(context, meshPath, dualQuaternion ? 1 : 0));

        internal static void DefineSkelRoot(IntPtr context, string primPath) =>
            Check(context, RUsd_DefineSkelRoot(context, primPath));

        internal static void AddSkeleton(IntPtr context, string skeletonPath, string[] jointPaths, float[] bind, float[] rest) =>
            Check(context, RUsd_AddSkeleton(context, skeletonPath, Utf8(string.Join("\n", jointPaths)), jointPaths.Length, bind, rest));

        internal static void BindSkinnedMesh(IntPtr context, string meshPath, string skeletonPath, int[] jointIndices,
                                             float[] jointWeights, int pointCount, int influences, RUsdMatrix4x4 geomBind) =>
            Check(context, RUsd_BindSkinnedMesh(context, meshPath, skeletonPath, jointIndices, jointWeights, pointCount,
                                                influences, ref geomBind));

        internal static void AddBlendShapeFrame(IntPtr context, string meshPath, string shapeName, float weight,
                                                int[] pointIndices, RUsdVec3[] offsets, RUsdVec3[] normalOffsets) =>
            Check(context, RUsd_AddBlendShapeFrame(context, meshPath, Utf8(shapeName), weight,
                                                   pointIndices.Length > 0 ? pointIndices : new int[1], pointIndices.Length,
                                                   offsets.Length > 0 ? offsets : new RUsdVec3[1], normalOffsets));

        // `times` null writes one sample at Default time.
        internal static void SetSkeletonAnimation(IntPtr context, string skeletonPath, string[] blendShapeNames,
                                                  double[] times, float[] jointTransforms, float[] blendShapeWeights) =>
            Check(context, RUsd_SetSkeletonAnimation(context, skeletonPath, Utf8(string.Join("\n", blendShapeNames)),
                                                     blendShapeNames.Length, times, times?.Length ?? 1, jointTransforms,
                                                     blendShapeNames.Length > 0 ? blendShapeWeights : null));

        internal static RUsdImportTimeInfo GetImportTimeInfo(IntPtr context)
        {
            Check(context, RUsd_GetImportTimeInfo(context, out RUsdImportTimeInfo info));
            return info;
        }

        // A count from the plugin, refused when it is not one this source could have produced, so
        // nothing is allocated from a corrupted or foreign payload.
        private static int CheckedCount(long value, long limit, string what)
        {
            if (value < 0 || value > limit || value > int.MaxValue)
            {
                throw new InvalidOperationException($"USD import: {what} is {value}, outside 0-{Math.Min(limit, int.MaxValue)}.");
            }

            return (int)value;
        }

        // Native out-buffers are never null: a zero-length array still gets an element, and the
        // capacity passed beside it says how many the plugin may write.
        private static T[] Buffer<T>(int length) => new T[Math.Max(1, length)];

        private static T[] Trim<T>(T[] buffer, int length) =>
            buffer.Length == length ? buffer : length == 0 ? Array.Empty<T>() : buffer;

        internal static List<UsdNativeNodeTrack> GetImportNodeTracks(IntPtr context)
        {
            var tracks = new List<UsdNativeNodeTrack>();
            Check(context, RUsd_GetImportNodeTrackCount(context, out int count));
            for (int i = 0; i < count; i++)
            {
                Check(context, RUsd_GetImportNodeTrack(context, i, out RUsdImportNodeTrack info));
                int samples = CheckedCount(info.SampleCount, MaxImportTrackSamples, $"node track {i}'s sample count");
                int width = info.Kind == 1 ? 16 : 1;
                double[] times = Buffer<double>(samples);
                float[] values = Buffer<float>(samples * width);
                Check(context, RUsd_CopyImportNodeTrack(context, i, times, samples, values, samples * width));
                tracks.Add(new UsdNativeNodeTrack
                {
                    Info = info,
                    Times = Trim(times, samples),
                    Values = Trim(values, samples * width)
                });
            }

            return tracks;
        }

        internal static List<UsdNativeSkeleton> GetImportSkeletons(IntPtr context)
        {
            var skeletons = new List<UsdNativeSkeleton>();
            Check(context, RUsd_GetImportSkeletonCount(context, out int count));
            for (int i = 0; i < count; i++)
            {
                Check(context, RUsd_GetImportSkeleton(context, i, out RUsdImportSkeleton info));
                int joints = CheckedCount(info.JointCount, MaxImportJoints, $"skeleton {i}'s joint count");
                int samples = CheckedCount(info.SampleCount, MaxImportTrackSamples, $"skeleton {i}'s sample count");
                long sampleValues = (long)samples * joints * 16;
                CheckedCount(sampleValues, int.MaxValue, $"skeleton {i}'s animation size");
                int namesLength = CheckedCount(info.JointNamesLength, (long)joints * 4096 + 1, $"skeleton {i}'s joint names length");

                byte[] names = Buffer<byte>(namesLength);
                int[] parents = Buffer<int>(joints);
                float[] bind = Buffer<float>(joints * 16);
                float[] rest = Buffer<float>(joints * 16);
                float[] pose = Buffer<float>(joints * 16);
                Check(context, RUsd_CopyImportSkeletonJoints(
                    context, i, names, namesLength, parents, bind, rest, pose, joints));

                double[] times = Buffer<double>(samples);
                float[] transforms = Buffer<float>((int)sampleValues);
                Check(context, RUsd_CopyImportSkeletonAnimation(context, i, times, samples, transforms, (int)sampleValues));

                string joined = Encoding.UTF8.GetString(names, 0, Math.Max(0, namesLength - 1));
                string[] jointPaths = joints == 0 ? Array.Empty<string>() : joined.Split('\n');
                if (jointPaths.Length != joints)
                {
                    throw new InvalidOperationException(
                        $"USD import: skeleton {i} names {jointPaths.Length} joints but reports {joints}.");
                }

                skeletons.Add(new UsdNativeSkeleton
                {
                    NodeIndex = info.NodeIndex,
                    JointPaths = jointPaths,
                    Parents = Trim(parents, joints),
                    BindTransforms = Trim(bind, joints * 16),
                    RestTransforms = Trim(rest, joints * 16),
                    PoseTransforms = Trim(pose, joints * 16),
                    Times = Trim(times, samples),
                    Samples = Trim(transforms, (int)sampleValues)
                });
            }

            return skeletons;
        }

        // The animated points of one imported mesh, or null when its points hold still.
        internal static UsdNativePointCache GetImportMeshPointCache(IntPtr context, int meshIndex, int vertexCount)
        {
            Check(context, RUsd_GetImportMeshPointCache(context, meshIndex, out RUsdImportPointCache info));
            if (info.SampleCount <= 0)
            {
                return null;
            }

            int samples = CheckedCount(info.SampleCount, MaxImportTrackSamples, $"mesh {meshIndex}'s point-cache sample count");
            if (info.VertexCount != vertexCount)
            {
                throw new InvalidOperationException($"Mesh {meshIndex}'s point cache has {info.VertexCount} vertices; the mesh has {vertexCount}.");
            }

            int values = CheckedCount((long)samples * vertexCount, int.MaxValue, $"mesh {meshIndex}'s point-cache size");
            double[] times = Buffer<double>(samples);
            RUsdVec3[] points = Buffer<RUsdVec3>(values);
            RUsdVec3[] normals = info.HasNormals != 0 ? Buffer<RUsdVec3>(values) : null;
            Check(context, RUsd_CopyImportMeshPointCache(context, meshIndex, times, samples, points, normals, values));
            return new UsdNativePointCache
            {
                Times = Trim(times, samples),
                Points = Trim(points, values),
                Normals = normals != null ? Trim(normals, values) : null
            };
        }

        // The skin of one imported mesh, or null when it is not skinned. `vertexCount` is the
        // imported vertex count (after the split), which every per-vertex array here matches.
        internal static UsdNativeSkin GetImportMeshSkin(IntPtr context, int meshIndex, int vertexCount)
        {
            Check(context, RUsd_GetImportMeshSkin(context, meshIndex, out RUsdImportMeshSkin info));
            if (info.SkeletonIndex < 0)
            {
                return null;
            }

            int influences = CheckedCount(info.InfluencesPerVertex, MaxImportInfluences, $"mesh {meshIndex}'s influences per vertex");
            int pairs = CheckedCount((long)vertexCount * influences, int.MaxValue, $"mesh {meshIndex}'s joint weight count");
            int shapes = CheckedCount(info.BlendShapeCount, MaxImportJoints, $"mesh {meshIndex}'s blend shape count");
            int samples = CheckedCount(info.BlendShapeSampleCount, MaxImportTrackSamples, $"mesh {meshIndex}'s blend-shape sample count");
            int weightValues = CheckedCount((long)samples * shapes, int.MaxValue, $"mesh {meshIndex}'s blend-shape weight count");

            int[] joints = Buffer<int>(pairs);
            float[] weights = Buffer<float>(pairs);
            if (pairs > 0)
            {
                Check(context, RUsd_CopyImportMeshSkinWeights(context, meshIndex, joints, weights, pairs));
            }

            var blendShapes = new UsdNativeBlendShape[shapes];
            var name = new byte[1024];
            for (int s = 0; s < shapes; s++)
            {
                Array.Clear(name, 0, name.Length);
                Check(context, RUsd_GetImportBlendShape(context, meshIndex, s, name, name.Length,
                                                        out int frameCount, out int hasNormals));
                int frames = CheckedCount(frameCount, 1024, $"blend shape {s}'s frame count");
                var shape = new UsdNativeBlendShape
                {
                    Name = Encoding.UTF8.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0))),
                    Weights = new float[frames],
                    DeltaVertices = new RUsdVec3[frames][],
                    DeltaNormals = hasNormals != 0 ? new RUsdVec3[frames][] : null
                };

                for (int f = 0; f < frames; f++)
                {
                    RUsdVec3[] deltas = Buffer<RUsdVec3>(vertexCount);
                    RUsdVec3[] normals = hasNormals != 0 ? Buffer<RUsdVec3>(vertexCount) : null;
                    Check(context, RUsd_CopyImportBlendShapeFrame(
                        context, meshIndex, s, f, out float weight, deltas, normals, vertexCount));
                    shape.Weights[f] = weight;
                    shape.DeltaVertices[f] = Trim(deltas, vertexCount);
                    if (shape.DeltaNormals != null)
                    {
                        shape.DeltaNormals[f] = Trim(normals, vertexCount);
                    }
                }

                blendShapes[s] = shape;
            }

            float[] staticWeights = Buffer<float>(shapes);
            double[] times = Buffer<double>(samples);
            float[] weightSamples = Buffer<float>(weightValues);
            Check(context, RUsd_CopyImportMeshBlendShapeWeights(
                context, meshIndex, staticWeights, shapes, times, samples, weightSamples, weightValues));

            return new UsdNativeSkin
            {
                SkeletonIndex = info.SkeletonIndex,
                Influences = influences,
                JointIndices = Trim(joints, pairs),
                JointWeights = Trim(weights, pairs),
                GeomBindTransform = info.GeomBindTransform,
                DualQuaternion = info.DualQuaternion != 0,
                BlendShapes = blendShapes,
                BlendShapeWeights = Trim(staticWeights, shapes),
                BlendShapeWeightTimes = Trim(times, samples),
                BlendShapeWeightSamples = Trim(weightSamples, weightValues)
            };
        }

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

        internal static bool SupportsRemoteMaterials =>
            IsSupportedPlatform && LoadedApiVersion >= TextureColorSpaceApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_SetRemoteAssetCache(string folder);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportRemoteRequestCount(IntPtr context, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_GetImportRemoteRequest(IntPtr context, int index, StringBuilder buffer, int bufferCapacity);

        /// <summary>
        /// The folder later imports read remote MDL modules from (null turns it off). API 13+.
        /// </summary>
        internal static void SetRemoteAssetCache(string folder)
        {
            if (SupportsRemoteMaterials)
            {
                RUsd_SetRemoteAssetCache(folder ?? string.Empty);
            }
        }

        /// <summary>
        /// Remote MDL modules the import met and did not find in the cache, by URL.
        /// </summary>
        internal static List<string> GetImportRemoteRequests(IntPtr context)
        {
            var urls = new List<string>();
            if (!SupportsRemoteMaterials || context == IntPtr.Zero)
            {
                return urls;
            }

            Check(context, RUsd_GetImportRemoteRequestCount(context, out int count));
            var buffer = new StringBuilder(2048);
            for (int i = 0; i < count; i++)
            {
                buffer.Clear();
                Check(context, RUsd_GetImportRemoteRequest(context, i, buffer, buffer.Capacity));
                if (buffer.Length > 0)
                {
                    urls.Add(buffer.ToString());
                }
            }

            return urls;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMaterialNormalFlip(IntPtr context, int materialIndex, out int flipU, out int flipV);

        /// <summary>
        /// Whether a material's normal map flips its tangent frame along U / V (OmniPBR's
        /// flip_tangent_u / flip_tangent_v). API 13+; false otherwise.
        /// </summary>
        internal static bool GetImportMaterialNormalFlip(IntPtr context, int materialIndex, out bool flipU, out bool flipV)
        {
            flipU = false;
            flipV = false;
            if (!SupportsTextureColorSpace)
            {
                return false;
            }

            Check(context, RUsd_GetImportMaterialNormalFlip(context, materialIndex, out int u, out int v));
            flipU = u != 0;
            flipV = v != 0;
            return true;
        }

        internal static bool SupportsTextureColorSpace =>
            IsSupportedPlatform && LoadedApiVersion >= TextureColorSpaceApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_GetImportTextureColorSpace(
            IntPtr context,
            string texturePath,
            StringBuilder buffer,
            int bufferCapacity);

        /// <summary>
        /// The colour space the stage authors for a texture path the import reported ("sRGB",
        /// "raw", ...), or null when none is authored ("auto") or the plugin predates API 13.
        /// </summary>
        internal static string GetImportTextureColorSpace(IntPtr context, string texturePath)
        {
            if (!SupportsTextureColorSpace || string.IsNullOrEmpty(texturePath))
            {
                return null;
            }

            var buffer = new StringBuilder(64);
            Check(context, RUsd_GetImportTextureColorSpace(context, texturePath, buffer, buffer.Capacity));
            return buffer.Length > 0 ? buffer.ToString() : null;
        }

        internal static bool SupportsAlbedoMask =>
            IsSupportedPlatform && LoadedApiVersion >= AlbedoMaskApiVersion;

        internal static bool SupportsUnrealBlend =>
            IsSupportedPlatform && LoadedApiVersion >= UnrealBlendApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetImportMaterialUnrealBlend(IntPtr context, int materialIndex, [Out] float[] values);

        /// <summary>
        /// How an albedo mask is used (API 13): mode -1 none, 0 a single colour where the mask is 0,
        /// 1 three colours painted by the mask's red, green and blue with the albedo in its alpha;
        /// and the roughness range lerp(min, max, ORM.g) when <paramref name="hasRoughnessRange"/>.
        /// </summary>
        internal static bool GetImportMaterialUnrealBlend(IntPtr context, int materialIndex, out int mode,
            out Vector3[] channelColours, out bool hasRoughnessRange, out Vector2 roughnessRange)
        {
            mode = -1;
            channelColours = null;
            hasRoughnessRange = false;
            roughnessRange = new Vector2(0f, 1f);
            if (!SupportsUnrealBlend)
            {
                return false;
            }

            var v = new float[13];
            Check(context, RUsd_GetImportMaterialUnrealBlend(context, materialIndex, v));
            mode = Mathf.RoundToInt(v[0]);
            channelColours = new[] { new Vector3(v[1], v[2], v[3]), new Vector3(v[4], v[5], v[6]), new Vector3(v[7], v[8], v[9]) };
            hasRoughnessRange = v[10] != 0f;
            roughnessRange = new Vector2(v[11], v[12]);
            return true;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int RUsd_GetImportMaterialAlbedoMask(
            IntPtr context,
            int materialIndex,
            StringBuilder maskTexturePathBuffer,
            int maskTexturePathBufferCapacity,
            [Out] float[] maskColorRgb);

        /// <summary>
        /// The mask texture and constant colour an albedo is blended with (API 13): the base colour
        /// is lerp(colour, albedo, mask), linear. Null when the material has no such blend.
        /// </summary>
        internal static bool GetImportMaterialAlbedoMask(
            IntPtr context, int materialIndex, out string maskPath, out Vector3 maskColor)
        {
            maskPath = null;
            maskColor = Vector3.zero;
            if (!SupportsAlbedoMask)
            {
                return false;
            }

            var buffer = new StringBuilder(ErrorBufferSize);
            var rgb = new float[3];
            Check(context, RUsd_GetImportMaterialAlbedoMask(context, materialIndex, buffer, buffer.Capacity, rgb));
            maskPath = buffer.Length > 0 ? buffer.ToString() : null;
            maskColor = new Vector3(rgb[0], rgb[1], rgb[2]);
            return maskPath != null;
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

        // API v13; the caller checks SupportsAnimation.
        internal static void AddRelocate(IntPtr context, string sourcePath, string targetPath)
        {
            Check(context, RUsd_AddRelocate(context, sourcePath, targetPath));
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

        // `limits` other than the defaults go through RUsd_OpenStageWithLimits (API v13); the
        // caller checks SupportsAnimation first.
        internal static IntPtr OpenStageEx(
            string inputPath,
            bool captureDiagnostics,
            bool loadNoPayloads,
            string variantSelections,
            string loadPaths,
            bool readAtDefaultTime = false,
            UsdImportLimits limits = null,
            bool noRead = false)
        {
            ThrowIfUnsupported();
            UsdExporter.EnsureRuntimeConfigured();
            int flags = (captureDiagnostics ? OpenCaptureDiagnostics : 0) | (loadNoPayloads ? OpenLoadNoPayloads : 0) |
                        (readAtDefaultTime ? OpenDefaultTime : 0) | (noRead ? OpenNoRead : 0);
            IntPtr context;
            int result;
            if ((limits == null || limits.IsDefault) && !noRead)
            {
                result = RUsd_OpenStageEx(inputPath, flags, variantSelections ?? string.Empty, loadPaths ?? string.Empty,
                                          out context);
            }
            else
            {
                limits = limits ?? new UsdImportLimits();
                var native = new RUsdImportLimits
                {
                    StructSize = Marshal.SizeOf<RUsdImportLimits>(),
                    SkipWhenExceeded = limits.WhenExceeded == UsdImportLimitBehavior.SkipWithWarning ? 1 : 0,
                    MaxNodes = limits.MaxNodes,
                    MaxMeshes = limits.MaxMeshes,
                    MaxInstancePlacements = limits.MaxInstancePlacements,
                    MaxGeometryElements = limits.MaxGeometryElements,
                    MaxTrackSamples = limits.MaxTrackSamples,
                    MaxAnimationValues = limits.MaxAnimationValues,
                    MaxJoints = limits.MaxJoints,
                };
                result = RUsd_OpenStageWithLimits(inputPath, flags, variantSelections ?? string.Empty,
                                                  loadPaths ?? string.Empty, ref native, out context);
            }

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
