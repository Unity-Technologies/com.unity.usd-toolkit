using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Unity.USDToolkit.Native;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit
{
    public static class UsdExporter
    {
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

        private static void ValidateNativeApiVersion()
        {
            // Newer-than-expected plugins are fine (the added entry points are additive); older
            // ones simply lose the features gated on UsdNative.LoadedApiVersion.
            int apiVersion = UsdNative.LoadedApiVersion;
            if (apiVersion < UsdNative.MinimumApiVersion)
            {
                throw new UsdExportException(
                    $"Unity USD Toolkit native API version mismatch. Expected {UsdNative.MinimumApiVersion} or newer, loaded {apiVersion}. Rebuild {UsdNative.NativeDllName} from this package.");
            }
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

                // readable=false 메시는 Play 모드에서 CPU 접근(mesh.vertices 등)이 막힌다.
                // GPU 정점/인덱스 버퍼를 readback해 임시 readable 사본을 만들어 사용하고, export 후 폐기한다.
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
                        // GPU 버퍼 접근 불가(정점/인덱스 데이터 없음) → skip
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

        // GPU 정점/인덱스 버퍼를 readback해 readable Mesh 사본을 생성한다.
        // (Play 모드 + isReadable=false 메시 대응: GPU 메모리엔 버퍼가 남아 있으므로 읽어온다.)
        private static Mesh TryCreateReadableCopyFromGpu(Mesh src)
        {
            int vcount = src.vertexCount;
            if (vcount == 0)
            {
                return null;
            }

            // 정점 attribute별 stream / stream 내 byte offset / format / dimension 파악
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

            // 사용 stream 버퍼 readback
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

            // 인덱스 버퍼 readback
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

                // PBR 텍스처(albedo/normal/metallic+smoothness)를 PNG로 USD 옆에 저장하고 상대경로를 받아온다.
                // textureContext가 비활성(Mesh Only 모드)이면 빈 문자열 → native는 단색만 사용.
                if (textureContext != null)
                {
                    albedoPath = textureContext.ExportAlbedo(material);
                    normalPath = textureContext.ExportNormal(material);
                    metallicPath = textureContext.ExportMetallic(material);

                    // metallic 슬롯에 albedo와 '같은 텍스처'가 꽂힌 흔한 오배치 보정(옵션, 기본 on).
                    // (예: MetalFrame은 _MetallicGlossMap에 basecolor 텍스처가 들어가 있음)
                    // 그 텍스처의 .r을 metallic로 쓰면 표면이 거의 거울(metallic≈0.7, roughness 0)이 되어,
                    // 환경 반사를 안 해주는 렌더러(예: Isaac 실시간 뷰포트)에서 검은 배경만 비쳐 새까맣게 보인다.
                    // 오배치로 판단해 metallic 텍스처를 버리고 스칼라 _Metallic/_Smoothness만 쓴다(diffuse 회색으로 보임).
                    // albedo/metallic은 같은 Texture면 동일 캐시 경로가 나오므로 경로 비교로 감지한다.
                    // 의도적으로 같은 텍스처를 공유하는 경우 IgnoreAlbedoInMetallicSlot=false로 끌 수 있다.
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

                // emission(발광): _EMISSION 키워드가 켜진 material만 (창문/발광 패널 등)
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

        // material의 albedo 텍스처를 USD 파일 옆 "<usd이름>_textures/" 폴더에 PNG로 저장하고,
        // USD 기준 상대경로를 돌려준다. 텍스처는 한 번만 저장하고 캐시한다.
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

            // metallic 슬롯에 albedo와 같은 텍스처가 꽂힌 오배치를 자동 보정할지 여부.
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

            // properties 중 첫 유효 텍스처를 PNG로 저장하고 USD 기준 상대경로를 돌려준다.
            // sRGB=true(albedo)면 sRGB로, false(normal/metallic)면 linear로 readback.
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
                        relativePath = directoryRelative + "/" + fileName; // USD가 상대경로로 참조
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

            // Unity normal map(DXT5nm: x=A·y=G, R≈255 / 비압축·BC5: x=R·y=G)을 USD 표준 RGB normal(xyz)로 복원한다.
            private static void DecodeUnityNormalMap(Texture2D normalTex)
            {
                Color32[] pixels = normalTex.GetPixels32();
                if (pixels.Length == 0)
                {
                    return;
                }

                // 중앙 픽셀로 인코딩 방식 판정 (DXT5nm이면 R이 거의 255이고 alpha에 데이터가 있음)
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

            // 압축/비-readable 텍스처도 GPU Blit으로 RGBA32 readable 사본을 만든다.
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

            string pluginSearchPath = options.PluginSearchPath;
            if (string.IsNullOrWhiteSpace(pluginSearchPath))
            {
                pluginSearchPath = TryGetDefaultPluginSearchPath();
            }

            info.PluginSearchPath = pluginSearchPath;
            if (!string.IsNullOrWhiteSpace(pluginSearchPath))
            {
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
            // self-contained 레이아웃: toolkit .so는 Linux/, 의존 .so·schema는 Linux/lib/(lib/usd)
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
            // usd_rt: OpenUSD monolithic 을 고유 이름으로 rename 해 다른 패키지(예:
            // com.unity.pixyz.sdk-plus)가 번들하는 usd_ms 와의 base 이름 충돌을 피한다.
            // (구 usd_ms 이름은 하위호환 fallback 으로 유지)
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new[] { "libusd_rt.dylib", "libusd_ms.dylib", "libusd_m.dylib" };
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            // Linux는 component 빌드(monolithic 없음) — 대표 파일명, 실제 검출은 패턴으로
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
            return new[] { "tbb.dll" };
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
