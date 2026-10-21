using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    public static partial class UsdImporter
    {
        public static UsdImportPreviewInfo GetPreviewInfo(string inputPath, UsdImportOptions options = null)
        {
            options ??= new UsdImportOptions();
            IntPtr context = IntPtr.Zero;
            try
            {
                ConfigureNativeRuntime(options);
                // Under the import's own limits, so the stage kept below is the one it would open.
                context = RequiresLimitsApi(options)
                    ? UsdNative.OpenStageEx(GetValidatedInputPath(inputPath), options.CaptureNativeDiagnostics, false,
                                            string.Empty, string.Empty, false, options.Limits)
                    : UsdNative.OpenStage(GetValidatedInputPath(inputPath), options.CaptureNativeDiagnostics);
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
                var preview = new UsdImportPreviewInfo(
                    Path.GetFullPath(inputPath),
                    info.DefaultPrimPath,
                    info.MetersPerUnit,
                    GetUpAxisName(info.UpAxis),
                    info.MeshCount,
                    info.MaterialCount,
                    triangleCount,
                    vertexCount,
                    diagnostics);

                // The stage this walked is the stage the import that usually follows would
                // walk again -- the same open, the same mesh tables, 3 to 5 seconds on the
                // survey stages. It is kept for that import instead of being destroyed here.
                KeepPreviewStage(ref context, preview.Path, options.CaptureNativeDiagnostics,
                                 UsdImportLimits.KeyOf(options.Limits));
                return preview;
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

        // ---- One opened stage carried from GetPreviewInfo to the next Import of the same file.

        private static readonly object PreviewStageGate = new object();
        private static IntPtr previewStage = IntPtr.Zero;
        private static string previewStagePath;
        private static bool previewStageDiagnostics;
        private static string previewStageLimits;
        private static DateTime previewStageWriteTime;

        private static void KeepPreviewStage(ref IntPtr context, string fullPath, bool diagnostics, string limits)
        {
            lock (PreviewStageGate)
            {
                if (previewStage != IntPtr.Zero)
                {
                    UsdNative.Destroy(previewStage);
                }

                previewStage = context;
                previewStagePath = fullPath;
                previewStageDiagnostics = diagnostics;
                previewStageLimits = limits;
                previewStageWriteTime = SafeWriteTime(fullPath);
                context = IntPtr.Zero; // the finally above must not destroy it
            }
        }

        // Frees the kept preview stage: the Editor is about to reload scripts, which would lose the
        // pointer and leak the stage.
        internal static void ReleasePreviewStage()
        {
            lock (PreviewStageGate)
            {
                UsdNative.Destroy(previewStage);
                previewStage = IntPtr.Zero;
            }
        }

        // The kept stage, when it is the one this import would open: same file, unchanged
        // since, same diagnostics capture, and no composition options (variants, payloads)
        // that a plain open does not apply. Taken once; a second import opens afresh.
        private static IntPtr TakePreviewStage(string fullPath, UsdImportOptions options, bool wantsComposition)
        {
            lock (PreviewStageGate)
            {
                if (previewStage == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                IntPtr context = previewStage;
                previewStage = IntPtr.Zero;
                bool matches = !wantsComposition &&
                               string.Equals(previewStagePath, fullPath, StringComparison.Ordinal) &&
                               previewStageDiagnostics == options.CaptureNativeDiagnostics &&
                               string.Equals(previewStageLimits, UsdImportLimits.KeyOf(options.Limits), StringComparison.Ordinal) &&
                               previewStageWriteTime == SafeWriteTime(fullPath);
                if (!matches)
                {
                    UsdNative.Destroy(context);
                    return IntPtr.Zero;
                }

                return context;
            }
        }

        private static DateTime SafeWriteTime(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        public static async Task<UsdImportResult> ImportAsync(string inputPath, UsdImportOptions options = null)
        {
            options ??= new UsdImportOptions();

            // Native runtime validation and path checks run on the calling (main) thread.
            ConfigureNativeRuntime(options);
            NoteDeviceLimits();
            string fullInputPath = GetValidatedInputPath(inputPath);

            // The heavy work — USD parse, buffer copies, and reading texture files — runs off
            // the main thread and produces only plain CPU data (no Unity objects).
            bool streamTextures = options.StreamTextures && options.ImportMaterials && options.ImportTextures;
            StageData data;
            try
            {
                data = await Task.Run(() => ReadStageData(fullInputPath, options, streamTextures));
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }

            // Unity object creation must happen on the main thread (this awaited continuation),
            // spread across frames so the editor/player stays responsive.
            if (!streamTextures)
            {
                try
                {
                    return await BuildResultAsync(data, options);
                }
                finally
                {
                    ReleaseNativeImages(data);
                    ReleaseKeptStage(data);
                }
            }

            // Streamed: the textures are read and decoded by the same two calls the non-streamed
            // path makes inside ReadStageData, started now so they overlap the object build. Until
            // they finish the main thread reads nothing they write (see BuildResultAsync).
            Exception decodeFailure = null;
            Task decode = Task.Run(() =>
            {
                try
                {
                    ReadUniqueTextureBytes(IntPtr.Zero, data);
                    DecodeUniqueTextures(data, options);
                }
                catch (Exception exception)
                {
                    decodeFailure = exception;
                }
            });

            var materials = new List<Material>(data.Materials.Count);
            UsdImportResult result;
            try
            {
                result = await BuildResultAsync(data, options, materials);
            }
            catch
            {
                ReleaseKeptStage(data);
                // Nothing will attach the textures; free what the decode builds once it ends.
                _ = decode.ContinueWith(_ => ReleaseNativeImages(data), TaskScheduler.Default);
                throw;
            }

            result.TexturesReady = AttachStreamedTexturesAsync(data, options, result, materials, decode,
                                                               () => decodeFailure);
            return result;
        }

        // Builds each material again now that its textures are decoded -- with BuildMaterial, the
        // function a non-streamed import uses -- and copies the result onto the material the
        // renderers already hold, so the two paths cannot end in different states. Spread across
        // frames by the same budget as the object build.
        private static async Task AttachStreamedTexturesAsync(
            StageData data,
            UsdImportOptions options,
            UsdImportResult result,
            List<Material> materials,
            Task decode,
            Func<Exception> decodeFailure)
        {
            Action<float, string> progress = options.ProgressCallback;
            await decode;
            try
            {
                if (decodeFailure() != null)
                {
                    Debug.LogWarning("USD import: textures could not be loaded; materials keep their authored values. " +
                                     DescribeFailure(decodeFailure()));
                    return;
                }

                RunDeferredPacking(data);
                float budget = Mathf.Max(1f, options.MaxMillisecondsPerFrame);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var decodeCache = new Dictionary<string, Texture2D>();
                for (int i = 0; i < materials.Count && i < data.Materials.Count; i++)
                {
                    // The import was thrown away before its textures arrived.
                    if (result.RootObject == null)
                    {
                        return;
                    }

                    Material target = materials[i];
                    if (target != null)
                    {
                        Material built = BuildMaterial(data.Materials[i], options, data, decodeCache);
                        CopyMaterialState(built, target);
                        DestroyUnityObject(built);
                    }

                    if (clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        progress?.Invoke(StreamedBuildProgress + (1f - StreamedBuildProgress) * i / materials.Count,
                                         "Attaching textures");
                        await Task.Yield();
                        clock.Restart();
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("USD import: attaching streamed textures failed; materials keep their authored values. " +
                                 DescribeFailure(exception));
            }
            finally
            {
                // Uploading releases each chain it uses; this frees the rest -- those of an import
                // destroyed before its textures arrived, or left behind by a failure.
                ReleaseNativeImages(data);
                progress?.Invoke(1f, "Done");
            }
        }

        // Where a streamed import's progress stands once its objects are built; texture
        // attachment reports the rest, and "Done" (1) only when the textures are on.
        private const float StreamedBuildProgress = 0.9f;

        // Frees the compressed chains the native side still holds for an import (API 12). They
        // have no finalizer, so one never uploaded -- the import failed, or was thrown away
        // before its textures arrived -- is lost unless released here. Call only once nothing
        // decodes into data any more. Release is idempotent; an image under two keys is fine.
        // A stage kept open (UsdImportOptions.KeepStageOpen) that no UsdStageSource adopted: the
        // import failed, or recorded no source.
        private static void ReleaseKeptStage(StageData data)
        {
            UsdNative.Destroy(data.KeptStage);
            data.KeptStage = IntPtr.Zero;
        }

        private static void ReleaseNativeImages(StageData data)
        {
            foreach (DecodedImage image in data.DecodedTextures.Values)
            {
                if (image?.Native != null)
                {
                    image.Native.Release();
                    image.Native = null;
                }
            }
        }

        // An AggregateException's own message ("One or more errors occurred") names nothing.
        private static string DescribeFailure(Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                var messages = new List<string>();
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                {
                    messages.Add(inner.Message);
                }

                return string.Join("; ", messages);
            }

            return exception.Message;
        }

        // Everything AssembleMaterial and ApplyOpacity set: properties (textures, colours,
        // floats, texture transforms), keywords, render queue, the RenderType tag and GI flags.
        private static void CopyMaterialState(Material source, Material target)
        {
            target.shader = source.shader;
            target.CopyPropertiesFromMaterial(source);
            target.shaderKeywords = source.shaderKeywords;
            target.renderQueue = source.renderQueue;
            target.SetOverrideTag("RenderType", source.GetTag("RenderType", false, string.Empty));
            target.globalIlluminationFlags = source.globalIlluminationFlags;
        }

        private static void DestroyUnityObject(UnityEngine.Object target)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        public static UsdImportResult Import(string inputPath, UsdImportOptions options = null)
        {
            options ??= new UsdImportOptions();
            try
            {
                ConfigureNativeRuntime(options);
                NoteDeviceLimits();
                string fullInputPath = GetValidatedInputPath(inputPath);
                StageData data = ReadStageData(fullInputPath, options);
                try
                {
                    return BuildResult(data, options);
                }
                finally
                {
                    ReleaseNativeImages(data);
                    ReleaseKeptStage(data);
                }
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
        }

        // Loads a PNG or JPEG from disk within UsdImageLimits, or returns null. For images that
        // arrive beside a USD file rather than inside it -- a library browser's thumbnails -- which
        // would otherwise go to Texture2D.LoadImage unchecked (CWE-400): the file
        // size is refused before reading and the header's dimensions before decoding. Main thread
        // only, as it creates a Texture2D.
        public static Texture2D LoadImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                long length = new FileInfo(path).Length;
                if (length > UsdImageLimits.MaxFileBytes)
                {
                    Debug.LogWarning(
                        $"USD import: refused image {path}: {length} bytes exceeds the {UsdImageLimits.MaxFileBytes}-byte limit.");
                    return null;
                }

                // Not compressed: this path loads thumbnails for a browser to show, where block
                // compression's artefacts would be looked at directly rather than wrapped around a
                // model. The CPU copy is still released, which it previously was not.
                return CreateTextureViaLoadImage(File.ReadAllBytes(path), false, Path.GetFileName(path), false);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"USD import: error reading image {path}: {exception.Message}");
                return null;
            }
        }

        // ---- Stage read: native + file I/O only; safe to run on a background thread. ----

        // deferTextures: read only the packaged (.usdz) texture bytes, which need this stage's
        // resolver; the file reads and decoding are left to the caller (ImportAsync streaming).
        private static StageData ReadStageData(string fullInputPath, UsdImportOptions options, bool deferTextures = false)
        {
            IntPtr context = IntPtr.Zero;
            try
            {
                context = OpenStageForImport(fullInputPath, options);
                StageData data = ReadStageTables(context, fullInputPath, options, deferTextures);

                // Remote MDL modules the stage names by URL and the cache does not hold yet:
                // fetched, then the stage is read again so its materials take them.
                if (options.DownloadRemoteMaterials && UsdNative.SupportsRemoteMaterials)
                {
                    List<string> requests = UsdNative.GetImportRemoteRequests(context);
                    if (requests.Count > 0 && UsdRemoteMaterials.Fetch(requests, s_remoteMaterialCache) > 0)
                    {
                        ReleaseNativeImages(data);
                        UsdNative.Destroy(context);
                        context = IntPtr.Zero;
                        context = OpenStageForImport(fullInputPath, options);
                        data = ReadStageTables(context, fullInputPath, options, deferTextures);
                    }
                }

                ReadPhysicsData(context, data, options);

                // UsdImportOptions.KeepStageOpen: the tables are read, so they go, and the stage
                // stays for the UsdStageSource RecordSource puts on the root.
                if (options.KeepStageOpen && options.RecordSource && UsdNative.SupportsStageApi)
                {
                    UsdNative.ReleaseImportData(context);
                    data.KeptStage = context;
                    context = IntPtr.Zero;
                }

                return data;
            }
            finally
            {
                UsdNative.Destroy(context);
            }
        }

        // Reads what a walk of the stage put in the import tables -- materials and their textures,
        // nodes, meshes, instancers, scene components, animation -- into managed data. Touches no
        // Unity API; for ImportAsync it runs on a worker thread. A live switch reads the tables a
        // reread of part of the stage left (RUsd_ReadSubtrees) the same way.
        private static StageData ReadStageTables(IntPtr context, string fullInputPath, UsdImportOptions options, bool deferTextures)
        {
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
                MaterialData material = ReadMaterialData(
                    context, data, UsdNative.GetImportMaterial(context, i), opacity, options);

                // How the metallic map is packed, and any separate occlusion map (API 9+).
                if (options.ImportTextures &&
                    UsdNative.GetImportMaterialMetallicLayout(context, i, out bool ormPacked,
                                                              out string occlusion))
                {
                    material.MetallicIsOrm = ormPacked;
                    material.OcclusionPath = ResolveTexturePath(context, data, occlusion);
                }

                // OmniPBR flips its normal map's tangent frame along V by default (API 13);
                // the channel is flipped once decoded. See FlipNormalChannels.
                if (options.ImportTextures && !string.IsNullOrEmpty(material.NormalPath) &&
                    UsdNative.GetImportMaterialNormalFlip(context, i, out bool flipU, out bool flipV))
                {
                    material.NormalFlipU = flipU;
                    material.NormalFlipV = flipV;
                }

                // A plain metallic map and a roughness map of its own (API 10+); they are
                // packed into one texture once decoded. See PackMetallicRoughness.
                if (options.ImportTextures &&
                    UsdNative.GetImportMaterialTextureLayout(context, i, out bool metallicIsPlain,
                                                             out string roughness))
                {
                    material.MetallicIsPlain = metallicIsPlain;
                    material.RoughnessPath = ResolveTexturePath(context, data, roughness);
                }

                // An albedo blended with a colour by a mask (API 13); baked once decoded.
                // See PackAlbedoMask.
                if (options.ImportTextures && !string.IsNullOrEmpty(material.AlbedoPath) &&
                    UsdNative.GetImportMaterialAlbedoMask(context, i, out string mask, out Vector3 maskColor))
                {
                    material.MaskPath = ResolveTexturePath(context, data, mask);
                    material.MaskColor = maskColor;
                    material.MaskMode = 0;
                }

                // Per-channel mask colours and the roughness range (API 13). See PackAlbedoMask
                // and PackRoughnessRange.
                if (options.ImportTextures &&
                    UsdNative.GetImportMaterialUnrealBlend(context, i, out int maskMode, out Vector3[] channelColours,
                                                           out bool hasRange, out Vector2 range))
                {
                    if (material.MaskPath != null && maskMode >= 0)
                    {
                        material.MaskMode = maskMode;
                        material.ChannelColours = channelColours;
                    }

                    if (hasRange && material.MetallicIsOrm && !string.IsNullOrEmpty(material.MetallicGlossPath))
                    {
                        material.HasRoughnessRange = true;
                        material.RoughnessRange = range;
                    }
                }

                data.Materials.Add(material);
            }

            // Read + decode each referenced texture file once (deduplicated across materials).
            // For ImportAsync this whole method runs on a worker thread, so the heavy PNG
            // decode happens off the main thread.
            if (deferTextures)
            {
                CachePackagedTextureBytes(context, data);
            }
            else
            {
                ReadUniqueTextureBytes(context, data);
                DecodeUniqueTextures(data, options);
            }

            bool composition = UsdNative.SupportsComposition;
            int nodeCount = UsdNative.GetImportNodeCount(context);
            for (int i = 0; i < nodeCount; i++)
            {
                RUsdImportNode node = UsdNative.GetImportNodeInfo(context, i);
                data.Nodes.Add(new NodeData
                {
                    Path = node.Path,
                    LocalMatrix = node.LocalMatrix,
                    Visible = node.Visible,
                    Flags = composition ? UsdNative.GetImportNodeFlags(context, i) : 0
                });
            }

            if (options.RecordSource && composition)
            {
                data.CompositionReport = UsdNative.GetImportComposition(context);
            }

            // Point instancers are geometry, not a scene component, and are read whether or
            // not lights are being imported.
            data.InstancedMeshes.AddRange(UsdNative.GetImportInstancedMeshes(context));

            if (UsdNative.SupportsSceneComponents)
            {
                if (options.ImportLights || options.ApplyDomeLightToEnvironment)
                {
                    // A dome texture is read from disk, so it goes through the same resolution
                    // and stage-folder confinement as material textures.
                    foreach (KeyValuePair<RUsdImportLight, string> light in UsdNative.GetImportLights(context))
                    {
                        string texture = string.IsNullOrEmpty(light.Value)
                            ? string.Empty
                            : ResolveTexturePath(context, data, light.Value) ?? string.Empty;
                        data.Lights.Add(new KeyValuePair<RUsdImportLight, string>(light.Key, texture));
                    }
                }

                if (options.ImportCameras)
                {
                    data.Cameras.AddRange(UsdNative.GetImportCameras(context));
                }

                if (options.ImportSpatialAudio)
                {
                    data.Audio.AddRange(UsdNative.GetImportAudio(context));
                }
            }

            for (int i = 0; i < info.MeshCount; i++)
            {
                MeshData mesh = ReadMeshData(context, i);
                if (options.DoubleSidedOpenMeshes && !mesh.DoubleSided && HasOpenEdges(mesh.Points, mesh.Indices))
                {
                    mesh.DoubleSided = true;
                }

                data.Meshes.Add(mesh);
            }

            if (UsdNative.SupportsAnimation)
            {
                data.TimeInfo = UsdNative.GetImportTimeInfo(context);
                data.HasTimeInfo = true;
                data.LayerFiles = UsdNative.GetImportLayers(context);
                if (options.ImportAnimation)
                {
                    data.NodeTracks.AddRange(UsdNative.GetImportNodeTracks(context));
                    for (int i = 0; i < data.Meshes.Count; i++)
                    {
                        data.Meshes[i].PointCache = UsdNative.GetImportMeshPointCache(context, i, data.Meshes[i].Points.Length);
                    }
                }

                if (options.ImportSkinning)
                {
                    data.Skeletons.AddRange(UsdNative.GetImportSkeletons(context));
                    for (int i = 0; i < data.Meshes.Count; i++)
                    {
                        data.Meshes[i].Skin = UsdNative.GetImportMeshSkin(context, i, data.Meshes[i].Points.Length);
                    }
                }
            }

            data.NativeDiagnostics = UsdNative.GetDiagnostics(context);
            LogNativeImportWarnings(data.NativeDiagnostics);
            WriteNativeDiagnosticsLog(options, data.NativeDiagnostics);
            return data;
        }

        // Said once per session: a plugin older than API 13 (a Windows or Linux payload not yet
        // rebuilt) imports skinned meshes static, imports no animation and reads at Default time.
        private static bool animationApiNoteShown;

        private static void NoteMissingAnimationApi(UsdImportOptions options)
        {
            if (animationApiNoteShown || UsdNative.SupportsAnimation ||
                !(options.ImportSkinning || options.ImportAnimation || options.ReadAtStageStartTime))
            {
                return;
            }

            animationApiNoteShown = true;
            Debug.LogWarning(
                $"USD import: {UsdNative.LoadedPluginDescription}; skinning, " +
                $"animation and reading at the stage's start time need {UsdNative.NeedsPlugin(UsdNative.AnimationApiVersion)}, so skinned meshes " +
                "import static, no animation is imported and time-sampled values are ignored. Rebuild the native plugin for " +
                "this platform (see Native~/REBUILD_WINDOWS_LINUX.md).");
        }

        // Variant selections and payload control need native API 6; the plain open is identical
        // to API 6's without them, so it stays the path for older plugins.
        private static IntPtr OpenStageForImport(string fullInputPath, UsdImportOptions options)
        {
            NoteMissingAnimationApi(options);
            bool wantsComposition = !options.LoadPayloads || (options.VariantSelections?.Count ?? 0) > 0;
            // GetPreviewInfo's kept stage was read at the start time, so a Default-time import
            // cannot reuse it.
            bool readAtDefaultTime = !options.ReadAtStageStartTime && UsdNative.SupportsAnimation;
            IntPtr kept = TakePreviewStage(fullInputPath, options, wantsComposition || readAtDefaultTime);
            if (kept != IntPtr.Zero)
            {
                return kept;
            }

            bool limited = RequiresLimitsApi(options);
            if (!UsdNative.SupportsComposition && !limited)
            {
                if (wantsComposition)
                {
                    throw new UsdImportException(
                        $"Variant selections and payload control need {UsdNative.NeedsPlugin(UsdNative.CompositionApiVersion)}; {UsdNative.LoadedPluginDescription}. Rebuild the native plugin for this platform (see Native~/REBUILD_WINDOWS_LINUX.md).");
                }

                return UsdNative.OpenStage(fullInputPath, options.CaptureNativeDiagnostics);
            }

            var selections = new System.Text.StringBuilder();
            foreach (UsdVariantSelection selection in options.VariantSelections ?? new List<UsdVariantSelection>())
            {
                if (selection == null || string.IsNullOrEmpty(selection.PrimPath) || string.IsNullOrEmpty(selection.VariantSet))
                {
                    continue;
                }

                selections.Append(selection.PrimPath).Append('\t')
                    .Append(selection.VariantSet).Append('\t')
                    .Append(selection.Variant ?? string.Empty).Append('\n');
            }

            string loads = options.PayloadsToLoad != null ? string.Join("\n", options.PayloadsToLoad) : string.Empty;
            return UsdNative.OpenStageEx(
                fullInputPath,
                options.CaptureNativeDiagnostics,
                !options.LoadPayloads,
                selections.ToString(),
                loads,
                readAtDefaultTime,
                limited ? options.Limits : null);
        }

        // Limits other than the defaults need RUsd_OpenStageWithLimits (native API 13). Refused on
        // an older plugin rather than ignored: a caller that lowered them for a device would
        // otherwise get the full import it was budgeting against.
        private static bool RequiresLimitsApi(UsdImportOptions options)
        {
            if (options.Limits == null || options.Limits.IsDefault)
            {
                return false;
            }

            if (!UsdNative.SupportsAnimation)
            {
                throw new UsdImportException(
                    $"UsdImportOptions.Limits other than the defaults need {UsdNative.NeedsPlugin(UsdNative.AnimationApiVersion)}; " +
                    $"{UsdNative.LoadedPluginDescription}. Rebuild the native plugin for this platform " +
                    "(see Native~/REBUILD_WINDOWS_LINUX.md), or import with the default limits.");
            }

            return true;
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
                DoubleSided = ReadDoubleSided(context, meshInfo.PrimPath),
                Points = points,
                Indices = indices,
                Normals = normals,
                UvSets = uvSets,
                Submeshes = submeshes
            };
        }

        // True when the triangle mesh has an edge used by an odd number of triangles: a boundary
        // (a sheet, a panel, an open box) or a non-manifold edge. Vertices are welded on their exact
        // position first, since the importer splits a vertex wherever normals or UVs differ and
        // those seams are not openings. One pass, a set of edges toggled in and out.
        internal static bool HasOpenEdges(RUsdVec3[] points, int[] indices)
        {
            if (points == null || indices == null || indices.Length < 3)
            {
                return false;
            }

            var weld = new Dictionary<(float, float, float), int>(points.Length);
            var ids = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var key = (points[i].X, points[i].Y, points[i].Z);
                if (!weld.TryGetValue(key, out int id))
                {
                    id = weld.Count;
                    weld.Add(key, id);
                }

                ids[i] = id;
            }

            var odd = new HashSet<long>();
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = ids[indices[t + e]], b = ids[indices[t + (e + 1) % 3]];
                    if (a == b)
                    {
                        continue;
                    }

                    long edge = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (!odd.Remove(edge))
                    {
                        odd.Add(edge);
                    }
                }
            }

            return odd.Count > 0;
        }

        // UsdGeomGprim.doubleSided at the default time. USD's fallback is false (one-sided); Isaac
        // Sim and Omniverse draw both sides of a doubleSided mesh, so a mesh meant to be seen from
        // inside (a room, a backdrop box) is open from outside unless the import honours it.
        // Read through the Stage API on the import's own stage: no plugin rebuild, and a plugin
        // without that API (before native API 13) reads every mesh as one-sided, as before.
        private static bool ReadDoubleSided(IntPtr context, string primPath)
        {
            if (!UsdNative.SupportsStageApi || string.IsNullOrEmpty(primPath))
            {
                return false;
            }

            try
            {
                if (!UsdNative.MeasureAttributeValue(context, primPath, "doubleSided", UsdNative.TimeDefault, 0.0,
                                                     (int)UsdValueType.Bool, out _, out long bytes) || bytes < 1)
                {
                    return false;
                }

                var value = new byte[bytes];
                UsdNative.ReadAttributeValue(context, primPath, "doubleSided", UsdNative.TimeDefault, 0.0,
                                             (int)UsdValueType.Bool, value, bytes);
                return value[0] != 0;
            }
            catch (UsdStageException)
            {
                // A path the Stage API cannot address (a prototype under an instance proxy) or a
                // value of another type: keep the one-sided default rather than fail the import.
                return false;
            }
        }

        // The Render Face Both copy of an imported material, for the doubleSided meshes whose back
        // faces cannot be baked (BakesBackFaces), made once per material per import so one-sided
        // meshes that share it keep culling back faces. URP Lit and the built-in
        // Standard shader read _Cull (0 = off); HDRP Lit reads _DoubleSidedEnable and _CullMode.
        private static Material GetDoubleSidedMaterial(Material material, StageData data)
        {
            if (material == null)
            {
                return null;
            }

            if (data.DoubleSidedMaterials.TryGetValue(material, out Material copy))
            {
                return copy;
            }

            bool cull = material.HasProperty("_Cull");
            bool hdrp = material.HasProperty("_DoubleSidedEnable");
            if (!cull && !hdrp)
            {
                // A shader with no render-face switch: leave it as it is.
                data.DoubleSidedMaterials[material] = material;
                return material;
            }

            copy = new Material(material) { name = material.name + " (Double Sided)" };
            if (cull)
            {
                copy.SetFloat("_Cull", 0f);
            }

            if (hdrp)
            {
                copy.SetFloat("_DoubleSidedEnable", 1f);
                copy.EnableKeyword("_DOUBLESIDED_ON");
                if (copy.HasProperty("_CullMode"))
                {
                    copy.SetFloat("_CullMode", 0f);
                }
            }

            copy.doubleSidedGI = true;
            data.DoubleSidedMaterials[material] = copy;
            return copy;
        }

        // ---- Build: Unity object creation; must run on the main thread. ----

        private static UsdImportResult BuildResult(StageData data, UsdImportOptions options)
        {
            try
            {
                RunDeferredPacking(data);
                var decodeCache = new Dictionary<string, Texture2D>();
                var materials = new List<Material>(data.Materials.Count);
                var materialShare = options.ShareIdenticalMaterials ? new Dictionary<string, Material>() : null;
                foreach (MaterialData materialData in data.Materials)
                {
                    string shareKey = materialShare != null ? MaterialShareKey(materialData) : null;
                    if (shareKey != null && materialShare.TryGetValue(shareKey, out Material shared))
                    {
                        materials.Add(shared);
                        continue;
                    }

                    Material built = BuildMaterial(materialData, options, data, decodeCache);
                    if (shareKey != null)
                    {
                        materialShare[shareKey] = built;
                    }

                    materials.Add(built);
                }

                UsdImportResult result = CreateRootResult(data, options, materials.Count);
                Transform root = result.RootObject.transform;

                data.JointMirrorPrims = FindJointMirrorPrims(data, options);
                foreach (NodeData node in data.Nodes)
                {
                    if (!data.JointMirrorPrims.Contains(node.Path))
                    {
                        BuildNode(node, root, options);
                    }
                }

                List<UsdSkeleton> skeletons = BuildSkeletons(data, root, options);
                MeshShareCache meshCache = options.ShareIdenticalMeshes ? new MeshShareCache() : null;
                var meshObjects = new List<GameObject>(data.Meshes.Count);
                foreach (MeshData meshData in data.Meshes)
                {
                    meshObjects.Add(BuildMesh(meshData, root, materials, options, result, meshCache, data, skeletons));
                }

                // Point instancers first: they switch their prototypes' renderers off, which the
                // source snapshot has to see as the imported state.
                BuildPointInstancers(data, root, meshObjects, options);
                RecordSource(data, options, result);
                BuildSceneComponents(data, root, options);
                result.Animation = BuildAnimation(data, root, skeletons, options);
                BuildPhysics(data, root, result);
                if (options.ImportLights && options.PhysicalLightUnits && options.EmissiveMeshLights)
                {
                    UsdIsaacEnvironment.BuildEmissiveLights(root.transform);
                }

                if (options.ImportLights && options.PhysicalLightUnits)
                {
                    UsdIsaacEnvironment.ShadowExtraSuns(root.transform);
                }

                ScheduleEnvironmentCaptures(root, options);
                return result;
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
        }

        // Async build that spreads the main-thread work across frames so the editor/player stays
        // responsive. Yields whenever the per-frame budget is exceeded and reports progress.
        // streamedMaterials: non-null when the textures are still being decoded on a worker
        // (ImportAsync with StreamTextures). The materials are then built from their constants
        // alone, without reading the texture caches or the UDIM layouts that worker is writing,
        // and are handed back so AttachStreamedTexturesAsync can fill them in.
        private static async Task<UsdImportResult> BuildResultAsync(StageData data, UsdImportOptions options,
                                                                    List<Material> streamedMaterials = null)
        {
            try
            {
                float budget = Mathf.Max(1f, options.MaxMillisecondsPerFrame);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Action<float, string> progress = options.ProgressCallback;

                // Materials (texture decode) — the dominant cost; report 0 → 0.85 here. Each
                // texture is decoded on a worker thread (await), so the main thread is released
                // during decoding and large textures no longer cause per-texture hitches.
                if (streamedMaterials == null)
                {
                    RunDeferredPacking(data);
                }

                var decodeCache = new Dictionary<string, Texture2D>();
                var materials = streamedMaterials ?? new List<Material>(data.Materials.Count);
                var materialShare = options.ShareIdenticalMaterials ? new Dictionary<string, Material>() : null;
                for (int i = 0; i < data.Materials.Count; i++)
                {
                    string shareKey = materialShare != null ? MaterialShareKey(data.Materials[i]) : null;
                    if (shareKey != null && materialShare.TryGetValue(shareKey, out Material shared))
                    {
                        materials.Add(shared);
                        continue;
                    }

                    Material made = streamedMaterials != null
                        ? AssembleMaterial(data.Materials[i], options, data, null, null, null, null, null, false)
                        : BuildMaterial(data.Materials[i], options, data, decodeCache);
                    if (shareKey != null)
                    {
                        materialShare[shareKey] = made;
                    }

                    materials.Add(made);
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
                data.JointMirrorPrims = FindJointMirrorPrims(data, options);
                for (int i = 0; i < data.Nodes.Count; i++)
                {
                    if (!data.JointMirrorPrims.Contains(data.Nodes[i].Path))
                    {
                        BuildNode(data.Nodes[i], root, options);
                    }

                    if (clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        progress?.Invoke(0.85f + (data.Nodes.Count > 0 ? 0.05f * (i + 1) / data.Nodes.Count : 0f), "Hierarchy");
                        await Task.Yield();
                        clock.Restart();
                    }
                }

                List<UsdSkeleton> skeletons = BuildSkeletons(data, root, options);

                // Meshes — 0.90 → 1.0.
                MeshShareCache meshCache = options.ShareIdenticalMeshes ? new MeshShareCache() : null;
                var meshObjects = new List<GameObject>(data.Meshes.Count);
                for (int i = 0; i < data.Meshes.Count; i++)
                {
                    meshObjects.Add(BuildMesh(data.Meshes[i], root, materials, options, result, meshCache, data, skeletons));
                    progress?.Invoke(0.90f + (data.Meshes.Count > 0 ? 0.10f * (i + 1) / data.Meshes.Count : 0.10f), "Meshes");
                    if (clock.Elapsed.TotalMilliseconds >= budget)
                    {
                        await Task.Yield();
                        clock.Restart();
                    }
                }

                BuildPointInstancers(data, root, meshObjects, options);
                RecordSource(data, options, result);
                BuildSceneComponents(data, root, options);
                result.Animation = BuildAnimation(data, root, skeletons, options);
                BuildPhysics(data, root, result);
                if (options.ImportLights && options.PhysicalLightUnits && options.EmissiveMeshLights)
                {
                    UsdIsaacEnvironment.BuildEmissiveLights(root.transform);
                }

                if (options.ImportLights && options.PhysicalLightUnits)
                {
                    UsdIsaacEnvironment.ShadowExtraSuns(root.transform);
                }

                ScheduleEnvironmentCaptures(root, options);
                progress?.Invoke(streamedMaterials != null ? StreamedBuildProgress : 1f,
                                 streamedMaterials != null ? "Streaming textures" : "Done");
                return result;
            }
            catch (Exception exception) when (!(exception is UsdImportException))
            {
                throw CreateImportException(exception);
            }
        }

        // The files on disk the import read: the stage's layers (native API 13; the input alone
        // before that) and every texture file, UDIM tiles included. Textures inside a package are
        // covered by the package, which is a layer.
        private static string[] CollectSourceFiles(StageData data)
        {
            var files = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string layer in data.LayerFiles)
            {
                files.Add(layer);
            }

            files.Add(data.InputPath);
            void AddTexture(string path)
            {
                if (!string.IsNullOrEmpty(path) && !data.PackageAssets.ContainsKey(path) && File.Exists(path))
                {
                    files.Add(Path.GetFullPath(path));
                }
            }

            foreach (string path in data.DecodedTextures.Keys)
            {
                AddTexture(path);
            }

            foreach (string path in data.TextureBytes.Keys)
            {
                AddTexture(path);
            }

            foreach (List<KeyValuePair<int, string>> tiles in data.UdimSets.Values)
            {
                foreach (KeyValuePair<int, string> tile in tiles)
                {
                    AddTexture(tile.Value);
                }
            }

            var result = new string[files.Count];
            files.CopyTo(result);
            return result;
        }

        private static UsdImportResult CreateRootResult(StageData data, UsdImportOptions options, int materialCount)
        {
            GameObject root = new GameObject(GetRootObjectName(data, options));
            if (options.Parent != null)
            {
                root.transform.SetParent(options.Parent, false);
            }

            var result = new UsdImportResult
            {
                InputPath = data.InputPath,
                SourceFiles = CollectSourceFiles(data),
                RootObject = root,
                DefaultPrimPath = data.DefaultPrimPath,
                MetersPerUnit = data.MetersPerUnit,
                UpAxis = GetUpAxisName(data.UpAxis),
                MaterialCount = materialCount,
                NativeDiagnostics = data.NativeDiagnostics
            };

            if (options.ApplyStageUpAxisAndUnits)
            {
                ApplyStageCorrection(root.transform, data, result);
            }

            return result;
        }

        // Shows the stage Y-up and in meters by putting its upAxis and metersPerUnit on the import
        // root (UsdImportOptions.ApplyStageUpAxisAndUnits). Nothing below the root changes:
        // vertices and every prim's local transform stay as authored, so a Preserve export diffs
        // them against the import snapshot and never sees the correction.
        //
        // Points and matrices below the root are already in Unity's basis, X negated
        // (USD (x, y, z) -> Unity (-x, y, z)). A Z-up stage then gets the same -90 degree rotation
        // about X that Omniverse and Isaac Sim use to go from Z-up to Y-up, which maps
        // (x, y, z) -> (x, z, -y). A rotation about X leaves the X component alone, so it commutes
        // with the X flip, and the whole chain is USD (x, y, z) -> Unity (-x, z, -y):
        //   USD +Z (0, 0, 1) -> flipped (0, 0, 1) -> rotated (0, 1, 0)  = Unity +Y (up)
        //   USD +Y (0, 1, 0) -> flipped (0, 1, 0) -> rotated (0, 0, -1) = Unity -Z
        //   USD +X (1, 0, 0) -> flipped (-1, 0, 0) -> unchanged         = Unity -X, as for a Y-up stage
        // Unity's Quaternion.Euler(-90, 0, 0) is that matrix: y' = z, z' = -y. The rotation and a
        // positive uniform scale keep the determinant positive, so winding is unaffected.
        //
        // Unity units are meters, so the scale is metersPerUnit itself (0.01 for a centimeter
        // stage). A missing or invalid value is left at 1.
        private static void ApplyStageCorrection(Transform root, StageData data, UsdImportResult result)
        {
            Quaternion rotation = data.UpAxis == 2 ? Quaternion.Euler(-90f, 0f, 0f) : Quaternion.identity;
            double metersPerUnit = data.MetersPerUnit;
            float scale = metersPerUnit > 0.0 && !double.IsInfinity(metersPerUnit) && !double.IsNaN(metersPerUnit)
                ? (float)metersPerUnit
                : 1f;

            root.localRotation = rotation;
            root.localScale = Vector3.one * scale;
            result.StageCorrectionRotation = rotation;
            result.StageCorrectionScale = scale;
        }

        // ---- Physics (UsdImportOptions.ImportPhysics; native API 13) ----

        // Reads the stage's physics into the stage data while the stage is open. Native and
        // managed copies only, so it runs on ImportAsync's worker too. A failed read leaves the
        // rest of the import as it is and is reported when the scene is built.
        private static void ReadPhysicsData(IntPtr context, StageData data, UsdImportOptions options)
        {
            data.Physics = null;
            data.PhysicsReadError = null;
            if (!options.ImportPhysics)
            {
                return;
            }

            if (!UsdNative.SupportsPhysics)
            {
                data.PhysicsReadError = UsdNative.PhysicsUnavailableReason;
                return;
            }

            try
            {
                data.Physics = UsdNative.ReadPhysics(context);
                data.Physics.Mimics = ReadMimicJoints(context, data.Physics);
            }
            catch (Exception exception)
            {
                data.PhysicsReadError = exception.Message;
            }
        }

        // PhysxMimicJointAPI:<dof> on a joint the native read reported: its referenceJoint,
        // gearing (fallback 1) and offset (fallback 0), read through the Stage API on the import's
        // own stage, as ReadDoubleSided does. A coupling that cannot be read stays a note.
        private static UsdPhysicsMimicSource[] ReadMimicJoints(IntPtr context, UsdNativePhysics physics)
        {
            var mimics = new List<UsdPhysicsMimicSource>();
            if (!UsdNative.SupportsStageApi)
            {
                return mimics.ToArray();
            }

            foreach (RUsdPhysicsUnsupported entry in physics.Unsupported)
            {
                string path = physics.String(entry.PathIndex);
                string schema = physics.String(entry.DetailIndex) ?? string.Empty;
                int colon = schema.IndexOf(':');
                if (entry.Kind != UsdNative.PhysicsUnsupportedMimicJoint || string.IsNullOrEmpty(path) || colon < 0)
                {
                    continue;
                }

                string prefix = "physxMimicJoint:" + schema.Substring(colon + 1) + ":";
                try
                {
                    string[] targets = UsdNative.QueryText(context, UsdNative.QueryTargets, path, prefix + "referenceJoint")
                        .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (targets.Length == 0)
                    {
                        continue;
                    }

                    mimics.Add(new UsdPhysicsMimicSource
                    {
                        JointPath = path,
                        ReferenceJointPath = targets[0],
                        Gearing = ReadFloat(context, path, prefix + "gearing", 1f),
                        Offset = ReadFloat(context, path, prefix + "offset", 0f),
                    });
                }
                catch (UsdStageException)
                {
                    // A path the Stage API cannot address: the joint stays unsupported.
                }
            }

            return mimics.ToArray();
        }

        // A float attribute's default-time value, or `fallback` when it has none (an attribute the
        // prim does not author is an error to the Stage API, not an empty value).
        private static float ReadFloat(IntPtr context, string primPath, string name, float fallback)
        {
            try
            {
                if (!UsdNative.MeasureAttributeValue(context, primPath, name, UsdNative.TimeDefault, 0.0,
                                                     (int)UsdValueType.Float, out _, out long bytes) || bytes < sizeof(float))
                {
                    return fallback;
                }

                var value = new float[1];
                UsdNative.ReadAttributeValue(context, primPath, name, UsdNative.TimeDefault, 0.0, (int)UsdValueType.Float, value, sizeof(float));
                return value[0];
            }
            catch (UsdStageException)
            {
                return fallback;
            }
        }

        // Runs after RecordSource: the builder may move articulation links under their parent
        // link, and each object's UsdPrimSource has its prim path by then.
        private static void BuildPhysics(StageData data, Transform root, UsdImportResult result)
        {
            if (data.PhysicsReadError != null)
            {
                Debug.LogWarning($"USD import: physics not imported: {data.PhysicsReadError}");
                result.PhysicsNotes = new[] { "Physics not imported: " + data.PhysicsReadError };
                return;
            }

            if (data.Physics == null)
            {
                return;
            }

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (NodeData node in data.Nodes)
            {
                known.Add(node.Path);
            }

            List<string> notes = UsdPhysicsBuilder.Build(data.Physics, root, (path, create) =>
                create || known.Contains(path) ? CreatePrimPathObject(root, path) : null);
            result.PhysicsNotes = notes;
            if (notes.Count > 0)
            {
                Debug.LogWarning($"USD import: {notes.Count} physics note(s) for {data.InputPath}:\n  " + string.Join("\n  ", notes));
            }
        }

        // ---- Scene components (lights, cameras, spatial audio; native API 7) ----

        // USD lights emit and cameras look along local -Z with +Y up; Unity's along +Z. The node
        // keeps its USD transform (it is a prim), so the component goes on a child turned 180
        // degrees about Y, marked so exporters do not write it as a prim. The native X flip on
        // node matrices leaves Z alone, so -Z here is the authored direction.
        internal static GameObject CreateLightHelper(Transform node, string name) => CreateComponentHelper(node, name);

        /// <summary>
        /// Re-captures the scene's environment reflection from inside an imported stage (see
        /// <see cref="UsdImportOptions.InteriorReflection"/>). The capture is a snapshot of the
        /// lighting at that moment: call this after turning lights on or off.
        /// </summary>
        public static bool RefreshReflection(GameObject root) =>
            root != null && UsdIsaacEnvironment.CaptureInteriorReflection(root.transform);

        // The dome's reflection cubemap and the interior reflection both render the scene, which
        // must not happen in the frame that created the import's renderers (see
        // UsdDeferredRender). They run once the scene has drawn a frame; until then a dome-lit
        // scene reflects the skybox through RenderSettings and nothing else is missing.
        private static void ScheduleEnvironmentCaptures(Transform root, UsdImportOptions options)
        {
            bool dome = options.ApplyDomeLightToEnvironment && options.PhysicalLightUnits &&
                RenderSettings.skybox != null && RenderSettings.skybox.name == "UsdDomeLight";
            // Only a stage with something overhead is an interior; an open stage (a turntable, a
            // bench under a dome) keeps the sky's reflection, as Isaac Sim's metals show the sky
            // there. A probe in the middle of an open stage sees mostly nothing, or the inside of
            // an object, and turned every mirror black (the bench's chrome under a uniform dome).
            bool interior = options.PhysicalLightUnits && options.InteriorReflection &&
                UsdIsaacEnvironment.LooksEnclosed(root);
            if (!dome && !interior)
            {
                return;
            }

            UsdDeferredRender.AfterNextFrame(() =>
            {
                if (root == null)
                {
                    return;
                }

                if (dome && !UsdIsaacEnvironment.TryRefreshSkyReflection())
                {
                    UsdDeferredRender.AfterNextFrame(() => CaptureAfterSky(root, dome, interior, 10));
                    return;
                }

                UsdDeferredRender.AfterNextFrame(() => CaptureAfterSky(root, false, interior, 0));
            });
        }

        // The sky capture in the first frames after an import can come back black (all six faces
        // zero on the bench's uniform dome: every chrome and gold sphere reflected black) although
        // the same call later fills it; it is retried a frame at a time (up to ten) before the
        // interior passes, which reflect it.
        private static void CaptureAfterSky(Transform root, bool dome, bool interior, int retries)
        {
            if (root == null)
            {
                return;
            }

            if (dome && !UsdIsaacEnvironment.TryRefreshSkyReflection() && retries > 0)
            {
                UsdDeferredRender.AfterNextFrame(() => CaptureAfterSky(root, dome, interior, retries - 1));
                return;
            }

            if (!interior)
            {
                return;
            }

            // Three passes a frame apart: the first reflects the sky, the later ones the previous
            // capture, which is what converges to the hall's own specular bounce (see
            // CaptureInteriorReflection).
            UsdIsaacEnvironment.CaptureInteriorReflection(root);
            UsdDeferredRender.AfterNextFrame(() =>
            {
                if (root == null) return;
                UsdIsaacEnvironment.CaptureInteriorReflection(root);
                UsdDeferredRender.AfterNextFrame(() =>
                {
                    if (root != null) UsdIsaacEnvironment.CaptureInteriorReflection(root);
                });
            });
        }

        private static GameObject CreateComponentHelper(Transform node, string name)
        {
            var helper = new GameObject(name);
            helper.transform.SetParent(node, false);
            helper.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            helper.AddComponent<UsdImportHelperObject>();
            return helper;
        }

        // USD light intensities have no single convention: Pixar-style scenes author a distant light
        // around 1 to 5, Omniverse and Isaac Sim around 1000 to 3000 (and spheres higher). Guessing
        // the convention from the value does not work. The previous rule -- divide by 1000 above 10,
        // pass through below -- is discontinuous at the threshold: 10 stays 10 while 11 becomes
        // 0.011, a 700x cliff. ALab lands on both sides of it (status lights authored at 10, sun at
        // 4096), so the status lights came out brighter than the sun and tinted the whole set green.
        //
        // Normalise against the stage's own brightest light instead. Relative brightness is what the
        // scene actually encodes, it needs no convention, and it is continuous. Directional lights
        // get a lower ceiling because Unity treats 1 as full daylight.
        private const float MaxPunctualIntensity = 8f;
        private const float MaxDirectionalIntensity = 2f;

        private static float ReferenceLightIntensity(StageData data)
        {
            float reference = 0f;
            foreach (KeyValuePair<RUsdImportLight, string> entry in data.Lights)
            {
                // Dome lights drive the environment, not a Light component, and ALab authors its
                // dome at 1.0 next to a 4096 sun. Including it would skew the reference.
                if (entry.Key.Type == 6)
                {
                    continue;
                }

                reference = Mathf.Max(reference, entry.Key.Intensity);
            }

            return reference;
        }

        // Dome lights feed RenderSettings.ambientLight, not a Light component, so they are not part
        // of the stage-relative normalisation above. Behaviour is deliberately left as it was while
        // the punctual lights change: ALab authors its dome at 1.0 against a 4096 sun, so a full
        // strength flat ambient is almost certainly too strong, but that is a look decision that
        // belongs with the render defaults work rather than with this fix.
        private static float MapDomeAmbient(float usdIntensity)
        {
            float value = usdIntensity > 10f ? usdIntensity / 1000f : usdIntensity;
            return Mathf.Clamp(value, 0f, 8f);
        }

        private static float MapLightIntensity(float usdIntensity, float reference, bool directional)
        {
            float ceiling = directional ? MaxDirectionalIntensity : MaxPunctualIntensity;
            if (reference <= 0f || usdIntensity <= 0f)
            {
                return 0f;
            }

            return Mathf.Clamp(ceiling * (usdIntensity / reference), 0f, ceiling);
        }

        private static void BuildSceneComponents(StageData data, Transform root, UsdImportOptions options)
        {
            // A live switch rereads part of the stage; its lights keep the reference the whole
            // stage's set when it was imported, so they match the ones left in place.
            float lightReference = data.LightReference > 0f ? data.LightReference : ReferenceLightIntensity(data);
            foreach (KeyValuePair<RUsdImportLight, string> entry in data.Lights)
            {
                RUsdImportLight usd = entry.Key;
                if (usd.NodeIndex < 0 || usd.NodeIndex >= data.Nodes.Count)
                {
                    continue;
                }

                Transform node = CreatePrimPathObject(root, data.Nodes[usd.NodeIndex].Path).transform;
                var color = new Color(usd.ColorR, usd.ColorG, usd.ColorB, 1f);
                // A Light's colour is converted from sRGB to linear for rendering, as a material
                // colour is; see UsdLightColor.
                Color lightColor = UsdLightColor(color);

                if (usd.Type == 6)  // RUSD_LIGHT_DOME
                {
                    UsdDomeLight dome = node.gameObject.AddComponent<UsdDomeLight>();
                    dome.Color = color;
                    dome.Intensity = usd.Intensity;
                    dome.TexturePath = entry.Value ?? string.Empty;
                    if (options.ApplyDomeLightToEnvironment)
                    {
                        if (options.PhysicalLightUnits)
                        {
                            UsdIsaacEnvironment.ApplyDome(dome, node);
                        }
                        else
                        {
                            ApplyDomeLight(dome);
                        }
                    }

                    continue;
                }

                if (!options.ImportLights)
                {
                    continue;
                }

                if (options.PhysicalLightUnits)
                {
                    float worldScale = Mathf.Abs(node.lossyScale.x) > 0f ? Mathf.Abs(node.lossyScale.x) : 1f;
                    int before = node.childCount;
                    if (usd.Type == 3)  // rect: a grid of spots
                    {
                        UsdIsaacEnvironment.BuildRectGrid(node, usd, lightColor, worldScale);
                    }
                    else
                    {
                        GameObject physicalHelper = CreateComponentHelper(node, "UsdLight");
                        Light physical = physicalHelper.AddComponent<Light>();
                        physical.color = lightColor;
                        UsdIsaacEnvironment.ConfigurePhysical(physical, usd, worldScale);
                    }

                    // An invisible light lights the scene only through bounces in Isaac Sim; see
                    // UsdLightUnits.InvisibleLight.
                    if (!data.Nodes[usd.NodeIndex].Visible)
                    {
                        for (int c = before; c < node.childCount; c++)
                        {
                            foreach (Light made in node.GetChild(c).GetComponentsInChildren<Light>(true))
                            {
                                // Direct light down to Isaac Sim's share, bounce kept whole. A
                                // bounce-only light (a rect's GI stand-in) is left as it is.
                                if (made.gameObject.name == "UsdLightGI")
                                {
                                    continue;
                                }

                                made.intensity *= UsdLightUnits.InvisibleLight;
                                made.bounceIntensity /= UsdLightUnits.InvisibleLight;
                                // 1.5 % of the direct light casts no shadow anyone sees, and each
                                // shadowed point light renders the stage six more times.
                                made.shadows = LightShadows.None;
                            }
                        }
                    }

                    continue;
                }

                GameObject helper = CreateComponentHelper(node, "UsdLight");
                Light light = helper.AddComponent<Light>();
                light.color = lightColor;
                light.intensity = MapLightIntensity(usd.Intensity, lightReference, usd.Type == 1);
                switch (usd.Type)
                {
                    case 1:  // distant
                        light.type = LightType.Directional;
                        break;
                    case 3:  // rect: one-sided, closest real-time match is a wide spot
                        light.type = LightType.Spot;
                        light.spotAngle = usd.ConeAngle > 0f ? usd.ConeAngle * 2f : 120f;
                        break;
                    default: // sphere, disk, cylinder
                        if (usd.ConeAngle > 0f)
                        {
                            light.type = LightType.Spot;
                            light.spotAngle = Mathf.Clamp(usd.ConeAngle * 2f, 1f, 179f);
                            light.innerSpotAngle = light.spotAngle * Mathf.Clamp01(1f - usd.ConeSoftness);
                        }
                        else
                        {
                            light.type = LightType.Point;
                        }

                        break;
                }
            }

            if (options.PhysicalLightUnits && options.ImportLights)
            {
                UsdIsaacEnvironment.ApplyShadowBudget(root);
            }

            foreach (RUsdImportCamera usd in data.Cameras)
            {
                if (usd.NodeIndex < 0 || usd.NodeIndex >= data.Nodes.Count)
                {
                    continue;
                }

                Transform node = CreatePrimPathObject(root, data.Nodes[usd.NodeIndex].Path).transform;
                GameObject helper = CreateComponentHelper(node, "UsdCamera");
                Camera camera = helper.AddComponent<Camera>();
                camera.enabled = false;

                // Clipping range and apertures are in stage units; Unity's are world distances.
                float scale = Mathf.Abs(node.lossyScale.y) > 0f ? Mathf.Abs(node.lossyScale.y) : 1f;
                camera.nearClipPlane = Mathf.Max(1e-4f, usd.NearClip * scale);
                camera.farClipPlane = Mathf.Max(camera.nearClipPlane * 2f, usd.FarClip * scale);
                if (usd.Projection == 1)  // orthographic: apertures in tenths of a stage unit
                {
                    camera.orthographic = true;
                    camera.orthographicSize = usd.VerticalAperture * 0.1f * 0.5f * scale;
                }
                else if (usd.FocalLength > 0f && usd.VerticalAperture > 0f)
                {
                    camera.fieldOfView = 2f * Mathf.Atan(usd.VerticalAperture / (2f * usd.FocalLength)) * Mathf.Rad2Deg;
                }

                if (usd.VerticalAperture > 0f && usd.HorizontalAperture > 0f)
                {
                    camera.aspect = usd.HorizontalAperture / usd.VerticalAperture;
                }
            }

            foreach (KeyValuePair<RUsdImportAudio, string> entry in data.Audio)
            {
                RUsdImportAudio usd = entry.Key;
                if (usd.NodeIndex < 0 || usd.NodeIndex >= data.Nodes.Count)
                {
                    continue;
                }

                GameObject node = CreatePrimPathObject(root, data.Nodes[usd.NodeIndex].Path);
                AudioSource source = node.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = usd.Loop != 0;
                source.volume = Mathf.Clamp01(usd.Gain);
                source.spatialBlend = usd.AuralMode == 0 ? 1f : 0f;

                UsdSpatialAudio record = node.AddComponent<UsdSpatialAudio>();
                // Confined like a texture: the component documents FilePath as a file to load, so a
                // rooted path, a `../` climb or a URL authored in an untrusted stage would otherwise
                // be handed to whatever loads it. A refused path is left empty.
                string authored = entry.Value ?? string.Empty;
                string candidate = authored.Length > 0 && !Path.IsPathRooted(authored) && !string.IsNullOrEmpty(data.BaseDirectory)
                    ? Path.Combine(data.BaseDirectory, authored)
                    : authored;
                record.FilePath = string.Empty;
                if (candidate.Length > 0)
                {
                    if (IsAssetPathAllowed(data, candidate, out string canonicalAudio))
                    {
                        record.FilePath = canonicalAudio;
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"USD import: spatial audio path resolves outside the stage folder, cleared: {authored}");
                    }
                }
                record.StartTime = usd.StartTime;
                record.EndTime = usd.EndTime;
            }
        }

        // Flat ambient from the dome's colour and intensity, and a panoramic skybox when its texture
        // is a PNG or JPG inside the stage folder (EXR and HDR cannot be decoded at run time).
        private static void ApplyDomeLight(UsdDomeLight dome)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = dome.Color * MapDomeAmbient(dome.Intensity);

            string path = dome.TexturePath;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (string.IsNullOrEmpty(path) || !File.Exists(path) ||
                (extension != ".png" && extension != ".jpg" && extension != ".jpeg"))
            {
                return;
            }

            Shader panoramic = Shader.Find("Skybox/Panoramic");
            if (panoramic == null)
            {
                return;
            }

            // Through the importer's image limits like every other texture: read straight into
            // LoadImage, a few-KB PNG whose header claims 65535x65535 allocated gigabytes on the
            // main thread (CWE-400).
            Texture2D texture = LoadImageFile(path);
            if (texture == null)
            {
                return;
            }

            var skybox = new Material(panoramic) { name = "UsdDomeLight" };
            skybox.SetTexture("_MainTex", texture);
            RenderSettings.skybox = skybox;
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

        // Attaches a drawer for every point instancer the stage declares. The prototype's own
        // GameObject stays in the hierarchy with its renderer off, so what is being scattered can
        // still be inspected but is drawn only at its placements; the scattering itself is an
        // array on the instancer node, not prims. A skinned prototype keeps its skeleton there, which
        // the stage's animation poses, and the drawer bakes that pose for every instance.
        private static void BuildPointInstancers(
            StageData data, Transform root, List<GameObject> meshObjects, UsdImportOptions options)
        {
            if (data.InstancedMeshes.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<RUsdImportInstancedMesh, float[]> entry in data.InstancedMeshes)
            {
                RUsdImportInstancedMesh record = entry.Key;
                if (record.MeshIndex < 0 || record.MeshIndex >= meshObjects.Count ||
                    record.InstancerNodeIndex < 0 || record.InstancerNodeIndex >= data.Nodes.Count)
                {
                    continue;
                }

                GameObject prototype = meshObjects[record.MeshIndex];
                Renderer source = prototype != null ? prototype.GetComponent<Renderer>() : null;
                var skinned = source as SkinnedMeshRenderer;
                MeshFilter filter = prototype != null ? prototype.GetComponent<MeshFilter>() : null;
                Mesh prototypeMesh = skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;
                if (prototypeMesh == null || source == null)
                {
                    continue;
                }

                Transform node = CreatePrimPathObject(root, data.Nodes[record.InstancerNodeIndex].Path).transform;
                var drawer = node.gameObject.AddComponent<UsdPointInstancer>();
                drawer.Mesh = prototypeMesh;
                drawer.Materials = source.sharedMaterials;
                if (skinned != null || prototype.GetComponent<UsdDualQuaternionSkin>() != null)
                {
                    drawer.SkinnedSource = source;
                }

                source.enabled = false;

                var placements = new Matrix4x4[entry.Value.Length / 16];
                for (int i = 0; i < placements.Length; i++)
                {
                    placements[i] = ToUnityMatrix(entry.Value, i * 16);
                }

                drawer.Instances = placements;
            }
        }

        // Instance placements arrive as USD authored them: row-major, row-vector convention and
        // USD's basis. The same conversion UsdLocalToUnity does for a node transform applies here
        // -- transpose, then the X flip on both sides -- because an instance is a transform like
        // any other, just one of many.
        private static Matrix4x4 ToUnityMatrix(float[] source, int offset)
        {
            var matrix = new Matrix4x4();
            for (int i = 0; i < 4; i++)
            {
                float si = i == 0 ? -1f : 1f;
                for (int j = 0; j < 4; j++)
                {
                    float sj = j == 0 ? -1f : 1f;
                    matrix[i, j] = si * sj * source[offset + (j * 4) + i];
                }
            }

            return matrix;
        }

        private static GameObject BuildMesh(
            MeshData meshData,
            Transform root,
            List<Material> materials,
            UsdImportOptions options,
            UsdImportResult result,
            MeshShareCache meshCache,
            StageData data,
            List<UsdSkeleton> skeletons)
        {
            if (!options.IncludeInvisible && !meshData.Visible)
            {
                return null;
            }

            // A skinned mesh keeps its own Mesh: the bind poses and weights belong to its binding,
            // not to its geometry, so it is never shared.
            UsdNativeSkin skin = options.ImportSkinning ? meshData.Skin : null;
            UsdSkeleton skeleton = null;
            UsdNativeSkeleton skeletonData = null;
            if (skin != null)
            {
                bool known = skin.SkeletonIndex >= 0 && skin.SkeletonIndex < skeletons.Count;
                skeleton = known ? skeletons[skin.SkeletonIndex] : null;
                skeletonData = known ? data.Skeletons[skin.SkeletonIndex] : null;
                if (skeleton == null && skin.Influences > 0)
                {
                    Debug.LogWarning($"USD import: mesh '{meshData.PrimPath}' is bound to a skeleton the import did not build; it imports unskinned.");
                    skin = null;
                }
            }

            RUsdVec3[] points = meshData.Points;
            int[] indices = meshData.Indices;
            RUsdSubmesh[] submeshes = meshData.Submeshes;
            int submeshCount = submeshes.Length;

            // Instanceable prims and PointInstancer instances arrive as separate entries with
            // identical buffers. Reusing the Unity Mesh built for the first of them keeps memory
            // and build time flat in the instance count. Materials live on the renderer, so
            // entries that differ only in their material bindings still share one Mesh.
            Mesh mesh = skin == null && meshCache != null ? meshCache.Find(meshData) : null;
            int[] blendShapeIndices = null;
            if (mesh == null)
            {
                mesh = CreateUnityMesh(meshData, options);
                if (skin != null)
                {
                    blendShapeIndices = ApplySkin(mesh, skin, skeletonData);
                    meshData.UnityBlendShapeIndices = blendShapeIndices;
                }
                else
                {
                    meshCache?.Add(meshData, mesh);
                }

                result.UniqueMeshCount++;
            }

            GameObject meshObject = CreatePrimPathObject(root, meshData.PrimPath);
            meshObject.SetActive(meshData.Visible);
            Renderer renderer;
            bool dualQuaternion = skin != null && skin.DualQuaternion && skeleton != null && skin.Influences > 0;
            if (dualQuaternion && !options.DualQuaternionSkinning)
            {
                Debug.LogWarning($"USD import: mesh '{meshData.PrimPath}' asks for dual-quaternion skinning; with UsdImportOptions.DualQuaternionSkinning off it is skinned linearly.");
                dualQuaternion = false;
            }

            if (dualQuaternion)
            {
                // Dual quaternions are not something a SkinnedMeshRenderer does: the mesh keeps a
                // MeshFilter and MeshRenderer, and UsdDualQuaternionSkin poses it from the joints.
                meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                renderer = meshObject.AddComponent<MeshRenderer>();
                var weights = new float[mesh.blendShapeCount];
                for (int s = 0; s < blendShapeIndices.Length && s < skin.BlendShapeWeights.Length; s++)
                {
                    if (blendShapeIndices[s] >= 0)
                    {
                        weights[blendShapeIndices[s]] = skin.BlendShapeWeights[s] * 100f;
                    }
                }

                meshObject.AddComponent<UsdDualQuaternionSkin>().Initialize(
                    skeleton.JointArray, skeleton.transform, UsdLocalToUnity(skin.GeomBindTransform), weights);
            }
            else if (skin != null)
            {
                SkinnedMeshRenderer skinned = meshObject.AddComponent<SkinnedMeshRenderer>();
                skinned.sharedMesh = mesh;
                if (skeleton != null && skin.Influences > 0)
                {
                    skinned.bones = skeleton.JointArray;
                    skinned.rootBone = skeleton.transform;
                    skinned.localBounds = SkinnedBounds(mesh, skin);
                }

                for (int s = 0; s < blendShapeIndices.Length && s < skin.BlendShapeWeights.Length; s++)
                {
                    if (blendShapeIndices[s] >= 0)
                    {
                        skinned.SetBlendShapeWeight(blendShapeIndices[s], skin.BlendShapeWeights[s] * 100f);
                    }
                }

                renderer = skinned;
            }
            else
            {
                MeshFilter filter = meshObject.GetComponent<MeshFilter>();
                if (filter == null)
                {
                    filter = meshObject.AddComponent<MeshFilter>();
                }

                filter.sharedMesh = mesh;

                MeshRenderer meshRenderer = meshObject.GetComponent<MeshRenderer>();
                if (meshRenderer == null)
                {
                    meshRenderer = meshObject.AddComponent<MeshRenderer>();
                }

                renderer = meshRenderer;
            }

            if (options.ImportMaterials)
            {
                if (submeshCount > 1)
                {
                    var submeshMaterials = new Material[submeshCount];
                    for (int s = 0; s < submeshCount; s++)
                    {
                        int mi = submeshes[s].MaterialIndex;
                        submeshMaterials[s] = mi >= 0 && mi < materials.Count
                            ? materials[mi]
                            : mi < 0 ? GetDefaultMaterial(options, result) : null;
                        if (meshData.DoubleSided && !BakesBackFaces(meshData))
                        {
                            submeshMaterials[s] = GetDoubleSidedMaterial(submeshMaterials[s], data);
                        }
                    }

                    renderer.sharedMaterials = submeshMaterials;
                }
                else
                {
                    int mi = submeshCount == 1 ? submeshes[0].MaterialIndex : meshData.MaterialIndex;
                    Material single = mi >= 0 && mi < materials.Count
                        ? materials[mi]
                        : mi < 0 ? GetDefaultMaterial(options, result) : null;
                    if (single != null)
                    {
                        renderer.sharedMaterial = meshData.DoubleSided && !BakesBackFaces(meshData) ? GetDoubleSidedMaterial(single, data) : single;
                    }
                }
            }

            // A MeshCollider would hold the bind pose while the skin moves, so skinned meshes get none.
            if (options.GenerateColliders && skin == null)
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

            return meshObject;
        }

        // Builds the Unity Mesh for one imported mesh entry: vertices, per-submesh triangles,
        // normals, every UV set and bounds. Holds nothing per prim, so the result can be shared by
        // every entry with the same geometry (see MeshShareCache).
        private static Mesh CreateUnityMesh(MeshData meshData, UsdImportOptions options)
        {
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
                    // Bounds are computed once after every buffer is in, below. Asking for
                    // them here as well recomputes them for each submesh in turn, over a vertex
                    // array that is not finished being described.
                    mesh.SetTriangles(submeshTriangles, s, false);
                }
            }
            else
            {
                mesh.SetTriangles(indices, 0, false);
            }

            if (options.SmoothNormalsLikeIsaac)
            {
                // Isaac Sim shades with its own smooth normals whatever the stage authors; see
                // UsdMeshNormals. The authored ones (already split per corner by the native
                // layer) are replaced, not merged, so a hard break an exporter wrote disappears
                // the way it does in Isaac Sim.
                mesh.SetNormals(UsdMeshNormals.ComputeCreaseSmoothed(ToUnityVectors(points), indices));
            }
            else if (normals.Length == points.Length)
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

            if (BakesBackFaces(meshData))
            {
                AddBackFaces(mesh);
            }

            // Normal maps need tangents, and USD carries none: without them URP Lit builds its
            // tangent frame from zero vectors and the normal map turns the surface into blotches.
            // Computed after the back faces so the mirrored copies get their own.
            if (mesh.vertexCount > 0 && mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal) &&
                mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
            {
                mesh.RecalculateTangents();
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        // A doubleSided mesh gets its back faces as geometry: Unity's lit shaders keep the front
        // normal on a back face even with Render Face Both, so a wall seen from behind shades as
        // if it faced away from the light (black), where Isaac Sim and Omniverse flip it. Skinned
        // and point-cached meshes keep their vertex count (weights and animated points index it)
        // and fall back to a Render Face Both material instead (GetDoubleSidedMaterial).
        private static bool BakesBackFaces(MeshData meshData) =>
            meshData.DoubleSided && meshData.Skin == null && meshData.PointCache == null;

        // Appends a mirrored copy of every vertex with its normal negated, and to every submesh
        // the same triangles wound the other way over the copies, so each side has its own,
        // correctly facing, normals. The materials stay one-sided.
        private static void AddBackFaces(Mesh mesh)
        {
            int count = mesh.vertexCount;
            if (count == 0)
            {
                return;
            }

            var vertices = new List<Vector3>(count * 2);
            mesh.GetVertices(vertices);
            var normals = new List<Vector3>(count * 2);
            mesh.GetNormals(normals);
            if (normals.Count != count)
            {
                mesh.RecalculateNormals();
                normals.Clear();
                mesh.GetNormals(normals);
            }

            var uvSets = new List<Vector2>[8];
            for (int channel = 0; channel < uvSets.Length; channel++)
            {
                var uv = new List<Vector2>(count * 2);
                mesh.GetUVs(channel, uv);
                uvSets[channel] = uv.Count == count ? uv : null;
            }

            int submeshCount = mesh.subMeshCount;
            var triangles = new int[submeshCount][];
            for (int s = 0; s < submeshCount; s++)
            {
                triangles[s] = mesh.GetTriangles(s);
            }

            for (int i = 0; i < count; i++)
            {
                vertices.Add(vertices[i]);
                normals.Add(-normals[i]);
            }

            foreach (List<Vector2> uv in uvSets)
            {
                if (uv != null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        uv.Add(uv[i]);
                    }
                }
            }

            string name = mesh.name;
            mesh.Clear();
            mesh.name = name;
            mesh.indexFormat = count * 2 > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            for (int channel = 0; channel < uvSets.Length; channel++)
            {
                if (uvSets[channel] != null)
                {
                    mesh.SetUVs(channel, uvSets[channel]);
                }
            }

            mesh.subMeshCount = submeshCount;
            for (int s = 0; s < submeshCount; s++)
            {
                int[] front = triangles[s];
                var both = new int[front.Length * 2];
                Array.Copy(front, both, front.Length);
                for (int i = 0; i + 2 < front.Length; i += 3)
                {
                    both[front.Length + i] = front[i] + count;
                    both[front.Length + i + 1] = front[i + 2] + count;
                    both[front.Length + i + 2] = front[i + 1] + count;
                }

                mesh.SetTriangles(both, s, false);
            }
        }

        // Adds UsdStageSource to the root and UsdPrimSource to every object below it, each with a
        // snapshot of the state just built. Runs after the whole hierarchy exists, so the
        // snapshot is what the user starts from. Object names are the prim names (the importer
        // creates one object per path segment), so the walk rebuilds each prim path from them.
        private static void RecordSource(StageData data, UsdImportOptions options, UsdImportResult result)
        {
            if (!options.RecordSource)
            {
                return;
            }

            GameObject root = result.RootObject;
            UsdStageSource stage = root.AddComponent<UsdStageSource>();
            stage.Initialize(data.InputPath, data.DefaultPrimPath, data.MetersPerUnit, data.UpAxis, options);
            stage.SetStageCorrection(result.StageCorrectionRotation, result.StageCorrectionScale);
            stage.SetLiveState(data.LayerFiles, ReferenceLightIntensity(data));
            if (data.KeptStage != IntPtr.Zero)
            {
                stage.AdoptStage(UsdStage.Adopt(data.KeptStage, data.InputPath));
                data.KeptStage = IntPtr.Zero;
            }

            var sets = new List<UsdVariantSetInfo>();
            var payloads = new List<UsdPayloadInfo>();
            var arcs = new List<UsdCompositionArcInfo>();
            UsdStageSource.ParseCompositionReport(data.CompositionReport, sets, payloads, arcs);
            stage.SetComposition(sets, payloads, arcs);
            result.Source = stage;

            var nodeFlags = new Dictionary<string, int>(data.Nodes.Count, StringComparer.Ordinal);
            foreach (NodeData node in data.Nodes)
            {
                nodeFlags[node.Path] = node.Flags;
            }

            var pending = new Stack<KeyValuePair<Transform, string>>();
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = root.transform.GetChild(i);
                if (child.GetComponent<UsdImportHelperObject>() != null)
                {
                    continue;
                }

                pending.Push(new KeyValuePair<Transform, string>(child, "/" + child.name));
            }

            while (pending.Count > 0)
            {
                KeyValuePair<Transform, string> entry = pending.Pop();
                Transform current = entry.Key;
                string primPath = entry.Value;
                bool xformable = nodeFlags.TryGetValue(primPath, out int flags);
                string instanceRoot = (flags & (int)UsdPrimCompositionFlags.InstanceProxy) != 0
                    ? FindInstanceRoot(primPath, nodeFlags)
                    : null;

                current.gameObject.AddComponent<UsdPrimSource>().Initialize(stage, primPath, flags, instanceRoot, xformable);
                stage.AddPrimPath(primPath);
                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    Transform child = current.GetChild(i);
                    if (child.GetComponent<UsdImportHelperObject>() != null)
                    {
                        continue;
                    }

                    pending.Push(new KeyValuePair<Transform, string>(child, primPath + "/" + child.name));
                }
            }
        }

        private static string FindInstanceRoot(string primPath, Dictionary<string, int> nodeFlags)
        {
            string current = primPath;
            while (true)
            {
                int slash = current.LastIndexOf('/');
                if (slash <= 0)
                {
                    return null;
                }

                current = current.Substring(0, slash);
                if (nodeFlags.TryGetValue(current, out int flags) && (flags & (int)UsdPrimCompositionFlags.Instance) != 0)
                {
                    return current;
                }
            }
        }

        private static void ApplyLocalTransform(Transform target, RUsdMatrix4x4 localMatrix)
        {
            ApplyLocalTransform(target, UsdLocalToUnity(localMatrix));
        }

        private static void ApplyLocalTransform(Transform target, Matrix4x4 unity)
        {
            Decompose(unity, out Vector3 position, out Quaternion rotation, out Vector3 scale);
            target.localPosition = position;
            target.localRotation = rotation;
            target.localScale = scale;
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

        // ---- Animation (native API 13) ----

        // The stage's animation as a UsdAnimationData on a UsdAnimationPlayer at the root. Samples
        // are converted to Unity's basis and decomposed here, once, so the player only
        // interpolates; consecutive rotations are kept on the same hemisphere so interpolating
        // between them takes the short way, as USD's does. The player is evaluated at the read
        // time, so everything starts where the static values put it (unless they were read at
        // Default, which no sample corresponds to).
        private static UsdAnimationPlayer BuildAnimation(
            StageData data, Transform root, List<UsdSkeleton> skeletons, UsdImportOptions options)
        {
            if (!options.ImportAnimation || !data.HasTimeInfo)
            {
                return null;
            }

            var animation = ScriptableObject.CreateInstance<UsdAnimationData>();
            animation.name = root.name + " Animation";
            double first = double.MaxValue;
            double last = double.MinValue;
            void Span(double[] times)
            {
                if (times.Length > 0)
                {
                    first = Math.Min(first, times[0]);
                    last = Math.Max(last, times[times.Length - 1]);
                }
            }

            foreach (UsdNativeNodeTrack track in data.NodeTracks)
            {
                if (track.Info.NodeIndex < 0 || track.Info.NodeIndex >= data.Nodes.Count || track.Times.Length == 0 ||
                    data.JointMirrorPrims.Contains(data.Nodes[track.Info.NodeIndex].Path))
                {
                    continue;
                }

                Transform target = CreatePrimPathObject(root, data.Nodes[track.Info.NodeIndex].Path).transform;
                string path = RelativePath(root, target);
                int samples = track.Times.Length;
                if (track.Info.Kind == 1 && track.Values.Length == samples * 16)
                {
                    var transformTrack = new UsdAnimationData.TransformTrack
                    {
                        Path = path,
                        Times = track.Times,
                        Positions = new Vector3[samples],
                        Rotations = new Quaternion[samples],
                        Scales = new Vector3[samples]
                    };

                    for (int i = 0; i < samples; i++)
                    {
                        Decompose(ToUnityMatrix(track.Values, i * 16), out transformTrack.Positions[i],
                                  out transformTrack.Rotations[i], out transformTrack.Scales[i]);
                        if (i > 0)
                        {
                            transformTrack.Rotations[i] = SameHemisphere(transformTrack.Rotations[i - 1], transformTrack.Rotations[i]);
                        }
                    }

                    animation.Add(transformTrack);
                    Span(track.Times);
                }
                else if (track.Info.Kind == 2 && track.Values.Length == samples)
                {
                    var visibilityTrack = new UsdAnimationData.VisibilityTrack
                    {
                        Path = path,
                        Times = track.Times,
                        Visible = new bool[samples]
                    };

                    for (int i = 0; i < samples; i++)
                    {
                        visibilityTrack.Visible[i] = track.Values[i] != 0f;
                    }

                    animation.Add(visibilityTrack);
                    Span(track.Times);
                }
            }

            for (int s = 0; s < data.Skeletons.Count && s < skeletons.Count; s++)
            {
                UsdNativeSkeleton skeleton = data.Skeletons[s];
                int samples = skeleton.Times.Length;
                int joints = skeleton.JointPaths.Length;
                if (skeletons[s] == null || samples == 0 || skeleton.Samples.Length != samples * joints * 16)
                {
                    continue;
                }

                var skeletonTrack = new UsdAnimationData.SkeletonTrack
                {
                    Path = RelativePath(root, skeletons[s].transform),
                    JointCount = joints,
                    Times = skeleton.Times,
                    Positions = new Vector3[samples * joints],
                    Rotations = new Quaternion[samples * joints],
                    Scales = new Vector3[samples * joints]
                };

                for (int i = 0; i < samples * joints; i++)
                {
                    Decompose(ToUnityMatrix(skeleton.Samples, i * 16), out skeletonTrack.Positions[i],
                              out skeletonTrack.Rotations[i], out skeletonTrack.Scales[i]);
                    if (i >= joints)
                    {
                        skeletonTrack.Rotations[i] = SameHemisphere(skeletonTrack.Rotations[i - joints], skeletonTrack.Rotations[i]);
                    }
                }

                animation.Add(skeletonTrack);
                Span(skeleton.Times);
            }

            foreach (MeshData mesh in data.Meshes)
            {
                UsdNativeSkin skin = mesh.Skin;
                if (skin == null || mesh.UnityBlendShapeIndices == null || skin.BlendShapeWeightTimes.Length == 0)
                {
                    continue;
                }

                Transform target = CreatePrimPathObject(root, mesh.PrimPath).transform;
                if (target.GetComponent<SkinnedMeshRenderer>() == null && target.GetComponent<UsdDualQuaternionSkin>() == null)
                {
                    continue;
                }

                var weights = new float[skin.BlendShapeWeightSamples.Length];
                for (int i = 0; i < weights.Length; i++)
                {
                    weights[i] = skin.BlendShapeWeightSamples[i] * 100f;
                }

                animation.Add(new UsdAnimationData.BlendShapeTrack
                {
                    Path = RelativePath(root, target),
                    ShapeIndices = mesh.UnityBlendShapeIndices,
                    Times = skin.BlendShapeWeightTimes,
                    Weights = weights
                });
                Span(skin.BlendShapeWeightTimes);
            }

            foreach (MeshData mesh in data.Meshes)
            {
                UsdNativePointCache cache = mesh.PointCache;
                int vertices = mesh.Points.Length;
                if (cache == null || cache.Times.Length == 0 || vertices == 0)
                {
                    continue;
                }

                Transform target = CreatePrimPathObject(root, mesh.PrimPath).transform;
                if (target.GetComponent<MeshRenderer>() == null || target.GetComponent<UsdDualQuaternionSkin>() != null)
                {
                    continue;
                }

                var positions = new Vector3[cache.Points.Length];
                var bounds = new Bounds(new Vector3(cache.Points[0].X, cache.Points[0].Y, cache.Points[0].Z), Vector3.zero);
                for (int i = 0; i < positions.Length; i++)
                {
                    positions[i] = new Vector3(cache.Points[i].X, cache.Points[i].Y, cache.Points[i].Z);
                    bounds.Encapsulate(positions[i]);
                }

                Vector3[] normals = Array.Empty<Vector3>();
                if (cache.Normals != null)
                {
                    normals = new Vector3[cache.Normals.Length];
                    for (int i = 0; i < normals.Length; i++)
                    {
                        normals[i] = new Vector3(cache.Normals[i].X, cache.Normals[i].Y, cache.Normals[i].Z);
                    }
                }

                animation.Add(new UsdAnimationData.PointCacheTrack
                {
                    Path = RelativePath(root, target),
                    VertexCount = vertices,
                    Times = cache.Times,
                    Positions = positions,
                    Normals = normals,
                    Bounds = bounds
                });
                Span(cache.Times);
            }

            if (animation.IsEmpty)
            {
                DestroyCreated(animation);
                return null;
            }

            RUsdImportTimeInfo time = data.TimeInfo;
            bool range = time.HasTimeRange != 0;
            animation.SetTimeRange(range ? time.StartTimeCode : first, range ? time.EndTimeCode : last, time.TimeCodesPerSecond);

            UsdAnimationPlayer player = root.gameObject.AddComponent<UsdAnimationPlayer>();
            player.Data = animation;
            if (time.ReadTimeKind != 0)
            {
                player.Evaluate(0.0);
                // The imported meshes already are the read-time frame; a point cache only needs its
                // vertex stream once playback moves it on.
                player.RestoreMeshes();
            }

            return player;
        }

        // Position, rotation and scale of a local matrix. Matrix4x4.rotation reads the rotation
        // straight from the 3x3 part, which is only right while the scale is uniform: a box scaled
        // (1, 2, 0.5) and turned (10, 30, 0) came back turned (10.9, 19.6, 357.2), 0.12 m off at its
        // corners. The axes are normalized first; a mirroring matrix puts its sign on X. A matrix
        // with shear has no exact TRS: its Z axis is kept and Y is made perpendicular to it.
        internal static void Decompose(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = new Vector3(matrix.m03, matrix.m13, matrix.m23);
            Vector3 x = matrix.GetColumn(0);
            Vector3 y = matrix.GetColumn(1);
            Vector3 z = matrix.GetColumn(2);
            float sy = y.magnitude;
            float sz = z.magnitude;
            if (sy < 1e-12f || sz < 1e-12f)
            {
                // A collapsed axis has no direction to read; Unity's own answer is as good as any.
                rotation = matrix.rotation;
                scale = matrix.lossyScale;
                return;
            }

            rotation = Quaternion.LookRotation(z / sz, y / sy);
            float sx = x.magnitude;
            scale = new Vector3(Vector3.Dot(Vector3.Cross(y, z), x) < 0f ? -sx : sx, sy, sz);
        }

        // q and -q are the same rotation; interpolating towards the one nearer the previous sample
        // goes the short way.
        private static Quaternion SameHemisphere(Quaternion previous, Quaternion current) =>
            Quaternion.Dot(previous, current) < 0f
                ? new Quaternion(-current.x, -current.y, -current.z, -current.w)
                : current;

        // `target`'s path below `root`, as Transform.Find takes it.
        private static string RelativePath(Transform root, Transform target)
        {
            var names = new List<string>();
            for (Transform current = target; current != null && current != root; current = current.parent)
            {
                names.Add(current.name);
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static void DestroyCreated(UnityEngine.Object created)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(created);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(created);
            }
        }

        // ---- Skinning (UsdSkel; native API 13) ----

        // Prims below a Skeleton prim with nothing to draw under them. Omniverse writes the joint
        // hierarchy there a second time as SkelJoint prims (Isaac Sim's characters do), with the
        // joints' own names, so their objects would sit beside the joints BuildSkeletons makes:
        // two siblings called "Root", and an AnimationClip, which finds objects by name, animates
        // whichever comes first. The joints stand for them, so they are left out.
        private static HashSet<string> FindJointMirrorPrims(StageData data, UsdImportOptions options)
        {
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            if (!options.ImportSkinning || data.Skeletons.Count == 0)
            {
                return skipped;
            }

            var skeletons = new HashSet<string>(StringComparer.Ordinal);
            foreach (UsdNativeSkeleton skeleton in data.Skeletons)
            {
                if (skeleton.NodeIndex >= 0 && skeleton.NodeIndex < data.Nodes.Count)
                {
                    skeletons.Add(data.Nodes[skeleton.NodeIndex].Path);
                }
            }

            // A mesh below a Skeleton prim keeps its ancestors.
            var holdsContent = new HashSet<string>(StringComparer.Ordinal);
            foreach (MeshData mesh in data.Meshes)
            {
                for (string path = mesh.PrimPath; !string.IsNullOrEmpty(path) && holdsContent.Add(path);
                     path = path.Substring(0, Math.Max(0, path.LastIndexOf('/'))))
                {
                }
            }

            foreach (NodeData node in data.Nodes)
            {
                if (holdsContent.Contains(node.Path))
                {
                    continue;
                }

                for (int slash = node.Path.LastIndexOf('/'); slash > 0; slash = node.Path.LastIndexOf('/', slash - 1))
                {
                    if (skeletons.Contains(node.Path.Substring(0, slash)))
                    {
                        skipped.Add(node.Path);
                        break;
                    }
                }
            }

            return skipped;
        }

        // Joint hierarchies for the skeletons skinned meshes are bound to. Joints become children
        // of the Skeleton prim's object in joint order (UsdSkel orders every parent before its
        // children), posed as the skeleton is at the read time. They are not prims, so each root
        // joint is marked as an import helper, which RecordSource and both exporters skip with its
        // subtree. The list is indexed like the native skeleton table; null where nothing was built.
        private static List<UsdSkeleton> BuildSkeletons(StageData data, Transform root, UsdImportOptions options)
        {
            var built = new List<UsdSkeleton>(data.Skeletons.Count);
            foreach (UsdNativeSkeleton skeleton in data.Skeletons)
            {
                if (!options.ImportSkinning || skeleton.NodeIndex < 0 || skeleton.NodeIndex >= data.Nodes.Count)
                {
                    built.Add(null);
                    continue;
                }

                Transform node = CreatePrimPathObject(root, data.Nodes[skeleton.NodeIndex].Path).transform;
                var joints = new Transform[skeleton.JointPaths.Length];
                for (int j = 0; j < joints.Length; j++)
                {
                    int parent = j < skeleton.Parents.Length ? skeleton.Parents[j] : -1;
                    Transform parentTransform = parent >= 0 && parent < j ? joints[parent] : node;
                    string path = skeleton.JointPaths[j];
                    string name = SanitizeObjectName(path.Substring(path.LastIndexOf('/') + 1));
                    if (parentTransform == node)
                    {
                        // Root joints share the Skeleton prim's object with its child prims; a
                        // clip's paths need the names to differ (see FindJointMirrorPrims).
                        string baseName = name;
                        for (int suffix = 1; node.Find(name) != null; suffix++)
                        {
                            name = baseName + "_" + suffix;
                        }
                    }

                    var joint = new GameObject(name);
                    joint.transform.SetParent(parentTransform, false);
                    ApplyLocalTransform(joint.transform, ToUnityMatrix(skeleton.PoseTransforms, j * 16));
                    if (parentTransform == node)
                    {
                        joint.AddComponent<UsdImportHelperObject>();
                    }

                    joints[j] = joint.transform;
                }

                UsdSkeleton component = node.GetComponent<UsdSkeleton>();
                if (component == null)
                {
                    component = node.gameObject.AddComponent<UsdSkeleton>();
                }

                var binds = new Matrix4x4[joints.Length];
                for (int j = 0; j < binds.Length && (j + 1) * 16 <= skeleton.BindTransforms.Length; j++)
                {
                    binds[j] = ToUnityMatrix(skeleton.BindTransforms, j * 16);
                }

                component.Initialize(skeleton.JointPaths, joints, binds);
                built.Add(component);
            }

            return built;
        }

        // Bone weights, bind poses and blend shapes. Unity skins a vertex with
        // bones[i].localToWorldMatrix * bindposes[i]. The bones are the joints, posed in the
        // Skeleton prim's space, and UsdSkel's linear blend skinning takes a mesh point through
        // geomBindTransform into skeleton space and then out of joint i's bind transform, so
        // bindposes[i] = inverse(bind_i) * geomBindTransform -- both converted to Unity's basis,
        // which keeps products in order. Returns, for each USD blend shape, the Unity blend-shape
        // index it became, or -1 when it had nothing to add.
        private static int[] ApplySkin(Mesh mesh, UsdNativeSkin skin, UsdNativeSkeleton skeleton)
        {
            int vertexCount = mesh.vertexCount;
            int influences = skin.Influences;
            if (influences > 0 && skeleton != null && skin.JointIndices.Length == vertexCount * influences)
            {
                var bonesPerVertex = new NativeArray<byte>(vertexCount, Allocator.Temp);
                var weights = new List<BoneWeight1>(vertexCount * Math.Min(influences, 4));
                var pairs = new List<BoneWeight1>(influences);
                for (int v = 0; v < vertexCount; v++)
                {
                    pairs.Clear();
                    for (int i = 0; i < influences; i++)
                    {
                        float weight = skin.JointWeights[v * influences + i];
                        if (weight > 0f)
                        {
                            pairs.Add(new BoneWeight1 { boneIndex = skin.JointIndices[v * influences + i], weight = weight });
                        }
                    }

                    // Unity wants each vertex's weights heaviest first.
                    pairs.Sort((a, b) => b.weight.CompareTo(a.weight));
                    bonesPerVertex[v] = (byte)pairs.Count;
                    weights.AddRange(pairs);
                }

                var weightArray = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp);
                mesh.SetBoneWeights(bonesPerVertex, weightArray);
                weightArray.Dispose();
                bonesPerVertex.Dispose();

                Matrix4x4 geomBind = UsdLocalToUnity(skin.GeomBindTransform);
                var bindposes = new Matrix4x4[skeleton.JointPaths.Length];
                for (int j = 0; j < bindposes.Length; j++)
                {
                    bindposes[j] = ToUnityMatrix(skeleton.BindTransforms, j * 16).inverse * geomBind;
                }

                mesh.bindposes = bindposes;
            }

            var unityIndices = new int[skin.BlendShapes.Length];
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int s = 0; s < skin.BlendShapes.Length; s++)
            {
                UsdNativeBlendShape shape = skin.BlendShapes[s];
                unityIndices[s] = -1;
                if (shape.Weights.Length == 0)
                {
                    continue;
                }

                // Unity needs blend-shape names unique within a mesh; USD only within the binding.
                string name = string.IsNullOrEmpty(shape.Name) ? "BlendShape" : shape.Name;
                for (int suffix = 1; !names.Add(name); suffix++)
                {
                    name = (string.IsNullOrEmpty(shape.Name) ? "BlendShape" : shape.Name) + "_" + suffix;
                }

                for (int f = 0; f < shape.Weights.Length; f++)
                {
                    Vector3[] deltaNormals = shape.DeltaNormals != null ? ToUnityVectors(shape.DeltaNormals[f]).ToArray() : null;
                    mesh.AddBlendShapeFrame(name, shape.Weights[f] * 100f, ToUnityVectors(shape.DeltaVertices[f]).ToArray(),
                                            deltaNormals, null);
                }

                unityIndices[s] = mesh.blendShapeCount - 1;
            }

            return unityIndices;
        }

        // SkinnedMeshRenderer.localBounds is in the root bone's space, here the Skeleton prim's
        // object; the mesh's own bounds are in mesh space, which geomBindTransform leaves.
        private static Bounds SkinnedBounds(Mesh mesh, UsdNativeSkin skin)
        {
            Matrix4x4 geomBind = UsdLocalToUnity(skin.GeomBindTransform);
            Vector3[] vertices = mesh.vertices;
            if (vertices.Length == 0)
            {
                return new Bounds();
            }

            var bounds = new Bounds(geomBind.MultiplyPoint3x4(vertices[0]), Vector3.zero);
            foreach (Vector3 vertex in vertices)
            {
                bounds.Encapsulate(geomBind.MultiplyPoint3x4(vertex));
            }

            return bounds;
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
                data.NormalIsSrgb = options.NormalMapColorSpaceLikeIsaac &&
                                    NormalMapReadsAsSrgb(context, source.NormalTexturePath);
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
            // Distinct files first, then the reads in parallel: a page-cached file still costs
            // a copy, and a cold one costs the disk, and neither waits on the other.
            var distinct = new List<string>();
            var seen = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                foreach (string path in new[] { material.AlbedoPath, material.NormalPath, material.MetallicGlossPath,
                                                material.EmissionPath, material.OcclusionPath, material.RoughnessPath,
                                                material.OpacityPath, material.MaskPath })
                {
                    if (!string.IsNullOrEmpty(path) && !data.UdimSets.ContainsKey(path) &&
                        !data.PackageAssets.ContainsKey(path) && seen.Add(path))
                    {
                        distinct.Add(path);
                    }
                }
            }

            var bytes = new byte[distinct.Count][];
            var errors = new string[distinct.Count];
            System.Threading.Tasks.Parallel.For(0, distinct.Count, index =>
            {
                string path = distinct[index];
                if (!IsAssetPathAllowed(data, path, out string readPath))
                {
                    errors[index] = "USD import: refused to read a texture outside the stage folder.";
                    return;
                }

                try
                {
                    long length = new FileInfo(readPath).Length;
                    if (length > UsdImageLimits.MaxFileBytes)
                    {
                        errors[index] = $"USD import: refused texture {path}: {length} bytes exceeds the {UsdImageLimits.MaxFileBytes}-byte limit.";
                        return;
                    }

                    bytes[index] = File.ReadAllBytes(readPath);
                }
                catch (Exception exception)
                {
                    errors[index] = $"USD import: error reading texture {path}: {exception.Message}";
                }
            });

            for (int index = 0; index < distinct.Count; index++)
            {
                data.TextureBytes[distinct[index]] = bytes[index];
                if (errors[index] != null)
                {
                    Debug.LogWarning(errors[index]);
                }
            }

            // Packaged (.usdz) textures still go through the resolver one at a time below.
            foreach (MaterialData material in data.Materials)
            {
                CacheTextureBytes(context, data, material.AlbedoPath);
                CacheTextureBytes(context, data, material.NormalPath);
                CacheTextureBytes(context, data, material.MetallicGlossPath);
                CacheTextureBytes(context, data, material.EmissionPath);
                // Neither of these was read before: an ao_texture or a roughness map of its own
                // reached the uploader with no bytes behind it and silently became no texture.
                CacheTextureBytes(context, data, material.OcclusionPath);
                CacheTextureBytes(context, data, material.RoughnessPath);
                CacheTextureBytes(context, data, material.OpacityPath);
            }
        }

        // The packaged (.usdz) texture bytes only. They are read through the stage's resolver, so
        // they must be read before the stage is closed; a streamed import reads the files later,
        // and ReadUniqueTextureBytes then skips what is already cached here.
        private static void CachePackagedTextureBytes(IntPtr context, StageData data)
        {
            foreach (MaterialData material in data.Materials)
            {
                foreach (string path in new[] { material.AlbedoPath, material.NormalPath, material.MetallicGlossPath,
                                                material.EmissionPath, material.OcclusionPath, material.RoughnessPath,
                                                material.OpacityPath, material.MaskPath })
                {
                    if (!string.IsNullOrEmpty(path) && data.PackageAssets.ContainsKey(path))
                    {
                        CacheTextureBytes(context, data, path);
                    }
                }
            }
        }

        private static void CacheTextureBytes(IntPtr context, StageData data, string path)
        {
            if (string.IsNullOrEmpty(path) || data.TextureBytes.ContainsKey(path))
            {
                return;
            }

            // A UDIM set: its tiles are read one at a time while the atlas is built, not here.
            // Reading them all up front held ALab's 8 GB of tiles in memory at once.
            if (data.UdimSets.ContainsKey(path))
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
        // Decoding is where an import with textures spends most of its time: on a stage with 150
        // of them it was 36 seconds of a 48-second import, three quarters of the whole, while the
        // stage composition and all its geometry came to twelve. The work was already off the main
        // thread, but on one core of it -- and each image is independent of every other, produces
        // nothing but a byte array, and touches no Unity API, which is as clean a case for running
        // them at once as there is.
        //
        // The dictionaries are written after the decoding rather than during it. Writing them from
        // several threads would need a concurrent collection for no benefit; the results land in
        // arrays indexed by position and are folded back in order, which also keeps any warning in
        // the order the stage lists its textures rather than in whatever order the threads finish.
        // Moves an OmniPBR ORM map into the channels Unity reads, in place.
        //
        // OmniPBR packs occlusion, roughness and metallic into red, green and blue of one linear
        // map. URP's Lit shader reads metallic from red and smoothness from alpha of its metallic
        // map, and occlusion from green of its occlusion map -- which do not overlap, so one
        // rewritten image serves both slots and is decoded, compressed and uploaded once.
        //
        // Done here because here is where the pixels already are, on a worker thread. Doing it at
        // upload time would put it back on the main thread, which is the cost this import spent
        // the morning moving off.
        private static void RepackOrm(byte[] rgba)
        {
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                byte occlusion = rgba[i];
                byte roughness = rgba[i + 1];
                byte metallic = rgba[i + 2];
                rgba[i] = metallic;                      // red: metallic
                rgba[i + 1] = occlusion;                 // green: occlusion
                rgba[i + 2] = 0;                         // blue: unused
                rgba[i + 3] = (byte)(255 - roughness);   // alpha: smoothness
            }
        }

        // Records how a texture is read, and notices when one image is read both ways.
        // Negates a normal map's tangent X (red) and/or Y (green) in place, the flip OmniPBR's
        // flip_tangent_u / flip_tangent_v apply. A component is 2c - 1 of the value the shader
        // samples, so negating it maps c to 1 - c in that space: the byte itself for linear data,
        // and through linear for a map that decodes as sRGB.
        private static void FlipNormalChannels(byte[] rgba, bool flipRed, bool flipGreen, bool srgb)
        {
            if (rgba == null || (!flipRed && !flipGreen))
            {
                return;
            }

            var map = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                if (srgb)
                {
                    float linear = Mathf.GammaToLinearSpace(i / 255f);
                    map[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(1f - linear) * 255f), 0, 255);
                }
                else
                {
                    map[i] = (byte)(255 - i);
                }
            }

            for (int at = 0; at + 3 < rgba.Length; at += 4)
            {
                if (flipRed)
                {
                    rgba[at] = map[rgba[at]];
                }

                if (flipGreen)
                {
                    rgba[at + 1] = map[rgba[at + 1]];
                }
            }
        }

        // Whether a normal map path the native layer reported reads as sRGB: an authored "sRGB",
        // or no authored colour space ("auto") on an 8-bit image format (PNG is taken as 8-bit;
        // a 16-bit one, which "auto" would read as linear, is rare for normal maps). EXR and HDR
        // under "auto" are linear, as is anything authored "raw".
        private static bool NormalMapReadsAsSrgb(IntPtr context, string reportedPath)
        {
            if (string.IsNullOrEmpty(reportedPath))
            {
                return false;
            }

            string space = UsdNative.GetImportTextureColorSpace(context, reportedPath);
            if (space != null)
            {
                return string.Equals(space, "sRGB", StringComparison.OrdinalIgnoreCase);
            }

            if (!UsdNative.SupportsTextureColorSpace)
            {
                return false;
            }

            string extension = System.IO.Path.GetExtension(reportedPath).ToLowerInvariant();
            return extension == ".jpg" || extension == ".jpeg" || extension == ".tga" ||
                   extension == ".bmp" || extension == ".png";
        }

        private static void NoteColourSpace(Dictionary<string, bool> srgbUse, HashSet<string> mixedUse,
                                            string path, bool srgb)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (srgbUse.TryGetValue(path, out bool existing))
            {
                if (existing != srgb)
                {
                    mixedUse.Add(path);
                }

                return;
            }

            srgbUse[path] = srgb;
        }

        private static void DecodeUniqueTextures(StageData data, UsdImportOptions options)
        {
            var paths = new List<string>(data.TextureBytes.Keys);

            // Largest first. The work is wildly uneven -- one 8K rug took 3.5 seconds of a 4.7
            // second pass, with the other twenty-three workers finishing early and waiting on it
            // -- and starting the long ones first is what keeps the tail from becoming the whole
            // duration. Measured efficiency was 12 of 24 workers before this.
            var order = new int[paths.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int sizeA = data.TextureBytes[paths[a]]?.Length ?? 0;
                int sizeB = data.TextureBytes[paths[b]]?.Length ?? 0;
                return sizeB.CompareTo(sizeA);
            });

            // Which colour space each image is read in, decided from the slots the materials bind
            // it to: base colour and emission are sRGB, normal and metallic-roughness are not.
            // Mip levels have to be averaged in linear space, so compressing an image before this
            // is known would get its mip chain wrong -- and an image bound to slots of both kinds
            // has no single answer, so it is left for the uploader, which does know.
            var srgbUse = new Dictionary<string, bool>();
            // Normal maps whose channels are flipped once decoded (OmniPBR flip_tangent_u/v), by
            // path: (flip red, flip green, the map decodes as sRGB). They skip the native one-call
            // build, which would compress before the flip.
            var normalFlips = new Dictionary<string, (bool u, bool v, bool srgb)>();
            foreach (MaterialData material in data.Materials)
            {
                if (!string.IsNullOrEmpty(material.NormalPath) && (material.NormalFlipU || material.NormalFlipV))
                {
                    normalFlips[material.NormalPath] = (material.NormalFlipU, material.NormalFlipV, material.NormalIsSrgb);
                }
            }
            var mixedUse = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                NoteColourSpace(srgbUse, mixedUse, material.AlbedoPath, true);
                NoteColourSpace(srgbUse, mixedUse, material.EmissionPath, true);
                NoteColourSpace(srgbUse, mixedUse, material.NormalPath, material.NormalIsSrgb);
                NoteColourSpace(srgbUse, mixedUse, material.MetallicGlossPath, false);
                NoteColourSpace(srgbUse, mixedUse, material.OcclusionPath, false);
                NoteColourSpace(srgbUse, mixedUse, material.RoughnessPath, false);
            }

            // Images that feed a packed map rather than a slot of their own: a plain metallic map,
            // a roughness map in a file of its own, or an opacity map that is not the albedo's
            // alpha. Their pixels are needed after decoding, so they are not compressed on the
            // decode pass; the packed result is compressed instead, and a source also read
            // directly by some other slot is compressed once the packing is done. See
            // PackMetallicRoughness and PackAlbedoOpacity.
            var packSources = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                if (!string.IsNullOrEmpty(material.RoughnessPath))
                {
                    packSources.Add(material.RoughnessPath);
                }

                if (material.MetallicIsPlain && !string.IsNullOrEmpty(material.MetallicGlossPath))
                {
                    packSources.Add(material.MetallicGlossPath);
                }

                if (HasMaskBlend(material))
                {
                    packSources.Add(material.MaskPath);
                    packSources.Add(material.AlbedoPath);
                }

                if (material.HasRoughnessRange)
                {
                    packSources.Add(material.MetallicGlossPath);
                }

                if (HasSeparateOpacity(material))
                {
                    packSources.Add(material.OpacityPath);
                    if (!string.IsNullOrEmpty(material.AlbedoPath))
                    {
                        packSources.Add(material.AlbedoPath);
                    }
                }
            }

            // Of those, the ones whose only use is a single channel of a packed map: kept as
            // red alone while they wait, see DecodedImage.Gray.
            var colourUse = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                foreach (string path in new[] { material.AlbedoPath, material.NormalPath, material.EmissionPath,
                                                material.OcclusionPath,
                                                material.MetallicIsPlain ? null : material.MetallicGlossPath })
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        colourUse.Add(path);
                    }
                }
            }

            foreach (MaterialData material in data.Materials)
            {
                if (HasMaskBlend(material) && (material.MaskMode == 1 || material.MaskMode == 2))
                {
                    colourUse.Add(material.MaskPath);
                }
            }

            var graySources = new HashSet<string>(packSources);
            graySources.ExceptWith(colourUse);

            // Which images are ORM maps, and which are read some other way as well. Repacking
            // rewrites the decoded pixels in place, which is free on the thread that just decoded
            // them -- but the decode is shared by path, so an image read both as ORM and as
            // something else cannot be rewritten without corrupting the other reader. Those are
            // left alone and keep the old behaviour.
            var ormUse = new HashSet<string>();
            var nonOrmUse = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                if (!string.IsNullOrEmpty(material.MetallicGlossPath))
                {
                    (material.MetallicIsOrm ? ormUse : nonOrmUse).Add(material.MetallicGlossPath);
                }

                foreach (string other in new[] { material.AlbedoPath, material.NormalPath,
                                                 material.EmissionPath, material.OpacityPath,
                                                 material.OcclusionPath })
                {
                    if (!string.IsNullOrEmpty(other))
                    {
                        nonOrmUse.Add(other);
                    }
                }
            }

            ormUse.ExceptWith(nonOrmUse);
            var decodedImages = new DecodedImage[paths.Count];
            var refusals = new string[paths.Count];
            var consumed = new bool[paths.Count];

            System.Threading.Tasks.Parallel.For(0, paths.Count, position =>
            {
                int index = order[position];
                byte[] bytes = data.TextureBytes[paths[index]];
                if (bytes == null || bytes.Length == 0)
                {
                    return;
                }

                bool readAsSrgb = srgbUse.TryGetValue(paths[index], out bool isSrgb) && isSrgb &&
                                  !mixedUse.Contains(paths[index]);

                // Native first (API 12): decode, ORM repack and block compression in one call,
                // and a compressed image never becomes a managed array at all.
                if (UsdNative.SupportsImageBuild && !normalFlips.ContainsKey(paths[index]))
                {
                    bool wantsBlocks = options.CompressTextures && !mixedUse.Contains(paths[index]) &&
                                       !packSources.Contains(paths[index]);
                    int mode = graySources.Contains(paths[index]) ? 2 : (wantsBlocks ? 1 : 0);
                    UsdNative.NativeImage built = UsdNative.BuildImage(bytes, readAsSrgb, mode,
                                                                       ormUse.Contains(paths[index]), out string _);
                    if (built != null)
                    {
                        decodedImages[index] = FromNative(built, ormUse.Contains(paths[index]));
                        consumed[index] = true;
                        return;
                    }
                    // Not a PNG or EXR this reads (a JPEG, a 16-bit PNG): the managed path below.
                }

                int w, h;
                byte[] rgba;
                bool decoded = UsdPngDecoder.TryDecode(bytes, out w, out h, out rgba);
                if (!decoded && UsdExrDecoder.IsExr(bytes))
                {
                    // OpenEXR holds linear floating-point pixels; they are quantised to 8 bits here,
                    // encoded to sRGB when the slot reads them that way, so the rest of the path
                    // (mips, block compression, upload) is the one every other image takes.
                    decoded = TryDecodeExr(bytes, readAsSrgb, out w, out h, out rgba, out string why);
                    if (!decoded)
                    {
                        consumed[index] = true;
                        refusals[index] = why;
                        return;
                    }
                }

                if (!decoded && !UsdExrDecoder.IsExr(bytes))
                {
                    // Targa: LoadImage reads PNG and JPEG only, so a .tga (Isaac Sim's pallet
                    // props ship their albedo and normal maps that way) was refused outright.
                    decoded = UsdTgaDecoder.TryDecode(bytes, out w, out h, out rgba);
                }

                if (decoded)
                {
                    var image = new DecodedImage { Width = w, Height = h, Rgba = rgba };
                    if (ormUse.Contains(paths[index]))
                    {
                        RepackOrm(rgba);
                        image.Transformed = true;
                    }

                    if (normalFlips.TryGetValue(paths[index], out var flip))
                    {
                        FlipNormalChannels(rgba, flip.u, flip.v, flip.srgb);
                        image.Transformed = true;
                    }

                    if (graySources.Contains(paths[index]))
                    {
                        KeepOnlyRed(image);
                    }
                    // Compressing here rather than at upload time is the point: Texture2D.Compress
                    // runs on the main thread and was 22 seconds of a 38-second import, while this
                    // runs on the worker that already holds the pixels.
                    else if (options.CompressTextures && !mixedUse.Contains(paths[index]) &&
                             !packSources.Contains(paths[index]))
                    {
                        CompressDecoded(image, readAsSrgb);
                    }

                    decodedImages[index] = image;
                    consumed[index] = true; // decoded form supersedes the raw bytes
                }
                else if (!UsdImageLimits.PermitsLoadImage(bytes, out string reason))
                {
                    consumed[index] = true; // never reaches LoadImage, and frees the bytes now
                    refusals[index] = reason;
                }
            });

            for (int index = 0; index < paths.Count; index++)
            {
                string path = paths[index];
                if (consumed[index])
                {
                    data.TextureBytes[path] = null;
                }

                if (refusals[index] != null)
                {
                    Debug.LogWarning($"USD import: refused texture {path}: {refusals[index]}.");
                }

                data.DecodedTextures[path] = decodedImages[index];
            }

            DecodeUdimSets(data, options, srgbUse, mixedUse, packSources, graySources, ormUse);

            // JPEG normal maps whose channels OmniPBR flips (flip_tangent_v): like a packing source,
            // they have no pixels here to flip, and used to be uploaded unflipped.
            var deferredFlips = new List<string>();
            foreach (KeyValuePair<string, (bool u, bool v, bool srgb)> entry in normalFlips)
            {
                if ((!data.DecodedTextures.TryGetValue(entry.Key, out DecodedImage image) || !HasPixels(image)) &&
                    data.TextureBytes.TryGetValue(entry.Key, out byte[] bytes) && bytes != null && bytes.Length > 0)
                {
                    deferredFlips.Add(entry.Key);
                }
            }

            Action pack = () =>
            {
                foreach (string path in deferredFlips)
                {
                    if (!data.DecodedTextures.TryGetValue(path, out DecodedImage image) || !HasPixels(image) || image.Rgba == null)
                    {
                        continue;
                    }

                    var flip = normalFlips[path];
                    FlipNormalChannels(image.Rgba, flip.u, flip.v, flip.srgb);
                    image.Transformed = true;
                    if (options.CompressTextures && !mixedUse.Contains(path) && !packSources.Contains(path))
                    {
                        CompressDecoded(image, flip.srgb);
                    }
                }

                PackMetallicRoughness(data, options);
                PackAlbedoMask(data, options);
                PackRoughnessRange(data, options);
                PackAlbedoOpacity(data, options);
                ReleasePackSources(data, options, packSources, nonOrmUse, srgbUse);
            };

            // A JPEG is decoded by LoadImage at upload time, on the main thread; nothing on this
            // thread reads one. A packed map needs its sources' pixels, so one fed by a JPEG
            // (OpenChessSet's metalness and roughness maps all are) found nothing to pack and the
            // material kept its constants: every piece at 0.5 smoothness, gold flat, glass a white
            // ball. Those wait for the main thread, which decodes them and packs then
            // (RunDeferredPacking).
            var deferred = new List<string>(deferredFlips);
            foreach (string source in packSources)
            {
                if ((!data.DecodedTextures.TryGetValue(source, out DecodedImage image) || !HasPixels(image)) &&
                    data.TextureBytes.TryGetValue(source, out byte[] bytes) && bytes != null && bytes.Length > 0)
                {
                    deferred.Add(source);
                }
            }

            if (deferred.Count == 0)
            {
                pack();
            }
            else
            {
                data.DeferredPackSources = deferred;
                data.DeferredPack = pack;
            }
        }

        // The packing (and normal-map flips) DecodeUniqueTextures left for the main thread: decodes
        // the sources only LoadImage reads, then finishes them as the decode pass would have. Once per import; a no-op when
        // nothing was deferred.
        private static void RunDeferredPacking(StageData data)
        {
            Action pack = data.DeferredPack;
            if (pack == null)
            {
                return;
            }

            data.DeferredPack = null;
            foreach (string source in data.DeferredPackSources)
            {
                if (!data.TextureBytes.TryGetValue(source, out byte[] bytes) || bytes == null ||
                    !UsdImageLimits.PermitsLoadImage(bytes, out string reason))
                {
                    continue;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    if (!texture.LoadImage(bytes, false))
                    {
                        continue;
                    }

                    Color32[] pixels = texture.GetPixels32();
                    var rgba = new byte[pixels.Length * 4];
                    for (int i = 0, d = 0; i < pixels.Length; i++, d += 4)
                    {
                        rgba[d] = pixels[i].r;
                        rgba[d + 1] = pixels[i].g;
                        rgba[d + 2] = pixels[i].b;
                        rgba[d + 3] = pixels[i].a;
                    }

                    data.DecodedTextures[source] = new DecodedImage { Width = texture.width, Height = texture.height, Rgba = rgba };
                }
                finally
                {
                    DestroyUnityObject(texture);
                }
            }

            data.DeferredPackSources = null;
            pack();
        }

        // Decodes every tile of each UDIM set and stitches them into one atlas, laid out as the
        // tiles sit in UV space: tile 1001 + u + 10 v covers UV square (u, v), so the atlas
        // holds columns minU..maxU and rows minV..maxV and the material's texture transform
        // maps the mesh's UVs onto it. Empty squares inside that rectangle stay black.
        //
        // One set per worker at a time, so the decoded tiles of only a few sets are ever in
        // memory together: decoding all 6,777 of ALab's tiles up front would hold 27 GB of pixels.
        // An atlas wider or taller than the texture size limit has its tiles halved until it fits.
        // A malformed file must cost its own texture, not the import: an exception escaping a
        // Parallel.For body ends the whole loop. Out of memory included, which a hostile header
        // is the likeliest way to reach.
        private static bool TryDecodeExr(byte[] bytes, bool srgb, out int width, out int height, out byte[] rgba,
                                         out string reason)
        {
            try
            {
                return UsdExrDecoder.TryDecode(bytes, srgb, out width, out height, out rgba, out reason);
            }
            catch (Exception exception)
            {
                width = height = 0;
                rgba = null;
                reason = "OpenEXR could not be decoded: " + exception.Message;
                return false;
            }
        }

        private static void DecodeUdimSets(StageData data, UsdImportOptions options,
                                           Dictionary<string, bool> srgbUse, HashSet<string> mixedUse,
                                           HashSet<string> packSources, HashSet<string> graySources,
                                           HashSet<string> ormUse)
        {
            if (data.UdimSets.Count == 0)
            {
                return;
            }

            var keys = new List<string>(data.UdimSets.Keys);
            // Largest sets first, for the same reason the plain decode sorts by file size.
            keys.Sort((a, b) => data.UdimSets[b].Count.CompareTo(data.UdimSets[a].Count));

            var atlases = new DecodedImage[keys.Count];
            var layouts = new UdimLayout[keys.Count];
            var warnings = new List<string>[keys.Count];

            // Two waves. The sets with many tiles (large atlases) run on fewer workers than
            // cores, because each worker holds an atlas -- up to the device's largest texture,
            // four bytes a pixel -- beside the tile it is decoding, and the sets are sorted
            // largest first so that wave is the heaviest. The rest -- ALab's 1,132 single-tile
            // sets among them -- run on every core, where an atlas is a tile and the memory
            // argument does not apply.
            int bigCount = 0;
            while (bigCount < keys.Count && data.UdimSets[keys[bigCount]].Count >= 6)
            {
                bigCount++;
            }

            var fewWorkers = new System.Threading.Tasks.ParallelOptions
            {
                // With the atlas built and compressed natively and uploaded from that memory, a
                // worker holds one atlas's pixels plus one tile: every core fits the cap now. The
                // managed fallback (a payload older than API 12) holds up to a 256 MB atlas, a
                // 512 MB file and a 256 MB decoded tile per worker, so there a quarter of the
                // cores take the large sets.
                MaxDegreeOfParallelism = UsdNative.SupportsImageBuild
                    ? Math.Max(1, Environment.ProcessorCount)
                    : Math.Max(1, Environment.ProcessorCount / 4),
            };
            var allWorkers = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            };
            int maxSide = Math.Min(UsdImageLimits.MaxDimension, deviceMaxTextureSize);

            Action<int> decodeSet = index =>
            {
                string key = keys[index];
                List<KeyValuePair<int, string>> tiles = data.UdimSets[key];
                bool srgb = srgbUse.TryGetValue(key, out bool isSrgb) && isSrgb && !mixedUse.Contains(key);
                var notes = new List<string>();
                warnings[index] = notes;

                // Native first (API 12): the set's files are read, decoded, stitched and compressed
                // in one call, with no managed round trip per tile.
                if (UsdNative.SupportsImageBuild)
                {
                    var tilePaths = new List<string>(tiles.Count);
                    var tileNumbers = new List<int>(tiles.Count);
                    foreach (KeyValuePair<int, string> tile in tiles)
                    {
                        tilePaths.Add(tile.Value);
                        tileNumbers.Add(tile.Key);
                    }

                    bool wantsBlocks = options.CompressTextures && !mixedUse.Contains(key) && !packSources.Contains(key);
                    int mode = graySources.Contains(key) ? 2 : (wantsBlocks ? 1 : 0);
                    UsdNative.NativeImage built = UsdNative.BuildUdimAtlas(tilePaths, tileNumbers, srgb, mode,
                                                                           ormUse.Contains(key), maxSide, out string why);
                    if (built != null)
                    {
                        foreach (string line in built.Notes.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            notes.Add("USD import: UDIM tile left out of the atlas: " + line);
                        }

                        atlases[index] = FromNative(built, true);
                        layouts[index] = new UdimLayout
                        {
                            Scale = new Vector2(1f / built.Columns, 1f / built.Rows),
                            Offset = new Vector2(-(float)built.MinU / built.Columns, -(float)built.MinV / built.Rows),
                        };
                        return;
                    }

                    notes.Add($"USD import: UDIM set {key} could not be built natively ({why}); decoding in managed code.");
                }

                // The grid comes from the tile numbers alone: tile 1001 + u + 10 v.
                int minU = int.MaxValue, maxU = int.MinValue, minV = int.MaxValue, maxV = int.MinValue;
                foreach (KeyValuePair<int, string> tile in tiles)
                {
                    int square = tile.Key - 1001;
                    minU = Math.Min(minU, square % 10);
                    maxU = Math.Max(maxU, square % 10);
                    minV = Math.Min(minV, square / 10);
                    maxV = Math.Max(maxV, square / 10);
                }

                int columns = maxU - minU + 1;
                int rows = maxV - minV + 1;

                // Tiles are read and decoded one at a time and blitted straight into the atlas,
                // so a set never holds more than one tile's pixels beside the atlas. The first
                // tile that decodes sets the cell size; a later tile of another size is
                // resampled into its cell.
                DecodedImage atlas = null;
                int tileWidth = 0, tileHeight = 0;
                foreach (KeyValuePair<int, string> tile in tiles)
                {
                    byte[] bytes;
                    try
                    {
                        long length = new FileInfo(tile.Value).Length;
                        if (length > UsdImageLimits.MaxFileBytes)
                        {
                            notes.Add($"USD import: refused texture {tile.Value}: {length} bytes exceeds the {UsdImageLimits.MaxFileBytes}-byte limit.");
                            continue;
                        }

                        bytes = File.ReadAllBytes(tile.Value);
                    }
                    catch (Exception exception)
                    {
                        notes.Add($"USD import: error reading texture {tile.Value}: {exception.Message}");
                        continue;
                    }

                    int w, h;
                    byte[] rgba;
                    bool ok = UsdPngDecoder.TryDecode(bytes, out w, out h, out rgba) ||
                              (!UsdExrDecoder.IsExr(bytes) && UsdTgaDecoder.TryDecode(bytes, out w, out h, out rgba));
                    if (!ok && UsdExrDecoder.IsExr(bytes))
                    {
                        ok = TryDecodeExr(bytes, srgb, out w, out h, out rgba, out string why);
                        if (!ok)
                        {
                            notes.Add($"USD import: refused texture {tile.Value}: {why}.");
                        }
                    }
                    else if (!ok)
                    {
                        notes.Add($"USD import: UDIM tile {tile.Value} is not a PNG or OpenEXR the importer can decode off the main thread; it is left out of the atlas.");
                    }

                    if (!ok)
                    {
                        continue;
                    }

                    if (atlas == null)
                    {
                        tileWidth = w;
                        tileHeight = h;

                        // Halve the cells until the atlas fits what the device can create.
                        while ((long)columns * tileWidth > maxSide || (long)rows * tileHeight > maxSide ||
                               (long)columns * tileWidth * rows * tileHeight > UsdImageLimits.MaxPixelCount)
                        {
                            if (tileWidth < 8 || tileHeight < 8)
                            {
                                break;
                            }

                            tileWidth /= 2;
                            tileHeight /= 2;
                        }

                        // A thin tile (7x16384) stopped the loop above on its short side while the
                        // long one still overflowed. Each side is halved on its own until it fits,
                        // as in the native atlas, and a grid that still does not is refused.
                        while ((long)columns * tileWidth > maxSide && tileWidth > 1) tileWidth /= 2;
                        while ((long)rows * tileHeight > maxSide && tileHeight > 1) tileHeight /= 2;
                        while ((long)columns * tileWidth * rows * tileHeight > UsdImageLimits.MaxPixelCount &&
                               (tileWidth > 1 || tileHeight > 1))
                        {
                            if (tileWidth >= tileHeight) tileWidth /= 2; else tileHeight /= 2;
                        }

                        if ((long)columns * tileWidth > maxSide || (long)rows * tileHeight > maxSide ||
                            (long)columns * tileWidth * rows * tileHeight > UsdImageLimits.MaxPixelCount)
                        {
                            notes.Add($"USD import: UDIM set {key} has a {columns}x{rows} tile grid that does not fit the atlas limits; it is left out.");
                            return;
                        }

                        atlas = new DecodedImage
                        {
                            Width = columns * tileWidth,
                            Height = rows * tileHeight,
                            Rgba = new byte[(long)columns * tileWidth * rows * tileHeight * 4],
                            Transformed = true,
                        };

                        // Squares the set has no tile for stay black, but opaque: an alpha of 0
                        // there would make every atlas look transparent to the compressor.
                        for (long i = 3; i < atlas.Rgba.LongLength; i += 4)
                        {
                            atlas.Rgba[i] = 255;
                        }
                    }

                    int square = tile.Key - 1001;
                    BlitTile(new DecodedImage { Width = w, Height = h, Rgba = rgba }, atlas,
                             (square % 10 - minU) * tileWidth, (square / 10 - minV) * tileHeight,
                             tileWidth, tileHeight);
                }

                if (atlas == null)
                {
                    return;
                }

                if (ormUse.Contains(key))
                {
                    RepackOrm(atlas.Rgba);
                }

                if (graySources.Contains(key))
                {
                    KeepOnlyRed(atlas);
                }
                else if (options.CompressTextures && !mixedUse.Contains(key) && !packSources.Contains(key))
                {
                    CompressDecoded(atlas, srgb);
                }

                atlases[index] = atlas;
                layouts[index] = new UdimLayout
                {
                    Scale = new Vector2(1f / columns, 1f / rows),
                    Offset = new Vector2(-(float)minU / columns, -(float)minV / rows),
                };
            };

            System.Threading.Tasks.Parallel.For(0, bigCount, fewWorkers, decodeSet);
            System.Threading.Tasks.Parallel.For(bigCount, keys.Count, allWorkers, decodeSet);

            for (int index = 0; index < keys.Count; index++)
            {
                foreach (string note in warnings[index] ?? new List<string>())
                {
                    Debug.LogWarning(note);
                }

                data.DecodedTextures[keys[index]] = atlases[index];
                if (atlases[index] != null)
                {
                    data.UdimLayouts[keys[index]] = layouts[index];
                }
            }
        }

        // The largest texture the graphics device creates, read on the main thread when an import
        // starts. The Null device of a -nographics editor says 8192 where the GPU says 16384.
        private static int deviceMaxTextureSize = UsdImageLimits.MaxDimension;

        private static void NoteDeviceLimits()
        {
            int limit = SystemInfo.maxTextureSize;
            deviceMaxTextureSize = limit > 0 ? Math.Min(limit, UsdImageLimits.MaxDimension) : UsdImageLimits.MaxDimension;
        }

        // Copies `source` into `target` at (x0, y0), resampled to the cell size when the tile is
        // a different size from the cell (a set with mixed resolutions, or one that was halved
        // to fit). Rows run bottom-up in both, as Unity lays textures out.
        private static void BlitTile(DecodedImage source, DecodedImage target, int x0, int y0, int cellWidth, int cellHeight)
        {
            bool same = source.Width == cellWidth && source.Height == cellHeight;
            for (int y = 0; y < cellHeight; y++)
            {
                long targetRow = ((long)(y0 + y) * target.Width + x0) * 4;
                if (same)
                {
                    Buffer.BlockCopy(source.Rgba, y * source.Width * 4, target.Rgba, (int)targetRow, cellWidth * 4);
                    continue;
                }

                // Box-average the source pixels each cell pixel covers, so a halved tile keeps
                // its detail instead of dropping three pixels in four.
                int sy0 = (int)((long)y * source.Height / cellHeight);
                int sy1 = Math.Max(sy0 + 1, (int)((long)(y + 1) * source.Height / cellHeight));
                for (int x = 0; x < cellWidth; x++)
                {
                    int sx0 = (int)((long)x * source.Width / cellWidth);
                    int sx1 = Math.Max(sx0 + 1, (int)((long)(x + 1) * source.Width / cellWidth));
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int at = (sy * source.Width + sx) * 4;
                            r += source.Rgba[at];
                            g += source.Rgba[at + 1];
                            b += source.Rgba[at + 2];
                            a += source.Rgba[at + 3];
                            n++;
                        }
                    }

                    long to = targetRow + x * 4;
                    target.Rgba[to] = (byte)(r / n);
                    target.Rgba[to + 1] = (byte)(g / n);
                    target.Rgba[to + 2] = (byte)(b / n);
                    target.Rgba[to + 3] = (byte)(a / n);
                }
            }
        }

        // Replaces Rgba with its red channel alone. See DecodedImage.Gray.
        private static void KeepOnlyRed(DecodedImage image)
        {
            if (image.Rgba == null)
            {
                return;
            }

            var gray = new byte[image.Rgba.Length / 4];
            for (int i = 0; i < gray.Length; i++)
            {
                gray[i] = image.Rgba[i * 4];
            }

            image.Gray = gray;
            image.Rgba = null;
        }

        private static bool HasPixels(DecodedImage image)
        {
            return image != null && (image.Rgba != null || image.Gray != null);
        }

        // Whether the uploader has something to upload: pixels or a compressed chain. A
        // grey-only image has neither; it only ever fed a packed map.
        private static bool HasPixelsOrBlocks(DecodedImage image)
        {
            return image != null && (image.Rgba != null || image.Blocks != null ||
                                     (image.Native != null && image.Native.Handle != IntPtr.Zero));
        }

        // The atlas layout behind a texture key, if it is (or was packed from) a UDIM atlas.
        private static bool TryGetUdimLayout(StageData stage, string key, out UdimLayout layout)
        {
            layout = default;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            int packedAt = key.IndexOf("#packed:", StringComparison.Ordinal);
            return stage.UdimLayouts.TryGetValue(packedAt >= 0 ? key.Substring(0, packedAt) : key, out layout);
        }

        private static bool HasMaskBlend(MaterialData material)
        {
            return !string.IsNullOrEmpty(material.MaskPath) && !string.IsNullOrEmpty(material.AlbedoPath);
        }

        // Bakes lerp(MaskColor, albedo, mask) into the albedo, as Unreal-connector MDL computes its
        // base colour (MI_Floor_01: ColorAlbedo where MaskSelection is 0). The blend runs in linear
        // space, where MDL does it (the albedo is read as sRGB, the mask as linear), and the result
        // is stored sRGB like any albedo. Keyed on the albedo plus mask and colour, since the same
        // albedo file is blended with different colours by different materials.
        private static void PackAlbedoMask(StageData data, UsdImportOptions options)
        {
            var jobs = new List<(string key, string albedo, string mask, Vector3 colour, int mode, Vector3[] channels)>();
            var keys = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                if (!HasMaskBlend(material))
                {
                    continue;
                }

                string signature = material.MaskPath + "|" + material.MaskMode + "|" + Vec(material.MaskColor);
                if (material.MaskMode == 1 && material.ChannelColours != null)
                {
                    signature += "|" + Vec(material.ChannelColours[0]) + "|" + Vec(material.ChannelColours[1]) + "|" + Vec(material.ChannelColours[2]);
                }

                string key = material.AlbedoPath + "#packed:mask:" + signature.GetHashCode().ToString("x", CultureInfo.InvariantCulture);
                if (keys.Add(key))
                {
                    jobs.Add((key, material.AlbedoPath, material.MaskPath, material.MaskColor, material.MaskMode, material.ChannelColours));
                }

                material.AlbedoPath = key;
            }

            var toLinear = new float[256];
            for (int i = 0; i < 256; i++)
            {
                toLinear[i] = Mathf.GammaToLinearSpace(i / 255f);
            }

            var packed = new DecodedImage[jobs.Count];
            System.Threading.Tasks.Parallel.For(0, jobs.Count, index =>
            {
                var job = jobs[index];
                DecodedImage albedo = data.DecodedTextures.TryGetValue(job.albedo, out DecodedImage a) ? a : null;
                DecodedImage mask = data.DecodedTextures.TryGetValue(job.mask, out DecodedImage m) ? m : null;
                if (albedo?.Rgba == null || !HasPixels(mask))
                {
                    return;
                }

                int width = albedo.Width, height = albedo.Height;
                var rgba = new byte[width * height * 4];
                if (job.mode == 2 && mask.Rgba != null)
                {
                    // Unreal's BlendOverlay, as the connector writes it (M_AisleSign): the overlay
                    // is read linear (gamma_linear), the albedo as sRGB, and per channel
                    // t >= 0.5 ? 1 - 2(1 - t)(1 - a) : 2 t a, in linear space.
                    for (int y = 0; y < height; y++)
                    {
                        int my = mask.Height == height ? y : (int)((long)y * mask.Height / height);
                        for (int x = 0; x < width; x++)
                        {
                            int mx = mask.Width == width ? x : (int)((long)x * mask.Width / width);
                            int mi = (my * mask.Width + mx) * 4;
                            int at = (y * width + x) * 4;
                            for (int k = 0; k < 3; k++)
                            {
                                float t = mask.Rgba[mi + k] / 255f;
                                float a0 = toLinear[albedo.Rgba[at + k]];
                                float lin = t >= 0.5f ? 1f - 2f * (1f - t) * (1f - a0) : 2f * t * a0;
                                rgba[at + k] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(Mathf.Clamp01(lin)) * 255f), 0, 255);
                            }

                            rgba[at + 3] = albedo.Rgba[at + 3];
                        }
                    }
                }
                else if (job.mode == 1 && job.channels != null && mask.Rgba != null)
                {
                    // lerp(lerp(lerp(lerp(0, c0, m.r), c1, m.g), c2, m.b), albedo, m.a), linear.
                    Vector3 c0 = job.channels[0], c1 = job.channels[1], c2 = job.channels[2];
                    for (int y = 0; y < height; y++)
                    {
                        int my = mask.Height == height ? y : (int)((long)y * mask.Height / height);
                        for (int x = 0; x < width; x++)
                        {
                            int mx = mask.Width == width ? x : (int)((long)x * mask.Width / width);
                            int mi = (my * mask.Width + mx) * 4;
                            float r = mask.Rgba[mi] / 255f, g = mask.Rgba[mi + 1] / 255f, b = mask.Rgba[mi + 2] / 255f, w = mask.Rgba[mi + 3] / 255f;
                            Vector3 c = c0 * r;
                            c += (c1 - c) * g;
                            c += (c2 - c) * b;
                            int at = (y * width + x) * 4;
                            for (int k = 0; k < 3; k++)
                            {
                                float lin = c[k] + (toLinear[albedo.Rgba[at + k]] - c[k]) * w;
                                rgba[at + k] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(lin) * 255f), 0, 255);
                            }

                            rgba[at + 3] = albedo.Rgba[at + 3];
                        }
                    }
                }
                else
                {
                float cr = job.colour.x, cg = job.colour.y, cb = job.colour.z;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int at = (y * width + x) * 4;
                        float t = Sample(mask, x, y, width, height) / 255f;
                        rgba[at] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(cr + (toLinear[albedo.Rgba[at]] - cr) * t) * 255f), 0, 255);
                        rgba[at + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(cg + (toLinear[albedo.Rgba[at + 1]] - cg) * t) * 255f), 0, 255);
                        rgba[at + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(cb + (toLinear[albedo.Rgba[at + 2]] - cb) * t) * 255f), 0, 255);
                        rgba[at + 3] = albedo.Rgba[at + 3];
                    }
                }
                }

                var image = new DecodedImage { Width = width, Height = height, Rgba = rgba, Transformed = true };
                if (options.CompressTextures)
                {
                    CompressDecoded(image, true);
                }

                packed[index] = image;
            });

            for (int index = 0; index < jobs.Count; index++)
            {
                if (packed[index] != null)
                {
                    data.DecodedTextures[jobs[index].key] = packed[index];
                }
                else if (data.DecodedTextures.TryGetValue(jobs[index].albedo, out DecodedImage plain))
                {
                    data.DecodedTextures[jobs[index].key] = plain;
                }
            }
        }

        private static string Vec(Vector3 v)
        {
            return v.x.ToString("R", CultureInfo.InvariantCulture) + "," + v.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                   v.z.ToString("R", CultureInfo.InvariantCulture);
        }

        // Unreal-connector roughness: lerp(RoughnessMin, RoughnessMax, ORM.g). The ORM map was
        // repacked on decode (metallic in red, smoothness = 1 - ORM.g in alpha), so the alpha is
        // remapped to 1 - lerp(min, max, 1 - alpha). Taken as it was, ORM.g reached 0 on worn
        // concrete and the warehouse floor rendered as polished. A copy per range, keyed on it.
        private static void PackRoughnessRange(StageData data, UsdImportOptions options)
        {
            var jobs = new List<(string key, string source, Vector2 range)>();
            var keys = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                if (!material.HasRoughnessRange || string.IsNullOrEmpty(material.MetallicGlossPath))
                {
                    continue;
                }

                string key = material.MetallicGlossPath + "#packed:range:" +
                             (material.RoughnessRange.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                              material.RoughnessRange.y.ToString("R", CultureInfo.InvariantCulture)).GetHashCode().ToString("x", CultureInfo.InvariantCulture);
                if (keys.Add(key))
                {
                    jobs.Add((key, material.MetallicGlossPath, material.RoughnessRange));
                }

                material.MetallicGlossPath = key;
            }

            var packed = new DecodedImage[jobs.Count];
            System.Threading.Tasks.Parallel.For(0, jobs.Count, index =>
            {
                var job = jobs[index];
                DecodedImage source = data.DecodedTextures.TryGetValue(job.source, out DecodedImage o) ? o : null;
                if (source?.Rgba == null)
                {
                    return;
                }

                var rgba = (byte[])source.Rgba.Clone();
                float min = job.range.x, span = job.range.y - job.range.x;
                for (int at = 3; at < rgba.Length; at += 4)
                {
                    float rough = min + span * (1f - rgba[at] / 255f);
                    rgba[at] = (byte)Mathf.Clamp(Mathf.RoundToInt((1f - rough) * 255f), 0, 255);
                }

                var image = new DecodedImage { Width = source.Width, Height = source.Height, Rgba = rgba, Transformed = true };
                if (options.CompressTextures)
                {
                    CompressDecoded(image, false);
                }

                packed[index] = image;
            });

            for (int index = 0; index < jobs.Count; index++)
            {
                if (packed[index] != null)
                {
                    data.DecodedTextures[jobs[index].key] = packed[index];
                }
                else if (data.DecodedTextures.TryGetValue(jobs[index].source, out DecodedImage plain))
                {
                    data.DecodedTextures[jobs[index].key] = plain;
                }
            }
        }

        private static bool HasSeparateOpacity(MaterialData material)
        {
            return !string.IsNullOrEmpty(material.OpacityPath) &&
                   !string.Equals(material.OpacityPath, material.AlbedoPath, StringComparison.Ordinal);
        }

        // Puts an opacity map authored as a file of its own into the albedo's alpha channel,
        // which is the only place Unity's lit shaders read opacity from.
        //
        // Moore Lane's foliage does this nine times -- a colour map and a cut-out mask as two
        // files -- and each used to log "Unity reads alpha from the base map, so this opacity is
        // ignored" and draw its leaves as solid cards. A material with the mask but no colour map
        // gets a white image carrying the mask.
        private static void PackAlbedoOpacity(StageData data, UsdImportOptions options)
        {
            var jobs = new List<(string key, string albedo, string opacity)>();
            var keys = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                if (!HasSeparateOpacity(material))
                {
                    continue;
                }

                string key = (material.AlbedoPath ?? material.OpacityPath) + "#packed:opacity:" +
                             material.OpacityPath.GetHashCode().ToString("x", CultureInfo.InvariantCulture);
                if (keys.Add(key))
                {
                    jobs.Add((key, material.AlbedoPath, material.OpacityPath));
                }

                material.AlbedoPath = key;
                material.OpacityPath = key;
            }

            var packed = new DecodedImage[jobs.Count];
            System.Threading.Tasks.Parallel.For(0, jobs.Count, index =>
            {
                var job = jobs[index];
                DecodedImage albedo = job.albedo != null &&
                                      data.DecodedTextures.TryGetValue(job.albedo, out DecodedImage a) ? a : null;
                DecodedImage opacity = data.DecodedTextures.TryGetValue(job.opacity, out DecodedImage o) ? o : null;
                if (!HasPixels(opacity))
                {
                    return; // no mask decoded; the material keeps whatever albedo it had
                }

                if (UsdNative.SupportsImageBuild && options.CompressTextures)
                {
                    UsdNative.NativeImage built = UsdNative.PackAlbedoOpacity(
                        albedo?.Rgba, albedo?.Rgba != null ? albedo.Width : 0, albedo?.Rgba != null ? albedo.Height : 0,
                        opacity.Gray ?? opacity.Rgba, opacity.Width, opacity.Height, opacity.Gray != null ? 1 : 4, true);
                    if (built != null)
                    {
                        packed[index] = FromNative(built, true);
                        return;
                    }
                }

                DecodedImage size = albedo?.Rgba != null &&
                                    (long)albedo.Width * albedo.Height >= (long)opacity.Width * opacity.Height
                    ? albedo : opacity;
                int width = size.Width, height = size.Height;
                var rgba = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int at = (y * width + x) * 4;
                        if (albedo?.Rgba != null)
                        {
                            int sx = albedo.Width == width ? x : (int)((long)x * albedo.Width / width);
                            int sy = albedo.Height == height ? y : (int)((long)y * albedo.Height / height);
                            int from = (sy * albedo.Width + sx) * 4;
                            rgba[at] = albedo.Rgba[from];
                            rgba[at + 1] = albedo.Rgba[from + 1];
                            rgba[at + 2] = albedo.Rgba[from + 2];
                        }
                        else
                        {
                            rgba[at] = rgba[at + 1] = rgba[at + 2] = 255;
                        }

                        rgba[at + 3] = Sample(opacity, x, y, width, height);
                    }
                }

                var image = new DecodedImage { Width = width, Height = height, Rgba = rgba, Transformed = true };
                if (options.CompressTextures)
                {
                    CompressDecoded(image, true);
                }

                packed[index] = image;
            });

            for (int index = 0; index < jobs.Count; index++)
            {
                if (packed[index] != null)
                {
                    data.DecodedTextures[jobs[index].key] = packed[index];
                }
                else if (jobs[index].albedo != null)
                {
                    // Fall back to the plain albedo so a missing mask costs the colour nothing.
                    data.DecodedTextures[jobs[index].key] = data.DecodedTextures[jobs[index].albedo];
                }
            }
        }

        // A source read by nothing else has served its purpose once packed; one some other slot
        // also reads is compressed now, which the decode pass skipped so the pixels were still here.
        private static void ReleasePackSources(StageData data, UsdImportOptions options,
                                               HashSet<string> packSources, HashSet<string> directUse,
                                               Dictionary<string, bool> srgbUse)
        {
            var stillUsed = new HashSet<string>();
            foreach (MaterialData material in data.Materials)
            {
                foreach (string path in new[] { material.AlbedoPath, material.NormalPath, material.MetallicGlossPath,
                                                material.EmissionPath, material.OpacityPath, material.OcclusionPath })
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        stillUsed.Add(path);
                    }
                }
            }

            foreach (string source in packSources)
            {
                if (!data.DecodedTextures.TryGetValue(source, out DecodedImage image) || !HasPixels(image))
                {
                    continue;
                }

                if (!stillUsed.Contains(source))
                {
                    data.DecodedTextures[source] = null;
                }
                else if (options.CompressTextures && image.Rgba != null)
                {
                    CompressDecoded(image, srgbUse.TryGetValue(source, out bool srgb) && srgb);
                }
            }
        }

        // Wraps what the native side built. Pixels and grey come across as managed arrays, which
        // the packing passes read; a compressed chain stays where it is until it is uploaded.
        private static DecodedImage FromNative(UsdNative.NativeImage built, bool transformed)
        {
            var image = new DecodedImage { Width = built.Width, Height = built.Height, Transformed = transformed };
            switch (built.Mode)
            {
                case 1:
                    image.Native = built;
                    image.MipCount = built.MipCount;
                    image.HasAlpha = built.HasAlpha;
                    break;
                case 2:
                    image.Gray = built.CopyBytes();
                    built.Release();
                    break;
                default:
                    image.Rgba = built.CopyBytes();
                    built.Release();
                    break;
            }

            return image;
        }

        // Block-compresses a decoded image in place of its pixels, mips included.
        private static void CompressDecoded(DecodedImage image, bool srgb)
        {
            image.HasAlpha = !UsdBlockCompressor.IsOpaque(image.Rgba);
            image.Blocks = UsdBlockCompressor.Encode(image.Rgba, image.Width, image.Height,
                                                     image.HasAlpha, srgb, out int mips);
            image.MipCount = mips;
            if (image.Blocks != null)
            {
                image.Rgba = null; // the GPU takes the blocks; nothing reads these again
            }
        }

        // Folds a plain metallic map and a separate roughness map into the one texture Unity's
        // lit shaders read: metallic in red, smoothness (1 - roughness) in alpha.
        //
        // Every DCC and every USD sample authors them apart -- ALab has 1,853 materials with a
        // metallic map, a roughness map and an occlusion map as three files -- and the importer
        // used to put whichever it found into the metallic slot as if it were packed. A roughness
        // map read that way has its roughness taken as metallic and its missing alpha as full
        // smoothness: every surface a mirror. The packed image is keyed on both sources and the
        // constants that stand in for a missing one, so materials sharing them share the texture.
        private static void PackMetallicRoughness(StageData data, UsdImportOptions options)
        {
            var jobs = new List<(string key, string metallic, string roughness, float metallicConstant,
                                 float roughnessConstant)>();
            var keys = new Dictionary<string, int>();
            foreach (MaterialData material in data.Materials)
            {
                bool metallicIsMap = material.MetallicIsPlain && !string.IsNullOrEmpty(material.MetallicGlossPath);
                bool roughnessIsMap = !string.IsNullOrEmpty(material.RoughnessPath);
                if (!metallicIsMap && !roughnessIsMap)
                {
                    continue;
                }

                string metallic = metallicIsMap ? material.MetallicGlossPath : null;
                string roughness = roughnessIsMap ? material.RoughnessPath : null;
                string key = (metallic ?? roughness) + "#packed:" +
                             (metallic != null ? "m" : material.Source.Metallic.ToString("R", CultureInfo.InvariantCulture)) + ":" +
                             (roughness != null ? roughness.GetHashCode().ToString("x", CultureInfo.InvariantCulture)
                                                : material.Source.Roughness.ToString("R", CultureInfo.InvariantCulture));
                if (!keys.ContainsKey(key))
                {
                    keys[key] = jobs.Count;
                    jobs.Add((key, metallic, roughness, material.Source.Metallic, material.Source.Roughness));
                }

                material.MetallicGlossPath = key;
                material.MetallicIsPlain = false;
                material.MetallicIsOrm = false;
            }

            var packed = new DecodedImage[jobs.Count];
            System.Threading.Tasks.Parallel.For(0, jobs.Count, index =>
            {
                var job = jobs[index];
                DecodedImage metallic = job.metallic != null &&
                                        data.DecodedTextures.TryGetValue(job.metallic, out DecodedImage m) ? m : null;
                DecodedImage roughness = job.roughness != null &&
                                         data.DecodedTextures.TryGetValue(job.roughness, out DecodedImage r) ? r : null;
                if (!HasPixels(metallic) && !HasPixels(roughness))
                {
                    return; // neither source decoded; the material keeps its constants
                }

                // Native (API 12): the packing and the compression in one call, the chain
                // uploaded from where it was built.
                if (UsdNative.SupportsImageBuild && options.CompressTextures)
                {
                    UsdNative.NativeImage built = UsdNative.PackMetallicRoughness(
                        HasPixels(metallic) ? metallic.Gray ?? metallic.Rgba : null,
                        HasPixels(metallic) ? metallic.Width : 0, HasPixels(metallic) ? metallic.Height : 0,
                        HasPixels(metallic) && metallic.Gray != null ? 1 : 4,
                        HasPixels(roughness) ? roughness.Gray ?? roughness.Rgba : null,
                        HasPixels(roughness) ? roughness.Width : 0, HasPixels(roughness) ? roughness.Height : 0,
                        HasPixels(roughness) && roughness.Gray != null ? 1 : 4,
                        (int)Math.Round(Math.Max(0f, Math.Min(1f, job.metallicConstant)) * 255f),
                        (int)Math.Round((1f - Math.Max(0f, Math.Min(1f, job.roughnessConstant))) * 255f));
                    if (built != null)
                    {
                        packed[index] = FromNative(built, true);
                        return;
                    }
                }

                // The larger of the two sets the size; the other is point-sampled into it.
                DecodedImage size = HasPixels(roughness) &&
                                    (!HasPixels(metallic) || (long)roughness.Width * roughness.Height >=
                                                             (long)metallic.Width * metallic.Height)
                    ? roughness : metallic;
                int width = size.Width, height = size.Height;
                var rgba = new byte[width * height * 4];
                byte metallicByte = (byte)Math.Round(Math.Max(0f, Math.Min(1f, job.metallicConstant)) * 255f);
                byte smoothnessByte = (byte)Math.Round((1f - Math.Max(0f, Math.Min(1f, job.roughnessConstant))) * 255f);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int at = (y * width + x) * 4;
                        rgba[at] = HasPixels(metallic) ? Sample(metallic, x, y, width, height) : metallicByte;
                        rgba[at + 1] = 0;
                        rgba[at + 2] = 0;
                        rgba[at + 3] = HasPixels(roughness)
                            ? (byte)(255 - Sample(roughness, x, y, width, height))
                            : smoothnessByte;
                    }
                }

                var image = new DecodedImage { Width = width, Height = height, Rgba = rgba, Transformed = true };
                if (options.CompressTextures)
                {
                    CompressDecoded(image, false);
                }

                packed[index] = image;
            });

            for (int index = 0; index < jobs.Count; index++)
            {
                data.DecodedTextures[jobs[index].key] = packed[index];
            }
        }

        // The red channel of `source` at the position (x, y) of an image of the given size.
        private static byte Sample(DecodedImage source, int x, int y, int width, int height)
        {
            int sx = source.Width == width ? x : (int)((long)x * source.Width / width);
            int sy = source.Height == height ? y : (int)((long)y * source.Height / height);
            return source.Gray != null
                ? source.Gray[sy * source.Width + sx]
                : source.Rgba[(sy * source.Width + sx) * 4];
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
            Texture2D albedo = null, normal = null, metallicGloss = null, emission = null, occlusion = null;
            if (options.ImportTextures)
            {
                albedo = GetOrUploadTexture(data.AlbedoPath, false, stage, decodeCache, options);
                occlusion = GetOrUploadTexture(data.OcclusionPath, true, stage, decodeCache, options);
                normal = GetOrUploadTexture(data.NormalPath, !data.NormalIsSrgb, stage, decodeCache, options);
                metallicGloss = GetOrUploadTexture(data.MetallicGlossPath, true, stage, decodeCache, options);
                emission = GetOrUploadTexture(data.EmissionPath, false, stage, decodeCache, options);
            }

            return AssembleMaterial(data, options, stage, albedo, normal, metallicGloss, emission, occlusion);
        }

        // The lit shader every imported material uses: the active render pipeline's. A project on
        // the Built-in pipeline can still have URP or HDRP installed, and their shaders draw
        // nothing there, so the pipeline in use decides, not which shaders exist.
        private static Shader FindLitShader()
        {
            UnityEngine.Rendering.RenderPipelineAsset active = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            string pipeline = active != null
                ? active.GetType().Name
                : null;
            Shader preferred =
                pipeline == null ? Shader.Find("Standard")
                : pipeline.Contains("Universal") ? Shader.Find("Universal Render Pipeline/Lit")
                : pipeline.Contains("HDRenderPipeline") ? Shader.Find("HDRP/Lit")
                : null;
            return
                preferred ??
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("HDRP/Lit") ??
                Shader.Find("Standard") ??
                Shader.Find("Sprites/Default");
        }

        // The shared grey material for a mesh or submesh with no material binding
        // (UsdImportOptions.AssignDefaultMaterial). Without it the renderer has no material and
        // URP/HDRP draw it magenta, which reads as a broken shader. Built on first use, one per
        // import. When no lit shader exists it returns null, so the mesh stays magenta: that is
        // a real failure and should look like one.
        private static Material GetDefaultMaterial(UsdImportOptions options, UsdImportResult result)
        {
            if (!options.AssignDefaultMaterial)
            {
                return null;
            }

            if (!result.DefaultMaterialResolved)
            {
                result.DefaultMaterialResolved = true;
                Shader shader = FindLitShader();
                if (shader != null)
                {
                    // UsdPreviewSurface's own defaults (diffuseColor 0.18 linear, roughness 0.5,
                    // metallic 0): what Hydra and Isaac Sim draw a mesh with no material and no
                    // displayColor in. Measured on Isaac Sim 6.2 against 18% and 50% grey
                    // reference spheres under the same light, the unbound one matched the 18%.
                    var material = new Material(shader) { name = "UsdDefaultMaterial" };
                    SetMaterialColor(material, UsdColorToMaterialColor(new Color(0.18f, 0.18f, 0.18f, 1f)));
                    SetMaterialFloat(material, "_Metallic", 0f);
                    SetMaterialFloat(material, "_Smoothness", 0.5f);
                    SetMaterialFloat(material, "_Glossiness", 0.5f);
                    result.DefaultMaterial = material;
                }
            }

            return result.DefaultMaterial;
        }

        // Builds the Unity Material from already-resolved textures (shared by the sync and async
        // paths). Pure main-thread Material/Shader work — no decoding here.
        private static Material AssembleMaterial(
            MaterialData data,
            UsdImportOptions options,
            StageData stage,
            Texture2D albedo,
            Texture2D normal,
            Texture2D metallicGloss,
            Texture2D emission,
            Texture2D occlusion,
            bool readUdimLayouts = true)
        {
            Shader shader = FindLitShader();
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
            Color color = UsdColorToMaterialColor(new Color(source.R, source.G, source.B, source.A));
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
                // URP and Standard multiply _Smoothness into that alpha. The authored roughness
                // is the fallback for an unconnected input, as with the albedo above, and a
                // constant standing in for a missing roughness map is already in the packed
                // alpha: left at 1 - roughness, every packed surface came out at half its
                // smoothness (the 0.5 default roughness halving it again).
                SetMaterialFloat(material, "_Smoothness", 1f);

                // A repacked ORM map carries occlusion in green, which is the channel URP's
                // occlusion map is read from -- so the same texture fills both slots without a
                // second decode, a second compression or a second upload.
                if (data.MetallicIsOrm && occlusion == null)
                {
                    SetMaterialTexture(material, "_OcclusionMap", metallicGloss);
                    material.EnableKeyword("_OCCLUSIONMAP");
                    SetMaterialFloat(material, "_OcclusionStrength", 1f);
                }
            }

            // An authored ao_texture overrides ORM's red channel.
            if (occlusion != null)
            {
                SetMaterialTexture(material, "_OcclusionMap", occlusion);
                material.EnableKeyword("_OCCLUSIONMAP");
                SetMaterialFloat(material, "_OcclusionStrength", 1f);
            }

            // emission → _EmissionMap and/or _EmissionColor, enable _EMISSION
            // _EmissionColor (URP) and _EmissiveColor (HDRP) are [HDR] colours: Unity stores them
            // linear and does not convert them on SetColor, unlike the base colour. The USD value is
            // therefore written as authored. Converting it to sRGB like the base colour made every
            // emitter render as sRGB(E): washed out and too bright (an emissive 0.18 grey showed as 0.46).
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

            // A UDIM atlas: the mesh's UVs live in UDIM space (u in [column, column + 1)), and the
            // atlas holds only the columns and rows the set has, so the transform scales the UVs
            // down by the grid and shifts them by the first tile. Composed after the authored
            // transform, which USD applies to the UV first.
            foreach (string key in readUdimLayouts
                         ? new[] { data.AlbedoPath, data.NormalPath, data.MetallicGlossPath, data.EmissionPath, data.OcclusionPath }
                         : Array.Empty<string>())
            {
                if (TryGetUdimLayout(stage, key, out UdimLayout layout))
                {
                    scale = Vector2.Scale(scale, layout.Scale);
                    offset = Vector2.Scale(offset, layout.Scale) + layout.Offset;
                    break;
                }
            }

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
            // URP and Built-in draw a blended surface into the shadow map as if it were opaque, so
            // glass cast a solid shadow and a glazed roof blocked the sun completely. USD renderers
            // (Isaac Sim RTX) let light through it. Without translucent shadows the closer match is
            // no shadow: the pass is turned off on the material, the renderer is left alone.
            material.SetShaderPassEnabled("ShadowCaster", false);
        }

        // Turns an authored texture path into the key its bytes are cached under: the absolute
        // file path when it is a file on disk, or the resolved asset identifier when the stage
        // lives inside a package (a .usdz), whose textures no file-system read can reach.
        // Replaces a <UDIM> token with the first tile that exists on disk, or with 1001 when none
        // can be checked. Returns the path unchanged when it holds no token.
        private const string UdimToken = "<UDIM>";

        // A packed image is keyed on its source file plus a "#packed:" suffix, and a UDIM atlas on
        // the pattern with its <UDIM> token; the texture takes the file's name. The token goes
        // whatever separates it from the stem (tex.<UDIM>, tex_<UDIM>, tex<UDIM>): '<' and '>'
        // left in the name make Path throw on Windows. The separator left behind is trimmed.
        private static string TextureNameFromKey(string key)
        {
            int packedAt = key.IndexOf("#packed:", StringComparison.Ordinal);
            string source = packedAt >= 0 ? key.Substring(0, packedAt) : key;
            string name = Path.GetFileNameWithoutExtension(source.Replace(UdimToken, string.Empty));
            if (source.Contains(UdimToken))
            {
                string trimmed = name.TrimEnd('.', '_', '-');
                name = trimmed.Length > 0 ? trimmed : name;
            }

            return name;
        }

        // Finds every tile on disk behind a <UDIM> reference, records the set under one key and
        // returns that key; null when the path has no token or no tile exists for it.
        private static string ResolveUdimSet(StageData stage, string relativePath)
        {
            int at = relativePath.IndexOf(UdimToken, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            string prefix = relativePath.Substring(0, at);
            string suffix = relativePath.Substring(at + UdimToken.Length);
            string fullPrefix = Path.IsPathRooted(prefix)
                ? prefix
                : Path.Combine(stage.BaseDirectory ?? string.Empty, prefix);

            // The folder is checked first, so nothing outside the stage folder is listed. Each tile
            // is then checked again on its own: a sibling can be a symlink to anywhere, and both
            // tile readers (the native atlas and File.ReadAllBytes) follow links.
            if (!IsAssetPathAllowed(stage, fullPrefix + "1001" + suffix, out string canonicalFirst))
            {
                Debug.LogWarning(
                    $"USD import: texture path resolves outside the stage folder, skipping: {relativePath}");
                return null;
            }

            string directory = Path.GetDirectoryName(canonicalFirst);
            string stem = Path.GetFileName(fullPrefix);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var tiles = new List<KeyValuePair<int, string>>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, stem + "*" + suffix))
                {
                    string name = Path.GetFileName(file);
                    if (name.Length != stem.Length + 4 + suffix.Length)
                    {
                        continue;
                    }

                    string digits = name.Substring(stem.Length, 4);
                    if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int tile) &&
                        tile >= 1001 && tile <= 1999)
                    {
                        if (!IsAssetPathAllowed(stage, Path.Combine(directory, name), out string canonicalTile))
                        {
                            Debug.LogWarning(
                                $"USD import: UDIM tile resolves outside the stage folder, skipping: {name}");
                            continue;
                        }

                        tiles.Add(new KeyValuePair<int, string>(tile, canonicalTile));
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            if (tiles.Count == 0)
            {
                return null;
            }

            tiles.Sort((a, b) => a.Key.CompareTo(b.Key));
            string key = Path.Combine(directory, stem) + UdimToken + suffix;
            stage.UdimSets[key] = tiles;
            return key;
        }

        private static string ExpandUdim(string path, StageData stage)
        {
            const string Token = "<UDIM>";
            int at = path.IndexOf(Token, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return path;
            }

            string prefix = path.Substring(0, at);
            string suffix = path.Substring(at + Token.Length);

            // 1001 is the first tile and the one a single-tile asset uses. An asset that starts
            // elsewhere -- ALab has one whose tiles begin at 1011 and one at 1021 -- is found by
            // listing the folder for the lowest tile that exists, rather than probing a fixed
            // range that such an asset falls outside of.
            string fullPrefix = Path.IsPathRooted(prefix)
                ? prefix
                : Path.Combine(stage.BaseDirectory ?? string.Empty, prefix);
            string first = prefix + "1001" + suffix;

            // Nothing outside the stage folder is probed: the caller refuses such a path anyway,
            // and probing first meant File.Exists and a directory listing on whatever the stage
            // named -- on Windows a \\host\share path opens an SMB session and hands the host
            // the user's NTLM hash.
            if (!IsAssetPathAllowed(stage, fullPrefix + "1001" + suffix, out _))
            {
                return first;
            }

            if (File.Exists(fullPrefix + "1001" + suffix))
            {
                return first;
            }

            try
            {
                string directory = Path.GetDirectoryName(fullPrefix);
                string stem = Path.GetFileName(fullPrefix);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    int lowest = int.MaxValue;
                    foreach (string file in Directory.EnumerateFiles(directory, stem + "*" + suffix))
                    {
                        string name = Path.GetFileName(file);
                        string digits = name.Substring(stem.Length, name.Length - stem.Length - suffix.Length);
                        if (digits.Length == 4 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int tile) &&
                            tile >= 1001 && tile < lowest)
                        {
                            lowest = tile;
                        }
                    }

                    if (lowest != int.MaxValue)
                    {
                        return prefix + lowest.ToString(CultureInfo.InvariantCulture) + suffix;
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable folder: fall through to the first tile and let the read report it.
            }

            return first;
        }

        private static string ResolveTexturePath(IntPtr context, StageData stage, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            // A UDIM reference is one path standing for a grid of tiles: the <UDIM> token is
            // replaced by 1001, 1002 and so on, one file per tile of UV space. Unity has no UDIM
            // texture, so the tiles are stitched into one atlas and the material's texture
            // transform maps the mesh's UVs onto it. ALab carries 2,047 such references over
            // 6,777 tiles; using the first tile alone drew every panel outside UV square 1001
            // with the wrong image, which read as black patches and repeating roofs next to
            // Isaac Sim's render of the same stage.
            string udimKey = ResolveUdimSet(stage, relativePath);
            if (udimKey != null)
            {
                return udimKey;
            }

            // No tile on disk under the UDIM pattern: fall through with the first tile's name,
            // so the "texture not found" below names a real file.
            string authoredPath = ExpandUdim(relativePath, stage);

            string fullPath = Path.IsPathRooted(authoredPath)
                ? authoredPath
                : Path.Combine(stage.BaseDirectory ?? string.Empty, authoredPath);

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
#if UNITY_EDITOR_LINUX || (!UNITY_EDITOR && UNITY_STANDALONE_LINUX)
        private const StringComparison PathComparison = StringComparison.Ordinal;
#else
        private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#endif

        // Textures of a remote material the importer downloaded itself (UsdRemoteMaterials) sit in
        // its cache folder, outside the stage; they are the importer's own files, not paths the
        // stage chose, so they are allowed.
        private static bool IsInRemoteMaterialCache(string canonicalPath)
        {
            if (string.IsNullOrEmpty(s_remoteMaterialCache))
            {
                return false;
            }

            try
            {
                string root = Path.GetFullPath(s_remoteMaterialCache)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return canonicalPath.StartsWith(root, PathComparison);
            }
            catch (Exception)
            {
                return false;
            }
        }

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

            if (stage.AllowExternalAssetPaths || IsInRemoteMaterialCache(canonicalPath))
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
            // above and is then read straight off disk (CWE-59/CWE-22).
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
            Dictionary<string, Texture2D> decodeCache,
            UsdImportOptions options)
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

            string name = TextureNameFromKey(path);
            Texture2D texture = null;
            bool pixelsAreTheFiles = false;
            if (stage.DecodedTextures.TryGetValue(path, out DecodedImage decoded) && decoded != null &&
                HasPixelsOrBlocks(decoded))
            {
                try
                {
                    texture = decoded.Blocks != null || decoded.Native != null
                        ? CreateTextureFromBlocks(decoded, linear, name, options.KeepTexturesReadable)
                        : CreateTextureFromRgba(decoded.Width, decoded.Height, decoded.Rgba, linear, name,
                                                options.CompressTextures, options.KeepTexturesReadable);

                    // The compressed chain now lives in the texture; keeping the managed copy
                    // until the import ends held every texture twice -- 5 GB of blocks beside
                    // 5 GB of textures on ALab, which is what put the import against the memory
                    // cap. A compressed image has one colour space, so no second upload wants it.
                    if (decoded.Blocks != null)
                    {
                        decoded.Blocks = null;
                    }
                }
                catch (Exception exception)
                {
                    // One texture the device refuses (an atlas past its size limit, say) must not
                    // take the import with it; the material keeps its constants and the reason
                    // names the texture, its size and the limit it ran into.
                    Debug.LogWarning(
                        $"USD import: could not create texture '{name}' ({decoded.Width}x{decoded.Height}, " +
                        $"{(decoded.Blocks != null ? (decoded.HasAlpha ? "DXT5" : "DXT1") : "RGBA32")}, " +
                        $"{decoded.MipCount} mips; device limit {SystemInfo.maxTextureSize}): {exception.Message}");
                    texture = null;
                }

                pixelsAreTheFiles = !decoded.Transformed;
            }
            else if (stage.TextureBytes.TryGetValue(path, out byte[] bytes) && bytes != null && bytes.Length > 0)
            {
                texture = CreateTextureViaLoadImage(bytes, linear, name, options.CompressTextures,
                                                    options.KeepTexturesReadable);
                pixelsAreTheFiles = true;
            }

            // Remembered so that an export can copy the file instead of reading the GPU copy
            // back -- which, once the import has block-compressed it, is a lossy copy of a
            // lossless file.
            if (texture != null && pixelsAreTheFiles && !stage.PackageAssets.ContainsKey(path) && File.Exists(path))
            {
                UsdTextureOrigins.Record(texture, path);
            }

            // An atlas is addressed by UVs the material transform has already placed inside it;
            // wrapping would pull the neighbouring tile in at the edges.
            if (texture != null && TryGetUdimLayout(stage, path, out _))
            {
                texture.wrapMode = TextureWrapMode.Clamp;
            }

            decodeCache[key] = texture; // cache nulls too, to avoid retrying a bad file
            return texture;
        }

        // Uploads blocks a worker thread already compressed. Nothing here touches the pixels: the
        // texture is created in its final format and the bytes go straight across, which is the
        // whole reason for compressing off the main thread in the first place.
        private static Texture2D CreateTextureFromBlocks(DecodedImage image, bool linear, string name, bool keepReadable)
        {
            TextureFormat format = image.HasAlpha ? TextureFormat.DXT5 : TextureFormat.DXT1;
            var texture = new Texture2D(image.Width, image.Height, format, image.MipCount, linear)
            {
                name = name,
            };

            if (image.Native != null)
            {
                // Straight from the memory the native side built it in; no managed copy.
                texture.LoadRawTextureData(image.Native.Data, (int)image.Native.ByteCount);
                image.Native.Release();
                image.Native = null;
            }
            else
            {
                texture.LoadRawTextureData(image.Blocks);
            }

            texture.Apply(false, !keepReadable); // no mip generation -- the chain is in the blocks
            return texture;
        }

        private static Texture2D CreateTextureFromRgba(int width, int height, byte[] rgba, bool linear,
                                                       string name, bool compress, bool keepReadable)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, linear) { name = name };
            texture.SetPixelData(rgba, 0);
            texture.Apply(true, false); // mipmaps now; stays readable for Finish
            return Finish(texture, compress, keepReadable);
        }

        private static Texture2D CreateTextureViaLoadImage(byte[] bytes, bool linear, string name, bool compress,
                                                           bool keepReadable = false)
        {
            // Re-checked at the sink (CWE-400). LoadImage applies no size limit
            // of its own, and DecodeUniqueTextures already refused what fails this -- this keeps
            // that true if another caller is ever added.
            if (!UsdImageLimits.PermitsLoadImage(bytes, out string reason))
            {
                Debug.LogWarning($"USD import: refused texture '{name}': {reason}.");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
            if (!texture.LoadImage(bytes, false))
            {
                UnityEngine.Object.Destroy(texture);
                Debug.LogWarning($"USD import: failed to decode texture '{name}'.");
                return null;
            }

            return Finish(texture, compress, keepReadable);
        }

        // Compresses a freshly decoded texture and releases its CPU copy, unless the texture is to be
        // saved as an asset, which serializes the CPU copy.
        //
        // RGBA32 is four bytes a pixel whatever the image contains, and a stage's worth of them is
        // what has been taking imports past the memory the machine has. Block compression is a
        // quarter of that, and the quality it gives up is the quality PBR maps are authored to
        // survive -- these are not lookup tables.
        //
        // Both steps are conditional on what the texture allows. Block compression works on
        // dimensions that are multiples of four, and a texture that is not gets left alone rather
        // than refused: a non-conforming image is still a valid texture, just a larger one.
        private static Texture2D Finish(Texture2D texture, bool compress, bool keepReadable)
        {
            if (compress && (texture.width & 3) == 0 && (texture.height & 3) == 0 &&
                texture.width >= 4 && texture.height >= 4)
            {
                texture.Compress(false);
            }

            // The GPU has the pixels; the CPU copy is what the import no longer needs.
            texture.Apply(false, !keepReadable);
            return texture;
        }

        private static void SetMaterialTexture(Material material, string property, Texture texture)
        {
            if (material.HasProperty(property))
            {
                material.SetTexture(property, texture);
            }
        }

        // USD shading colours (diffuseColor, displayColor, MaterialX/MDL colours) are
        // linear. emissiveColor is linear too but goes to an [HDR] property, which is not converted. A Unity material colour is authored in sRGB and converted to linear for
        // rendering when the project uses the Linear colour space, so the linear value went
        // through that conversion a second time: the Teapot's (0.325, 0.825, 0) rendered a dark
        // saturated green where every USD renderer shows yellow-green. Converting to sRGB here
        // undoes that; UsdExporter does the inverse. A Gamma-space project renders material
        // colours unconverted, so they are left as authored there.
        internal static Color UsdColorToMaterialColor(Color linear)
        {
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? linear.gamma : linear;
        }

        // inputs:color is linear, and Light.color is sRGB: with the Linear colour space URP renders
        // a light at color.linear x intensity. Assigning the USD value as it was darkened every
        // tinted light by that second conversion: NVIDIA's Factory hangs its 15 sphere lights at
        // inputs:color 0.216, which rendered at 0.038, 5.6 x too dark, and the hall outside the
        // central rect light came out at 0.4-0.6 x Isaac Sim while the white rect matched. White
        // lights (every other bench stage) were not affected. UsdExporter does the inverse.
        internal static Color UsdLightColor(Color linear)
        {
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? linear.gamma : linear;
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

        private static void ConfigureNativeRuntime(UsdImportOptions options = null)
        {
            UsdExporter.GetRuntimeInfo(new UsdExportOptions
            {
                VerifyNativeRuntimeIntegrity = options == null || options.VerifyNativeRuntimeIntegrity
            });

            // The version gate used to live only in the export path, so an import ran against
            // whatever plugin happened to be loaded. That was backwards: the import path is where
            // untrusted file content is parsed, and the security review fixes live there.
            UsdExporter.ValidateNativeApiVersion();

            // The remote material cache: set here, on the main thread, where
            // Application.temporaryCachePath may be read; the stage read uses it from a worker.
            s_remoteMaterialCache = options == null || string.IsNullOrEmpty(options.RemoteMaterialCacheFolder)
                ? UsdRemoteMaterials.DefaultCacheFolder
                : options.RemoteMaterialCacheFolder;
            UsdNative.SetRemoteAssetCache(s_remoteMaterialCache);
        }

        private static string s_remoteMaterialCache;

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

            // Packing DecodeUniqueTextures could not finish off the main thread (a source only
            // LoadImage decodes), and those sources. See RunDeferredPacking.
            public Action DeferredPack;
            public List<string> DeferredPackSources;

            // Build-time cache: each imported material's Render Face Both copy (GetDoubleSidedMaterial).
            public readonly Dictionary<Material, Material> DoubleSidedMaterials = new Dictionary<Material, Material>();

            // Copied from UsdImportOptions so the read pass can confine authored asset paths
            // without threading the options through every texture helper.
            public bool AllowExternalAssetPaths;
            public string DefaultPrimPath;
            public double MetersPerUnit;
            public int UpAxis;
            public string NativeDiagnostics;
            public string CompositionReport;

            // UsdImportOptions.ImportPhysics: the stage's physics, read on the read pass.
            public UsdNativePhysics Physics;
            public string PhysicsReadError;
            public readonly List<NodeData> Nodes = new List<NodeData>();
            public readonly List<KeyValuePair<RUsdImportLight, string>> Lights = new List<KeyValuePair<RUsdImportLight, string>>();
            public readonly List<RUsdImportCamera> Cameras = new List<RUsdImportCamera>();

            // Point instancers (API 8): one entry per prototype mesh, with a placement for every
            // instance. Kept as arrays rather than expanded into prims -- see UsdPointInstancer.
            public readonly List<KeyValuePair<RUsdImportInstancedMesh, float[]>> InstancedMeshes =
                new List<KeyValuePair<RUsdImportInstancedMesh, float[]>>();
            public readonly List<KeyValuePair<RUsdImportAudio, string>> Audio = new List<KeyValuePair<RUsdImportAudio, string>>();
            public readonly List<MeshData> Meshes = new List<MeshData>();
            public readonly List<MaterialData> Materials = new List<MaterialData>();

            // Raw bytes for each unique texture file (absolute path → bytes), read once on the
            // read pass. Kept only as a fallback for PNGs the managed decoder cannot handle.
            public readonly Dictionary<string, byte[]> TextureBytes = new Dictionary<string, byte[]>();

            // Pre-decoded pixels for each unique texture (absolute path → decoded, or null when
            // the managed decoder declined and a main-thread LoadImage fallback is needed).
            // Decoded on the read pass — off the main thread for ImportAsync.
            public readonly Dictionary<string, DecodedImage> DecodedTextures = new Dictionary<string, DecodedImage>();

            // UDIM references: the key a material carries ("<folder>/<name>.<UDIM>.<ext>") → the
            // tiles found on disk for it. A tile is not a texture of its own: the set becomes
            // one atlas, read tile by tile. See DecodeUdimSets.
            public readonly Dictionary<string, List<KeyValuePair<int, string>>> UdimSets =
                new Dictionary<string, List<KeyValuePair<int, string>>>();

            // For each atlas, the texture scale and offset that put the mesh's UDIM-space UVs
            // onto it. Applied to the material's base-map transform in AssembleMaterial.
            public readonly Dictionary<string, UdimLayout> UdimLayouts = new Dictionary<string, UdimLayout>();

            // Textures that are not files on disk because the stage lives inside a package
            // (a .usdz): resolved identifier → the authored path the resolver reads it back by.
            // Their bytes come from the native resolver instead of File.ReadAllBytes.
            public readonly Dictionary<string, string> PackageAssets = new Dictionary<string, string>();

            // Time and skinning (native API 13). HasTimeInfo is false on older plugins.
            public RUsdImportTimeInfo TimeInfo;
            public bool HasTimeInfo;
            public readonly List<UsdNativeSkeleton> Skeletons = new List<UsdNativeSkeleton>();

            // Set by the build: prims left out because the joints stand for them (FindJointMirrorPrims).
            public HashSet<string> JointMirrorPrims = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<UsdNativeNodeTrack> NodeTracks = new List<UsdNativeNodeTrack>();
            public string[] LayerFiles = Array.Empty<string>();

            // The stage, kept open past the read for the UsdStageSource (UsdImportOptions.KeepStageOpen).
            public IntPtr KeptStage;

            // A live switch's light reference (see BuildSceneComponents); 0 or less computes it.
            public float LightReference;
        }

        private struct NodeData
        {
            public string Path;
            public RUsdMatrix4x4 LocalMatrix;
            public bool Visible;
            public int Flags; // UsdPrimCompositionFlags; 0 on native API 5
        }

        private sealed class MeshData
        {
            public string PrimPath;
            public string Name;
            public bool Visible;
            public int MaterialIndex;

            // The mesh's authored doubleSided (read through the Stage API, native API 13); false
            // when unauthored or when the plugin has no Stage API.
            public bool DoubleSided;
            public RUsdVec3[] Points;
            public int[] Indices;
            public RUsdVec3[] Normals;
            public RUsdVec2[][] UvSets;
            public RUsdSubmesh[] Submeshes;

            // Joint weights and blend shapes when the mesh is skinned (native API 13); null otherwise.
            public UsdNativeSkin Skin;

            // Animated points (native API 13); null when the points hold still.
            public UsdNativePointCache PointCache;

            // Set while building: the Unity blend-shape index each USD blend shape became.
            public int[] UnityBlendShapeIndices;

            // MeshShareCache's content hash, computed on first use.
            public ulong GeometryKey;
            public bool GeometryKeyComputed;
        }

        // Per-import lookup from mesh geometry to the Unity Mesh already built for it
        // (UsdImportOptions.ShareIdenticalMeshes). The key is a 64-bit FNV-1a hash over the raw
        // bits of the points, indices, normals, every UV set and the submesh index ranges, plus
        // their counts. A hash hit is only a candidate: Find confirms the buffers are equal bit
        // for bit before reusing, so a collision can never alias two different meshes. Submesh
        // material indices are left out on purpose; materials go on the renderer, not the Mesh.
        private sealed class MeshShareCache
        {
            private const ulong FnvOffset = 14695981039346656037UL;
            private const ulong FnvPrime = 1099511628211UL;

            private readonly Dictionary<ulong, List<KeyValuePair<MeshData, Mesh>>> entries =
                new Dictionary<ulong, List<KeyValuePair<MeshData, Mesh>>>();

            public Mesh Find(MeshData data)
            {
                if (!entries.TryGetValue(ComputeKey(data), out List<KeyValuePair<MeshData, Mesh>> candidates))
                {
                    return null;
                }

                foreach (KeyValuePair<MeshData, Mesh> candidate in candidates)
                {
                    if (candidate.Value != null && SameGeometry(candidate.Key, data))
                    {
                        return candidate.Value;
                    }
                }

                return null;
            }

            public void Add(MeshData data, Mesh mesh)
            {
                ulong key = ComputeKey(data);
                if (!entries.TryGetValue(key, out List<KeyValuePair<MeshData, Mesh>> candidates))
                {
                    candidates = new List<KeyValuePair<MeshData, Mesh>>(1);
                    entries.Add(key, candidates);
                }

                candidates.Add(new KeyValuePair<MeshData, Mesh>(data, mesh));
            }

            // Hashes 32-bit words rather than single bytes: the same FNV-1a mixing in a quarter of
            // the steps. Every float is hashed by its bit pattern, matching SameGeometry. The key
            // is cached on the MeshData so Find followed by Add hashes each entry only once.
            private static ulong ComputeKey(MeshData data)
            {
                if (data.GeometryKeyComputed)
                {
                    return data.GeometryKey;
                }

                ulong hash = FnvOffset;
                hash = Mix(hash, data.Points.Length);
                hash = Mix(hash, data.Indices.Length);
                hash = Mix(hash, data.Normals.Length);
                hash = Mix(hash, data.Submeshes.Length);
                hash = Mix(hash, data.UvSets.Length);

                foreach (RUsdVec3 p in data.Points)
                {
                    hash = Mix(Mix(Mix(hash, Bits(p.X)), Bits(p.Y)), Bits(p.Z));
                }

                foreach (int index in data.Indices)
                {
                    hash = Mix(hash, index);
                }

                foreach (RUsdVec3 n in data.Normals)
                {
                    hash = Mix(Mix(Mix(hash, Bits(n.X)), Bits(n.Y)), Bits(n.Z));
                }

                foreach (RUsdVec2[] uv in data.UvSets)
                {
                    int count = uv != null ? uv.Length : 0;
                    hash = Mix(hash, count);
                    for (int i = 0; i < count; i++)
                    {
                        hash = Mix(Mix(hash, Bits(uv[i].X)), Bits(uv[i].Y));
                    }
                }

                foreach (RUsdSubmesh submesh in data.Submeshes)
                {
                    hash = Mix(Mix(hash, submesh.IndexStart), submesh.IndexCount);
                }

                data.GeometryKey = hash;
                data.GeometryKeyComputed = true;
                return hash;
            }

            private static ulong Mix(ulong hash, int word)
            {
                return (hash ^ (uint)word) * FnvPrime;
            }

            private static bool SameGeometry(MeshData a, MeshData b)
            {
                if (ReferenceEquals(a, b))
                {
                    return true;
                }

                // A double-sided mesh is built with its back faces baked in (AddBackFaces), so it
                // never shares a Mesh with a one-sided mesh of the same buffers.
                if (a.DoubleSided != b.DoubleSided)
                {
                    return false;
                }

                if (!SameVec3(a.Points, b.Points) || !SameInts(a.Indices, b.Indices) || !SameVec3(a.Normals, b.Normals))
                {
                    return false;
                }

                if (a.UvSets.Length != b.UvSets.Length || a.Submeshes.Length != b.Submeshes.Length)
                {
                    return false;
                }

                for (int i = 0; i < a.UvSets.Length; i++)
                {
                    if (!SameVec2(a.UvSets[i] ?? Array.Empty<RUsdVec2>(), b.UvSets[i] ?? Array.Empty<RUsdVec2>()))
                    {
                        return false;
                    }
                }

                for (int i = 0; i < a.Submeshes.Length; i++)
                {
                    if (a.Submeshes[i].IndexStart != b.Submeshes[i].IndexStart ||
                        a.Submeshes[i].IndexCount != b.Submeshes[i].IndexCount)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool SameInts(int[] a, int[] b)
            {
                if (a.Length != b.Length)
                {
                    return false;
                }

                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            // Floats are compared by bit pattern, not with ==: identical buffers are what makes a
            // shared Mesh safe, and it keeps the comparison consistent with the hash (NaN equals
            // itself, 0 and -0 differ).
            private static bool SameVec3(RUsdVec3[] a, RUsdVec3[] b)
            {
                if (a.Length != b.Length)
                {
                    return false;
                }

                for (int i = 0; i < a.Length; i++)
                {
                    if (Bits(a[i].X) != Bits(b[i].X) || Bits(a[i].Y) != Bits(b[i].Y) || Bits(a[i].Z) != Bits(b[i].Z))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool SameVec2(RUsdVec2[] a, RUsdVec2[] b)
            {
                if (a.Length != b.Length)
                {
                    return false;
                }

                for (int i = 0; i < a.Length; i++)
                {
                    if (Bits(a[i].X) != Bits(b[i].X) || Bits(a[i].Y) != Bits(b[i].Y))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static int Bits(float value)
            {
                return new FloatBits { Float = value }.Int;
            }

            // Reinterprets a float's bits as an int without unsafe code (the assembly does not
            // allow it) and without BitConverter.SingleToInt32Bits, which the .NET Framework API
            // profile lacks.
            [StructLayout(LayoutKind.Explicit)]
            private struct FloatBits
            {
                [FieldOffset(0)] public float Float;
                [FieldOffset(0)] public int Int;
            }
        }

        // Everything a built Material depends on, so two MaterialData with the same key build
        // identical Materials (UsdImportOptions.ShareIdenticalMaterials). The prim path and name are
        // left out on purpose: Omniverse and Unreal-connector stages bind a material prim per mesh
        // (NVIDIA's Simple_Warehouse: 3,471 material prims over 17 MDL modules), and one Unity
        // Material each is both memory and, under Surface Cache GI, one 256x256 atlas slice each --
        // past 2,048 the slice array cannot be created and GI fails for the whole scene.
        private static string MaterialShareKey(MaterialData m)
        {
            RUsdMaterial s = m.Source;
            var b = new System.Text.StringBuilder(256);
            b.Append(s.R.ToString("R")).Append('|').Append(s.G.ToString("R")).Append('|').Append(s.B.ToString("R")).Append('|')
             .Append(s.A.ToString("R")).Append('|').Append(s.Metallic.ToString("R")).Append('|').Append(s.Roughness.ToString("R")).Append('|')
             .Append(s.UvScaleX.ToString("R")).Append('|').Append(s.UvScaleY.ToString("R")).Append('|')
             .Append(s.UvOffsetX.ToString("R")).Append('|').Append(s.UvOffsetY.ToString("R")).Append('|')
             .Append(s.EmissiveR.ToString("R")).Append('|').Append(s.EmissiveG.ToString("R")).Append('|').Append(s.EmissiveB.ToString("R")).Append('|')
             .Append(m.AlbedoPath).Append('|').Append(m.NormalPath).Append('|').Append(m.MetallicGlossPath).Append('|')
             .Append(m.EmissionPath).Append('|').Append(m.OpacityPath).Append('|').Append(m.OpacityThreshold.ToString("R")).Append('|')
             .Append(m.NormalIsSrgb).Append('|').Append(m.NormalFlipU).Append('|').Append(m.NormalFlipV).Append('|').Append(m.MetallicIsOrm).Append('|').Append(m.OcclusionPath).Append('|').Append(m.MetallicIsPlain).Append('|').Append(m.RoughnessPath).Append('|')
             .Append(m.MaskPath).Append('|').Append(m.MaskColor.x.ToString("R")).Append(',').Append(m.MaskColor.y.ToString("R")).Append(',').Append(m.MaskColor.z.ToString("R"))
             .Append('|').Append(m.MaskMode).Append('|').Append(m.ChannelColours == null ? "" : Vec(m.ChannelColours[0]) + ";" + Vec(m.ChannelColours[1]) + ";" + Vec(m.ChannelColours[2]))
             .Append('|').Append(m.HasRoughnessRange).Append(',').Append(m.RoughnessRange.x.ToString("R")).Append(',').Append(m.RoughnessRange.y.ToString("R"));
            return b.ToString();
        }

        private sealed class MaterialData
        {
            public string Name;
            public RUsdMaterial Source;
            // Resolved absolute texture paths (or null). Bytes live in StageData.TextureBytes,
            // keyed by path, so files shared across materials are read/decoded only once.
            public string AlbedoPath;
            public string NormalPath;
            // The normal map decodes as sRGB: its colour space is authored "sRGB", or left "auto"
            // on an 8-bit file, which Isaac Sim reads as sRGB (NormalMapColorSpaceLikeIsaac).
            public bool NormalIsSrgb;
            // The normal map's tangent frame is flipped along U / V (OmniPBR flip_tangent_u / _v):
            // Isaac Sim reads an OmniPBR normal map with green down unless told otherwise.
            public bool NormalFlipU;
            public bool NormalFlipV;
            public string MetallicGlossPath;
            public string EmissionPath;
            // Authored opacity: the texture whose alpha drives transparency (null when the USD
            // `opacity` input is not connected) and the cutout threshold (0 = alpha blend).
            public string OpacityPath;
            public float OpacityThreshold;
            // MetallicGlossPath is an OmniPBR ORM map: occlusion, roughness and metallic in red,
            // green and blue, where Unity wants metallic in red and smoothness in alpha. The
            // channels are moved during decoding; see RepackOrm.
            public bool MetallicIsOrm;
            public string OcclusionPath;
            // MetallicGlossPath is a plain metallic map (metallic in red, nothing in alpha), and
            // RoughnessPath a roughness map in a file of its own. Folded into one packed map
            // during decoding; see PackMetallicRoughness.
            public bool MetallicIsPlain;
            public string RoughnessPath;
            // An albedo blended with MaskColor (linear) by this mask texture; see PackAlbedoMask.
            public string MaskPath;
            public Vector3 MaskColor;
            // 0: MaskColor where the mask is 0. 1: ChannelColours painted by the mask's red, green
            // and blue, the albedo in its alpha (API 13). -1: no mask.
            public int MaskMode = -1;
            public Vector3[] ChannelColours;
            // Roughness = lerp(x, y, ORM.g) (API 13); see PackRoughnessRange.
            public bool HasRoughnessRange;
            public Vector2 RoughnessRange;
        }

        // Raw decoded pixels (RGBA32, Unity layout) produced off the main thread.
        private sealed class DecodedImage
        {
            public int Width;
            public int Height;
            public byte[] Rgba;

            // Set when the image was block-compressed on the worker thread that decoded it. The
            // uploader then hands these straight to the GPU and never looks at Rgba, which is
            // what keeps compression off the main thread. See UsdBlockCompressor.
            public byte[] Blocks;
            public int MipCount;
            public bool HasAlpha;

            // The pixels are not the file's: an ORM map repacked, or a metallic+roughness pair
            // folded into one. An export cannot hand the file back for such a texture.
            public bool Transformed;

            // The red channel alone, one byte a pixel, for an image that only ever feeds the
            // red or alpha of a packed map. Four times smaller than Rgba while it waits to be
            // packed, which on a stage with 670 such maps is the difference between fitting in
            // memory and not. Set in place of Rgba; Sample reads whichever is present.
            public byte[] Gray;

            // A compressed chain the native side built and still owns (API 12). Uploaded straight
            // from its memory and released, so the blocks never exist as a managed array.
            public UsdNative.NativeImage Native;
        }

        // Where a UDIM atlas's tiles sit, as the texture transform that maps a mesh's UDIM-space
        // UVs (u in [minColumn, maxColumn + 1), v likewise) onto the atlas.
        private struct UdimLayout
        {
            public Vector2 Scale;
            public Vector2 Offset;
        }
    }

    // The texture size limits, and the header check that enforces them on the Texture2D.LoadImage
    // fallback (CWE-400). UsdPngDecoder obeys these on the managed path; LoadImage
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
    // Targa (.tga), uncompressed and RLE, true-colour (24/32-bit BGR[A]) and greyscale (8-bit).
    // TGA has no signature, so the header is validated field by field before anything is read:
    // no colour map, a supported image type and depth, dimensions within UsdImageLimits, and a
    // buffer long enough for what the header claims (an RLE stream is bounds-checked as it is
    // expanded). Output is RGBA32 in Unity layout (row 0 = bottom), like UsdPngDecoder.
    internal static class UsdTgaDecoder
    {
        public static bool TryDecode(byte[] data, out int width, out int height, out byte[] rgba)
        {
            width = 0;
            height = 0;
            rgba = null;
            if (data == null || data.Length < 18)
            {
                return false;
            }

            int idLength = data[0];
            int colorMapType = data[1];
            int imageType = data[2];
            int w = data[12] | (data[13] << 8);
            int h = data[14] | (data[15] << 8);
            int depth = data[16];
            int descriptor = data[17];
            bool rle = imageType == 10 || imageType == 11;
            bool gray = imageType == 3 || imageType == 11;
            if (colorMapType != 0 || (imageType != 2 && imageType != 3 && imageType != 10 && imageType != 11))
            {
                return false;
            }

            if (gray ? depth != 8 : (depth != 24 && depth != 32))
            {
                return false;
            }

            if (!UsdImageLimits.IsWithin(w, h))
            {
                return false;
            }

            int bpp = depth / 8;
            long pixels = (long)w * h;
            int offset = 18 + idLength;
            if (!rle && data.Length < offset + pixels * bpp)
            {
                return false;
            }

            var src = new byte[pixels * bpp];
            if (rle)
            {
                long written = 0;
                int read = offset;
                while (written < src.Length)
                {
                    if (read >= data.Length)
                    {
                        return false;
                    }

                    int header = data[read++];
                    int count = (header & 0x7F) + 1;
                    if (written + (long)count * bpp > src.Length)
                    {
                        return false;
                    }

                    if ((header & 0x80) != 0)
                    {
                        if (read + bpp > data.Length)
                        {
                            return false;
                        }

                        for (int i = 0; i < count; i++)
                        {
                            Buffer.BlockCopy(data, read, src, (int)written, bpp);
                            written += bpp;
                        }

                        read += bpp;
                    }
                    else
                    {
                        int bytes = count * bpp;
                        if (read + bytes > data.Length)
                        {
                            return false;
                        }

                        Buffer.BlockCopy(data, read, src, (int)written, bytes);
                        written += bytes;
                        read += bytes;
                    }
                }
            }
            else
            {
                Buffer.BlockCopy(data, offset, src, 0, src.Length);
            }

            // Bit 5 of the descriptor: rows run top to bottom. Unity wants bottom to top.
            bool topDown = (descriptor & 0x20) != 0;
            bool rightToLeft = (descriptor & 0x10) != 0;
            var outPixels = new byte[pixels * 4];
            for (int y = 0; y < h; y++)
            {
                int srcRow = topDown ? (h - 1 - y) : y;
                for (int x = 0; x < w; x++)
                {
                    int srcX = rightToLeft ? (w - 1 - x) : x;
                    long s = ((long)srcRow * w + srcX) * bpp;
                    long d = ((long)y * w + x) * 4;
                    if (gray)
                    {
                        outPixels[d] = outPixels[d + 1] = outPixels[d + 2] = src[s];
                        outPixels[d + 3] = 255;
                    }
                    else
                    {
                        outPixels[d] = src[s + 2];
                        outPixels[d + 1] = src[s + 1];
                        outPixels[d + 2] = src[s];
                        outPixels[d + 3] = bpp == 4 ? src[s + 3] : (byte)255;
                    }
                }
            }

            width = w;
            height = h;
            rgba = outPixels;
            return true;
        }
    }

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
