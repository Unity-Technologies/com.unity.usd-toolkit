using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Unity.USDToolkit.Native
{
    // The stage API (native API 13): a stage kept open to read and edit. Every string crosses as
    // NUL-terminated UTF-8, so prim and property names outside ASCII arrive intact on every
    // platform. Callers serialize use of one context (UsdStage holds a lock per stage).
    internal static partial class UsdNative
    {
        // RUsd_EditTarget kinds.
        internal const int EditTargetRoot = 0;
        internal const int EditTargetSession = 1;
        internal const int EditTargetLayer = 2;

        // RUsd_GetPrimInfo flags.
        internal const int PrimValid = 1;
        internal const int PrimActive = 2;
        internal const int PrimDefined = 4;
        internal const int PrimAbstract = 8;
        internal const int PrimLoaded = 16;
        internal const int PrimHasPayload = 32;
        internal const int PrimInstance = 64;
        internal const int PrimInstanceProxy = 128;
        internal const int PrimVariantSets = 256;
        internal const int PrimModel = 512;
        internal const int PrimHasReferences = 1024;

        // RUsd_QueryText queries.
        internal const int QueryDefaultPrim = 0;
        internal const int QueryChildren = 1;
        internal const int QueryAllChildren = 2;
        internal const int QueryTypeName = 3;
        internal const int QueryProperties = 4;
        internal const int QueryVariantSets = 5;
        internal const int QueryTargets = 6;
        internal const int QueryMetadata = 7;
        internal const int QueryLayers = 8;
        internal const int QueryRebuildRoot = 9;
        internal const int QueryChanges = 10;

        // RUsd_EditPrim edits.
        internal const int EditDefine = 1;
        internal const int EditOverride = 2;
        internal const int EditClass = 3;
        internal const int EditRemove = 4;
        internal const int EditActive = 5;
        internal const int EditLoad = 6;
        internal const int EditUnload = 7;
        internal const int EditTypeName = 8;
        internal const int EditMetadata = 9;
        internal const int EditInstanceable = 10;
        internal const int EditDefaultPrim = 11;
        internal const int EditSelectVariant = 12;
        internal const int EditAddVariant = 13;
        internal const int EditAddReference = 14;
        internal const int EditAddPayload = 15;
        internal const int EditClearReferences = 16;
        internal const int EditClearPayloads = 17;
        internal const int EditAddInherit = 18;
        internal const int EditAddSpecialize = 19;
        internal const int EditSetTargets = 20;
        internal const int EditRemoveProperty = 21;
        internal const int EditClearValue = 22;

        // RUsd_SetStageMetadata fields.
        internal const int StageStartTimeCode = 1;
        internal const int StageEndTimeCode = 2;
        internal const int StageTimeCodesPerSecond = 3;
        internal const int StageFramesPerSecond = 4;
        internal const int StageMetersPerUnit = 5;
        internal const int StageUpAxis = 6;

        // Value type flags (the codes themselves are UsdValueType).
        internal const int ValueArray = 0x100;
        internal const int ValueResolved = 0x200;

        // Time kinds.
        internal const int TimeDefault = 0;
        internal const int TimeCode = 1;
        internal const int TimeEarliest = 2;

        // RUsd_SetAttributeValue flags.
        internal const int SetCustom = 1;
        internal const int SetUniform = 2;
        internal const int SetBlock = 4;
        internal const int SetClear = 8;

        internal const int TransformLocal = 0;
        internal const int TransformWorld = 1;

        internal static bool SupportsStageApi => IsSupportedPlatform && LoadedApiVersion >= StageApiVersion;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_CreateStage(byte[] path, int inMemory, out IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SaveStage(IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ExportStage(IntPtr context, byte[] path, int flatten);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ReloadStage(IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetStageInfo(IntPtr context, ref RUsdStageInfo info);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetStageMetadata(IntPtr context, int field, double value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetEditTarget(
            IntPtr context, int target, byte[] layer, byte[] variantPrimPath, byte[] variantSet, byte[] variant);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetPrimInfo(IntPtr context, byte[] primPath, out int flags, out int specifier);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_QueryText(
            IntPtr context, int query, byte[] primPath, byte[] name, [Out] byte[] buffer, int bufferCapacity,
            out int requiredLength);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_EditPrim(IntPtr context, byte[] primPath, int edit, byte[] name, byte[] value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetAttributeInfo(IntPtr context, byte[] primPath, byte[] name, ref RUsdAttributeInfo info);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetAttributeValue(
            IntPtr context, byte[] primPath, byte[] name, int timeKind, double time, int valueType, IntPtr buffer,
            long bufferBytes, out long elementCount, out long requiredBytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetAttributeValue(
            IntPtr context, byte[] primPath, byte[] name, byte[] typeName, int flags, int timeKind, double time,
            int valueType, IntPtr data, long elementCount, long dataBytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetAttributeTimeSamples(
            IntPtr context, byte[] primPath, byte[] name, [Out] double[] times, int capacity, out int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_GetPrimTransform(
            IntPtr context, byte[] primPath, int space, int timeKind, double time, out RUsdMatrix4x4 matrix,
            out int resetsXformStack);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_SetPrimTransform(
            IntPtr context, byte[] primPath, int timeKind, double time, ref RUsdMatrix4x4 matrix);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ReadSubtrees(IntPtr context, byte[] rootPaths, int flags, ref RUsdImportLimits limits);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ReleaseImportData(IntPtr context);

        // NUL-terminated UTF-8, or null for a null string (the native side reads null as empty).
        private static byte[] Text(string text) => text == null ? null : Encoding.UTF8.GetBytes(text + "\0");

        private static void CheckStage(IntPtr context, int result)
        {
            if (result != 0)
            {
                throw new UsdStageException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static IntPtr CreateStage(string path, bool inMemory)
        {
            ThrowIfUnsupported();
            UsdExporter.EnsureRuntimeConfigured();
            int result = RUsd_CreateStage(Text(path), inMemory ? 1 : 0, out IntPtr context);
            if (result != 0)
            {
                string error = GetLastError(context);
                Destroy(context);
                throw new UsdStageException(error);
            }

            return context;
        }

        internal static void SaveStage(IntPtr context) => CheckStage(context, RUsd_SaveStage(context));

        internal static void ExportStage(IntPtr context, string path, bool flatten) =>
            CheckStage(context, RUsd_ExportStage(context, Text(path), flatten ? 1 : 0));

        internal static void ReloadStage(IntPtr context) => CheckStage(context, RUsd_ReloadStage(context));

        internal static RUsdStageInfo GetStageInfo(IntPtr context)
        {
            var info = new RUsdStageInfo { StructSize = Marshal.SizeOf<RUsdStageInfo>() };
            CheckStage(context, RUsd_GetStageInfo(context, ref info));
            return info;
        }

        internal static void SetStageMetadata(IntPtr context, int field, double value) =>
            CheckStage(context, RUsd_SetStageMetadata(context, field, value));

        internal static void SetEditTarget(IntPtr context, int target, string layer, string variantPrimPath, string variantSet,
                                           string variant) =>
            CheckStage(context, RUsd_SetEditTarget(context, target, Text(layer), Text(variantPrimPath), Text(variantSet),
                                                   Text(variant)));

        internal static int GetPrimInfo(IntPtr context, string primPath, out int specifier)
        {
            CheckStage(context, RUsd_GetPrimInfo(context, Text(primPath), out int flags, out specifier));
            return flags;
        }

        internal static string QueryText(IntPtr context, int query, string primPath, string name = null)
        {
            byte[] path = Text(primPath);
            byte[] field = Text(name);
            // A query's text can grow between the size query and the copy (the change record), so
            // the copy is retried with the larger size it reports.
            CheckStage(context, RUsd_QueryText(context, query, path, field, null, 0, out int required));
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (required <= 1 && query != QueryChanges)
                {
                    return string.Empty;
                }

                var buffer = new byte[Math.Max(1, required)];
                if (RUsd_QueryText(context, query, path, field, buffer, buffer.Length, out int copied) == 0)
                {
                    return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Math.Min(copied, buffer.Length) - 1));
                }

                if (copied <= buffer.Length)
                {
                    CheckStage(context, 1);
                }

                required = copied;
            }

            throw new UsdStageException("The stage kept changing while it was queried.");
        }

        internal static void EditPrim(IntPtr context, string primPath, int edit, string name = null, string value = null) =>
            CheckStage(context, RUsd_EditPrim(context, Text(primPath), edit, Text(name), Text(value)));

        internal static RUsdAttributeInfo GetAttributeInfo(IntPtr context, string primPath, string name)
        {
            var info = new RUsdAttributeInfo { StructSize = Marshal.SizeOf<RUsdAttributeInfo>() };
            CheckStage(context, RUsd_GetAttributeInfo(context, Text(primPath), Text(name), ref info));
            return info;
        }

        // The value's size: false when there is none. `elementCount` is its array length (1 for a
        // single value); the native side holds the value for the ReadAttributeValue that follows.
        internal static bool MeasureAttributeValue(IntPtr context, string primPath, string name, int timeKind, double time,
                                                   int valueType, out long elementCount, out long requiredBytes)
        {
            CheckStage(context, RUsd_GetAttributeValue(context, Text(primPath), Text(name), timeKind, time, valueType,
                                                       IntPtr.Zero, 0, out elementCount, out requiredBytes));
            return requiredBytes >= 0;
        }

        // Copies the value into `target` (an array of the wire layout, pinned for the call).
        internal static void ReadAttributeValue(IntPtr context, string primPath, string name, int timeKind, double time,
                                                int valueType, Array target, long bytes)
        {
            GCHandle pin = GCHandle.Alloc(target, GCHandleType.Pinned);
            try
            {
                CheckStage(context, RUsd_GetAttributeValue(context, Text(primPath), Text(name), timeKind, time, valueType,
                                                           pin.AddrOfPinnedObject(), bytes, out _, out long required));
                if (required > bytes)
                {
                    throw new UsdStageException($"The value of {primPath}.{name} changed while it was read.");
                }
            }
            finally
            {
                pin.Free();
            }
        }

        // Writes `data` (an array of the wire layout, or null for a block or a clear).
        internal static void WriteAttributeValue(IntPtr context, string primPath, string name, string typeName, int flags,
                                                 int timeKind, double time, int valueType, Array data, long elementCount,
                                                 long bytes)
        {
            GCHandle pin = data != null ? GCHandle.Alloc(data, GCHandleType.Pinned) : default;
            try
            {
                CheckStage(context, RUsd_SetAttributeValue(context, Text(primPath), Text(name), Text(typeName), flags, timeKind,
                                                           time, valueType, data != null ? pin.AddrOfPinnedObject() : IntPtr.Zero,
                                                           elementCount, bytes));
            }
            finally
            {
                if (data != null)
                {
                    pin.Free();
                }
            }
        }

        internal static double[] GetAttributeTimeSamples(IntPtr context, string primPath, string name)
        {
            byte[] path = Text(primPath);
            byte[] field = Text(name);
            CheckStage(context, RUsd_GetAttributeTimeSamples(context, path, field, null, 0, out int count));
            if (count <= 0)
            {
                return Array.Empty<double>();
            }

            var times = new double[count];
            CheckStage(context, RUsd_GetAttributeTimeSamples(context, path, field, times, times.Length, out count));
            return times;
        }

        internal static RUsdMatrix4x4 GetPrimTransform(IntPtr context, string primPath, int space, int timeKind, double time,
                                                       out bool resetsXformStack)
        {
            CheckStage(context, RUsd_GetPrimTransform(context, Text(primPath), space, timeKind, time, out RUsdMatrix4x4 matrix,
                                                      out int resets));
            resetsXformStack = resets != 0;
            return matrix;
        }

        internal static void SetPrimTransform(IntPtr context, string primPath, int timeKind, double time, RUsdMatrix4x4 matrix) =>
            CheckStage(context, RUsd_SetPrimTransform(context, Text(primPath), timeKind, time, ref matrix));

        internal static void ReadSubtrees(IntPtr context, string rootPaths, UsdImportLimits limits)
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
            int result = RUsd_ReadSubtrees(context, Text(rootPaths), 0, ref native);
            if (result != 0)
            {
                throw new UsdImportException(GetLastError(context), GetDiagnostics(context));
            }
        }

        internal static void ReleaseImportData(IntPtr context) => CheckStage(context, RUsd_ReleaseImportData(context));
    }
}
