using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    public static class UsdImporter
    {
        public static UsdImportPreviewInfo GetPreviewInfo(string inputPath, UsdImportOptions options = null)
        {
            options ??= new UsdImportOptions();
            IntPtr context = IntPtr.Zero;
            try
            {
                ConfigureNativeRuntime();
                context = UsdNative.OpenStage(GetValidatedInputPath(inputPath), options.CaptureNativeDiagnostics);
                RUsdImportInfo info = UsdNative.GetImportInfo(context);

                // Totals come from the mesh table the stage read already built, so this is a
                // cheap walk over cached counts rather than a second parse.
                int triangleCount = 0;
                int vertexCount = 0;
                for (int meshIndex = 0; meshIndex < info.MeshCount; meshIndex++)
                {
                    RUsdImportMeshInfo meshInfo = UsdNative.GetImportMeshInfo(context, meshIndex);
                    triangleCount += meshInfo.IndexCount / 3;
                    vertexCount += meshInfo.PointCount;
                }

                string diagnostics = UsdNative.GetDiagnostics(context);
                WriteNativeDiagnosticsLog(options, diagnostics);
                return new UsdImportPreviewInfo(
                    Path.GetFullPath(inputPath),
                    info.DefaultPrimPath,
                    info.MetersPerUnit,
                    GetUpAxisName(info.UpAxis),
                    info.MeshCount,
                    info.MaterialCount,
                    triangleCount,
                    vertexCount,
                    diagnostics);
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
            finally
            {
                UsdNative.Destroy(context);
            }
        }

        public static async Task<UsdImportResult> ImportAsync(string inputPath, UsdImportOptions options = null)
        {
            options ??= new UsdImportOptions();

            // Native runtime validation and path checks run on the calling (main) thread.
            ConfigureNativeRuntime();
            string fullInputPath = GetValidatedInputPath(inputPath);

            // The heavy work — USD parse, buffer copies, and reading texture files — runs off
            // the main thread and produces only plain CPU data (no Unity objects).
            StageData data;
            try
            {
                data = await Task.Run(() => ReadStageData(fullInputPath, options));
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }

            // Unity object creation must happen on the main thread (this awaited continuation),
            // spread across frames so the editor/player stays responsive.
            return await BuildResultAsync(data, options);
        }

        public static UsdImportResult Import(string inputPath, UsdImportOptions options = null)
        {
            options ??= new UsdImportOptions();
            try
            {
                ConfigureNativeRuntime();
                string fullInputPath = GetValidatedInputPath(inputPath);
                StageData data = ReadStageData(fullInputPath, options);
                return BuildResult(data, options);
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
        }

        // ---- Stage read: native + file I/O only; safe to run on a background thread. ----

        private static StageData ReadStageData(string fullInputPath, UsdImportOptions options)
        {
            IntPtr context = IntPtr.Zero;
            try
            {
                context = UsdNative.OpenStage(fullInputPath, options.CaptureNativeDiagnostics);
                RUsdImportInfo info = UsdNative.GetImportInfo(context);

                var data = new StageData
                {
                    InputPath = fullInputPath,
                    BaseDirectory = Path.GetDirectoryName(fullInputPath),
                    AllowExternalAssetPaths = options.AllowExternalAssetPaths,
                    DefaultPrimPath = info.DefaultPrimPath,
                    MetersPerUnit = info.MetersPerUnit,
                    UpAxis = info.UpAxis
                };

                for (int i = 0; i < info.MaterialCount; i++)
                {
                    // Authored opacity needs API 4; older plugins simply report no transparency.
                    RUsdImportOpacity opacity = UsdNative.LoadedApiVersion >= 4
                        ? UsdNative.GetImportMaterialOpacity(context, i)
                        : default;
                    data.Materials.Add(ReadMaterialData(
                        context, data, UsdNative.GetImportMaterial(context, i), opacity, options));
                }

                // Read + decode each referenced texture file once (deduplicated across materials).
                // For ImportAsync this whole method runs on a worker thread, so the heavy PNG
                // decode happens off the main thread.
                ReadUniqueTextureBytes(context, data);
                DecodeUniqueTextures(data);

                int nodeCount = UsdNative.GetImportNodeCount(context);
                for (int i = 0; i < nodeCount; i++)
                {
                    RUsdImportNode node = UsdNative.GetImportNodeInfo(context, i);
                    data.Nodes.Add(new NodeData { Path = node.Path, LocalMatrix = node.LocalMatrix, Visible = node.Visible });
                }

                for (int i = 0; i < info.MeshCount; i++)
                {
                    data.Meshes.Add(ReadMeshData(context, i));
                }

                data.NativeDiagnostics = UsdNative.GetDiagnostics(context);
                LogNativeImportWarnings(data.NativeDiagnostics);
                WriteNativeDiagnosticsLog(options, data.NativeDiagnostics);
                return data;
            }
            finally
            {
                UsdNative.Destroy(context);
            }
        }

        private static MeshData ReadMeshData(IntPtr context, int meshIndex)
        {
            RUsdImportMeshInfo meshInfo = UsdNative.GetImportMeshInfo(context, meshIndex);

            var points = new RUsdVec3[meshInfo.PointCount];
            var indices = new int[meshInfo.IndexCount];
            var normals = meshInfo.NormalCount > 0 ? new RUsdVec3[meshInfo.NormalCount] : Array.Empty<RUsdVec3>();
            var uv0 = meshInfo.Uv0Count > 0 ? new RUsdVec2[meshInfo.Uv0Count] : Array.Empty<RUsdVec2>();
            UsdNative.CopyImportMesh(context, meshIndex, points, indices, normals, uv0);

            // Set 0 rides along with the mesh copy; the rest need API 3, so older plugins just
            // return the single set they support.
            var uvSets = new RUsdVec2[UsdNative.MaxUvSets][];
            uvSets[0] = uv0;
            for (int uvSet = 1; uvSet < uvSets.Length; uvSet++)
            {
                uvSets[uvSet] = Array.Empty<RUsdVec2>();
                if (UsdNative.LoadedApiVersion < 3)
                {
                    continue;
                }

                int uvCount = UsdNative.GetImportMeshUvSetCount(context, meshIndex, uvSet);
                if (uvCount <= 0)
                {
                    continue;
                }

                var uv = new RUsdVec2[uvCount];
                UsdNative.CopyImportMeshUvSet(context, meshIndex, uvSet, uv);
                uvSets[uvSet] = uv;
            }

            int submeshCount = UsdNative.GetImportSubmeshCount(context, meshIndex);
            var submeshes = submeshCount > 0 ? new RUsdSubmesh[submeshCount] : Array.Empty<RUsdSubmesh>();
            if (submeshCount > 0)
            {
                UsdNative.CopyImportSubmeshes(context, meshIndex, submeshes);
            }

            return new MeshData
            {
                PrimPath = meshInfo.PrimPath,
                Name = meshInfo.Name,
                Visible = meshInfo.Visible,
                MaterialIndex = meshInfo.MaterialIndex,
                Points = points,
                Indices = indices,
                Normals = normals,
                UvSets = uvSets,
                Submeshes = submeshes
            };
        }

        // ---- Build: Unity object creation; must run on the main thread. ----

        private static UsdImportResult BuildResult(StageData data, UsdImportOptions options)
        {
            try
            {
                var decodeCache = new Dictionary<string, Texture2D>();
                var materials = new List<Material>(data.Materials.Count);
                foreach (MaterialData materialData in data.Materials)
                {
                    materials.Add(BuildMaterial(materialData, options, data, decodeCache));
                }

                UsdImportResult result = CreateRootResult(data, options, materials.Count);
                Transform root = result.RootObject.transform;

                foreach (NodeData node in data.Nodes)
                {
                    BuildNode(node, root, options);
                }

                foreach (MeshData meshData in data.Meshes)
                {
                    BuildMesh(meshData, root, materials, options, result);
                }

                return result;
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
        }

        // Async build that spreads the main-thread work across frames so the editor/player stays
        // responsive. Yields whenever the per-frame budget is exceeded and reports progress.
        private static async Task<UsdImportResult> BuildResultAsync(StageData data, UsdImportOptions options)
        {
            try
            {
                float budget = Mathf.Max(1f, options.MaxMillisecondsPerFrame);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Action<float, string> progress = options.ProgressCallback;

                // Materials (texture decode) — the dominant cost; report 0 → 0.85 here. Each
                // texture is decoded on a worker thread (await), so the main thread is released
                // during decoding and large textures no longer cause per-texture hitches.
                var decodeCache = new Dictionary<string, Texture2D>();
                var materials = new List<Material>(data.Materials.Count);
                for (int i = 0; i < data.Materials.Count; i++)
                {
                    materials.Add(BuildMaterial(data.Materials[i], options, data, decodeCache));
                    progress?.Invoke(data.Materials.Count > 0 ? 0.85f * (i + 1) / data.Materials.Count : 0.85f, "Materials");
                    if (clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        await Task.Yield();
                        clock.Restart();
                    }
                }

                UsdImportResult result = CreateRootResult(data, options, materials.Count);
                Transform root = result.RootObject.transform;

                // Nodes (cheap) — 0.85 → 0.90.
                for (int i = 0; i < data.Nodes.Count; i++)
                {
                    BuildNode(data.Nodes[i], root, options);
                    if (clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        progress?.Invoke(0.85f + (data.Nodes.Count > 0 ? 0.05f * (i + 1) / data.Nodes.Count : 0f), "Hierarchy");
                        await Task.Yield();
                        clock.Restart();
                    }
                }

                // Meshes — 0.90 → 1.0.
                for (int i = 0; i < data.Meshes.Count; i++)
                {
                    BuildMesh(data.Meshes[i], root, materials, options, result);
                    progress?.Invoke(0.90f + (data.Meshes.Count > 0 ? 0.10f * (i + 1) / data.Meshes.Count : 0.10f), "Meshes");
                    if (clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        await Task.Yield();
                        clock.Restart();
                    }
                }

                progress?.Invoke(1f, "Done");
                return result;
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
        }

        private static UsdImportResult CreateRootResult(StageData data, UsdImportOptions options, int materialCount)
        {
            GameObject root = new GameObject(GetRootObjectName(data, options));
            if (options.Parent != null)
            {
                root.transform.SetParent(options.Parent, false);
            }

            return new UsdImportResult
            {
                InputPath = data.InputPath,
                RootObject = root,
                DefaultPrimPath = data.DefaultPrimPath,
                MetersPerUnit = data.MetersPerUnit,
                UpAxis = GetUpAxisName(data.UpAxis),
                MaterialCount = materialCount,
                NativeDiagnostics = data.NativeDiagnostics
            };
        }

        // Rebuilds one USD Xform node as a Unity Transform with its local transform (API v2).
        private static void BuildNode(NodeData node, Transform root, UsdImportOptions options)
        {
            GameObject nodeObject = CreatePrimPathObject(root, node.Path);
            ApplyLocalTransform(nodeObject.transform, node.LocalMatrix);
            if (!node.Visible && !options.IncludeInvisible)
            {
                nodeObject.SetActive(false);
            }
        }

        private static void BuildMesh(
            MeshData meshData,
            Transform root,
            List<Material> materials,
            UsdImportOptions options,
            UsdImportResult result)
        {
            if (!options.IncludeInvisible && !meshData.Visible)
            {
                return;
            }

            RUsdVec3[] points = meshData.Points;
            int[] indices = meshData.Indices;
            RUsdVec3[] normals = meshData.Normals;
            RUsdVec2[][] uvSets = meshData.UvSets;
            RUsdSubmesh[] submeshes = meshData.Submeshes;
            int submeshCount = submeshes.Length;

            Mesh mesh = new Mesh
            {
                name = SanitizeObjectName(string.IsNullOrEmpty(meshData.Name) ? "UsdMesh" : meshData.Name)
            };

            if (points.Length > 65535)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(ToUnityVectors(points));
            if (submeshCount > 1)
            {
                mesh.subMeshCount = submeshCount;
                for (int s = 0; s < submeshCount; s++)
                {
                    var submeshTriangles = new int[submeshes[s].IndexCount];
                    Array.Copy(indices, submeshes[s].IndexStart, submeshTriangles, 0, submeshes[s].IndexCount);
                    mesh.SetTriangles(submeshTriangles, s, true);
                }
            }
            else
            {
                mesh.SetTriangles(indices, 0, true);
            }

            if (normals.Length == points.Length)
            {
                mesh.SetNormals(ToUnityVectors(normals));
            }
            else if (options.RecalculateNormalsIfMissing)
            {
                mesh.RecalculateNormals();
            }

            for (int uvSet = 0; uvSet < uvSets.Length; uvSet++)
            {
                RUsdVec2[] uv = uvSets[uvSet];
                if (uv != null && uv.Length == points.Length)
                {
                    mesh.SetUVs(uvSet, ToUnityVector2(uv));
                }
            }

            mesh.RecalculateBounds();

            GameObject meshObject = CreatePrimPathObject(root, meshData.PrimPath);
            meshObject.SetActive(meshData.Visible);
            MeshFilter filter = meshObject.GetComponent<MeshFilter>();
            if (filter == null)
            {
                filter = meshObject.AddComponent<MeshFilter>();
            }

            filter.sharedMesh = mesh;

            MeshRenderer renderer = meshObject.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = meshObject.AddComponent<MeshRenderer>();
            }

            if (options.ImportMaterials)
            {
                if (submeshCount > 1)
                {
                    var submeshMaterials = new Material[submeshCount];
                    for (int s = 0; s < submeshCount; s++)
                    {
                        int mi = submeshes[s].MaterialIndex;
                        submeshMaterials[s] = mi >= 0 && mi < materials.Count ? materials[mi] : null;
                    }

                    renderer.sharedMaterials = submeshMaterials;
                }
                else
                {
                    int mi = submeshCount == 1 ? submeshes[0].MaterialIndex : meshData.MaterialIndex;
                    if (mi >= 0 && mi < materials.Count)
                    {
                        renderer.sharedMaterial = materials[mi];
                    }
                }
            }

            if (options.GenerateColliders)
            {
                MeshCollider collider = meshObject.GetComponent<MeshCollider>();
                if (collider == null)
                {
                    collider = meshObject.AddComponent<MeshCollider>();
                }

                collider.sharedMesh = mesh;
            }

            result.AddMesh(
                meshData.PrimPath,
                GetTransformPath(root, meshObject.transform),
                points.Length,
                indices.Length / 3,
                meshData.MaterialIndex);
        }

        private static void ApplyLocalTransform(Transform target, RUsdMatrix4x4 localMatrix)
        {
            Matrix4x4 unity = UsdLocalToUnity(localMatrix);
            target.localPosition = new Vector3(unity.m03, unity.m13, unity.m23);
            target.localRotation = unity.rotation;
            target.localScale = unity.lossyScale;
        }

        // Converts a USD local transform (row-vector convention, MrXcY = matrix[row r][col c])
        // to a Unity column-vector matrix in Unity basis: M_unity = B * transpose(M_usd) * B,
        // where B = diag(-1, 1, 1) is the USD→Unity X-axis flip. Element form:
        // M_unity[i,j] = sign(i) * sign(j) * M_usd[j][i], with sign(0) = -1 and sign(>0) = +1.
        private static Matrix4x4 UsdLocalToUnity(RUsdMatrix4x4 m)
        {
            float[,] usd =
            {
                { (float)m.M00, (float)m.M01, (float)m.M02, (float)m.M03 },
                { (float)m.M10, (float)m.M11, (float)m.M12, (float)m.M13 },
                { (float)m.M20, (float)m.M21, (float)m.M22, (float)m.M23 },
                { (float)m.M30, (float)m.M31, (float)m.M32, (float)m.M33 },
            };

            var unity = new Matrix4x4();
            for (int i = 0; i < 4; i++)
            {
                float si = i == 0 ? -1f : 1f;
                for (int j = 0; j < 4; j++)
                {
                    float sj = j == 0 ? -1f : 1f;
                    unity[i, j] = si * sj * usd[j, i];
                }
            }

            return unity;
        }

        private static GameObject CreatePrimPathObject(Transform root, string primPath)
        {
            string[] segments = primPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Transform current = root;
            foreach (string segment in segments)
            {
                string objectName = SanitizeObjectName(segment);
                Transform child = current.Find(objectName);
                if (child == null)
                {
                    var childObject = new GameObject(objectName);
                    childObject.transform.SetParent(current, false);
                    child = childObject.transform;
                }

                current = child;
            }

            return current.gameObject;
        }

        // Reads a material's scalar values and resolves its texture paths. No bytes are read here
        // and no Unity API is touched; the actual file reads are deduplicated in ReadStageData.
        private static MaterialData ReadMaterialData(
            IntPtr context,
            StageData stage,
            RUsdImportMaterial nativeMaterial,
            RUsdImportOpacity opacity,
            UsdImportOptions options)
        {
            var data = new MaterialData
            {
                Name = nativeMaterial.Name,
                Source = nativeMaterial.Material,
                OpacityThreshold = opacity.Threshold
            };

            if (options.ImportTextures)
            {
                RUsdMaterial source = nativeMaterial.Material;
                data.AlbedoPath = ResolveTexturePath(context, stage, source.AlbedoTexturePath);
                data.NormalPath = ResolveTexturePath(context, stage, source.NormalTexturePath);
                data.MetallicGlossPath = ResolveTexturePath(context, stage, source.MetallicTexturePath);
                data.EmissionPath = ResolveTexturePath(context, stage, source.EmissiveTexturePath);

                // Opacity almost always rides in the albedo file's alpha channel; reuse the
                // already-resolved path in that case so a missing file is reported once.
                data.OpacityPath =
                    !opacity.IsTextured ? null
                    : string.Equals(opacity.TexturePath, source.AlbedoTexturePath, StringComparison.Ordinal)
                        ? data.AlbedoPath
                        : ResolveTexturePath(context, stage, opacity.TexturePath);
            }

            return data;
        }

        // Reads each unique texture file referenced by any material exactly once into
        // data.TextureBytes (keyed by absolute path). Safe on a background thread.
        private static void ReadUniqueTextureBytes(IntPtr context, StageData data)
        {
            foreach (MaterialData material in data.Materials)
            {
                CacheTextureBytes(context, data, material.AlbedoPath);
                CacheTextureBytes(context, data, material.NormalPath);
                CacheTextureBytes(context, data, material.MetallicGlossPath);
                CacheTextureBytes(context, data, material.EmissionPath);
            }
        }

        private static void CacheTextureBytes(IntPtr context, StageData data, string path)
        {
            if (string.IsNullOrEmpty(path) || data.TextureBytes.ContainsKey(path))
            {
                return;
            }

            // Packaged textures (inside a .usdz) are read through the stage's resolver; everything
            // else is an ordinary file.
            if (data.PackageAssets.TryGetValue(path, out string authoredPath))
            {
                byte[] packaged = UsdNative.ReadImportAsset(context, authoredPath);
                if (packaged != null && packaged.LongLength > UsdImageLimits.MaxFileBytes)
                {
                    data.TextureBytes[path] = null;
                    Debug.LogWarning(
                        $"USD import: refused packaged texture {path}: {packaged.LongLength} bytes exceeds the {UsdImageLimits.MaxFileBytes}-byte limit.");
                    return;
                }

                data.TextureBytes[path] = packaged;
                if (packaged == null || packaged.Length == 0)
                {
                    Debug.LogWarning($"USD import: error reading packaged texture {path}");
                }

                return;
            }

            // Re-checked at the sink: every path reaching here was validated in
            // ResolveTexturePath, and this keeps that true if another caller is ever added.
            if (!IsAssetPathAllowed(data, path, out string readPath))
            {
                data.TextureBytes[path] = null;
                Debug.LogWarning("USD import: refused to read a texture outside the stage folder.");
                return;
            }

            try
            {
                // Refused before reading, so an oversized file is never held in memory.
                long length = new FileInfo(readPath).Length;
                if (length > UsdImageLimits.MaxFileBytes)
                {
                    data.TextureBytes[path] = null;
                    Debug.LogWarning(
                        $"USD import: refused texture {path}: {length} bytes exceeds the {UsdImageLimits.MaxFileBytes}-byte limit.");
                    return;
                }

                data.TextureBytes[path] = File.ReadAllBytes(readPath);
            }
            catch (Exception exception)
            {
                data.TextureBytes[path] = null;
                Debug.LogWarning($"USD import: error reading texture {path}: {exception.Message}");
            }
        }

        // Decodes each unique PNG to raw pixels. Runs inside ReadStageData, so for ImportAsync it
        // executes on a worker thread (off the main thread) — this is the expensive step that
        // used to freeze the editor. Unsupported PNGs leave a null entry → main-thread LoadImage,
        // but only if their header is within UsdImageLimits: the managed decoder's own caps used to
        // be advisory, because an image it declined for being too large went to LoadImage anyway.
        private static void DecodeUniqueTextures(StageData data)
        {
            var paths = new List<string>(data.TextureBytes.Keys);
            foreach (string path in paths)
            {
                byte[] bytes = data.TextureBytes[path];
                DecodedImage decoded = null;
                if (bytes != null && bytes.Length > 0 &&
                    UsdPngDecoder.TryDecode(bytes, out int w, out int h, out byte[] rgba))
                {
                    decoded = new DecodedImage { Width = w, Height = h, Rgba = rgba };
                    data.TextureBytes[path] = null; // decoded form supersedes the raw bytes
                }
                else if (bytes != null && bytes.Length > 0 &&
                         !UsdImageLimits.PermitsLoadImage(bytes, out string reason))
                {
                    data.TextureBytes[path] = null; // never reaches LoadImage, and frees the bytes now
                    Debug.LogWarning($"USD import: refused texture {path}: {reason}.");
                }

                data.DecodedTextures[path] = decoded;
            }
        }

        // Builds a material. Textures were already decoded to raw pixels on the read pass (off
        // the main thread for ImportAsync); here they are only uploaded to GPU (cheap) and reused
        // across materials via the per-import cache.
        private static Material BuildMaterial(
            MaterialData data,
            UsdImportOptions options,
            StageData stage,
            Dictionary<string, Texture2D> decodeCache)
        {
            Texture2D albedo = null, normal = null, metallicGloss = null, emission = null;
            if (options.ImportTextures)
            {
                albedo = GetOrUploadTexture(data.AlbedoPath, false, stage, decodeCache);
                normal = GetOrUploadTexture(data.NormalPath, true, stage, decodeCache);
                metallicGloss = GetOrUploadTexture(data.MetallicGlossPath, true, stage, decodeCache);
                emission = GetOrUploadTexture(data.EmissionPath, false, stage, decodeCache);
            }

            return AssembleMaterial(data, options, albedo, normal, metallicGloss, emission);
        }

        // Builds the Unity Material from already-resolved textures (shared by the sync and async
        // paths). Pure main-thread Material/Shader work — no decoding here.
        private static Material AssembleMaterial(
            MaterialData data,
            UsdImportOptions options,
            Texture2D albedo,
            Texture2D normal,
            Texture2D metallicGloss,
            Texture2D emission)
        {
            Shader shader =
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("HDRP/Lit") ??
                Shader.Find("Standard") ??
                Shader.Find("Sprites/Default");

            if (shader == null)
            {
                throw new UsdImportException("USD import failed because no compatible Unity material shader was found.");
            }

            var material = new Material(shader);
            material.name = SanitizeObjectName(string.IsNullOrEmpty(data.Name) ? "UsdMaterial" : data.Name);

            RUsdMaterial source = data.Source;

            // UsdPreviewSurface treats an authored input value as the fallback for when that
            // input is NOT connected, so a material carrying both `diffuseColor = (...)` and
            // `diffuseColor.connect = <albedo texture>` must render the texture alone. URP and
            // Standard multiply _BaseColor into the albedo map, so keeping the fallback imports
            // every textured material darkened and tinted by it. Opacity still comes from the
            // authored value, and a texture that failed to load leaves the fallback in place —
            // which is what makes a missing texture degrade to a flat colour instead of white.
            bool albedoTextureBound = options.ImportTextures && albedo != null;
            Color color = new Color(source.R, source.G, source.B, source.A);
            SetMaterialColor(material, albedoTextureBound ? new Color(1f, 1f, 1f, color.a) : color);
            SetMaterialFloat(material, "_Metallic", source.Metallic);
            SetMaterialFloat(material, "_Smoothness", Mathf.Clamp01(1.0f - source.Roughness));

            if (!options.ImportTextures)
            {
                ApplyOpacity(material, data, null);
                return material;
            }

            // albedo (sRGB) → _BaseMap / _MainTex
            if (albedo != null)
            {
                SetMaterialTexture(material, "_BaseMap", albedo);
                SetMaterialTexture(material, "_MainTex", albedo);
            }

            // normal (linear) → _BumpMap, enable _NORMALMAP
            if (normal != null && (material.HasProperty("_BumpMap") || material.HasProperty("_NormalMap")))
            {
                SetMaterialTexture(material, "_BumpMap", normal);
                SetMaterialTexture(material, "_NormalMap", normal);
                material.EnableKeyword("_NORMALMAP");
            }

            // packed metallic(.r) + smoothness(.a) (linear) → _MetallicGlossMap
            if (metallicGloss != null && (material.HasProperty("_MetallicGlossMap") || material.HasProperty("_MetallicMap")))
            {
                SetMaterialTexture(material, "_MetallicGlossMap", metallicGloss);
                SetMaterialTexture(material, "_MetallicMap", metallicGloss);
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
                // Smoothness is sourced from the metallic map's alpha (matches export packing).
                SetMaterialFloat(material, "_SmoothnessTextureChannel", 0f);
            }

            // emission → _EmissionMap and/or _EmissionColor, enable _EMISSION
            Color emissiveColor = new Color(source.EmissiveR, source.EmissiveG, source.EmissiveB);
            bool hasEmissiveColor = source.EmissiveR > 0f || source.EmissiveG > 0f || source.EmissiveB > 0f;
            if (emission != null || hasEmissiveColor)
            {
                if (emission != null && (material.HasProperty("_EmissionMap") || material.HasProperty("_EmissiveColorMap")))
                {
                    SetMaterialTexture(material, "_EmissionMap", emission);
                    SetMaterialTexture(material, "_EmissiveColorMap", emission);
                }

                // Same fallback rule as the base colour: a bound emission map wins over the
                // authored emissiveColor, which is only there for renderers that cannot resolve
                // the connection.
                Color emissionTint = emission != null ? Color.white : emissiveColor;
                if (material.HasProperty("_EmissionColor"))
                {
                    material.SetColor("_EmissionColor", emissionTint);
                }
                else if (material.HasProperty("_EmissiveColor"))
                {
                    material.SetColor("_EmissiveColor", emissionTint);
                }

                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            // UV tiling/offset → texture scale/offset on the base map
            var scale = new Vector2(source.UvScaleX, source.UvScaleY);
            var offset = new Vector2(source.UvOffsetX, source.UvOffsetY);
            if (scale != Vector2.one || offset != Vector2.zero)
            {
                if (material.HasProperty("_BaseMap"))
                {
                    material.SetTextureScale("_BaseMap", scale);
                    material.SetTextureOffset("_BaseMap", offset);
                }

                if (material.HasProperty("_MainTex"))
                {
                    material.SetTextureScale("_MainTex", scale);
                    material.SetTextureOffset("_MainTex", offset);
                }
            }

            ApplyOpacity(material, data, albedo);
            return material;
        }

        // Translates the authored USD opacity into the shader's surface state. Unity needs this
        // set explicitly: a material left on the default Opaque surface ignores its alpha
        // completely, which is why cut-out foliage imported as solid quads and stained glass as a
        // solid block even though the alpha was sitting in the albedo texture.
        private static void ApplyOpacity(Material material, MaterialData data, Texture2D albedo)
        {
            bool fromTexture = albedo != null && !string.IsNullOrEmpty(data.OpacityPath);
            if (fromTexture && !string.Equals(data.OpacityPath, data.AlbedoPath, StringComparison.Ordinal))
            {
                // URP and Standard both take alpha from the base map, so opacity authored in a
                // different file cannot drive it without a custom shader.
                Debug.LogWarning(
                    $"USD import: material '{data.Name}' drives opacity from a texture other than its albedo " +
                    $"({data.OpacityPath}); Unity reads alpha from the base map, so this opacity is ignored.");
                fromTexture = false;
            }

            if (fromTexture)
            {
                // UsdPreviewSurface: opacityThreshold > 0 is a hard cutout at that value, 0 asks
                // for real alpha blending.
                if (data.OpacityThreshold > 0f)
                {
                    SetAlphaCutout(material, data.OpacityThreshold);
                }
                else
                {
                    SetAlphaBlend(material);
                }

                return;
            }

            // No opacity texture: only a constant below 1 makes the material see-through.
            if (data.Source.A < 1f)
            {
                SetAlphaBlend(material);
            }
        }

        private static void SetAlphaCutout(Material material, float cutoff)
        {
            SetMaterialFloat(material, "_AlphaClip", 1f);
            SetMaterialFloat(material, "_Cutoff", Mathf.Clamp01(cutoff));
            SetMaterialFloat(material, "_AlphaToMask", 1f);
            SetMaterialFloat(material, "_Mode", 1f); // Built-in Standard: Cutout
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private static void SetAlphaBlend(Material material)
        {
            SetMaterialFloat(material, "_Surface", 1f); // URP: Transparent
            SetMaterialFloat(material, "_Blend", 0f);   // URP: Alpha
            SetMaterialFloat(material, "_AlphaClip", 0f);
            SetMaterialFloat(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            SetMaterialFloat(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            SetMaterialFloat(material, "_ZWrite", 0f);
            SetMaterialFloat(material, "_Mode", 3f); // Built-in Standard: Transparent
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); // URP
            material.EnableKeyword("_ALPHABLEND_ON");            // Built-in Standard
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // Turns an authored texture path into the key its bytes are cached under: the absolute
        // file path when it is a file on disk, or the resolved asset identifier when the stage
        // lives inside a package (a .usdz), whose textures no file-system read can reach.
        private static string ResolveTexturePath(IntPtr context, StageData stage, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            string fullPath = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(stage.BaseDirectory ?? string.Empty, relativePath);

            // The authored path comes out of the file being imported, so it is untrusted: a
            // rooted path or a `../` climb used to be read straight off disk. Confinement is
            // decided on the canonicalized path and *before* either read path is taken — the
            // native resolver below is a second, independent sink, so a guard placed only in
            // front of File.Exists would simply be walked around.
            if (!IsAssetPathAllowed(stage, fullPath, out string canonicalPath))
            {
                // Only the authored path is echoed. Reporting the resolved location would tell
                // the author of a hostile stage where files sit on this machine.
                Debug.LogWarning(
                    $"USD import: texture path resolves outside the stage folder, skipping: {relativePath}");
                return null;
            }

            if (File.Exists(canonicalPath))
            {
                return canonicalPath;
            }

            if (UsdNative.TryResolveImportAsset(context, relativePath, out string resolvedPath, out long byteCount) &&
                byteCount > 0)
            {
                stage.PackageAssets[resolvedPath] = relativePath;
                return resolvedPath;
            }

            Debug.LogWarning($"USD import: texture not found, skipping: {canonicalPath}");
            return null;
        }

        // Linux filesystems are case-sensitive, so a case-insensitive comparison there would
        // accept a sibling directory that merely differs in case as being "inside" the stage
        // folder. Windows and macOS default to case-insensitive, where the strict comparison
        // would reject legitimate paths.
#if UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
        private const StringComparison PathComparison = StringComparison.Ordinal;
#else
        private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#endif

        // True when `candidate` canonicalizes to a location inside the stage's own folder, which
        // is the only place an untrusted stage may pull assets from unless the caller opted in
        // with UsdImportOptions.AllowExternalAssetPaths. Canonicalizing first is what makes this
        // sound: scanning for the substring ".." would miss an absolute path, and would also
        // miss a path that only escapes once it is normalized. `canonicalPath` is handed back so
        // callers read exactly the path that was validated.
        private static bool IsAssetPathAllowed(StageData stage, string candidate, out string canonicalPath)
        {
            canonicalPath = null;
            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            // A packaged asset (inside a .usdz) is not a filesystem path and is read back through
            // the stage's resolver, not File.ReadAllBytes. Its authored path was already confined
            // before the resolver was consulted.
            if (stage.PackageAssets.ContainsKey(candidate))
            {
                canonicalPath = candidate;
                return true;
            }

            try
            {
                canonicalPath = Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                // Malformed path (invalid characters, too long, bad root): not readable anyway.
                return false;
            }

            if (stage.AllowExternalAssetPaths)
            {
                return true;
            }

            if (string.IsNullOrEmpty(stage.BaseDirectory))
            {
                return false;
            }

            string root;
            try
            {
                root = Path.GetFullPath(stage.BaseDirectory);
            }
            catch (Exception)
            {
                return false;
            }

            // Trailing separator on the root so "/stage" does not also match "/stage-secrets".
            root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            if (!canonicalPath.StartsWith(root, PathComparison))
            {
                return false;
            }

            // Lexical containment is not containment. Path.GetFullPath normalizes "." and ".."
            // as text; it does not follow links, so a symlink (or a Windows junction) sitting
            // inside the stage folder and pointing at /etc/passwd or an SSH key passes the check
            // above and is then read straight off disk (SECURITY-282834, CWE-59/CWE-22).
            return !HasLinkBelowRoot(root, canonicalPath);
        }

        // True when any path component *below* the stage folder is a link. The stage folder
        // itself and everything above it are deliberately not examined: a project living under a
        // symlinked path is ordinary -- on macOS /var is itself a link -- and rejecting that
        // would break normal setups while protecting nothing, since the root is where the stage
        // legitimately lives.
        //
        // Links are refused rather than resolved. Resolving would mean realpath() on POSIX and
        // GetFinalPathNameByHandle on Windows, neither of which is reachable from .NET Standard
        // 2.1 without per-platform P/Invoke, and it would still have to answer what a link
        // pointing inside the folder means. For untrusted stage content, refusing is the
        // defensible default; a stage that genuinely needs links is what
        // UsdImportOptions.AllowExternalAssetPaths is for.
        private static bool HasLinkBelowRoot(string root, string fullPath)
        {
            string current = fullPath;
            while (!string.IsNullOrEmpty(current) && current.Length > root.Length)
            {
                try
                {
                    // ReparsePoint covers Unix symlinks as well as Windows symlinks and
                    // junctions. Verified on macOS: a symlinked file reports it.
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                    // A component that does not exist is not a link, so move on to its parent,
                    // which still gets checked. This is the normal case for a texture packaged in
                    // a .usdz: "0/tex.png" names nothing on disk, and refusing it here blocked every
                    // packaged texture before the resolver below could read it out of the package.
                    // Nothing is read off disk for such a path -- File.Exists fails -- and the
                    // native resolver it falls through to confines with TfRealPath on its own.
                }
                catch (Exception)
                {
                    // Cannot classify the component (permissions, malformed): treat it as unsafe
                    // rather than assume it is fine.
                    return true;
                }

                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, PathComparison))
                {
                    break;
                }

                current = parent;
            }

            return false;
        }

        // Uploads a texture to the GPU, reusing it across materials via the per-import cache.
        // The expensive PNG decode already happened on the read pass (off the main thread for
        // ImportAsync); here we only do the cheap upload from pre-decoded pixels, or fall back to
        // a main-thread LoadImage for PNGs the managed decoder did not handle.
        private static Texture2D GetOrUploadTexture(
            string path,
            bool linear,
            StageData stage,
            Dictionary<string, Texture2D> decodeCache)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string key = path + (linear ? "#linear" : "#srgb");
            if (decodeCache.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            Texture2D texture = null;
            if (stage.DecodedTextures.TryGetValue(path, out DecodedImage decoded) && decoded != null)
            {
                texture = CreateTextureFromRgba(decoded.Width, decoded.Height, decoded.Rgba, linear, name);
            }
            else if (stage.TextureBytes.TryGetValue(path, out byte[] bytes) && bytes != null && bytes.Length > 0)
            {
                texture = CreateTextureViaLoadImage(bytes, linear, name); // fallback (16-bit/interlaced/paletted)
            }

            decodeCache[key] = texture; // cache nulls too, to avoid retrying a bad file
            return texture;
        }

        private static Texture2D CreateTextureFromRgba(int width, int height, byte[] rgba, bool linear, string name)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, linear) { name = name };
            texture.SetPixelData(rgba, 0);
            texture.Apply(true, true); // generate mipmaps, then make non-readable
            return texture;
        }

        private static Texture2D CreateTextureViaLoadImage(byte[] bytes, bool linear, string name)
        {
            // Re-checked at the sink (SECURITY-282834, CWE-400). LoadImage applies no size limit
            // of its own, and DecodeUniqueTextures already refused what fails this -- this keeps
            // that true if another caller is ever added.
            if (!UsdImageLimits.PermitsLoadImage(bytes, out string reason))
            {
                Debug.LogWarning($"USD import: refused texture '{name}': {reason}.");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                Debug.LogWarning($"USD import: failed to decode texture '{name}'.");
                return null;
            }

            return texture;
        }

        private static void SetMaterialTexture(Material material, string property, Texture texture)
        {
            if (material.HasProperty(property))
            {
                material.SetTexture(property, texture);
            }
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
        }

        private static void SetMaterialFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        private static List<Vector3> ToUnityVectors(RUsdVec3[] values)
        {
            var result = new List<Vector3>(values.Length);
            foreach (RUsdVec3 value in values)
            {
                result.Add(new Vector3(value.X, value.Y, value.Z));
            }

            return result;
        }

        private static List<Vector2> ToUnityVector2(RUsdVec2[] values)
        {
            var result = new List<Vector2>(values.Length);
            foreach (RUsdVec2 value in values)
            {
                result.Add(new Vector2(value.X, value.Y));
            }

            return result;
        }

        private static string GetValidatedInputPath(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new ArgumentException("Input path is empty.", nameof(inputPath));
            }

            string fullPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("USD input file was not found.", fullPath);
            }

            return fullPath;
        }

        private static string GetRootObjectName(StageData data, UsdImportOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.RootObjectName))
            {
                return SanitizeObjectName(options.RootObjectName);
            }

            if (!string.IsNullOrWhiteSpace(data.DefaultPrimPath))
            {
                string[] segments = data.DefaultPrimPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0)
                {
                    return SanitizeObjectName(segments[segments.Length - 1]);
                }
            }

            return SanitizeObjectName(Path.GetFileNameWithoutExtension(data.InputPath));
        }

        private static string SanitizeObjectName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "UsdObject";
            }

            return name.Replace('/', '_').Replace('\\', '_').Trim();
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

        private static string GetUpAxisName(int upAxis)
        {
            return upAxis == 2 ? "Z" : "Y";
        }

        private static void ConfigureNativeRuntime()
        {
            UsdExporter.GetRuntimeInfo();

            // The version gate used to live only in the export path, so an import ran against
            // whatever plugin happened to be loaded. That was backwards: the import path is where
            // untrusted file content is parsed, and the SECURITY-282834 fixes live there.
            UsdExporter.ValidateNativeApiVersion();
        }

        private static UsdImportException CreateImportException(Exception exception)
        {
            if (exception is UsdExportException exportException)
            {
                return new UsdImportException(exportException.Message, exportException, exportException.Diagnostics);
            }

            return new UsdImportException("USD import failed.", exception);
        }

        // The native importer reports what it had to drop -- a material input that is connected
        // but whose texture path could not be read, a surface shader that is not
        // UsdPreviewSurface -- through the diagnostics buffer, prefixed so it can be told apart
        // from captured OpenUSD output. Those cases used to be entirely silent: textures simply
        // never appeared and nothing was logged, because the managed "texture not found" warning
        // only fires for a path that exists but points at a missing file.
        private static void LogNativeImportWarnings(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics))
            {
                return;
            }

            const string prefix = "USD import: ";
            const int maxLogged = 10;
            int logged = 0;
            int suppressed = 0;

            foreach (string line in diagnostics.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || !trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (logged < maxLogged)
                {
                    Debug.LogWarning(trimmed);
                    logged++;
                }
                else
                {
                    suppressed++;
                }
            }

            if (suppressed > 0)
            {
                Debug.LogWarning(
                    $"USD import: {suppressed} further import warning(s) not logged; " +
                    "the full list is in UsdImportResult.NativeDiagnostics.");
            }
        }

        private static void WriteNativeDiagnosticsLog(UsdImportOptions options, string diagnostics)
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

        // Plain CPU data extracted from a USD stage on the read pass (no Unity objects), then
        // turned into Unity objects on the main thread in BuildResult.
        private sealed class StageData
        {
            public string InputPath;
            public string BaseDirectory;

            // Copied from UsdImportOptions so the read pass can confine authored asset paths
            // without threading the options through every texture helper.
            public bool AllowExternalAssetPaths;
            public string DefaultPrimPath;
            public double MetersPerUnit;
            public int UpAxis;
            public string NativeDiagnostics;
            public readonly List<NodeData> Nodes = new List<NodeData>();
            public readonly List<MeshData> Meshes = new List<MeshData>();
            public readonly List<MaterialData> Materials = new List<MaterialData>();

            // Raw bytes for each unique texture file (absolute path → bytes), read once on the
            // read pass. Kept only as a fallback for PNGs the managed decoder cannot handle.
            public readonly Dictionary<string, byte[]> TextureBytes = new Dictionary<string, byte[]>();

            // Pre-decoded pixels for each unique texture (absolute path → decoded, or null when
            // the managed decoder declined and a main-thread LoadImage fallback is needed).
            // Decoded on the read pass — off the main thread for ImportAsync.
            public readonly Dictionary<string, DecodedImage> DecodedTextures = new Dictionary<string, DecodedImage>();

            // Textures that are not files on disk because the stage lives inside a package
            // (a .usdz): resolved identifier → the authored path the resolver reads it back by.
            // Their bytes come from the native resolver instead of File.ReadAllBytes.
            public readonly Dictionary<string, string> PackageAssets = new Dictionary<string, string>();
        }

        private struct NodeData
        {
            public string Path;
            public RUsdMatrix4x4 LocalMatrix;
            public bool Visible;
        }

        private sealed class MeshData
        {
            public string PrimPath;
            public string Name;
            public bool Visible;
            public int MaterialIndex;
            public RUsdVec3[] Points;
            public int[] Indices;
            public RUsdVec3[] Normals;
            public RUsdVec2[][] UvSets;
            public RUsdSubmesh[] Submeshes;
        }

        private sealed class MaterialData
        {
            public string Name;
            public RUsdMaterial Source;
            // Resolved absolute texture paths (or null). Bytes live in StageData.TextureBytes,
            // keyed by path, so files shared across materials are read/decoded only once.
            public string AlbedoPath;
            public string NormalPath;
            public string MetallicGlossPath;
            public string EmissionPath;
            // Authored opacity: the texture whose alpha drives transparency (null when the USD
            // `opacity` input is not connected) and the cutout threshold (0 = alpha blend).
            public string OpacityPath;
            public float OpacityThreshold;
        }

        // Raw decoded pixels (RGBA32, Unity layout) produced off the main thread.
        private sealed class DecodedImage
        {
            public int Width;
            public int Height;
            public byte[] Rgba;
        }
    }

    // The texture size limits, and the header check that enforces them on the Texture2D.LoadImage
    // fallback (SECURITY-282834, CWE-400). UsdPngDecoder obeys these on the managed path; LoadImage
    // obeys nothing, so every image headed there -- a PNG the managed decoder declined, or a JPEG,
    // which it never handles -- is sized from its header first. LoadImage reads PNG and JPEG only,
    // so anything whose header is neither is refused without losing a texture that would have loaded.
    // Touches no Unity API, so it is safe on the import worker thread.
    internal static class UsdImageLimits
    {
        public const int MaxDimension = 16384;
        public const long MaxPixelCount = 64L * 1024 * 1024;

        // The largest decoded payload the pixel cap admits -- 16-bit RGBA, 8 bytes a pixel. A
        // texture file bigger than the biggest image it could legitimately hold is not one.
        public const long MaxFileBytes = MaxPixelCount * 8;

        public static bool IsWithin(int width, int height)
        {
            return width > 0 && height > 0 &&
                   width <= MaxDimension && height <= MaxDimension &&
                   (long)width * height <= MaxPixelCount;
        }

        public static bool PermitsLoadImage(byte[] data, out string reason)
        {
            if (!TryReadSize(data, out int width, out int height, out string format))
            {
                reason = "not a PNG or JPEG with a readable size in its header";
                return false;
            }

            if (!IsWithin(width, height))
            {
                reason = $"{format} header claims {width}x{height}, outside the limits of 1-{MaxDimension}px a side and {MaxPixelCount} pixels";
                return false;
            }

            reason = null;
            return true;
        }

        // Reads width and height from a PNG IHDR or a JPEG start-of-frame segment without
        // decoding anything. Every offset is bounds-checked against the buffer.
        public static bool TryReadSize(byte[] data, out int width, out int height, out string format)
        {
            width = 0;
            height = 0;
            format = null;
            if (data == null)
            {
                return false;
            }

            // PNG: signature, then IHDR must be the first chunk (length 13).
            if (data.Length >= 24 &&
                data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 &&
                data[4] == 13 && data[5] == 10 && data[6] == 26 && data[7] == 10)
            {
                if (BigEndian32(data, 8) != 13 ||
                    data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
                {
                    return false;
                }

                width = BigEndian32(data, 16);
                height = BigEndian32(data, 20);
                format = "PNG";
                return true;
            }

            // JPEG: walk the marker segments to the first start-of-frame, which carries the size.
            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            {
                int pos = 2;
                while (pos + 4 <= data.Length)
                {
                    if (data[pos] != 0xFF)
                    {
                        return false;
                    }

                    byte marker = data[pos + 1];
                    if (marker == 0xFF)
                    {
                        pos++; // fill byte
                        continue;
                    }

                    pos += 2;
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
                    {
                        continue; // standalone markers carry no length
                    }

                    if (marker == 0xD9 || marker == 0xDA)
                    {
                        return false; // end of image, or scan data, before any frame header
                    }

                    int length = (data[pos] << 8) | data[pos + 1];
                    if (length < 2 || (long)pos + length > data.Length)
                    {
                        return false;
                    }

                    // SOF0..SOF15, except DHT (C4), JPG (C8) and DAC (CC), which share the range.
                    bool startOfFrame = marker >= 0xC0 && marker <= 0xCF &&
                                        marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (startOfFrame)
                    {
                        if (length < 7)
                        {
                            return false;
                        }

                        height = (data[pos + 3] << 8) | data[pos + 4];
                        width = (data[pos + 5] << 8) | data[pos + 6];
                        format = "JPEG";
                        return true;
                    }

                    pos += length;
                }
            }

            return false;
        }

        private static int BigEndian32(byte[] d, int o)
        {
            return (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        }
    }

    // Minimal, thread-safe PNG decoder used to move texture decoding off Unity's main thread
    // during async import (touches no Unity API). Supports 8-bit, non-interlaced PNGs of color
    // types 0/2/4/6 with filter types 0–4 — the common case for the toolkit's exported PNGs and
    // typical PBR textures. Anything else returns false so the caller falls back to LoadImage.
    internal static class UsdPngDecoder
    {
        // Hard caps on the attacker-controlled IHDR dimensions. A malicious .usd/.usdz can
        // reference a few-hundred-byte PNG whose header claims an enormous image, and those
        // numbers drive every allocation below. Anything past these bounds is refused before
        // memory is reserved. The false return is not the end of it: a declined PNG goes to the
        // Texture2D.LoadImage fallback, which is why the same limits live in UsdImageLimits and
        // are checked again there -- on their own these caps only guarded the path that obeyed them.
        private const int MaxDimension = UsdImageLimits.MaxDimension;
        private const long MaxPixelCount = UsdImageLimits.MaxPixelCount;
        private const long MaxDecodedBytes = 512L * 1024 * 1024;

        // On success, rgba is width*height*4 bytes in Unity layout (row 0 = bottom), matching
        // the orientation produced by Texture2D.LoadImage.
        public static bool TryDecode(byte[] data, out int width, out int height, out byte[] rgba)
        {
            width = 0;
            height = 0;
            rgba = null;

            if (data == null || data.Length < 8)
            {
                return false;
            }

            if (data[0] != 137 || data[1] != 80 || data[2] != 78 || data[3] != 71 ||
                data[4] != 13 || data[5] != 10 || data[6] != 26 || data[7] != 10)
            {
                return false;
            }

            int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
            var idat = new MemoryStream();
            int pos = 8;
            bool sawIhdr = false;

            while (pos + 8 <= data.Length)
            {
                int len = ReadBigEndianInt32(data, pos);
                pos += 4;
                // 64-bit sum: a chunk length near int.MaxValue overflows this in 32-bit math,
                // passes the guard, and then throws out of idat.Write below.
                if (len < 0 || (long)pos + 4 + len + 4 > data.Length)
                {
                    break;
                }

                string type = new string(new[] { (char)data[pos], (char)data[pos + 1], (char)data[pos + 2], (char)data[pos + 3] });
                pos += 4;

                if (type == "IHDR")
                {
                    w = ReadBigEndianInt32(data, pos);
                    h = ReadBigEndianInt32(data, pos + 4);
                    bitDepth = data[pos + 8];
                    colorType = data[pos + 9];
                    interlace = data[pos + 12];
                    sawIhdr = true;
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, pos, len);
                }
                else if (type == "IEND")
                {
                    break;
                }

                pos += len + 4; // skip chunk data + CRC
            }

            if (!sawIhdr || w <= 0 || h <= 0 || bitDepth != 8 || interlace != 0)
            {
                return false;
            }

            // Magnitude, not just sign: the positive check above still admits a header claiming
            // 20000x25000, which is what drove the ~2 GB allocation.
            if (w > MaxDimension || h > MaxDimension || (long)w * h > MaxPixelCount)
            {
                return false;
            }

            int channels;
            switch (colorType)
            {
                case 0: channels = 1; break;
                case 2: channels = 3; break;
                case 4: channels = 2; break;
                case 6: channels = 4; break;
                default: return false;
            }

            byte[] compressed = idat.ToArray();
            if (compressed.Length < 2)
            {
                return false;
            }

            // Every size below derives from the IHDR dimensions, so it is computed in 64-bit and
            // checked before anything is allocated. In 32-bit math a 65536x65536 header wraps
            // `stride` negative, which drags `expected` negative too and lets the raw.Length
            // guard pass with a negative array size behind it.
            int bpp = channels;
            long stride64 = (long)w * bpp;
            long expected = (long)h * (stride64 + 1);
            long rgbaBytes = (long)w * h * 4;
            if (stride64 > int.MaxValue || rgbaBytes > int.MaxValue || expected > MaxDecodedBytes)
            {
                return false;
            }

            byte[] raw;
            try
            {
                // IDAT is a zlib stream: skip the 2-byte header, inflate the raw DEFLATE payload.
                // The output stream is deliberately not pre-sized from the header — that
                // allocation was the denial of service — and the inflate is capped at what these
                // dimensions can legitimately produce, so a tiny IDAT cannot expand without bound.
                using (var input = new MemoryStream(compressed, 2, compressed.Length - 2))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var chunk = new byte[64 * 1024];
                    long total = 0;
                    int read;
                    while ((read = deflate.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        total += read;
                        if (total > expected)
                        {
                            return false;
                        }

                        output.Write(chunk, 0, read);
                    }

                    raw = output.ToArray();
                }
            }
            catch
            {
                return false;
            }

            if (raw.Length < expected)
            {
                return false;
            }

            int stride = (int)stride64;
            var cur = new byte[stride];
            var prev = new byte[stride];
            var result = new byte[(int)rgbaBytes];
            int rp = 0;

            for (int y = 0; y < h; y++)
            {
                int filter = raw[rp++];
                Buffer.BlockCopy(raw, rp, cur, 0, stride);
                rp += stride;

                Unfilter(filter, cur, prev, bpp);

                int outRow = (h - 1 - y) * w * 4; // flip to Unity layout
                for (int x = 0; x < w; x++)
                {
                    int s = x * bpp;
                    int d = outRow + x * 4;
                    byte r, g, b, a;
                    if (channels == 1) { r = g = b = cur[s]; a = 255; }
                    else if (channels == 2) { r = g = b = cur[s]; a = cur[s + 1]; }
                    else if (channels == 3) { r = cur[s]; g = cur[s + 1]; b = cur[s + 2]; a = 255; }
                    else { r = cur[s]; g = cur[s + 1]; b = cur[s + 2]; a = cur[s + 3]; }
                    result[d] = r; result[d + 1] = g; result[d + 2] = b; result[d + 3] = a;
                }

                byte[] tmp = prev;
                prev = cur;
                cur = tmp;
            }

            width = w;
            height = h;
            rgba = result;
            return true;
        }

        private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
        {
            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (int i = bpp; i < cur.Length; i++)
                    {
                        cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    }
                    break;
                case 2:
                    for (int i = 0; i < cur.Length; i++)
                    {
                        cur[i] = (byte)(cur[i] + prev[i]);
                    }
                    break;
                case 3:
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int left = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((left + prev[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int left = i >= bpp ? cur[i - bpp] : 0;
                        int up = prev[i];
                        int upLeft = i >= bpp ? prev[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + Paeth(left, up, upLeft));
                    }
                    break;
                default:
                    break;
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        private static int ReadBigEndianInt32(byte[] d, int o)
        {
            return (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        }
    }
}
