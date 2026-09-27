using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Unity.USDToolkit.Native;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit
{
    public static class UsdExporter
    {
        /// <summary>
        /// Allows <see cref="UsdExportOptions.PluginSearchPath"/> to point outside the package's
        /// own native runtime folders. Off by default: OpenUSD's plugin registry loads and
        /// executes any library named by a plugInfo.json under that path, in-process, so an
        /// unvetted directory is arbitrary code execution. Set this from code only when you
        /// deliberately run against a custom OpenUSD install.
        ///
        /// Deliberately a static, non-serialized switch rather than a field on
        /// <see cref="UsdExportOptions"/>: that type is [Serializable], so an options object
        /// arriving from a scene, prefab or asset could otherwise carry both a hostile search
        /// path and its own permission to use it.
        /// </summary>
        public static bool AllowExternalPluginSearchPath { get; set; }

        private static readonly char[] InvalidPrimNameChars =
        {
            ' ', '-', '.', ':', '/', '\\', '(', ')', '[', ']', '{', '}', ',', ';', '\'', '"'
        };

        private static readonly object RuntimeConfigureLock = new object();
        private static bool runtimeConfigured;

        /// <summary>
        /// Guarantees the native runtime has been configured at least once before OpenUSD is
        /// asked to resolve an asset. Without it OpenUSD cannot discover its plugInfo.json files
        /// and aborts the process from PlugFindPluginResource via TfFatalError - a native abort
        /// that no managed try/catch can intercept.
        ///
        /// Public entry points already call ConfigureNativeRuntime themselves with the caller's
        /// options; this is only a backstop for direct UsdNative use. It is a no-op once any
        /// configuration has run, so an explicit UsdExportOptions.PluginSearchPath is never
        /// overwritten by defaults.
        ///
        /// Thread-safe: concurrent first callers block until configuration completes. Must never
        /// be called from the version queries GetRuntimeInfo itself issues, which would recurse.
        /// </summary>
        internal static void EnsureRuntimeConfigured()
        {
            if (Volatile.Read(ref runtimeConfigured))
            {
                return;
            }

            lock (RuntimeConfigureLock)
            {
                if (runtimeConfigured)
                {
                    return;
                }

                // Sets runtimeConfigured by way of ConfigureNativeRuntime.
                GetRuntimeInfo();
            }
        }

        public static void ExportGameObject(GameObject root, string outputPath, UsdExportOptions options = null)
        {
            ExportGameObjectWithResult(root, outputPath, options);
        }

        public static UsdRuntimeInfo GetRuntimeInfo(UsdExportOptions options = null)
        {
            if (options == null)
            {
                options = new UsdExportOptions();
            }

            NativeRuntimeInfo nativeRuntime = ConfigureNativeRuntime(options);
            int apiVersion = UsdNative.IsSupportedPlatform ? UsdNative.GetApiVersion() : 0;
            string openUsdVersion = UsdNative.IsSupportedPlatform ? UsdNative.GetOpenUsdVersion() : string.Empty;
            return new UsdRuntimeInfo(
                UsdNative.IsSupportedPlatform,
                UsdNative.NativeDllName,
                apiVersion,
                openUsdVersion,
                nativeRuntime.DllSearchPath,
                nativeRuntime.PluginSearchPath);
        }

        public static UsdExportResult ExportGameObjectWithResult(
            GameObject root,
            string outputPath,
            UsdExportOptions options = null)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path is empty.", nameof(outputPath));
            }

            if (options == null)
            {
                options = new UsdExportOptions();
            }

            if (options.MetersPerUnit <= 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(options.MetersPerUnit), "MetersPerUnit must be greater than zero.");
            }

            NativeRuntimeInfo nativeRuntime = ConfigureNativeRuntime(options);

            string fullOutputPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string rootPrimName = SanitizePrimName(string.IsNullOrWhiteSpace(options.RootPrimName)
                ? root.name
                : options.RootPrimName);

            var result = new UsdExportResult
            {
                OutputPath = fullOutputPath,
                RootPrimName = rootPrimName,
                PluginSearchPath = nativeRuntime.PluginSearchPath,
                NativeDllSearchPath = nativeRuntime.DllSearchPath
            };

            bool packageAsUsdz = IsUsdzPath(fullOutputPath);
            string stagingDirectory = null;
            IntPtr context = IntPtr.Zero;
            try
            {
                ValidateNativeApiVersion();

                // usdz is a read-only format, so it cannot be written as a stage. Write the stage
                // (and, with ExportTextures, its texture folder) into a staging directory, then
                // let OpenUSD package that directory's contents into the .usdz.
                string stagePath = fullOutputPath;
                if (packageAsUsdz)
                {
                    RequireUsdzSupport();
                    stagingDirectory = CreateUsdzStagingDirectory();
                    stagePath = Path.Combine(
                        stagingDirectory,
                        Path.GetFileNameWithoutExtension(fullOutputPath) + ".usdc");
                }

                context = UsdNative.BeginExport(
                    stagePath,
                    rootPrimName,
                    options.MetersPerUnit,
                    options.CaptureNativeDiagnostics);
                var textureContext = new TextureExportContext(stagePath, options.ExportTextures, options.IgnoreAlbedoInMetallicSlot);
                ExportMeshes(root, rootPrimName, context, options, result, textureContext);
                UsdNative.EndExport(context);
                if (packageAsUsdz)
                {
                    UsdNative.CreateUsdzPackage(
                        context,
                        stagePath,
                        fullOutputPath,
                        Path.GetFileName(stagePath),
                        options.UsdzArkitCompatible);
                }

                result.NativeDiagnostics = UsdNative.GetDiagnostics(context);
                WriteNativeDiagnosticsLog(options, result.NativeDiagnostics);

                if (options.LogExportSummary)
                {
                    Debug.Log(result.ToString());
                }

                return result;
            }
            catch (DllNotFoundException exception)
            {
                throw CreateNativeLoadException(exception, nativeRuntime);
            }
            catch (BadImageFormatException exception)
            {
                throw CreateNativeLoadException(exception, nativeRuntime);
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new UsdExportException(
                    $"Unity USD Toolkit loaded {UsdNative.NativeDllName}, but it does not expose the expected native API. Rebuild the native plugin from the package Native~ folder.",
                    exception,
                    nativeRuntime.ToDiagnosticString());
            }
            catch (UsdExportException exception)
            {
                result.NativeDiagnostics = !string.IsNullOrEmpty(exception.Diagnostics)
                    ? exception.Diagnostics
                    : UsdNative.GetDiagnostics(context);
                WriteNativeDiagnosticsLog(options, result.NativeDiagnostics);
                throw;
            }
            finally
            {
                UsdNative.Destroy(context);
                DeleteUsdzStagingDirectory(stagingDirectory);
            }
        }

        internal static bool IsUsdzPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                string.Equals(Path.GetExtension(path), ".usdz", StringComparison.OrdinalIgnoreCase);
        }

        private static void RequireUsdzSupport()
        {
            if (!UsdNative.SupportsUsdz)
            {
                throw new UsdExportException(
                    $"Writing .usdz needs native API {UsdNative.UsdzApiVersion} or newer; the loaded {UsdNative.NativeDllName} reports {UsdNative.LoadedApiVersion}. Rebuild the native plugin for this platform (see Native~/REBUILD_WINDOWS_LINUX.md), or export .usdc/.usda instead.");
            }
        }

        private static string CreateUsdzStagingDirectory()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "com.unity.usd-toolkit",
                "usdz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        // Only ever removes a directory this class created under the temp folder.
        private static void DeleteUsdzStagingDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Unity USD Toolkit: could not remove the usdz staging folder '" + directory + "': " + exception.Message);
            }
        }

        // Called by both the export and the import entry points. Newer-than-expected plugins are
        // still accepted (added entry points are additive), but an *older* one is refused rather
        // than degraded: beyond the features it cannot deliver, any fix that is not gated on the
        // ABI version is simply absent from it with nothing in the version number to say so --
        // the SECURITY-282834 topology and asset-path fixes are exactly that shape.
        internal static void ValidateNativeApiVersion()
        {
            int apiVersion = UsdNative.LoadedApiVersion;
            if (apiVersion >= UsdNative.MinimumApiVersion)
            {
                return;
            }

            throw new UsdExportException(
                $"Unity USD Toolkit native API version mismatch. This package's source is API " +
                $"{UsdNative.LatestApiVersion} and requires at least API {UsdNative.MinimumApiVersion}, " +
                $"but the loaded plugin reports API {apiVersion}. A plugin older than the source is " +
                $"missing entry points *and* any fix made since it was built, including the " +
                $"SECURITY-282834 import hardening. Rebuild {UsdNative.NativeDllName} from this " +
                $"package's Native~ sources (see Native~/REBUILD_WINDOWS_LINUX.md).");
        }

        private static void ExportMeshes(
            GameObject root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options,
            UsdExportResult result,
            TextureExportContext textureContext)
        {
            var usedPaths = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<Transform, string> xformPaths = null;
            if (options.TransformPolicy == UsdTransformPolicy.PreserveHierarchy)
            {
                xformPaths = ExportXforms(root, rootPrimName, context, options);
            }

            var filters = root.GetComponentsInChildren<MeshFilter>(options.IncludeInactive);
            foreach (MeshFilter filter in filters)
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (renderer != null && !renderer.enabled && !options.ExportDisabledRenderers)
                {
                    continue;
                }

                // A mesh with readable=false blocks CPU access (mesh.vertices and friends) in
                // Play mode. Read the GPU vertex/index buffers back into a temporary readable
                // copy, use that, and discard it after the export.
                Mesh workMesh = mesh;
                bool tempMesh = false;
                if (!mesh.isReadable)
                {
                    if (options.RequireReadableMeshes)
                    {
                        throw new UsdExportException(
                            "Mesh is not readable. Enable Read/Write Enabled on the source asset before building. " +
                            DescribeMeshSource(filter, mesh));
                    }

                    workMesh = TryCreateReadableCopyFromGpu(mesh);
                    if (workMesh == null)
                    {
                        // The GPU buffers are unreachable (no vertex/index data) -> skip.
                        continue;
                    }

                    tempMesh = true;
                }

                try
                {
                    RUsdMaterial[] materials = CreateMaterials(renderer, textureContext);
                    bool visible = !options.PreserveInactiveAndDisabledVisibility ||
                        filter.gameObject.activeInHierarchy && (renderer == null || renderer.enabled);
                    string primPath = options.TransformPolicy == UsdTransformPolicy.PreserveHierarchy
                        ? MakePreservedMeshPrimPath(root.transform, filter.transform, rootPrimName, xformPaths, usedPaths)
                        : MakeBakedMeshPrimPath(root.transform, filter.transform, rootPrimName, usedPaths);

                    BuildMeshPayload(root.transform, filter.transform, workMesh, options, out RUsdVec3[] points,
                        out int[] indices, out RUsdSubmesh[] submeshes, out RUsdVec3[] normals, out RUsdVec2[] uv0);

                    UsdNative.AddMesh(
                        context,
                        primPath,
                        points,
                        indices,
                        submeshes,
                        normals,
                        uv0,
                        materials,
                        visible,
                        options.ExportBounds);
                    result.AddMesh(
                        GetTransformPath(root.transform, filter.transform),
                        mesh.name,
                        primPath,
                        points.Length,
                        indices.Length / 3,
                        submeshes.Length,
                        materials.Length);
                }
                finally
                {
                    if (tempMesh && workMesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(workMesh);
                    }
                }
            }
        }

        // Builds a readable Mesh copy by reading the GPU vertex/index buffers back.
        // (For isReadable=false meshes in Play mode: the buffers are still in GPU memory.)
        private static Mesh TryCreateReadableCopyFromGpu(Mesh src)
        {
            int vcount = src.vertexCount;
            if (vcount == 0)
            {
                return null;
            }

            // Work out each vertex attribute's stream, byte offset within it, format and
            // dimension.
            VertexAttributeDescriptor[] attrs = src.GetVertexAttributes();
            var streamCursor = new Dictionary<int, int>();
            int posStream = -1, posOffset = 0; VertexAttributeFormat posFmt = VertexAttributeFormat.Float32;
            int nrmStream = -1, nrmOffset = 0; VertexAttributeFormat nrmFmt = VertexAttributeFormat.Float32;
            int uvStream = -1, uvOffset = 0; VertexAttributeFormat uvFmt = VertexAttributeFormat.Float32;
            foreach (var a in attrs)
            {
                int off = streamCursor.TryGetValue(a.stream, out int c) ? c : 0;
                if (a.attribute == VertexAttribute.Position) { posStream = a.stream; posOffset = off; posFmt = a.format; }
                else if (a.attribute == VertexAttribute.Normal) { nrmStream = a.stream; nrmOffset = off; nrmFmt = a.format; }
                else if (a.attribute == VertexAttribute.TexCoord0) { uvStream = a.stream; uvOffset = off; uvFmt = a.format; }
                streamCursor[a.stream] = off + VertexFormatSize(a.format) * a.dimension;
            }

            if (posStream < 0)
            {
                return null;
            }

            // Read back the streams actually in use.
            src.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            int streamCount = src.vertexBufferCount;
            var streamBytes = new Dictionary<int, byte[]>();
            var streamStride = new Dictionary<int, int>();
            for (int s = 0; s < streamCount; s++)
            {
                GraphicsBuffer gb = src.GetVertexBuffer(s);
                if (gb == null)
                {
                    continue;
                }

                AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(gb);
                req.WaitForCompletion();
                bool error = req.hasError;
                if (!error)
                {
                    streamBytes[s] = req.GetData<byte>().ToArray();
                    streamStride[s] = gb.stride;
                }

                gb.Dispose();
                if (error)
                {
                    return null;
                }
            }

            if (!streamBytes.ContainsKey(posStream))
            {
                return null;
            }

            var positions = new Vector3[vcount];
            Vector3[] normals = nrmStream >= 0 && streamBytes.ContainsKey(nrmStream) ? new Vector3[vcount] : null;
            Vector2[] uvs = uvStream >= 0 && streamBytes.ContainsKey(uvStream) ? new Vector2[vcount] : null;
            for (int i = 0; i < vcount; i++)
            {
                positions[i] = ReadVector3(streamBytes[posStream], i * streamStride[posStream] + posOffset, posFmt);
                if (normals != null) { normals[i] = ReadVector3(streamBytes[nrmStream], i * streamStride[nrmStream] + nrmOffset, nrmFmt); }
                if (uvs != null) { uvs[i] = ReadVector2(streamBytes[uvStream], i * streamStride[uvStream] + uvOffset, uvFmt); }
            }

            // Read back the index buffer.
            src.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            GraphicsBuffer ib = src.GetIndexBuffer();
            if (ib == null)
            {
                return null;
            }

            int[] allIndices;
            AsyncGPUReadbackRequest ireq = AsyncGPUReadback.Request(ib);
            ireq.WaitForCompletion();
            if (ireq.hasError)
            {
                ib.Dispose();
                return null;
            }

            byte[] ibytes = ireq.GetData<byte>().ToArray();
            ib.Dispose();
            if (src.indexFormat == IndexFormat.UInt16)
            {
                int n = ibytes.Length / 2;
                allIndices = new int[n];
                for (int i = 0; i < n; i++) { allIndices[i] = BitConverter.ToUInt16(ibytes, i * 2); }
            }
            else
            {
                int n = ibytes.Length / 4;
                allIndices = new int[n];
                for (int i = 0; i < n; i++) { allIndices[i] = (int)BitConverter.ToUInt32(ibytes, i * 4); }
            }

            var copy = new Mesh { indexFormat = src.indexFormat };
            copy.SetVertices(positions);
            if (normals != null) { copy.SetNormals(normals); }
            if (uvs != null) { copy.SetUVs(0, uvs); }
            copy.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                SubMeshDescriptor sm = src.GetSubMesh(s);
                var sub = new int[sm.indexCount];
                for (int k = 0; k < sm.indexCount; k++) { sub[k] = allIndices[sm.indexStart + k] + sm.baseVertex; }
                copy.SetIndices(sub, sm.topology, s, false);
            }

            copy.name = src.name;
            copy.RecalculateBounds();
            return copy;
        }

        private static int VertexFormatSize(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    return 2;
                default:
                    return 1; // UNorm8 / SNorm8 / UInt8 / SInt8
            }
        }

        private static float ReadComponent(byte[] data, int offset, VertexAttributeFormat format, int idx)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(data, offset + idx * 4);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(data, offset + idx * 2));
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(data, offset + idx * 2) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(BitConverter.ToInt16(data, offset + idx * 2) / 32767f, -1f);
                case VertexAttributeFormat.UNorm8: return data[offset + idx] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)data[offset + idx] / 127f, -1f);
                default: return 0f;
            }
        }

        private static Vector3 ReadVector3(byte[] data, int offset, VertexAttributeFormat format)
        {
            return new Vector3(
                ReadComponent(data, offset, format, 0),
                ReadComponent(data, offset, format, 1),
                ReadComponent(data, offset, format, 2));
        }

        private static Vector2 ReadVector2(byte[] data, int offset, VertexAttributeFormat format)
        {
            return new Vector2(
                ReadComponent(data, offset, format, 0),
                ReadComponent(data, offset, format, 1));
        }

        private static Dictionary<Transform, string> ExportXforms(
            GameObject root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options)
        {
            var usedPaths = new HashSet<string>(StringComparer.Ordinal)
            {
                "/" + rootPrimName
            };
            var xformPaths = new Dictionary<Transform, string>
            {
                { root.transform, "/" + rootPrimName }
            };

            Transform[] transforms = root.GetComponentsInChildren<Transform>(options.IncludeInactive);
            foreach (Transform transform in transforms)
            {
                if (transform == root.transform)
                {
                    continue;
                }

                bool visible = !options.PreserveInactiveAndDisabledVisibility || transform.gameObject.activeInHierarchy;
                string parentPath = xformPaths.TryGetValue(transform.parent, out string resolvedParentPath)
                    ? resolvedParentPath
                    : "/" + rootPrimName;
                string primPath = MakeUniquePath(parentPath + "/" + SanitizePrimName(transform.name), usedPaths);
                xformPaths[transform] = primPath;
                UsdNative.AddXform(context, primPath, ToUsdMatrix(GetLocalMatrix(transform)), visible);
            }

            return xformPaths;
        }

        private static void BuildMeshPayload(
            Transform root,
            Transform meshTransform,
            Mesh mesh,
            UsdExportOptions options,
            out RUsdVec3[] points,
            out int[] indices,
            out RUsdSubmesh[] submeshes,
            out RUsdVec3[] normals,
            out RUsdVec2[] uv0)
        {
            bool preserveHierarchy = options.TransformPolicy == UsdTransformPolicy.PreserveHierarchy;
            Matrix4x4 geometryMatrix = preserveHierarchy
                ? Matrix4x4.identity
                : root.worldToLocalMatrix * meshTransform.localToWorldMatrix;
            Matrix4x4 normalMatrix = geometryMatrix.inverse.transpose;
            bool reverseWinding = preserveHierarchy || Determinant3x3(geometryMatrix) >= 0.0f;

            Vector3[] vertices = mesh.vertices;
            points = new RUsdVec3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 exportPoint = geometryMatrix.MultiplyPoint3x4(vertices[i]);
                points[i] = ToUsdVector(exportPoint);
            }

            BuildSubmeshIndices(mesh, reverseWinding, out indices, out submeshes);

            Vector3[] sourceNormals = options.ExportNormals ? mesh.normals : null;
            if (sourceNormals != null && sourceNormals.Length == vertices.Length)
            {
                normals = new RUsdVec3[sourceNormals.Length];
                for (int i = 0; i < sourceNormals.Length; i++)
                {
                    Vector3 exportNormal = normalMatrix.MultiplyVector(sourceNormals[i]).normalized;
                    normals[i] = ToUsdVector(exportNormal);
                }
            }
            else
            {
                normals = Array.Empty<RUsdVec3>();
            }

            BuildUv0(mesh, options, vertices.Length, out uv0);
        }

        private static void BuildSubmeshIndices(
            Mesh mesh,
            bool reverseWinding,
            out int[] indices,
            out RUsdSubmesh[] submeshes)
        {
            int submeshCount = Math.Max(1, mesh.subMeshCount);
            var allIndices = new List<int>();
            var descriptors = new List<RUsdSubmesh>();

            for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
            {
                int[] source = submeshIndex < mesh.subMeshCount
                    ? mesh.GetTriangles(submeshIndex, true)
                    : mesh.triangles;

                if (source == null || source.Length == 0)
                {
                    continue;
                }

                int start = allIndices.Count;
                AppendTriangleIndices(allIndices, source, reverseWinding);
                descriptors.Add(new RUsdSubmesh(start, allIndices.Count - start, submeshIndex));
            }

            indices = allIndices.ToArray();
            submeshes = descriptors.ToArray();
        }

        private static void AppendTriangleIndices(List<int> destination, int[] source, bool reverseWinding)
        {
            for (int i = 0; i + 2 < source.Length; i += 3)
            {
                if (reverseWinding)
                {
                    destination.Add(source[i]);
                    destination.Add(source[i + 2]);
                    destination.Add(source[i + 1]);
                }
                else
                {
                    destination.Add(source[i]);
                    destination.Add(source[i + 1]);
                    destination.Add(source[i + 2]);
                }
            }
        }

        private static float Determinant3x3(Matrix4x4 matrix)
        {
            return
                matrix.m00 * (matrix.m11 * matrix.m22 - matrix.m12 * matrix.m21) -
                matrix.m01 * (matrix.m10 * matrix.m22 - matrix.m12 * matrix.m20) +
                matrix.m02 * (matrix.m10 * matrix.m21 - matrix.m11 * matrix.m20);
        }

        private static Matrix4x4 GetLocalMatrix(Transform transform)
        {
            return Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale);
        }

        private static RUsdMatrix4x4 ToUsdMatrix(Matrix4x4 unityMatrix)
        {
            Matrix4x4 usdMatrix = FlipX(unityMatrix).transpose;
            return new RUsdMatrix4x4
            {
                M00 = usdMatrix.m00,
                M01 = usdMatrix.m01,
                M02 = usdMatrix.m02,
                M03 = usdMatrix.m03,
                M10 = usdMatrix.m10,
                M11 = usdMatrix.m11,
                M12 = usdMatrix.m12,
                M13 = usdMatrix.m13,
                M20 = usdMatrix.m20,
                M21 = usdMatrix.m21,
                M22 = usdMatrix.m22,
                M23 = usdMatrix.m23,
                M30 = usdMatrix.m30,
                M31 = usdMatrix.m31,
                M32 = usdMatrix.m32,
                M33 = usdMatrix.m33
            };
        }

        private static Matrix4x4 FlipX(Matrix4x4 matrix)
        {
            Matrix4x4 result = matrix;
            for (int row = 0; row < 4; row++)
            {
                result[row, 0] = -result[row, 0];
            }

            for (int column = 0; column < 4; column++)
            {
                result[0, column] = -result[0, column];
            }

            return result;
        }

        private static void BuildUv0(
            Mesh mesh,
            UsdExportOptions options,
            int vertexCount,
            out RUsdVec2[] uv0)
        {
            Vector2[] sourceUv = options.ExportUv0 ? mesh.uv : null;
            if (sourceUv != null && sourceUv.Length == vertexCount)
            {
                uv0 = new RUsdVec2[sourceUv.Length];
                for (int i = 0; i < sourceUv.Length; i++)
                {
                    uv0[i] = new RUsdVec2(sourceUv[i].x, sourceUv[i].y);
                }
            }
            else
            {
                uv0 = Array.Empty<RUsdVec2>();
            }
        }

        private static RUsdVec3 ToUsdVector(Vector3 unityVector)
        {
            return new RUsdVec3(-unityVector.x, unityVector.y, unityVector.z);
        }

        private static RUsdMaterial[] CreateMaterials(Renderer renderer, TextureExportContext textureContext)
        {
            Material[] sourceMaterials = renderer != null ? renderer.sharedMaterials : null;
            if (sourceMaterials == null || sourceMaterials.Length == 0)
            {
                return new[] { CreateMaterial(null, textureContext) };
            }

            var materials = new RUsdMaterial[sourceMaterials.Length];
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                materials[i] = CreateMaterial(sourceMaterials[i], textureContext);
            }

            return materials;
        }

        private static RUsdMaterial CreateMaterial(Material material, TextureExportContext textureContext)
        {
            Color color = Color.white;
            float metallic = 0.0f;
            float roughness = 0.5f;
            string albedoPath = string.Empty;
            string normalPath = string.Empty;
            string metallicPath = string.Empty;
            Vector2 uvScale = Vector2.one;
            Vector2 uvOffset = Vector2.zero;
            Color emissive = Color.black;
            string emissivePath = string.Empty;

            if (material != null)
            {
                uvScale = material.mainTextureScale;
                uvOffset = material.mainTextureOffset;

                if (material.HasProperty("_BaseColor"))
                {
                    color = material.GetColor("_BaseColor");
                }
                else if (material.HasProperty("_Color"))
                {
                    color = material.GetColor("_Color");
                }

                if (material.HasProperty("_Metallic"))
                {
                    metallic = material.GetFloat("_Metallic");
                }

                if (material.HasProperty("_Smoothness"))
                {
                    roughness = Mathf.Clamp01(1.0f - material.GetFloat("_Smoothness"));
                }

                // Write the PBR textures (albedo / normal / metallic+smoothness) as PNGs beside
                // the USD file and take back their relative paths. When textureContext is
                // inactive (Mesh Only mode) these come back empty and the native side uses flat
                // colours only.
                if (textureContext != null)
                {
                    albedoPath = textureContext.ExportAlbedo(material);
                    normalPath = textureContext.ExportNormal(material);
                    metallicPath = textureContext.ExportMetallic(material);

                    // Corrects a common misassignment: the *same texture* plugged into both the
                    // albedo and the metallic slot (optional, on by default). MetalFrame, for
                    // instance, has its base colour texture in _MetallicGlossMap.
                    // Using that texture's .r as metallic makes the surface a near mirror
                    // (metallic around 0.7, roughness 0), so a renderer without environment
                    // reflection -- the Isaac real-time viewport, say -- shows only the black
                    // background and the object reads as pitch black.
                    // Treating it as misassigned drops the metallic texture and keeps the scalar
                    // _Metallic/_Smoothness instead, which renders as a diffuse grey.
                    // The same Texture yields the same cache path, so comparing paths detects it.
                    // Set IgnoreAlbedoInMetallicSlot=false when the two share a texture on
                    // purpose.
                    if (textureContext.IgnoreAlbedoInMetallicSlot &&
                        !string.IsNullOrEmpty(metallicPath) && metallicPath == albedoPath)
                    {
                        Debug.LogWarning(
                            $"[UsdExporter] Material '{material.name}': metallic map is the same texture as the " +
                            "base color map (likely a slot misassignment). Ignoring the metallic map and using the " +
                            "scalar _Metallic value instead, so the surface is not exported as a near-mirror metal " +
                            "(which renders black in viewers without environment reflection). " +
                            "Set UsdExportOptions.IgnoreAlbedoInMetallicSlot = false to keep the metallic map.");
                        metallicPath = string.Empty;
                    }
                }

                // Emission: only for materials with the _EMISSION keyword enabled (windows,
                // glowing panels and the like).
                if (material.IsKeywordEnabled("_EMISSION"))
                {
                    if (material.HasProperty("_EmissionColor"))
                    {
                        emissive = material.GetColor("_EmissionColor");
                    }

                    if (textureContext != null)
                    {
                        emissivePath = textureContext.ExportEmissive(material);
                    }
                }
            }

            return new RUsdMaterial
            {
                R = color.r,
                G = color.g,
                B = color.b,
                A = color.a,
                Metallic = metallic,
                Roughness = roughness,
                AlbedoTexturePath = albedoPath ?? string.Empty,
                NormalTexturePath = normalPath ?? string.Empty,
                MetallicTexturePath = metallicPath ?? string.Empty,
                UvScaleX = uvScale.x,
                UvScaleY = uvScale.y,
                UvOffsetX = uvOffset.x,
                UvOffsetY = uvOffset.y,
                EmissiveR = emissive.r,
                EmissiveG = emissive.g,
                EmissiveB = emissive.b,
                EmissiveTexturePath = emissivePath ?? string.Empty
            };
        }

        // Writes a material's albedo texture as a PNG into the "<usd-name>_textures/" folder
        // beside the USD file and returns the path relative to the USD file. Each texture is
        // written once and cached.
        private sealed class TextureExportContext
        {
            private static readonly string[] AlbedoProperties = { "_BaseMap", "_MainTex", "_BaseColorMap" };
            private static readonly string[] NormalProperties = { "_BumpMap", "_NormalMap" };
            private static readonly string[] MetallicProperties = { "_MetallicGlossMap", "_MetallicMap" };
            private static readonly string[] EmissiveProperties = { "_EmissionMap" };

            private readonly bool enabled;
            private readonly string directoryAbsolute;
            private readonly string directoryRelative;
            private readonly Dictionary<Texture, string> cache = new Dictionary<Texture, string>();

            // Whether to auto-correct the misassignment of the albedo texture into the
            // metallic slot.
            public bool IgnoreAlbedoInMetallicSlot { get; }

            public TextureExportContext(string usdOutputPath, bool enabled, bool ignoreAlbedoInMetallicSlot)
            {
                this.enabled = enabled;
                IgnoreAlbedoInMetallicSlot = ignoreAlbedoInMetallicSlot;
                string dir = Path.GetDirectoryName(usdOutputPath) ?? string.Empty;
                directoryRelative = Path.GetFileNameWithoutExtension(usdOutputPath) + "_textures";
                directoryAbsolute = Path.Combine(dir, directoryRelative);
            }

            public string ExportAlbedo(Material material) => ExportSlot(material, AlbedoProperties, sRGB: true);
            public string ExportNormal(Material material) => ExportSlot(material, NormalProperties, sRGB: false, decodeNormal: true);
            public string ExportMetallic(Material material) => ExportSlot(material, MetallicProperties, sRGB: false);
            public string ExportEmissive(Material material) => ExportSlot(material, EmissiveProperties, sRGB: true);

            // Writes the first valid texture among `properties` as a PNG and returns its path
            // relative to the USD file. sRGB=true (albedo) reads back as sRGB; false
            // (normal/metallic) reads back as linear.
            private string ExportSlot(Material material, string[] properties, bool sRGB, bool decodeNormal = false)
            {
                if (!enabled || material == null)
                {
                    return string.Empty;
                }

                Texture texture = null;
                foreach (string prop in properties)
                {
                    if (material.HasProperty(prop))
                    {
                        texture = material.GetTexture(prop);
                        if (texture != null)
                        {
                            break;
                        }
                    }
                }

                if (texture == null)
                {
                    return string.Empty;
                }

                if (cache.TryGetValue(texture, out string cached))
                {
                    return cached;
                }

                string relativePath = string.Empty;
                Texture2D readable = null;
                try
                {
                    readable = ToReadableTexture2D(texture, sRGB);
                    if (decodeNormal)
                    {
                        DecodeUnityNormalMap(readable);
                    }

                    byte[] png = readable.EncodeToPNG();
                    if (png != null && png.Length > 0)
                    {
                        Directory.CreateDirectory(directoryAbsolute);
                        string fileName = SanitizeFileName(string.IsNullOrEmpty(texture.name) ? "texture" : texture.name) + ".png";
                        File.WriteAllBytes(Path.Combine(directoryAbsolute, fileName), png);
                        relativePath = directoryRelative + "/" + fileName; // referenced relatively from the USD
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Unity USD Toolkit: texture export failed for '" + texture.name + "': " + exception.Message);
                }
                finally
                {
                    if (readable != null)
                    {
                        UnityEngine.Object.DestroyImmediate(readable);
                    }
                }

                cache[texture] = relativePath;
                return relativePath;
            }

            // Decodes a Unity normal map (DXT5nm: x=A, y=G, R close to 255; uncompressed/BC5:
            // x=R, y=G) back into a standard USD RGB normal (xyz).
            private static void DecodeUnityNormalMap(Texture2D normalTex)
            {
                Color32[] pixels = normalTex.GetPixels32();
                if (pixels.Length == 0)
                {
                    return;
                }

                // Decide the encoding from the centre pixel: DXT5nm has R close to 255 and
                // carries data in alpha.
                Color32 probe = pixels[pixels.Length / 2];
                bool dxt5nm = probe.r >= 250 && probe.a < 250;

                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    float x = (dxt5nm ? p.a : p.r) / 255f * 2f - 1f;
                    float y = p.g / 255f * 2f - 1f;
                    float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                    pixels[i] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 0, 255),
                        255);
                }

                normalTex.SetPixels32(pixels);
                normalTex.Apply();
            }

            // Produces an RGBA32 readable copy via a GPU Blit, which also works for compressed
            // and non-readable textures.
            private static Texture2D ToReadableTexture2D(Texture source, bool sRGB)
            {
                int width = source.width;
                int height = source.height;
                RenderTextureReadWrite readWrite = sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
                RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWrite);
                RenderTexture previous = RenderTexture.active;
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                return readable;
            }

            private static string SanitizeFileName(string name)
            {
                foreach (char invalid in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(invalid, '_');
                }

                return name.Replace(' ', '_');
            }
        }

        private static string MakeBakedMeshPrimPath(
            Transform root,
            Transform target,
            string rootPrimName,
            HashSet<string> usedPaths)
        {
            var names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(SanitizePrimName(current.name));
                current = current.parent;
            }

            names.Reverse();
            if (names.Count == 0)
            {
                names.Add(SanitizePrimName(target.name));
            }

            string path = "/" + rootPrimName + "/" + string.Join("/", names) + "_Mesh";
            string uniquePath = path;
            int suffix = 1;
            while (!usedPaths.Add(uniquePath))
            {
                uniquePath = path + "_" + suffix++;
            }

            return uniquePath;
        }

        private static string MakePreservedXformPrimPath(
            Transform root,
            Transform target,
            string rootPrimName,
            HashSet<string> usedPaths)
        {
            string path = "/" + rootPrimName + BuildRelativePrimPath(root, target);
            return MakeUniquePath(path, usedPaths);
        }

        private static string MakePreservedMeshPrimPath(
            Transform root,
            Transform target,
            string rootPrimName,
            Dictionary<Transform, string> xformPaths,
            HashSet<string> usedPaths)
        {
            string parentPath = xformPaths != null && xformPaths.TryGetValue(target, out string resolvedPath)
                ? resolvedPath
                : "/" + rootPrimName;
            string meshName = SanitizePrimName(target.name) + "_Mesh";
            return MakeUniquePath(parentPath + "/" + meshName, usedPaths);
        }

        private static string BuildRelativePrimPath(Transform root, Transform target)
        {
            var names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(SanitizePrimName(current.name));
                current = current.parent;
            }

            names.Reverse();
            return names.Count == 0 ? string.Empty : "/" + string.Join("/", names);
        }

        private static string MakeUniquePath(string path, HashSet<string> usedPaths)
        {
            string uniquePath = path;
            int suffix = 1;
            while (!usedPaths.Add(uniquePath))
            {
                uniquePath = path + "_" + suffix++;
            }

            return uniquePath;
        }

        private static string GetTransformPath(Transform root, Transform target)
        {
            var names = new List<string>();
            Transform current = target;
            while (current != null)
            {
                names.Add(current.name);
                if (current == root)
                {
                    break;
                }

                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static string DescribeMeshSource(MeshFilter filter, Mesh mesh)
        {
            var builder = new StringBuilder();
            builder.Append("GameObject: '");
            builder.Append(GetTransformPath(filter.transform.root, filter.transform));
            builder.Append("', Mesh: '");
            builder.Append(mesh != null ? mesh.name : "<null>");
            builder.Append("'");

#if UNITY_EDITOR
            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                builder.Append(", Asset: '");
                builder.Append(assetPath);
                builder.Append("'");
            }
#endif

            builder.Append(".");
            return builder.ToString();
        }

        private static string SanitizePrimName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Prim";
            }

            string sanitized = name.Trim();
            foreach (char invalid in InvalidPrimNameChars)
            {
                sanitized = sanitized.Replace(invalid, '_');
            }

            if (char.IsDigit(sanitized[0]))
            {
                sanitized = "_" + sanitized;
            }

            return sanitized;
        }

        private static NativeRuntimeInfo ConfigureNativeRuntime(UsdExportOptions options)
        {
            var info = new NativeRuntimeInfo();
            foreach (string basePath in GetDefaultNativeBasePaths())
            {
                info.BasePaths.Add(basePath);
            }

            string dllSearchPath = TryGetDefaultDllSearchPath();
            info.DllSearchPath = dllSearchPath;
            if (!string.IsNullOrWhiteSpace(dllSearchPath))
            {
                PrependEnvironmentPath(GetNativeLibrarySearchEnvironmentVariable(), dllSearchPath);
            }

            if (options.ValidateNativeRuntime)
            {
                ValidateNativeRuntimeFiles(info);
            }

            // The opt-out is honoured in the Editor and in development builds, where a developer
            // may be iterating on the native plugin without regenerating the manifest. A release
            // player verifies regardless: post-distribution substitution is the threat this check
            // exists for, and a shipped build is precisely where it applies (SECURITY-282834).
            if (options.VerifyNativeRuntimeIntegrity || IsReleasePlayer)
            {
                if (!options.VerifyNativeRuntimeIntegrity)
                {
                    Debug.LogWarning(
                        "Unity USD Toolkit: UsdExportOptions.VerifyNativeRuntimeIntegrity was set " +
                        "to false, but this is a release player, where the native payload is " +
                        "always verified.");
                }

                VerifyNativeRuntimeIntegrity(info);
            }

            string pluginSearchPath = options.PluginSearchPath;
            if (string.IsNullOrWhiteSpace(pluginSearchPath))
            {
                pluginSearchPath = TryGetDefaultPluginSearchPath();
            }
            else if (!AllowExternalPluginSearchPath &&
                !IsTrustedPluginSearchPath(pluginSearchPath, out string untrustedEntry))
            {
                // Rejected *before* PXR_PLUGINPATH_NAME is written: the variable is read lazily
                // by OpenUSD's plugin registry, so setting it first and validating afterwards
                // (which is what ValidateOpenUsdPluginPath used to do) leaves the process
                // already pointed at the untrusted directory.
                throw new UsdExportException(
                    "UsdExportOptions.PluginSearchPath points outside the package's own native runtime " +
                    "folders: " + untrustedEntry + ". OpenUSD loads and executes any library named by a " +
                    "plugInfo.json under this path, so an unvetted directory is refused. Set " +
                    "UsdExporter.AllowExternalPluginSearchPath from code to use a custom OpenUSD install.",
                    info.ToDiagnosticString());
            }

            info.PluginSearchPath = pluginSearchPath;
            if (!string.IsNullOrWhiteSpace(pluginSearchPath))
            {
                // Checked before the variable is written, because writing it is what lets OpenUSD
                // act on these descriptors.
                ValidatePluginDescriptorLibraryPaths(info);
                Environment.SetEnvironmentVariable("PXR_PLUGINPATH_NAME", pluginSearchPath);
            }

            if (options.ValidateOpenUsdPluginPath)
            {
                ValidatePluginSearchPath(info);
            }

            // Marks the process as configured so EnsureRuntimeConfigured stops being a no-op
            // gate for direct UsdNative callers. Set last: only a completed configuration counts.
            Volatile.Write(ref runtimeConfigured, true);
            return info;
        }

        private static void ValidateNativeRuntimeFiles(NativeRuntimeInfo info)
        {
            if (!UsdNative.IsSupportedPlatform)
            {
                return;
            }

            bool hasToolkitDll = false;
            bool hasUsdDll = false;
            bool hasTbbDll = false;
            foreach (string basePath in info.BasePaths)
            {
                if (HasAnyFile(basePath, GetToolkitNativeFileNames()))
                {
                    hasToolkitDll = true;
                }

                if (HasAnyFile(basePath, GetOpenUsdNativeFileNames()) ||
                    HasAnyMatchingFile(basePath, GetOpenUsdNativeFilePatterns()))
                {
                    hasUsdDll = true;
                }

                if (HasAnyFile(basePath, GetTbbNativeFileNames()))
                {
                    hasTbbDll = true;
                }
            }

            if (hasToolkitDll && hasUsdDll && hasTbbDll)
            {
                return;
            }

            if (!hasToolkitDll)
            {
                info.MissingRuntimeFiles.Add(string.Join(" or ", GetToolkitNativeFileNames()));
            }

            if (!hasUsdDll)
            {
                info.MissingRuntimeFiles.Add(string.Join(" or ", GetOpenUsdNativeFileNames()));
            }

            if (!hasTbbDll)
            {
                info.MissingRuntimeFiles.Add(string.Join(" or ", GetTbbNativeFileNames()));
            }

            throw new UsdExportException(
                "Unity USD Toolkit native runtime files are missing. Rebuild the native plugin and confirm the platform runtime payload is included in the project or player build.",
                info.ToDiagnosticString());
        }

        // 0 until the payload has been checked, 1 afterwards. ConfigureNativeRuntime runs on
        // every export and import entry point, and hashing ~90 MB of dylib each time would be a
        // real cost, so the check runs once per process -- the binaries cannot change under a
        // loaded process without it being restarted anyway.
        private static int integrityVerified;

        // A built, non-development player. UNITY_EDITOR covers the Editor; DEVELOPMENT_BUILD is
        // defined only in a development player.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const bool IsReleasePlayer = false;
#else
        private const bool IsReleasePlayer = true;
#endif

        // Verifies the payload against the digests recorded in NativeRuntimeHashes at build time.
        // ValidateNativeRuntimeFiles above only asks whether a file of the right name exists, which
        // cannot tell a genuine file from a substituted one; this reads the contents.
        //
        // Descriptors are covered, not only libraries. OpenUSD decides which library to load from
        // plugInfo.json, so hashing binaries alone left an attacker free to add a library and
        // repoint a descriptor at it, with the check still reporting OK (SECURITY-282834,
        // CWE-345). For the same reason an *unlisted* file inside the plugin resource trees is a
        // failure, not a warning: those trees are entirely ours, so anything extra in them was
        // added by someone else.
        //
        // This is still not a defence against someone who already has write access to the package
        // -- they could patch this assembly too. It catches substitution and addition in
        // distribution, and it makes the payload auditable: anyone can hash the shipped files and
        // compare them against the manifest.
        private static void VerifyNativeRuntimeIntegrity(NativeRuntimeInfo info)
        {
            if (!UsdNative.IsSupportedPlatform || Volatile.Read(ref integrityVerified) != 0)
            {
                return;
            }

            string root = ResolveVerificationRoot(info);
            if (string.IsNullOrEmpty(root))
            {
                // No payload root to check. ValidateNativeRuntimeFiles reports a missing payload
                // properly when it is enabled; staying silent here avoids a second, vaguer error.
                return;
            }

            var mismatches = new List<string>();
            var added = new List<string>();
            var unverified = new List<string>();
            int verified = 0;

            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                bool binary = IsNativeBinaryName(name);
                if (!binary && !IsPluginDescriptorName(name))
                {
                    continue;
                }

                string key = NativeRuntimeHashes.PlatformId + "/" + ToPayloadRelativePath(root, file);
                if (!NativeRuntimeHashes.Expected.TryGetValue(key, out string expected))
                {
                    if (IsInsidePluginResourceTree(root, file))
                    {
                        // We ship these trees whole, so an entry we do not know is an added file --
                        // exactly what a descriptor-redirect attack needs.
                        added.Add(key);
                    }
                    else
                    {
                        // The payload root can be a folder shared with other packages in a player
                        // build, so an unknown file beside ours is reported, not fatal.
                        unverified.Add(key);
                    }

                    continue;
                }

                string actual;
                try
                {
                    actual = ComputeSha256(file);
                }
                catch (Exception exception)
                {
                    mismatches.Add(key + " (could not be read: " + exception.Message + ")");
                    continue;
                }

                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    ++verified;
                }
                else
                {
                    mismatches.Add(key + " (expected " + expected + ", found " + actual + ")");
                }
            }

            if (mismatches.Count > 0 || added.Count > 0)
            {
                var builder = new StringBuilder();
                builder.Append("Unity USD Toolkit native runtime payload does not match the digests recorded ");
                builder.Append("for this package. The native code and the plugin descriptors that decide which ");
                builder.Append("library to load are read in-process, so a payload that does not match its ");
                builder.Append("manifest is refused.");

                if (mismatches.Count > 0)
                {
                    builder.Append(" Changed: ").Append(string.Join("; ", mismatches)).Append('.');
                }

                if (added.Count > 0)
                {
                    builder.Append(" Not in the manifest but present in a plugin resource tree: ")
                        .Append(string.Join(", ", added)).Append('.');
                }

                builder.Append(" If you rebuilt the native plugin yourself, regenerate the manifest with ");
                builder.Append("'python3 Native~/generate_native_hashes.py'; otherwise re-acquire the package. ");
                builder.Append("Set UsdExportOptions.VerifyNativeRuntimeIntegrity to false to skip this check.");

                throw new UsdExportException(builder.ToString(), info.ToDiagnosticString());
            }

            if (unverified.Count > 0)
            {
                Debug.LogWarning(
                    "Unity USD Toolkit: no recorded digest for " + string.Join(", ", unverified) +
                    "; those files sit beside the payload rather than inside it, so their contents " +
                    "were not verified.");
            }

            if (verified == 0)
            {
                // Nothing matched at all, which means the payload is missing rather than tampered.
                return;
            }

            Volatile.Write(ref integrityVerified, 1);
        }

        // The payload root to verify: the base path that actually holds the toolkit library. The
        // other base paths are search locations for the dynamic loader, and on Linux one of them
        // (.../Linux/lib) sits inside another, which would make the same file appear under two
        // different relative keys.
        private static string ResolveVerificationRoot(NativeRuntimeInfo info)
        {
            string[] toolkitNames = GetToolkitNativeFileNames();
            foreach (string basePath in info.BasePaths)
            {
                if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
                {
                    continue;
                }

                foreach (string toolkitName in toolkitNames)
                {
                    if (File.Exists(Path.Combine(basePath, toolkitName)))
                    {
                        return Path.GetFullPath(basePath);
                    }
                }
            }

            return null;
        }

        // Path relative to the payload root, with forward slashes, matching the keys the
        // generator writes.
        private static string ToPayloadRelativePath(string root, string file)
        {
            string full = Path.GetFullPath(file);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string relative = full.StartsWith(prefix, PathComparison)
                ? full.Substring(prefix.Length)
                : Path.GetFileName(full);
            return relative.Replace('\\', '/');
        }

        // The OpenUSD plugin resource trees, which this package ships in their entirety.
        private static readonly string[] PluginResourceTrees = { "lib/usd", "plugin/usd", "share/usd" };

        private static bool IsInsidePluginResourceTree(string root, string file)
        {
            string relative = ToPayloadRelativePath(root, file);
            foreach (string tree in PluginResourceTrees)
            {
                if (relative.StartsWith(tree + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int descriptorLibraryPathsValidated;

        // Every plugInfo.json names the library OpenUSD should load for that plugin, and OpenUSD
        // loads it in-process. A descriptor that points outside this package's own payload is
        // therefore arbitrary code execution, so each LibraryPath is resolved and confined before
        // PXR_PLUGINPATH_NAME is set (SECURITY-282834, CWE-345). The digest check above notices a
        // descriptor that was *edited*; this notices one that points somewhere it should not,
        // which also covers the case where the check is switched off.
        //
        // This package's payload is a monolithic OpenUSD build, so every shipped LibraryPath is
        // empty -- the code lives in the one library already loaded. A non-empty value is
        // therefore unusual enough to be worth confining rather than trusting.
        private static void ValidatePluginDescriptorLibraryPaths(NativeRuntimeInfo info)
        {
            if (!UsdNative.IsSupportedPlatform ||
                Volatile.Read(ref descriptorLibraryPathsValidated) != 0)
            {
                return;
            }

            string root = ResolveVerificationRoot(info);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var offenders = new List<string>();
            foreach (string descriptor in Directory.GetFiles(root, "plugInfo.json", SearchOption.AllDirectories))
            {
                string text;
                try
                {
                    text = File.ReadAllText(descriptor);
                }
                catch (Exception exception)
                {
                    offenders.Add(ToPayloadRelativePath(root, descriptor) +
                        " (could not be read: " + exception.Message + ")");
                    continue;
                }

                foreach (Match match in LibraryPathPattern.Matches(text))
                {
                    string value = match.Groups[1].Value;
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    // OpenUSD anchors a relative LibraryPath on the descriptor's own folder.
                    string candidate = value.Replace('/', Path.DirectorySeparatorChar);
                    string resolved;
                    try
                    {
                        resolved = Path.IsPathRooted(candidate)
                            ? Path.GetFullPath(candidate)
                            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(descriptor) ?? root, candidate));
                    }
                    catch (Exception)
                    {
                        offenders.Add(ToPayloadRelativePath(root, descriptor) + " -> '" + value + "' (unusable path)");
                        continue;
                    }

                    string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
                    if (!resolved.StartsWith(prefix, PathComparison))
                    {
                        offenders.Add(ToPayloadRelativePath(root, descriptor) + " -> '" + value + "'");
                    }
                }
            }

            if (offenders.Count > 0)
            {
                throw new UsdExportException(
                    "Unity USD Toolkit refused to configure OpenUSD plugin discovery: " +
                    string.Join("; ", offenders) +
                    ". A plugInfo.json names the library OpenUSD loads in-process, so a descriptor " +
                    "pointing outside this package's native payload is not loaded. Re-acquire the " +
                    "package if you did not edit these files yourself.",
                    info.ToDiagnosticString());
            }

            Volatile.Write(ref descriptorLibraryPathsValidated, 1);
        }

        private static readonly Regex LibraryPathPattern =
            new Regex("\"LibraryPath\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        private static bool IsNativeBinaryName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                // libtbb.so.2 and friends: a version suffix after .so
                name.IndexOf(".so.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The files that tell OpenUSD which library to load and what it provides.
        private static bool IsPluginDescriptorName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(name, "plugInfo.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".usda", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".glslfx", StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        // Layout diagnostic only: reports a plugin path that exists but has no plugInfo.json
        // under it, so schema/shader discovery would fail. Whether the path may be used at all
        // is decided earlier, by IsTrustedPluginSearchPath in ConfigureNativeRuntime — a
        // manifest being present says nothing about the directory being trustworthy.
        private static void ValidatePluginSearchPath(NativeRuntimeInfo info)
        {
            if (!UsdNative.IsSupportedPlatform)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(info.PluginSearchPath))
            {
                throw new UsdExportException(
                    "OpenUSD plugin discovery path is empty. The package could not find plugin/usd, lib/usd, or share/usd/plugins next to the native runtime files.",
                    info.ToDiagnosticString());
            }

            bool foundPlugInfo = false;
            foreach (string path in info.PluginSearchPath.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!Directory.Exists(path))
                {
                    info.PluginPathWarnings.Add("Missing directory: " + path);
                    continue;
                }

                if (File.Exists(Path.Combine(path, "plugInfo.json")) ||
                    Directory.GetFiles(path, "plugInfo.json", SearchOption.AllDirectories).Length > 0)
                {
                    foundPlugInfo = true;
                }
                else
                {
                    info.PluginPathWarnings.Add("No plugInfo.json under: " + path);
                }
            }

            if (!foundPlugInfo)
            {
                throw new UsdExportException(
                    "OpenUSD plugin discovery path does not contain any plugInfo.json files. USD schema and shader discovery will fail.",
                    info.ToDiagnosticString());
            }
        }

        // True when every entry of `searchPath` canonicalizes to one of the package's own native
        // runtime folders, or a directory beneath one. This is a check on the *location*: the
        // plugInfo.json presence test in ValidatePluginSearchPath cannot distinguish the
        // package's plugin tree from an attacker's folder, because a malicious folder contains
        // exactly that file too.
        private static bool IsTrustedPluginSearchPath(string searchPath, out string rejectedEntry)
        {
            rejectedEntry = null;

            var roots = new List<string>();
            foreach (string basePath in GetDefaultNativeBasePaths())
            {
                try
                {
                    roots.Add(Path.GetFullPath(basePath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch (Exception)
                {
                    // An unusable base path cannot serve as a trust root.
                }
            }

            if (roots.Count == 0)
            {
                // No known-good root to compare against (unsupported platform): trust nothing.
                rejectedEntry = searchPath;
                return false;
            }

            foreach (string entry in searchPath.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.GetFullPath(entry)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch (Exception)
                {
                    rejectedEntry = entry;
                    return false;
                }

                bool inside = false;
                foreach (string root in roots)
                {
                    if (candidate.Equals(root, PathComparison) ||
                        candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                {
                    rejectedEntry = entry;
                    return false;
                }
            }

            return true;
        }

        // Linux filesystems are case-sensitive, so comparing case-insensitively there would
        // accept a sibling directory differing only in case as being inside the trusted root.
#if UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
        private const StringComparison PathComparison = StringComparison.Ordinal;
#else
        private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#endif

        private static string TryGetDefaultPluginSearchPath()
        {
            var paths = new List<string>();
            foreach (string basePath in GetDefaultNativeBasePaths())
            {
                AddIfDirectoryExists(paths, Path.Combine(basePath, "plugin", "usd"));
                AddIfDirectoryExists(paths, Path.Combine(basePath, "plugin"));
                AddIfDirectoryExists(paths, Path.Combine(basePath, "lib", "usd"));
                AddIfDirectoryExists(paths, Path.Combine(basePath, "share", "usd", "plugins"));
            }

            return string.Join(Path.PathSeparator.ToString(), paths);
        }

        private static string TryGetDefaultDllSearchPath()
        {
            var paths = new List<string>();
            foreach (string basePath in GetDefaultNativeBasePaths())
            {
                AddIfDirectoryExists(paths, basePath);
            }

            return string.Join(Path.PathSeparator.ToString(), paths);
        }

        private static IEnumerable<string> GetDefaultNativeBasePaths()
        {
#if UNITY_EDITOR_WIN
            yield return Path.GetFullPath("Packages/com.unity.usd-toolkit/Runtime/Plugins/x86_64/Windows");
#elif UNITY_EDITOR_OSX
            yield return Path.GetFullPath("Packages/com.unity.usd-toolkit/Runtime/Plugins/macOS");
#elif UNITY_STANDALONE_WIN
            yield return Path.Combine(Application.dataPath, "Plugins", "x86_64", "Windows");
            yield return Path.Combine(Application.dataPath, "Plugins", "x86_64");
            yield return Path.Combine(Application.dataPath, "Plugins");
#elif UNITY_STANDALONE_OSX
            yield return Path.Combine(Application.dataPath, "PlugIns");
            yield return Path.Combine(Application.dataPath, "PlugIns", "macOS");
            yield return Path.Combine(Application.dataPath, "Plugins");
            yield return Path.Combine(Application.dataPath, "Plugins", "macOS");
#elif UNITY_EDITOR_LINUX
            // Self-contained layout: the toolkit .so sits in Linux/, its dependent .so files and
            // the schema tree in Linux/lib/ (lib/usd).
            yield return Path.GetFullPath("Packages/com.unity.usd-toolkit/Runtime/Plugins/x86_64/Linux");
            yield return Path.GetFullPath("Packages/com.unity.usd-toolkit/Runtime/Plugins/x86_64/Linux/lib");
#elif UNITY_STANDALONE_LINUX
            yield return Path.Combine(Application.dataPath, "Plugins", "x86_64", "Linux");
            yield return Path.Combine(Application.dataPath, "Plugins", "x86_64", "Linux", "lib");
            yield return Path.Combine(Application.dataPath, "Plugins", "x86_64");
            yield return Path.Combine(Application.dataPath, "Plugins");
#else
            yield break;
#endif
        }

        private static string GetNativeLibrarySearchEnvironmentVariable()
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return "DYLD_LIBRARY_PATH";
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            return "LD_LIBRARY_PATH";
#else
            return "PATH";
#endif
        }

        private static string[] GetToolkitNativeFileNames()
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new[] { "UnityUSDToolkitNative.dylib", "libUnityUSDToolkitNative.dylib", "UnityUSDToolkitNative.bundle" };
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            return new[] { "libUnityUSDToolkitNative.so", "UnityUSDToolkitNative.so" };
#else
            return new[] { "UnityUSDToolkitNative.dll" };
#endif
        }

        private static string[] GetOpenUsdNativeFileNames()
        {
            // usd_rt: the OpenUSD monolithic library renamed to something unique, to avoid a
            // base-name collision with the usd_ms bundled by another package (for example
            // com.unity.pixyz.sdk-plus). The old usd_ms name is kept as a compatibility
            // fallback.
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new[] { "libusd_rt.dylib", "libusd_ms.dylib", "libusd_m.dylib" };
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            // Linux uses a component build (no monolithic library): these are representative
            // names, and the actual detection is done by pattern.
            return new[] { "libusd_rt.so", "libusd_usd.so", "libusd_ms.so" };
#else
            return new[] { "usd_rt.dll", "usd_ms.dll", "usd_m.dll" };
#endif
        }

        private static string[] GetOpenUsdNativeFilePatterns()
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new[] { "libusd*.dylib", "usd*.dylib" };
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            return new[] { "libusd*.so" };
#else
            return new[] { "usd*.dll" };
#endif
        }

        private static string[] GetTbbNativeFileNames()
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new[] { "libtbb.dylib", "libtbb.12.dylib", "libtbbmalloc.dylib", "libtbbmalloc.2.dylib" };
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            return new[] { "libtbb.so", "libtbb.so.2", "libtbb.so.12" };
#else
            // tbb_usdrt.dll is Intel's TBB renamed so the Windows loader cannot hand us the
            // Editor's own tbb.dll instead (SECURITY-282834). tbb.dll stays in the list because a
            // payload built before that rename is still a valid payload.
            return new[] { "tbb_usdrt.dll", "tbb.dll" };
#endif
        }

        private static bool HasAnyFile(string basePath, string[] fileNames)
        {
            if (!Directory.Exists(basePath))
            {
                return false;
            }

            foreach (string fileName in fileNames)
            {
                if (File.Exists(Path.Combine(basePath, fileName)) ||
                    Directory.Exists(Path.Combine(basePath, fileName)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnyMatchingFile(string basePath, string[] patterns)
        {
            if (!Directory.Exists(basePath))
            {
                return false;
            }

            foreach (string pattern in patterns)
            {
                if (Directory.GetFiles(basePath, pattern, SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddIfDirectoryExists(List<string> paths, string path)
        {
            if (Directory.Exists(path) && !paths.Contains(path))
            {
                paths.Add(path);
            }
        }

        private static void PrependEnvironmentPath(string variableName, string value)
        {
            string existing = Environment.GetEnvironmentVariable(variableName) ?? string.Empty;
            var merged = new List<string>();
            foreach (string path in value.Split(Path.PathSeparator))
            {
                AddEnvironmentPath(merged, path);
            }

            foreach (string path in existing.Split(Path.PathSeparator))
            {
                AddEnvironmentPath(merged, path);
            }

            Environment.SetEnvironmentVariable(variableName, string.Join(Path.PathSeparator.ToString(), merged));
        }

        private static void AddEnvironmentPath(List<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            foreach (string existing in paths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            paths.Add(path);
        }

        private static UsdExportException CreateNativeLoadException(
            Exception exception,
            NativeRuntimeInfo nativeRuntime)
        {
            var message = new StringBuilder();
            message.Append("Unity USD Toolkit could not load the native plugin or one of its OpenUSD dependencies. ");
            message.Append("Confirm the platform native payload contains the UnityUSDToolkitNative plugin, OpenUSD dylib/DLLs, TBB dylib/DLLs, and the OpenUSD plugin resource folders.");

            // The payload being present is the usual cause, but not the only one: a loader can
            // also refuse a library that is there and intact because the host OS is older than
            // the one it was built for. That reads as "file not found" and sends people looking
            // for a missing file, so name the OS requirement here.
#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
            message.Append(" On Linux the payload is built on Ubuntu 24.04 and requires glibc 2.38 or newer and a libstdc++ providing GLIBCXX_3.4.32; Ubuntu 22.04 (glibc 2.35) cannot load it. Run `ldd --version` to check.");
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            message.Append(" On macOS the payload requires macOS 12.0 or newer.");
#endif
            return new UsdExportException(message.ToString(), exception, nativeRuntime.ToDiagnosticString());
        }

        private static void WriteNativeDiagnosticsLog(UsdExportOptions options, string diagnostics)
        {
            if (!options.CaptureNativeDiagnostics ||
                string.IsNullOrWhiteSpace(options.NativeDiagnosticsLogPath) ||
                string.IsNullOrWhiteSpace(diagnostics))
            {
                return;
            }

            string fullPath = Path.GetFullPath(options.NativeDiagnosticsLogPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, diagnostics);
        }

        private sealed class NativeRuntimeInfo
        {
            public readonly List<string> BasePaths = new List<string>();
            public readonly List<string> MissingRuntimeFiles = new List<string>();
            public readonly List<string> PluginPathWarnings = new List<string>();
            public string PluginSearchPath;
            public string DllSearchPath;

            public string ToDiagnosticString()
            {
                var builder = new StringBuilder();
                AppendSection(builder, "Native base paths", BasePaths);
                AppendSection(builder, "Missing runtime files", MissingRuntimeFiles);

                if (!string.IsNullOrWhiteSpace(DllSearchPath))
                {
                    builder.AppendLine("Native library search path:");
                    builder.AppendLine(DllSearchPath);
                }

                if (!string.IsNullOrWhiteSpace(PluginSearchPath))
                {
                    builder.AppendLine("PXR_PLUGINPATH_NAME:");
                    builder.AppendLine(PluginSearchPath);
                }

                AppendSection(builder, "Plugin path warnings", PluginPathWarnings);
                string searchVariable = GetNativeLibrarySearchEnvironmentVariable();
                builder.AppendLine(searchVariable + ":");
                builder.AppendLine(Environment.GetEnvironmentVariable(searchVariable) ?? string.Empty);
                return builder.ToString();
            }

            private static void AppendSection(StringBuilder builder, string title, List<string> values)
            {
                if (values.Count == 0)
                {
                    return;
                }

                builder.AppendLine(title + ":");
                foreach (string value in values)
                {
                    builder.AppendLine("- " + value);
                }
            }
        }
    }
}
